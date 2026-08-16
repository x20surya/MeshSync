using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreLib
{
    public enum SyncDirection { Sent, Received }

    public enum SyncItemKind { Text, Image }

    public sealed class SyncActivityEntry
    {
        public SyncDirection Direction { get; init; }
        public SyncItemKind Kind { get; init; }

        /// <summary>Short, single-line preview for text items. Empty for images.</summary>
        public string Preview { get; init; } = string.Empty;

        public int SizeBytes { get; init; }
        public DateTime AtUtc { get; init; } = DateTime.UtcNow;

        /// <summary>"2s", "4m", "3h" - compact enough for a dashboard row.</summary>
        public string RelativeAge
        {
            get
            {
                var elapsed = DateTime.UtcNow - AtUtc;
                if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
                if (elapsed.TotalSeconds < 5) return "just now";
                if (elapsed.TotalMinutes < 1) return $"{(int)elapsed.TotalSeconds}s ago";
                if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes}m ago";
                if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours}h ago";
                return $"{(int)elapsed.TotalDays}d ago";
            }
        }

        public string SizeLabel => SizeBytes switch
        {
            < 1024 => $"{SizeBytes} B",
            < 1024 * 1024 => $"{SizeBytes / 1024.0:F1} KB",
            _ => $"{SizeBytes / (1024.0 * 1024.0):F1} MB"
        };

        public string Title => Kind == SyncItemKind.Image
            ? (Direction == SyncDirection.Sent ? "Image sent" : "Image received")
            : Preview;
    }

    /// <summary>
    /// Bounded in-memory record of what has synced this session.
    ///
    /// Deliberately not persisted: the project rule is that clipboard traffic is ephemeral
    /// and never stored, so this exists only to give the dashboards something truthful to
    /// show and is discarded when the process exits.
    /// </summary>
    public sealed class SyncActivityLog
    {
        public const int MaxPreviewChars = 48;

        private readonly object _gate = new();
        private readonly LinkedList<SyncActivityEntry> _entries = new();
        private readonly int _capacity;

        public SyncActivityLog(int capacity = 20) => _capacity = Math.Max(1, capacity);

        public event EventHandler? Changed;

        public int SentCount { get; private set; }
        public int ReceivedCount { get; private set; }

        public DateTime? LastActivityUtc
        {
            get { lock (_gate) return _entries.First?.Value.AtUtc; }
        }

        public void Record(SyncDirection direction, SyncItemKind kind, int sizeBytes, string? textContent = null)
        {
            var entry = new SyncActivityEntry
            {
                Direction = direction,
                Kind = kind,
                SizeBytes = sizeBytes,
                Preview = kind == SyncItemKind.Text ? MakePreview(textContent) : string.Empty,
                AtUtc = DateTime.UtcNow
            };

            lock (_gate)
            {
                _entries.AddFirst(entry);
                while (_entries.Count > _capacity) _entries.RemoveLast();

                if (direction == SyncDirection.Sent) SentCount++;
                else ReceivedCount++;
            }

            try { Changed?.Invoke(this, EventArgs.Empty); }
            catch { /* a broken listener must not break syncing */ }
        }

        public IReadOnlyList<SyncActivityEntry> Snapshot()
        {
            lock (_gate) return _entries.ToList();
        }

        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
                SentCount = 0;
                ReceivedCount = 0;
            }
            try { Changed?.Invoke(this, EventArgs.Empty); } catch { }
        }

        /// <summary>Collapses whitespace and truncates, so one pasted paragraph cannot blow up a row.</summary>
        private static string MakePreview(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            Span<char> buffer = text.Length <= 512 ? stackalloc char[text.Length] : new char[512];
            int written = 0;
            bool lastWasSpace = false;

            foreach (char c in text)
            {
                if (written == buffer.Length) break;
                char normalised = char.IsWhiteSpace(c) ? ' ' : c;
                if (normalised == ' ')
                {
                    if (lastWasSpace || written == 0) continue;
                    lastWasSpace = true;
                }
                else
                {
                    lastWasSpace = false;
                }
                buffer[written++] = normalised;
            }

            var collapsed = new string(buffer.Slice(0, written)).TrimEnd();
            return collapsed.Length <= MaxPreviewChars
                ? collapsed
                : collapsed.Substring(0, MaxPreviewChars).TrimEnd() + "…";
        }
    }
}
