namespace DesktopCore.Clipboard;

/// <summary>
/// Reading and writing the desktop clipboard, by whatever route this session actually allows.
///
/// <para>There is no one answer on Linux. X11 sessions have <c>xclip</c> and <c>xsel</c>;
/// Wayland has <c>wl-clipboard</c>; a headless session has neither and the daemon still has to
/// run. So this is an interface with a do-nothing implementation rather than a hard dependency,
/// and the transport half of the daemon works with all three.</para>
/// </summary>
public interface IClipboardBridge
{
    /// <summary>What is doing the work, for the line printed at startup.</summary>
    string Name { get; }

    /// <summary>False when nothing on this machine can reach the clipboard.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// True when this bridge can be told about changes rather than asked repeatedly.
    /// Polling is the fallback, not the intent.
    /// </summary>
    bool SupportsWatching { get; }

    Task<string?> GetTextAsync(CancellationToken cancellationToken);

    Task<bool> SetTextAsync(string text, CancellationToken cancellationToken);
}

/// <summary>What runs when the session has no clipboard tooling at all.</summary>
public sealed class NoClipboard : IClipboardBridge
{
    public string Name => "none";
    public bool IsAvailable => false;
    public bool SupportsWatching => false;
    public Task<string?> GetTextAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    public Task<bool> SetTextAsync(string text, CancellationToken cancellationToken) => Task.FromResult(false);
}
