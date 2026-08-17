using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using CoreLib.Diagnostics;

namespace CoreLib.Transport
{
    /// <summary>
    /// Both halves of file transfer, and the routing between them.
    ///
    /// <para>Exists so the two apps hold one object rather than a receiver, a sender per peer and
    /// the bookkeeping to match an acknowledgement back to whoever is waiting for it. Everything
    /// platform-specific stays outside: where a finished file should end up is the app's
    /// business, because only it knows what Downloads means.</para>
    ///
    /// <para>A sender is kept per peer rather than per transfer. Transfers to one peer are
    /// serialised by the send lock in the transport anyway, and keeping the sender alive is what
    /// lets a late acknowledgement find the thing that is waiting for it.</para>
    /// </summary>
    public sealed class FileTransferService : IDisposable
    {
        private readonly FileTransferReceiver _receiver;
        private readonly Func<string, byte, byte[], CancellationToken, Task<bool>> _sendToPeer;
        private readonly ConcurrentDictionary<string, FileTransferSender> _senders = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        /// <summary>A file arrived whole and matched its hash. The path is a working copy to move.</summary>
        public event Action<ReceivedFile>? FileReceived;

        /// <summary>A transfer was abandoned, with something a person can read.</summary>
        public event Action<string, string>? FileFailed;

        /// <summary>Name, bytes sent, total. For a progress bar that is telling the truth.</summary>
        public event Action<string, long, long>? Progress;

        /// <param name="workDirectory">Where part-files live while they are arriving.</param>
        /// <param name="sendToPeer">Sends one payload to one peer, reporting whether it left.</param>
        public FileTransferService(string workDirectory,
                                   Func<string, byte, byte[], CancellationToken, Task<bool>> sendToPeer)
        {
            _sendToPeer = sendToPeer ?? throw new ArgumentNullException(nameof(sendToPeer));

            _receiver = new FileTransferReceiver(workDirectory);
            _receiver.FileReceived += file =>
            {
                try { FileReceived?.Invoke(file); }
                catch (Exception ex) { Log.Write("Files", "FileReceived handler threw", ex); }
            };
            _receiver.FileFailed += (name, reason) =>
            {
                try { FileFailed?.Invoke(name, reason); }
                catch (Exception ex) { Log.Write("Files", "FileFailed handler threw", ex); }
            };
        }

        /// <summary>Sends a file to one peer.</summary>
        public Task<FileSendResult> SendAsync(string peerFingerprint, string path,
                                              CancellationToken cancellationToken = default)
        {
            if (_disposed) return Task.FromResult(FileSendResult.Failed);
            return SenderFor(peerFingerprint).SendAsync(path, cancellationToken);
        }

        /// <summary>
        /// Takes one of the three file content types off a peer's link.
        ///
        /// Returns false for anything that is not part of a transfer, so the caller can carry on
        /// to its own handling rather than having to know the type numbers itself.
        /// </summary>
        public bool Handle(string peerFingerprint, byte contentType, byte[] body)
        {
            if (_disposed) return false;

            switch (contentType)
            {
                case SyncContent.FileOffer:
                    HandleOffer(peerFingerprint, body);
                    return true;

                case SyncContent.FileAck:
                    if (FileTransferProtocol.TryParseAck(body, out uint ackId, out bool accepted))
                    {
                        SenderFor(peerFingerprint).NoteAnswer(ackId, accepted);
                    }
                    return true;

                case SyncContent.FileChunk:
                    if (FileTransferProtocol.TryParseChunk(body, out uint chunkId, out long offset, out var data))
                    {
                        _receiver.Accept(peerFingerprint, chunkId, offset, data);
                    }
                    return true;

                default:
                    return false;
            }
        }

        private void HandleOffer(string peerFingerprint, byte[] body)
        {
            if (!FileTransferProtocol.TryParseOffer(body, out var offer) || offer == null)
            {
                Log.Write("Files", "Ignoring a malformed file offer.");
                return;
            }

            bool accepted = _receiver.Accept(peerFingerprint, offer);

            // Answered either way. A sender that is turned down stops immediately rather than
            // waiting out its timeout, and a refusal is an ordinary answer rather than a fault.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _sendToPeer(peerFingerprint, SyncContent.FileAck,
                                      FileTransferProtocol.BuildAck(offer.TransferId, accepted),
                                      CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Write("Files", $"Could not answer the offer of \"{offer.Name}\"", ex);
                }
            });
        }

        private FileTransferSender SenderFor(string peerFingerprint) =>
            _senders.GetOrAdd(peerFingerprint, fingerprint =>
            {
                var sender = new FileTransferSender(
                    (contentType, body, token) => _sendToPeer(fingerprint, contentType, body, token));

                sender.Progress += (name, sent, total) =>
                {
                    try { Progress?.Invoke(name, sent, total); }
                    catch (Exception ex) { Log.Write("Files", "Progress handler threw", ex); }
                };

                return sender;
            });

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _receiver.Dispose();
            _senders.Clear();

            FileReceived = null;
            FileFailed = null;
            Progress = null;
        }
    }
}
