using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using CoreLib.Diagnostics;

namespace CoreLib.Transport
{
    /// <summary>A file that arrived whole and passed its hash.</summary>
    public sealed class ReceivedFile
    {
        public ReceivedFile(string peerFingerprint, string name, string path, long size)
        {
            PeerFingerprint = peerFingerprint;
            Name = name;
            Path = path;
            Size = size;
        }

        public string PeerFingerprint { get; }

        public string Name { get; }

        /// <summary>
        /// Where it landed while it was being received. The app moves it somewhere the user can
        /// find - Downloads on Windows, MediaStore on Android - because only the app knows what
        /// that means.
        /// </summary>
        public string Path { get; }

        public long Size { get; }
    }

    /// <summary>
    /// Reassembles incoming files, one folder of parts at a time.
    ///
    /// <para><b>Why not just collect the bytes.</b> A clipboard image is a payload; a file is not.
    /// Buffering a video in memory to hash it at the end would put both ends at the mercy of
    /// whatever the sender felt like offering, so chunks are written to disk as they arrive and
    /// hashed on the way past. The peak memory cost of a transfer is one chunk.</para>
    ///
    /// <para><b>What it refuses.</b> A chunk that does not continue the transfer it claims to be
    /// part of, an offer larger than the ceiling, more bytes than were promised, and a completed
    /// file whose hash does not match. All of them discard the partial file rather than keeping
    /// something that looks finished and is not.</para>
    ///
    /// <para>One instance serves every peer. Transfers are keyed by peer and id together, so two
    /// devices sending at once cannot collide even if they pick the same id - which they will,
    /// eventually, because an id is only meaningful to its sender.</para>
    /// </summary>
    public sealed class FileTransferReceiver : IDisposable
    {
        /// <summary>
        /// How long a transfer may go without a chunk before it is abandoned. A peer that walks
        /// out of range mid-file must not pin a part-file and a file handle for ever.
        /// </summary>
        private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

        private readonly string _workDirectory;
        private readonly object _gate = new();
        private readonly Dictionary<string, Incoming> _incoming = new(StringComparer.Ordinal);
        private bool _disposed;

        /// <summary>Raised when a file has arrived whole and its hash matched.</summary>
        public event Action<ReceivedFile>? FileReceived;

        /// <summary>Raised when a transfer is abandoned, with something a person can read.</summary>
        public event Action<string, string>? FileFailed;

        public FileTransferReceiver(string workDirectory)
        {
            _workDirectory = workDirectory;
            Directory.CreateDirectory(workDirectory);
        }

        /// <summary>
        /// Accepts an offer and prepares somewhere to put it. Returns false when the transfer is
        /// refused, which the caller reports back so the sender stops rather than waiting.
        /// </summary>
        public bool Accept(string peerFingerprint, FileOffer offer)
        {
            if (_disposed) return false;

            if (offer.Size > FileTransferProtocol.MaxFileBytes)
            {
                Log.Write("Files", $"Refusing \"{offer.Name}\": {offer.Size} bytes is over the limit.");
                return false;
            }

            DiscardStale();

            string key = KeyFor(peerFingerprint, offer.TransferId);
            string path = Path.Combine(_workDirectory, $"{Guid.NewGuid():N}.part");

            try
            {
                var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

                lock (_gate)
                {
                    // A repeated offer replaces the earlier attempt rather than running beside
                    // it: the sender has plainly restarted, and two writers for one transfer is
                    // never what was meant.
                    if (_incoming.Remove(key, out var previous)) previous.Abandon();

                    _incoming[key] = new Incoming(peerFingerprint, offer, stream, path);
                }

                Log.Write("Files", $"Accepting \"{offer.Name}\", {Describe(offer.Size)}.");

                // An empty file is complete the moment it is accepted: there is no chunk coming,
                // because there is nothing to put in one. Without this it would sit open until
                // it went stale, and an empty file is a perfectly ordinary thing to send.
                if (offer.Size == 0) Complete(key, _incoming[key]);

                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Files", $"Could not prepare somewhere to put \"{offer.Name}\"", ex);
                return false;
            }
        }

        /// <summary>
        /// Takes one chunk. Returns true while the transfer is still going, and raises
        /// <see cref="FileReceived"/> on the chunk that completes it.
        /// </summary>
        public bool Accept(string peerFingerprint, uint transferId, long offset, ReadOnlySpan<byte> data)
        {
            if (_disposed) return false;

            string key = KeyFor(peerFingerprint, transferId);
            Incoming? transfer;

            lock (_gate)
            {
                if (!_incoming.TryGetValue(key, out transfer)) return false;
            }

            // Offsets are checked rather than trusted, so a chunk cannot be used to write
            // somewhere else in the file or to inflate it past what was offered.
            if (offset != transfer.Written)
            {
                Fail(key, transfer, $"expected the bytes at {transfer.Written} and got {offset}");
                return false;
            }

            if (transfer.Written + data.Length > transfer.Offer.Size)
            {
                Fail(key, transfer, "it sent more than it offered");
                return false;
            }

            try
            {
                transfer.Write(data);
            }
            catch (Exception ex)
            {
                Log.Write("Files", $"Writing \"{transfer.Offer.Name}\" failed", ex);
                Fail(key, transfer, "it could not be written to disk");
                return false;
            }

            if (transfer.Written < transfer.Offer.Size) return true;

            Complete(key, transfer);
            return true;
        }

        private void Complete(string key, Incoming transfer)
        {
            lock (_gate) _incoming.Remove(key);

            byte[] actual;
            try { actual = transfer.FinishAndHash(); }
            catch (Exception ex)
            {
                Log.Write("Files", $"Finishing \"{transfer.Offer.Name}\" failed", ex);
                transfer.Abandon();
                Raise(transfer.Offer.Name, "it could not be finished");
                return;
            }

            // The hash is what turns "all the bytes arrived" into "the right bytes arrived".
            // Fixed-time, because a mismatch is a security answer and not merely a fault.
            if (!CryptographicOperations.FixedTimeEquals(actual, transfer.Offer.Sha256))
            {
                Log.Write("Files", $"\"{transfer.Offer.Name}\" did not match its hash; discarding it.");
                transfer.Abandon();
                Raise(transfer.Offer.Name, "it arrived damaged");
                return;
            }

            Log.Write("Files", $"Received \"{transfer.Offer.Name}\", {Describe(transfer.Offer.Size)}.");

            try
            {
                FileReceived?.Invoke(new ReceivedFile(
                    transfer.PeerFingerprint, transfer.Offer.Name, transfer.Path, transfer.Offer.Size));
            }
            catch (Exception ex) { Log.Write("Files", "FileReceived handler threw", ex); }
        }

        private void Fail(string key, Incoming transfer, string reason)
        {
            lock (_gate) _incoming.Remove(key);

            Log.Write("Files", $"Dropping \"{transfer.Offer.Name}\": {reason}.");
            transfer.Abandon();
            Raise(transfer.Offer.Name, reason);
        }

        private void Raise(string name, string reason)
        {
            try { FileFailed?.Invoke(name, reason); }
            catch (Exception ex) { Log.Write("Files", "FileFailed handler threw", ex); }
        }

        /// <summary>Drops transfers whose sender has gone quiet, so nothing is pinned for ever.</summary>
        private void DiscardStale()
        {
            List<KeyValuePair<string, Incoming>> stale;

            lock (_gate)
            {
                stale = new List<KeyValuePair<string, Incoming>>();
                foreach (var pair in _incoming)
                {
                    if (DateTime.UtcNow - pair.Value.LastChunkUtc > StaleAfter) stale.Add(pair);
                }

                foreach (var pair in stale) _incoming.Remove(pair.Key);
            }

            foreach (var pair in stale)
            {
                Log.Write("Files", $"Abandoning \"{pair.Value.Offer.Name}\": nothing arrived for {StaleAfter.TotalMinutes:F0} minutes.");
                pair.Value.Abandon();
                Raise(pair.Value.Offer.Name, "the sender stopped");
            }
        }

        /// <summary>Keyed by peer as well as id, because an id only means anything to its sender.</summary>
        private static string KeyFor(string peerFingerprint, uint transferId) => $"{peerFingerprint}/{transferId}";

        internal static string Describe(long bytes) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
        };

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            List<Incoming> open;
            lock (_gate)
            {
                open = new List<Incoming>(_incoming.Values);
                _incoming.Clear();
            }

            foreach (var transfer in open) transfer.Abandon();

            FileReceived = null;
            FileFailed = null;
        }

        /// <summary>One transfer in flight: where it is going, how far it has got, and its hash so far.</summary>
        private sealed class Incoming
        {
            private readonly FileStream _stream;
            private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            public Incoming(string peerFingerprint, FileOffer offer, FileStream stream, string path)
            {
                PeerFingerprint = peerFingerprint;
                Offer = offer;
                Path = path;
                _stream = stream;
                LastChunkUtc = DateTime.UtcNow;
            }

            public string PeerFingerprint { get; }
            public FileOffer Offer { get; }
            public string Path { get; }
            public long Written { get; private set; }
            public DateTime LastChunkUtc { get; private set; }

            public void Write(ReadOnlySpan<byte> data)
            {
                _stream.Write(data);
                _hash.AppendData(data);
                Written += data.Length;
                LastChunkUtc = DateTime.UtcNow;
            }

            /// <summary>Closes the file and returns what was actually written, hashed on the way past.</summary>
            public byte[] FinishAndHash()
            {
                _stream.Flush();
                _stream.Dispose();
                return _hash.GetHashAndReset();
            }

            public void Abandon()
            {
                try { _stream.Dispose(); } catch { }
                try { _hash.Dispose(); } catch { }
                try { if (File.Exists(Path)) File.Delete(Path); } catch { }
            }
        }
    }
}
