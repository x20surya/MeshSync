using CoreLib.Diagnostics;

namespace DesktopCore.Platform;

/// <summary>
/// Whether Mesh Sync starts with the session.
///
/// <para>The Windows daemon writes a Run key and turns this on by itself the first time. The
/// equivalent on Linux is an XDG autostart entry, which every desktop environment reads from the
/// same place, and on macOS a LaunchAgent. Both are a file, so this is file handling rather than
/// an API - which is also why it works the same whether the app was installed from a package or
/// is being run out of a build directory.</para>
/// </summary>
public static class Autostart
{
    private const string FileName = "meshsync.desktop";
    private const string AgentName = "dev.meshsync.desktop.plist";

    private static string LinuxPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart", FileName);

    private static string MacPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", AgentName);

    private static string Target => OperatingSystem.IsMacOS() ? MacPath : LinuxPath;

    public static bool IsEnabled => File.Exists(Target);

    /// <summary>The command the entry runs. The published binary, or the one being debugged.</summary>
    private static string ExecutablePath =>
        Environment.ProcessPath ?? System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "meshsync";

    public static bool Set(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                if (File.Exists(Target)) File.Delete(Target);
                Log.Write("Autostart", "Mesh Sync will not start with the session.");
                return true;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Target)!);

            File.WriteAllText(Target, OperatingSystem.IsMacOS() ? MacAgent() : LinuxEntry());

            Log.Write("Autostart", "Mesh Sync will start with the session.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Autostart", "Could not change the autostart entry", ex);
            return false;
        }
    }

    private static string LinuxEntry() => $"""
        [Desktop Entry]
        Type=Application
        Name=Mesh Sync
        Comment=Local-first universal clipboard for your own devices
        Exec={ExecutablePath}
        Icon=meshsync
        Terminal=false
        Categories=Utility;Network;
        X-GNOME-Autostart-enabled=true

        """;

    private static string MacAgent() => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key><string>dev.meshsync.desktop</string>
            <key>ProgramArguments</key><array><string>{ExecutablePath}</string></array>
            <key>RunAtLoad</key><true/>
        </dict>
        </plist>

        """;
}
