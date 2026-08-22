namespace DesktopCore.Clipboard;

/// <summary>
/// Picks how this session reaches the clipboard.
///
/// <para>Order matters. Speaking <c>ext-data-control</c> to the compositor needs nothing
/// installed and is told about changes rather than polling for them, so it is tried first. A
/// command-line helper is the fallback for X11 sessions and for compositors that do not offer the
/// protocol, and doing nothing at all is the fallback for a machine with neither - the daemon
/// still pairs, holds links and sends.</para>
/// </summary>
public static class ClipboardFactory
{
    public static IClipboardBridge Detect() =>
        (IClipboardBridge?)WaylandClipboard.TryCreate() ?? CommandLineClipboard.Detect();
}
