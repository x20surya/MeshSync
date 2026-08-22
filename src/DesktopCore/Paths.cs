using CoreLib.Diagnostics;

namespace DesktopCore;

/// <summary>
/// Where this device keeps its identity, its peers and its log.
///
/// <para>Windows puts all three in <c>%LOCALAPPDATA%\MeshSync</c>. The Linux equivalent is
/// <c>$XDG_DATA_HOME/MeshSync</c>, falling back to <c>~/.local/share/MeshSync</c> when the
/// variable is unset - which it usually is, because most desktops rely on the documented default
/// rather than exporting it.</para>
///
/// <para><b>Why this is an instance rather than static state.</b> Two devices have to be able to
/// run on one machine, each with its own identity, or the mesh cannot be exercised without a
/// second physical device. HANDOFF.md records the same accommodation on the transport side, where
/// <c>MeshLinks</c> takes a <c>host:port</c> for exactly this reason.</para>
///
/// <para>The directory is created owner-only. <c>DeviceIdentity</c> tightens <c>device.key</c> to
/// 0600 itself, but it cannot fix a directory that was created world-readable before it got
/// there.</para>
/// </summary>
public sealed class Paths
{
    public string DataDirectory { get; }

    public string LogFile => Path.Combine(DataDirectory, "daemon.log");

    /// <summary>Where an image that arrives is written, since there is no UI to show it in.</summary>
    public string IncomingDirectory => Path.Combine(DataDirectory, "incoming");

    public Paths(string? overrideDirectory = null)
    {
        DataDirectory = Prepare(overrideDirectory ?? DefaultDirectory());
    }

    private static string DefaultDirectory()
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

        string baseDir = !string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

        return Path.Combine(baseDir, "MeshSync");
    }

    private static string Prepare(string dir)
    {
        try
        {
            dir = Path.GetFullPath(dir);
            Directory.CreateDirectory(dir);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(dir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch (Exception ex)
        {
            // Logged rather than fatal, for the same reason DeviceIdentity does it: syncing
            // still works for this run, it just may not survive a restart.
            Log.Write("Paths", $"Could not prepare {dir}", ex);
        }

        return dir;
    }
}
