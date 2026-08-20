using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using CoreLib.Diagnostics;

namespace CoreLib.Transport
{
    /// <summary>
    /// Browsing another device's shared folders, and answering when it browses this one.
    ///
    /// <para><b>Both halves in one place, because both devices are both.</b> Every other feature
    /// here ended up with a sender on one side and a receiver on the other, and each time that
    /// happened it turned out to be wrong the moment a second phone joined. A device that can be
    /// browsed and cannot browse is not a peer.</para>
    ///
    /// <para><b>A fetch is answered with an ordinary file offer.</b> Nothing about the transfer
    /// is new: the offer, the hash, the chunking and the refusal path are the ones already built
    /// and tested. The only new thing is that the sender was asked rather than deciding, which is
    /// exactly the part <see cref="SharedFolders"/> guards.</para>
    ///
    /// <para><b>Requests time out.</b> A browse rides the standing link, which may be Bluetooth,
    /// which may be mid-reconnect. A listing that never arrives has to end as an empty list with
    /// a reason rather than a spinner that never stops.</para>
    /// </summary>
    public sealed class BrowseService
    {
        /// <summary>Long enough for a slow Bluetooth round trip, short enough to give up on.</summary>
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        private readonly ConcurrentDictionary<string, TaskCompletionSource<BrowseReply>> _waiting =
            new(StringComparer.Ordinal);

        /// <summary>What this device is willing to let paired devices look inside.</summary>
        public SharedFolders Shared { get; } = new();

        /// <summary>Sends a payload to one peer. Set by whichever app owns the transport.</summary>
        public Func<string, byte, byte[], Task>? Send { get; set; }

        /// <summary>Sends a file to one peer, by the ordinary transfer path.</summary>
        public Func<string, string, Task>? SendFile { get; set; }

        // ------------------------------------------------------------------ asking

        /// <summary>
        /// Asks a peer what is in one of its shared folders.
        ///
        /// An empty <paramref name="folderId"/> asks for the list of shared folders themselves,
        /// which is where browsing always starts - the ids come back on those rows.
        /// </summary>
        public async Task<BrowseReply> BrowseAsync(string fingerprint, string folderId, string relativePath)
        {
            var send = Send;
            if (send == null) return Empty(folderId, relativePath, BrowseStatus.NotFound);

            string ticket = Ticket(fingerprint, folderId, relativePath);
            var waiter = new TaskCompletionSource<BrowseReply>(TaskCreationOptions.RunContinuationsAsynchronously);

            // A second request for the same folder replaces the first rather than racing it: the
            // usual cause is an impatient tap, and both would be answered by the same reply.
            _waiting[ticket] = waiter;

            try
            {
                await send(fingerprint, SyncContent.BrowseRequest,
                           BrowseProtocol.BuildRequest(folderId, relativePath)).ConfigureAwait(false);

                using var cancel = new CancellationTokenSource(Timeout);
                using (cancel.Token.Register(() => waiter.TrySetResult(Empty(folderId, relativePath, BrowseStatus.NotFound))))
                {
                    return await waiter.Task.ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Browse", "Could not ask for a listing", ex);
                return Empty(folderId, relativePath, BrowseStatus.NotFound);
            }
            finally
            {
                _waiting.TryRemove(ticket, out _);
            }
        }

        /// <summary>Asks a peer to send one file. It arrives by the ordinary file path.</summary>
        public async Task<bool> FetchAsync(string fingerprint, string folderId, string relativePath)
        {
            var send = Send;
            if (send == null) return false;

            try
            {
                await send(fingerprint, SyncContent.FetchRequest,
                           BrowseProtocol.BuildRequest(folderId, relativePath)).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Browse", "Could not ask for a file", ex);
                return false;
            }
        }

        // ------------------------------------------------------------------ answering

        /// <summary>
        /// Deals with anything browsing-related that arrived. True when it was one of ours.
        ///
        /// <paramref name="fingerprint"/> is a paired device by the time this runs - the payload
        /// opened under the connection's key - which is what makes it safe to answer at all.
        /// </summary>
        public bool Handle(string fingerprint, byte contentType, byte[] body)
        {
            switch (contentType)
            {
                case SyncContent.BrowseRequest:
                    _ = AnswerBrowseAsync(fingerprint, body);
                    return true;

                case SyncContent.BrowseReply:
                    AcceptReply(fingerprint, body);
                    return true;

                case SyncContent.FetchRequest:
                    _ = AnswerFetchAsync(fingerprint, body);
                    return true;

                default:
                    return false;
            }
        }

        private async Task AnswerBrowseAsync(string fingerprint, byte[] body)
        {
            var send = Send;
            if (send == null) return;

            try
            {
                if (!BrowseProtocol.TryParseRequest(body, out string folderId, out string relativePath))
                {
                    Log.Write("Browse", "A listing request could not be read.");
                    return;
                }

                byte[] reply = folderId.Length == 0
                    ? RootListing()
                    : FolderListing(folderId, relativePath);

                await send(fingerprint, SyncContent.BrowseReply, reply).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write("Browse", "Could not answer a listing request", ex);
            }
        }

        /// <summary>The shared folders themselves, as the rows a peer starts from.</summary>
        private byte[] RootListing()
        {
            var folders = Shared.All();
            var entries = new BrowseEntry[folders.Count];

            for (int i = 0; i < folders.Count; i++)
            {
                entries[i] = new BrowseEntry(folders[i].Name, isDirectory: true, 0,
                                             DateTime.UtcNow, folders[i].Id);
            }

            Log.Write("Browse", $"A peer asked what is shared: {folders.Count} folder(s).");
            return BrowseProtocol.BuildReply("", "", BrowseStatus.Ok, entries);
        }

        private byte[] FolderListing(string folderId, string relativePath)
        {
            var refusal = Shared.TryList(folderId, relativePath, out var entries);

            if (refusal != SharedFolders.Refusal.None)
            {
                // Said out loud: a refused browse is either a folder that was unshared while
                // somebody was looking at it, or something worth knowing about.
                Log.Write("Browse", $"Refused a listing: {refusal}.");
                return BrowseProtocol.BuildReply(folderId, relativePath, StatusFor(refusal), Array.Empty<BrowseEntry>());
            }

            return BrowseProtocol.BuildReply(folderId, relativePath, BrowseStatus.Ok, entries);
        }

        private async Task AnswerFetchAsync(string fingerprint, byte[] body)
        {
            var sendFile = SendFile;
            if (sendFile == null) return;

            try
            {
                if (!BrowseProtocol.TryParseRequest(body, out string folderId, out string relativePath)) return;

                var refusal = Shared.TryResolveFile(folderId, relativePath, out string path);

                if (refusal != SharedFolders.Refusal.None)
                {
                    Log.Write("Browse", $"Refused a fetch: {refusal}.");
                    return;
                }

                Log.Write("Browse", "Sending a file a peer asked for.");
                await sendFile(fingerprint, path).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write("Browse", "Could not answer a fetch", ex);
            }
        }

        private void AcceptReply(string fingerprint, byte[] body)
        {
            if (!BrowseProtocol.TryParseReply(body, out var reply))
            {
                Log.Write("Browse", "A listing could not be read.");
                return;
            }

            string ticket = Ticket(fingerprint, reply.FolderId, reply.RelativePath);

            if (_waiting.TryGetValue(ticket, out var waiter)) waiter.TrySetResult(reply);
            else Log.Write("Browse", "A listing arrived that nothing was waiting for.");
        }

        // ------------------------------------------------------------------ helpers

        private static BrowseStatus StatusFor(SharedFolders.Refusal refusal) => refusal switch
        {
            SharedFolders.Refusal.NoSuchFolder => BrowseStatus.NoSuchFolder,
            SharedFolders.Refusal.OutsideTheFolder => BrowseStatus.NotAllowed,
            _ => BrowseStatus.NotFound
        };

        private static BrowseReply Empty(string folderId, string relativePath, BrowseStatus status) =>
            new(folderId, relativePath, status, Array.Empty<BrowseEntry>(), false);

        /// <summary>Which request a reply answers: the peer, the folder and the path within it.</summary>
        private static string Ticket(string fingerprint, string folderId, string relativePath) =>
            $"{fingerprint} {folderId} {relativePath}";
    }
}
