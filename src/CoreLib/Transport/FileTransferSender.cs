using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CoreLib.Diagnostics;

namespace CoreLib.Transport
{
    /// <summary>How a transfer ended, in terms the UI can say out loud.</summary>
    public enum FileSendResult
    {
        Sent,
        Refused,
        NoAnswer,
        Unreachable,
        TooLarge,
        Failed
    }

    /// <summary>
    /// Drives the sending half: offer, wait to be told yes, then stream.
    ///
    /// <para>Deliberately knows nothing about sockets. It is handed a way to send one payload and
    /// a way to wait for an answer, so the same code sends over Wi-Fi from either app and can be
    /// tested without either - which is the only way this gets exercised at all, because a file
    /// transfer is exactly the thing nobody wants to test by hand.</para>
    ///
    /// <para>The hash is computed by reading the file through once before offering it. That is a
    /// second pass over the bytes, and worth it: the receiver knowing what it is expecting before
    /// the first chunk arrives is what makes a truncated transfer a failure rather than a file
    /// that looks complete and is not.</para>
    /// </summary>
    public sealed class FileTransferSender
    {
        /// <summary>
        /// How long to wait for the peer to say yes or no.
        ///
        /// Long enough for a peer that has to raise Wi-Fi first, because the offer may be the
        /// very thing that woke it.
        /// </summary>
        public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(30);

        private readonly Func<byte, byte[], CancellationToken, Task<bool>> _send;
        private readonly TimeSpan _answerTimeout;
        private readonly object _gate = new();

        private TaskCompletionSource<bool>? _answer;
        private uint _awaitingTransferId;
        private uint _nextTransferId;

        /// <param name="send">
        /// Sends one payload to the peer this sender is for, and reports whether it left. The
        /// caller owns which peer that is - a sender belongs to one link.
        /// </param>
        /// <param name="answerTimeout">
        /// Overrides how long to wait for the peer's decision. Only the tests pass this: waiting
        /// out the real half minute to prove a timeout works would put half a minute on every
        /// run of the suite, and a slow suite is one that stops being run.
        /// </param>
        public FileTransferSender(Func<byte, byte[], CancellationToken, Task<bool>> send,
                                  TimeSpan? answerTimeout = null)
        {
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _answerTimeout = answerTimeout ?? AnswerTimeout;
            _nextTransferId = (uint)Random.Shared.Next(1, int.MaxValue);
        }

        /// <summary>Raised as the transfer progresses, so a UI has something honest to show.</summary>
        public event Action<string, long, long>? Progress;

        /// <summary>Feeds in the peer's answer. Called by whatever handles an incoming FileAck.</summary>
        public void NoteAnswer(uint transferId, bool accepted)
        {
            lock (_gate)
            {
                if (_answer == null || transferId != _awaitingTransferId) return;
                _answer.TrySetResult(accepted);
            }
        }

        public async Task<FileSendResult> SendAsync(string path, CancellationToken cancellationToken = default)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists) return FileSendResult.Failed;
            }
            catch (Exception ex)
            {
                Log.Write("Files", $"Could not read {path}", ex);
                return FileSendResult.Failed;
            }

            if (info.Length > FileTransferProtocol.MaxFileBytes)
            {
                Log.Write("Files", $"Refusing to send \"{info.Name}\": {FileTransferReceiver.Describe(info.Length)} is over the limit.");
                return FileSendResult.TooLarge;
            }

            byte[] hash;
            try { hash = await HashAsync(path, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Log.Write("Files", $"Could not hash \"{info.Name}\"", ex);
                return FileSendResult.Failed;
            }

            uint transferId;
            Task<bool> answered;

            lock (_gate)
            {
                transferId = NextTransferId();
                _awaitingTransferId = transferId;
                _answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                answered = _answer.Task;
            }

            string name = FileTransferProtocol.SafeName(info.Name);

            if (!await _send(SyncContent.FileOffer,
                             FileTransferProtocol.BuildOffer(transferId, name, info.Length, hash),
                             cancellationToken).ConfigureAwait(false))
            {
                return FileSendResult.Unreachable;
            }

            Log.Write("Files", $"Offered \"{name}\", {FileTransferReceiver.Describe(info.Length)}.");

            bool accepted;
            try
            {
                accepted = await answered.WaitAsync(_answerTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Log.Write("Files", $"\"{name}\" went unanswered for {_answerTimeout.TotalSeconds:F0}s.");
                return FileSendResult.NoAnswer;
            }
            catch (OperationCanceledException)
            {
                return FileSendResult.Failed;
            }
            finally
            {
                lock (_gate) _answer = null;
            }

            if (!accepted)
            {
                Log.Write("Files", $"The peer turned down \"{name}\".");
                return FileSendResult.Refused;
            }

            return await StreamAsync(path, name, transferId, info.Length, cancellationToken).ConfigureAwait(false);
        }

        private async Task<FileSendResult> StreamAsync(string path, string name, uint transferId,
                                                       long size, CancellationToken cancellationToken)
        {
            try
            {
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                                      FileTransferProtocol.ChunkBytes, useAsync: true);

                var buffer = new byte[FileTransferProtocol.ChunkBytes];
                long offset = 0;

                while (offset < size)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int read = await file.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                        .ConfigureAwait(false);
                    if (read <= 0) break;

                    // Awaited one at a time on purpose. The transport holds a send lock anyway,
                    // and queuing the whole file would put it all in memory - which is the thing
                    // chunking exists to avoid.
                    if (!await _send(SyncContent.FileChunk,
                                     FileTransferProtocol.BuildChunk(transferId, offset, buffer.AsSpan(0, read)),
                                     cancellationToken).ConfigureAwait(false))
                    {
                        Log.Write("Files", $"\"{name}\" stopped partway: the peer went away.");
                        return FileSendResult.Unreachable;
                    }

                    offset += read;

                    try { Progress?.Invoke(name, offset, size); }
                    catch (Exception ex) { Log.Write("Files", "Progress handler threw", ex); }
                }

                if (offset < size)
                {
                    Log.Write("Files", $"\"{name}\" was shorter than expected; it may have changed while it was being sent.");
                    return FileSendResult.Failed;
                }

                Log.Write("Files", $"Sent \"{name}\", {FileTransferReceiver.Describe(size)}.");
                return FileSendResult.Sent;
            }
            catch (OperationCanceledException)
            {
                return FileSendResult.Failed;
            }
            catch (Exception ex)
            {
                Log.Write("Files", $"Sending \"{name}\" failed", ex);
                return FileSendResult.Failed;
            }
        }

        private static async Task<byte[]> HashAsync(string path, CancellationToken cancellationToken)
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                                  FileTransferProtocol.ChunkBytes, useAsync: true);
            return await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Never zero, so a transfer id is always distinguishable from an unset field, and
        /// wrapping simply starts again - ids only have to be unique among what is in flight.
        /// </summary>
        private uint NextTransferId()
        {
            unchecked { _nextTransferId++; }
            if (_nextTransferId == 0) _nextTransferId = 1;
            return _nextTransferId;
        }
    }
}
