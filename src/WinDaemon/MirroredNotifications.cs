using System;
using System.Collections.Generic;
using System.Linq;
using CoreLib.Diagnostics;
using CoreLib.Transport;

namespace WinDaemon
{
    /// <summary>One mirrored notification, and which device it came from.</summary>
    public sealed class MirroredEntry
    {
        public required string PeerFingerprint { get; init; }
        public required string PeerName { get; init; }
        public required MirroredNotification Notification { get; init; }

        public DateTime ReceivedUtc { get; } = DateTime.UtcNow;

        public string RelativeAge
        {
            get
            {
                var elapsed = DateTime.UtcNow - ReceivedUtc;
                if (elapsed.TotalSeconds < 5) return "just now";
                if (elapsed.TotalMinutes < 1) return $"{(int)elapsed.TotalSeconds}s ago";
                if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes}m ago";
                return $"{(int)elapsed.TotalHours}h ago";
            }
        }
    }

    /// <summary>
    /// What the phone is showing, held only for as long as it is showing it.
    ///
    /// <para><b>Never written down.</b> This is the most private thing the app carries, and the
    /// project rule for clipboard traffic - in memory, dies with the process - applies here with
    /// more force rather than less. There is no file, no cache and no log line carrying the
    /// contents.</para>
    ///
    /// <para><b>Bounded.</b> A phone that notifies constantly must not grow this without limit,
    /// and nobody reads the two hundredth row anyway.</para>
    /// </summary>
    public static class MirroredNotifications
    {
        private const int Capacity = 50;

        private static readonly object Gate = new();
        private static readonly LinkedList<MirroredEntry> Entries = new();

        /// <summary>Raised whenever the list changes, so a window can redraw it.</summary>
        public static event Action? Changed;

        /// <summary>Sends a dismissal back to the device it came from.</summary>
        public static Func<string, string, System.Threading.Tasks.Task>? DismissOnPeer { get; set; }

        public static IReadOnlyList<MirroredEntry> Snapshot()
        {
            lock (Gate) return Entries.ToList();
        }

        public static int Count { get { lock (Gate) return Entries.Count; } }

        public static void Add(string peerFingerprint, string peerName, MirroredNotification notification)
        {
            lock (Gate)
            {
                // An app updating a notification reposts it under the same key. Replacing rather
                // than appending is what stops a chat thread becoming twenty rows.
                var existing = Find(notification.Key);
                if (existing != null) Entries.Remove(existing);

                Entries.AddFirst(new MirroredEntry
                {
                    PeerFingerprint = peerFingerprint,
                    PeerName = peerName,
                    Notification = notification
                });

                while (Entries.Count > Capacity) Entries.RemoveLast();
            }

            // Deliberately no contents in the log line. Knowing one arrived is useful for
            // diagnosis; knowing what it said is nobody's business, including the log file's.
            Log.Write("Notify", $"Mirrored a notification from {peerName}.");
            Raise();
        }

        /// <summary>
        /// Drops a notification, and by default tells the device it came from.
        ///
        /// <paramref name="tellThePeer"/> is false when the peer is the one that dismissed it,
        /// which would otherwise bounce straight back and forth.
        /// </summary>
        public static void Remove(string key, bool tellThePeer = true)
        {
            MirroredEntry entry;

            lock (Gate)
            {
                var node = Find(key);
                if (node == null) return;

                entry = node.Value;
                Entries.Remove(node);
            }

            if (tellThePeer)
            {
                var send = DismissOnPeer;
                if (send != null)
                {
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        try { await send(entry.PeerFingerprint, key).ConfigureAwait(false); }
                        catch (Exception ex) { Log.Write("Notify", "Could not dismiss on the peer", ex); }
                    });
                }
            }

            Raise();
        }

        public static void Clear()
        {
            List<MirroredEntry> dropped;

            lock (Gate)
            {
                dropped = Entries.ToList();
                Entries.Clear();
            }

            if (dropped.Count == 0) return;

            var send = DismissOnPeer;
            if (send != null)
            {
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    foreach (var entry in dropped)
                    {
                        try { await send(entry.PeerFingerprint, entry.Notification.Key).ConfigureAwait(false); }
                        catch (Exception ex) { Log.Write("Notify", "Could not dismiss on the peer", ex); }
                    }
                });
            }

            Raise();
        }

        /// <summary>Drops everything from one device, for when it goes away or is forgotten.</summary>
        public static void ClearFrom(string peerFingerprint)
        {
            bool removed = false;

            lock (Gate)
            {
                var node = Entries.First;
                while (node != null)
                {
                    var next = node.Next;
                    if (string.Equals(node.Value.PeerFingerprint, peerFingerprint, StringComparison.OrdinalIgnoreCase))
                    {
                        Entries.Remove(node);
                        removed = true;
                    }
                    node = next;
                }
            }

            if (removed) Raise();
        }

        /// <summary>Caller holds the gate.</summary>
        private static LinkedListNode<MirroredEntry>? Find(string key)
        {
            for (var node = Entries.First; node != null; node = node.Next)
            {
                if (string.Equals(node.Value.Notification.Key, key, StringComparison.Ordinal)) return node;
            }

            return null;
        }

        private static void Raise()
        {
            try { Changed?.Invoke(); }
            catch (Exception ex) { Log.Write("Notify", "Changed handler threw", ex); }
        }
    }
}
