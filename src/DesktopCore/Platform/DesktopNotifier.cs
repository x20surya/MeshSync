using CoreLib.Diagnostics;
using DesktopCore.Clipboard;

namespace DesktopCore.Platform;

/// <summary>
/// Puts a notification in the desktop's own notification centre, the way KDE Connect does.
///
/// <para><b>Why the D-Bus command line rather than a library.</b> Every Linux desktop implements
/// <c>org.freedesktop.Notifications</c>, and <c>gdbus</c> ships with GLib, which is a dependency
/// of every one of them. Shelling out costs a process per notification and buys portability across
/// distributions with nothing bundled and nothing to keep in step with a D-Bus binding. macOS has
/// no such bus, so it falls back to <c>osascript</c>.</para>
///
/// <para>The id the server returns is kept, because that is what makes a mirrored notification
/// dismissable: when the phone says a notification is gone, the matching desktop one has to go
/// too, and closing it needs the id the server gave out.</para>
/// </summary>
public sealed class DesktopNotifier
{
    private const string Dest = "org.freedesktop.Notifications";
    private const string Path = "/org/freedesktop/Notifications";

    private readonly object _gate = new();
    private readonly Dictionary<string, uint> _shown = new(StringComparer.Ordinal);
    private readonly bool _available;

    public DesktopNotifier()
    {
        _available = OperatingSystem.IsMacOS()
            ? Proc.Exists("osascript")
            : Proc.Exists("gdbus") || Proc.Exists("notify-send");

        if (!_available) Log.Write("Notify", "No desktop notification service; notifications stay in the app.");
    }

    public bool IsAvailable => _available;

    /// <summary>
    /// Shows one notification. <paramref name="key"/> is the sender's own identifier for it, so a
    /// later dismissal can find it again; passing the same key replaces rather than stacks.
    /// </summary>
    public async Task ShowAsync(string key, string title, string body, bool urgent = false)
    {
        if (!_available) return;

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                await ShowMacAsync(title, body).ConfigureAwait(false);
                return;
            }

            uint replaces;
            lock (_gate) _shown.TryGetValue(key, out replaces);

            string? result = await Proc.RunAsync("gdbus", [
                "call", "--session", "--dest", Dest, "--object-path", Path,
                "--method", $"{Dest}.Notify",
                "Mesh Sync", replaces.ToString(), "meshsync",
                title, body,
                "[]",
                urgent ? "{'urgency': <byte 2>}" : "{'urgency': <byte 1>}",
                urgent ? "0" : "8000",
            ], null, TimeSpan.FromSeconds(6), CancellationToken.None).ConfigureAwait(false);

            uint id = ParseId(result);
            if (id != 0) lock (_gate) _shown[key] = id;
        }
        catch (Exception ex)
        {
            Log.Write("Notify", "Could not show a notification", ex);
        }
    }

    /// <summary>Takes one down, by the key it was shown under. A key never shown is ignored.</summary>
    public async Task CloseAsync(string key)
    {
        if (!_available || OperatingSystem.IsMacOS()) return;

        uint id;
        lock (_gate)
        {
            if (!_shown.Remove(key, out id)) return;
        }

        try
        {
            await Proc.RunAsync("gdbus", [
                "call", "--session", "--dest", Dest, "--object-path", Path,
                "--method", $"{Dest}.CloseNotification", id.ToString(),
            ], null, TimeSpan.FromSeconds(6), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Write("Notify", "Could not close a notification", ex);
        }
    }

    /// <summary>Drops every mirrored notification this session has shown.</summary>
    public async Task CloseAllAsync()
    {
        List<string> keys;
        lock (_gate) keys = _shown.Keys.ToList();

        foreach (string key in keys) await CloseAsync(key).ConfigureAwait(false);
    }

    private static async Task ShowMacAsync(string title, string body)
    {
        // Quotes are the only thing osascript will choke on here, and doubling them is what
        // AppleScript wants rather than a backslash.
        string script = $"display notification \"{Escape(body)}\" with title \"Mesh Sync\" subtitle \"{Escape(title)}\"";

        await Proc.RunAsync("osascript", ["-e", script], null,
            TimeSpan.FromSeconds(6), CancellationToken.None).ConfigureAwait(false);
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>gdbus prints the reply as a tuple, "(uint32 12,)".</summary>
    private static uint ParseId(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return 0;

        var digits = new string(reply.Where(char.IsDigit).ToArray());
        return uint.TryParse(digits, out uint id) ? id : 0;
    }
}
