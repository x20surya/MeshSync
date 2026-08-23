using CoreLib.Transport;

namespace DesktopCore;

/// <summary>One notification mirrored from a phone, as the window shows it.</summary>
public sealed class MirroredEntry
{
    public required string Key { get; init; }
    public required string From { get; init; }
    public required string AppName { get; init; }
    public required string Title { get; init; }
    public required string Text { get; init; }
    public required DateTime AtUtc { get; init; }

    /// <summary>
    /// True when the device this came from offered a reply action with it.
    ///
    /// <para>The sender's answer, never this end's guess. A reply box on a notification that
    /// carried no reply action is a message the user believes they sent.</para>
    /// </summary>
    public bool CanReply { get; init; }

    /// <summary>The app's own word for the action - "Reply" on most, not on all.</summary>
    public string ReplyLabel { get; init; } = "";

    public string Age
    {
        get
        {
            var elapsed = DateTime.UtcNow - AtUtc;
            if (elapsed.TotalSeconds < 5) return "just now";
            if (elapsed.TotalMinutes < 1) return $"{(int)elapsed.TotalSeconds}s ago";
            if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes}m ago";
            return $"{(int)elapsed.TotalHours}h ago";
        }
    }
}

/// <summary>
/// The mirrored notifications currently showing.
///
/// <para><b>Memory only, by rule.</b> These are the most private thing the app carries. They are
/// never written to the activity log, never cached to disk, and never put into a log line that
/// carries their contents - only that one arrived. This class exists so the window has something
/// to draw and so a dismissal can find the right one; it dies with the process.</para>
/// </summary>
public sealed class MirroredNotifications
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MirroredEntry> _live = new(StringComparer.Ordinal);

    /// <summary>Raised on any change, so a window can redraw.</summary>
    public event Action? Changed;

    public int Count { get { lock (_gate) return _live.Count; } }

    public IReadOnlyList<MirroredEntry> Snapshot()
    {
        lock (_gate) return _live.Values.OrderByDescending(e => e.AtUtc).ToList();
    }

    public void Add(string peerFingerprint, string from, MirroredNotification notification)
    {
        var entry = new MirroredEntry
        {
            // Namespaced by peer: two phones can produce the same key and one must not close
            // the other's notification.
            Key = $"{peerFingerprint}|{notification.Key}",
            From = from,
            AppName = notification.AppName,
            Title = notification.Title,
            Text = notification.Text,
            AtUtc = DateTime.UtcNow,
            CanReply = notification.CanReply,
            ReplyLabel = notification.ReplyLabel,
        };

        lock (_gate) _live[entry.Key] = entry;
        Changed?.Invoke();
    }

    /// <summary>Removes one and returns it, or null if it was already gone.</summary>
    public MirroredEntry? Remove(string peerFingerprint, string key)
    {
        MirroredEntry? entry;
        lock (_gate)
        {
            if (!_live.Remove($"{peerFingerprint}|{key}", out entry)) return null;
        }

        Changed?.Invoke();
        return entry;
    }

    /// <summary>Removes by the namespaced key, which is what a UI row carries.</summary>
    public MirroredEntry? RemoveByKey(string namespacedKey)
    {
        MirroredEntry? entry;
        lock (_gate)
        {
            if (!_live.Remove(namespacedKey, out entry)) return null;
        }

        Changed?.Invoke();
        return entry;
    }

    public IReadOnlyList<MirroredEntry> Clear()
    {
        List<MirroredEntry> dropped;
        lock (_gate)
        {
            dropped = _live.Values.ToList();
            _live.Clear();
        }

        if (dropped.Count > 0) Changed?.Invoke();
        return dropped;
    }

    /// <summary>Splits a namespaced key back into the peer and the phone's own key.</summary>
    public static (string Fingerprint, string Key) Split(string namespacedKey)
    {
        int bar = namespacedKey.IndexOf('|');
        return bar <= 0
            ? ("", namespacedKey)
            : (namespacedKey[..bar], namespacedKey[(bar + 1)..]);
    }
}
