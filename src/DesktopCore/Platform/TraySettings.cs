using CoreLib.Diagnostics;

namespace DesktopCore.Platform;

/// <summary>
/// Whether Mesh Sync draws its own tray icon.
///
/// <para><b>Why this exists at all.</b> The Plasma widget can sit in the system tray, and Mesh
/// Sync's own StatusNotifierItem sits there too - so a person who adds the widget gets two
/// identical icons side by side. That is the thing that makes KDE Connect ship its plasmoid and
/// its indicator as separate installs. Here they are one product, so the widget can simply offer
/// to turn the other one off.</para>
///
/// <para>A file rather than a settings framework, for the same reason the transport preference is
/// one: this head has no registry and the setting is a single bit that has to survive a restart.
/// Its absence means visible, so the default needs no file and a fresh install has none.</para>
/// </summary>
public static class TraySettings
{
    private const string HiddenMarker = "tray.hidden";
    private const string ContentMarker = "notifications.content";

    /// <summary>
    /// Whether mirrored notifications put their sender and text on the session bus.
    ///
    /// <para><b>Off by default, and that default is the whole point.</b> Everything on the
    /// session bus is readable by every program running as this user, and a mirrored
    /// notification is the most private thing Mesh Sync carries. With this off the panel gets a
    /// key, an app name, a device and a time - enough to badge "3 from S21 FE" and draw a reply
    /// box, and nothing to read.</para>
    ///
    /// <para>On, the panel can group by conversation and show a preview, which is what a phone's
    /// own shade does. That is a reasonable thing to want on your own laptop and an unreasonable
    /// default to impose, so it is a file the owner turns on.</para>
    /// </summary>
    public static bool ShowsContent(string dataDirectory)
    {
        try { return File.Exists(Path.Combine(dataDirectory, ContentMarker)); }
        catch { return false; }
    }

    public static void SetShowsContent(string dataDirectory, bool show)
    {
        string marker = Path.Combine(dataDirectory, ContentMarker);

        try
        {
            if (show)
            {
                File.WriteAllText(marker,
                    "Mirrored notifications put their sender and text on the session bus, where\n" +
                    "any program running as you can read them. Delete this file to stop that.\n");
            }
            else if (File.Exists(marker))
            {
                File.Delete(marker);
            }

            Log.Write("Notify", show
                ? "Notification senders and text are now on the bus, for the panel to show."
                : "Notification senders and text are off the bus again.");
        }
        catch (Exception ex)
        {
            Log.Write("Notify", "Could not change the notification detail setting", ex);
        }
    }

    public static bool IsVisible(string dataDirectory)
    {
        try { return !File.Exists(Path.Combine(dataDirectory, HiddenMarker)); }
        catch { return true; }
    }

    public static void SetVisible(string dataDirectory, bool visible)
    {
        string marker = Path.Combine(dataDirectory, HiddenMarker);

        try
        {
            if (visible)
            {
                if (File.Exists(marker)) File.Delete(marker);
            }
            else
            {
                File.WriteAllText(marker,
                    "Mesh Sync's own tray icon is turned off. Delete this file to bring it back.\n");
            }

            Log.Write("Tray", visible ? "The tray icon is on." : "The tray icon is off.");
        }
        catch (Exception ex)
        {
            Log.Write("Tray", "Could not change the tray icon setting", ex);
        }
    }
}
