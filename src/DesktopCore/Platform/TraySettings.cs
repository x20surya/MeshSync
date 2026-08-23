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
