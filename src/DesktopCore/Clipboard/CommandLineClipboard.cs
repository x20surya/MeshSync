using CoreLib.Diagnostics;

namespace DesktopCore.Clipboard;

/// <summary>
/// The clipboard by way of <c>wl-clipboard</c> or <c>xclip</c>/<c>xsel</c>.
///
/// <para><b>Why shelling out rather than binding a library.</b> Reading the clipboard is the easy
/// half on every Linux session. Watching it in the background is the hard half, and on Wayland it
/// needs the <c>ext-data-control</c> protocol, which a client can only speak over a native Wayland
/// connection. That is a component of its own and it is not this one. Until it exists these
/// helpers do the same job through a process boundary, which is exactly what they are for.</para>
///
/// <para>Nothing here assumes a helper is installed. <see cref="Detect"/> returns
/// <see cref="NoClipboard"/> when none is, and the daemon says so once and carries on.</para>
/// </summary>
public sealed class CommandLineClipboard : IClipboardBridge
{
    private readonly string _readCommand;
    private readonly string[] _readArgs;
    private readonly string _writeCommand;
    private readonly string[] _writeArgs;

    public string Name { get; }
    public bool IsAvailable => true;
    public bool SupportsWatching { get; }

    private CommandLineClipboard(string name, bool supportsWatching,
                                 string readCommand, string[] readArgs,
                                 string writeCommand, string[] writeArgs)
    {
        Name = name;
        SupportsWatching = supportsWatching;
        _readCommand = readCommand;
        _readArgs = readArgs;
        _writeCommand = writeCommand;
        _writeArgs = writeArgs;
    }

    /// <summary>
    /// Picks the best helper this session has.
    ///
    /// Wayland is checked first because an X11 helper on a Wayland session works only through
    /// XWayland's bridge, which is a translation rather than the real clipboard.
    ///
    /// <para>Only wl-clipboard reports <see cref="SupportsWatching"/>, because only it can be
    /// told when the selection changes. Everything else is polled, macOS included - NSPasteboard
    /// has no change notification either, only a change counter to compare against.</para>
    /// </summary>
    public static IClipboardBridge Detect()
    {
        // macOS ships its own, always, so there is nothing to detect and nothing to install.
        if (OperatingSystem.IsMacOS())
        {
            return new CommandLineClipboard(
                "pbcopy/pbpaste", supportsWatching: false,
                "pbpaste", [],
                "pbcopy", []);
        }

        bool wayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

        if (wayland && Proc.Exists("wl-paste") && Proc.Exists("wl-copy"))
        {
            return new CommandLineClipboard(
                "wl-clipboard", supportsWatching: true,
                "wl-paste", ["--no-newline", "--type", "text/plain"],
                "wl-copy", ["--type", "text/plain"]);
        }

        if (Proc.Exists("xclip"))
        {
            return new CommandLineClipboard(
                "xclip", supportsWatching: false,
                "xclip", ["-selection", "clipboard", "-o"],
                "xclip", ["-selection", "clipboard", "-i"]);
        }

        if (Proc.Exists("xsel"))
        {
            return new CommandLineClipboard(
                "xsel", supportsWatching: false,
                "xsel", ["--clipboard", "--output"],
                "xsel", ["--clipboard", "--input"]);
        }

        // Worth naming what is missing rather than only that something is: the fix is one
        // package away and the message is the only place that will say so.
        Log.Write("Clipboard", wayland
            ? "No wl-clipboard on this Wayland session; clipboard sync is off. Install wl-clipboard to turn it on."
            : "No xclip or xsel found; clipboard sync is off.");

        return new NoClipboard();
    }

    public async Task<string?> GetTextAsync(CancellationToken cancellationToken)
    {
        string? text = await Proc.RunAsync(_readCommand, _readArgs, null,
            TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

        // An empty clipboard and a failed read both come back empty. Neither is worth sending.
        return string.IsNullOrEmpty(text) ? null : text;
    }

    public async Task<bool> SetTextAsync(string text, CancellationToken cancellationToken)
    {
        // wl-copy and xclip both fork and stay resident to own the selection, so the wait here
        // is on the fork returning, not on the clipboard being given up.
        string? result = await Proc.RunAsync(_writeCommand, _writeArgs, text,
            TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

        return result != null;
    }
}
