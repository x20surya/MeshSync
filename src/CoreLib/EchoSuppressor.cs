using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace CoreLib
{
    /// <summary>
    /// Stops a clipboard entry that arrived from a peer being echoed straight back.
    ///
    /// Replaces the previous "set a bool, clear it 500 ms later" approach, which lost a
    /// genuine user copy that happened inside the window and, when two payloads arrived
    /// back to back, cleared the flag while the second was still being applied.
    /// Matching on content instead of on timing makes the decision exact.
    /// </summary>
    public sealed class EchoSuppressor
    {
        private readonly TimeSpan _window;
        private readonly TimeSpan _duplicateSendWindow;
        private readonly int _capacity;
        private readonly object _gate = new();
        private readonly Dictionary<string, DateTime> _recent = new(StringComparer.Ordinal);

        private readonly TimeSpan _imageGuardWindow;

        private string? _lastSentKey;
        private DateTime _lastSentAt = DateTime.MinValue;
        private DateTime _imageInjectedAt = DateTime.MinValue;

        public EchoSuppressor(
            TimeSpan? window = null,
            int capacity = 32,
            TimeSpan? duplicateSendWindow = null,
            TimeSpan? imageGuardWindow = null)
        {
            _window = window ?? TimeSpan.FromSeconds(10);
            // Short: only collapses the burst of notifications for one copy, so a genuine
            // re-copy a second or two later still syncs.
            _duplicateSendWindow = duplicateSendWindow ?? TimeSpan.FromMilliseconds(900);
            // Long enough to cover decode plus re-encode of a large screenshot, short enough
            // that deliberately copying a different image straight after is not swallowed.
            _imageGuardWindow = imageGuardWindow ?? TimeSpan.FromSeconds(3);
            _capacity = Math.Max(1, capacity);
        }

        /// <summary>Record content that we just applied locally after receiving it from a peer.</summary>
        public void NoteInbound(ReadOnlySpan<byte> content, SyncItemKind kind = SyncItemKind.Text)
        {
            string key = Fingerprint(content);
            lock (_gate)
            {
                Prune();
                _recent[key] = DateTime.UtcNow;

                // Images do not survive a clipboard round-trip byte-for-byte: Windows decodes
                // the received JPEG to a bitmap and the capture path re-encodes it, so the
                // bytes that come back differ from the bytes we stored and the fingerprint
                // never matches. A short kind-scoped guard covers that window; text is exact
                // and needs no such help.
                if (kind == SyncItemKind.Image) _imageInjectedAt = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// True if this content is one we recently received, meaning the local clipboard
        /// change was our own injection and must not be sent back.
        ///
        /// Deliberately does NOT consume the entry. Both platforms raise several clipboard
        /// notifications for a single change - Android fires OnPrimaryClipChanged more than
        /// once, and many Windows apps raise WM_CLIPBOARDUPDATE repeatedly - so consuming on
        /// the first check let the second notification look like a genuine user copy and
        /// bounce the content straight back, which ping-ponged between the devices and
        /// showed up as every item being sent and pasted twice.
        ///
        /// The trade-off is that deliberately re-copying identical content inside the window
        /// does not re-sync. That is harmless: both devices already hold that exact content.
        /// </summary>
        public bool IsEcho(ReadOnlySpan<byte> content)
        {
            string key = Fingerprint(content);
            lock (_gate)
            {
                Prune();
                if (!_recent.TryGetValue(key, out var at)) return false;
                if (DateTime.UtcNow - at > _window)
                {
                    _recent.Remove(key);
                    return false;
                }

                return true;
            }
        }

        /// <summary>
        /// True if this content was already sent moments ago, so a repeated clipboard
        /// notification for one user copy does not transmit it twice.
        /// Claims the content when it returns true.
        /// </summary>
        public bool IsDuplicateSend(ReadOnlySpan<byte> content)
        {
            string key = Fingerprint(content);
            var now = DateTime.UtcNow;

            lock (_gate)
            {
                if (_lastSentKey == key && now - _lastSentAt < _duplicateSendWindow) return true;

                _lastSentKey = key;
                _lastSentAt = now;
                return false;
            }
        }

        /// <summary>
        /// The single decision point for "should this clipboard content go to the peer?".
        ///
        /// Both checks live here together because doing them as separate early-returns at the
        /// call site let one skip the other: an echo returned before the duplicate check ran,
        /// so the next notification for that same content passed both and was transmitted.
        /// </summary>
        public bool ShouldSend(ReadOnlySpan<byte> content, SyncItemKind kind = SyncItemKind.Text)
        {
            if (kind == SyncItemKind.Image && WasImageJustInjected()) return false;
            if (IsEcho(content)) return false;
            if (IsDuplicateSend(content)) return false;
            return true;
        }

        /// <summary>
        /// True while a re-encoded copy of an image we just applied could still surface as a
        /// local clipboard notification.
        /// </summary>
        private bool WasImageJustInjected()
        {
            lock (_gate) return DateTime.UtcNow - _imageInjectedAt < _imageGuardWindow;
        }

        public void Clear()
        {
            lock (_gate)
            {
                _recent.Clear();
                _lastSentKey = null;
                _lastSentAt = DateTime.MinValue;
                _imageInjectedAt = DateTime.MinValue;
            }
        }

        private void Prune()
        {
            if (_recent.Count == 0) return;

            var cutoff = DateTime.UtcNow - _window;
            List<string>? stale = null;
            foreach (var kvp in _recent)
            {
                if (kvp.Value < cutoff) (stale ??= new List<string>()).Add(kvp.Key);
            }
            if (stale != null)
            {
                foreach (var key in stale) _recent.Remove(key);
            }

            // Belt and braces against unbounded growth if entries are never consumed.
            while (_recent.Count > _capacity)
            {
                string? oldestKey = null;
                DateTime oldest = DateTime.MaxValue;
                foreach (var kvp in _recent)
                {
                    if (kvp.Value < oldest) { oldest = kvp.Value; oldestKey = kvp.Key; }
                }
                if (oldestKey == null) break;
                _recent.Remove(oldestKey);
            }
        }

        private static string Fingerprint(ReadOnlySpan<byte> content)
        {
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(content, hash);
            return Convert.ToBase64String(hash);
        }
    }
}
