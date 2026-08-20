using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreLib
{
    public enum SyncDirection { Sent, Received }

    public enum SyncItemKind { Text, Image, File }

    public sealed class SyncActivityEntry
    {
        public SyncDirection Direction { get; init; }
        public SyncItemKind Kind { get; init; }

        /// <summary>Short, single-line preview for text items. Empty for images.</summary>
        public string Preview { get; init; } = string.Empty;

        /// <summary>
        /// Long rather than int since files joined the clipboard here. A clipboard item cannot
        /// reach two gigabytes; a video can, and an overflowed size would report a negative one.
        /// </summary>
        public long SizeBytes { get; init; }
        public DateTime AtUtc { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Where a received file ended up, in whatever form the platform can reopen it by.
        ///
        /// <para>Deliberately an opaque string rather than a path: on Android a file saved
        /// through MediaStore has no path the app is allowed to know, only a content URI handed
        /// back at the time of writing. Losing that URI means the file is on the device and
        /// unreachable from the app that put it there, which is why it is kept here rather than
        /// recomputed later.</para>
        ///
        /// <para>Empty for anything that is not a received file.</para>
        /// </summary>
        public string Location { get; init; } = string.Empty;

        /// <summary>Whether there is somewhere to go when this row is tapped.</summary>
        public bool CanOpen => Location.Length > 0;

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
            < 1024 * 1024 * 1024 => $"{SizeBytes / (1024.0 * 1024.0):F1} MB",
            _ => $"{SizeBytes / (1024.0 * 1024.0 * 1024.0):F1} GB"
        };

        /// <summary>
        /// What the row says. A file carries its own name in <see cref="Preview"/>, which is far
        /// more use than "File received" - it is the thing the user goes looking for.
        /// </summary>
        public string Title => Kind switch
        {
            SyncItemKind.Image => Direction == SyncDirection.Sent ? "Image sent" : "Image received",
            SyncItemKind.File => Preview.Length > 0 ? Preview : (Direction == SyncDirection.Sent ? "File sent" : "File received"),
            _ => Preview
        };
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

        public void Record(SyncDirection direction, SyncItemKind kind, long sizeBytes,
                           string? textContent = null, string? location = null)
        {
            var entry = new SyncActivityEntry
            {
                Direction = direction,
                Kind = kind,
                SizeBytes = sizeBytes,
                Location = location ?? string.Empty,
                // A file's name is worth showing as-is: it is what the user goes looking for.
                // Text is trimmed to a preview; an image has nothing to say.
                Preview = kind switch
                {
                    SyncItemKind.Text => MakePreview(textContent),
                    SyncItemKind.File => MakePreview(textContent),
                    _ => string.Empty
                },
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
