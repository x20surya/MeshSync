using System.Text.Json;
using CoreLib.Diagnostics;

namespace DesktopCore.Platform;

/// <summary>
/// Puts the Plasma widget where Plasma looks for it, once, on a desktop that has one.
///
/// <para><b>Why the app installs it rather than the package.</b> A plasmoid is a directory Plasma
/// reads out of a fixed location, and an AppImage cannot put anything there - it is one file that
/// mounts itself. Leaving it to the <c>.deb</c> alone would mean the widget existed for one of
/// the three ways Mesh Sync is distributed, which is the same as it not existing.</para>
///
/// <para><b>What stops it being rude.</b> It only runs on a Plasma session, it only writes when
/// the bundled version is newer than what is there, and a marker file in the data directory turns
/// it off for good. Nothing is added to a panel: the widget appears in <em>Add Widgets</em>, where
/// a person chooses it.</para>
/// </summary>
public static class WidgetInstaller
{
    private const string PackageId = "dev.meshsync.desktop";

    /// <summary>Drop this file next to the identity and the widget is never written again.</summary>
    private const string OptOutFile = "no-widget";

    /// <summary>
    /// Installs or upgrades the widget if this is a Plasma session and it is out of date.
    ///
    /// Never throws and never blocks anything: a widget that could not be written is a widget
    /// the user adds by hand, not a reason for the app to fail to start.
    /// </summary>
    public static void EnsureInstalled(string dataDirectory)
    {
        try
        {
            if (!OperatingSystem.IsLinux()) return;
            if (!IsPlasma()) return;

            if (File.Exists(Path.Combine(dataDirectory, OptOutFile))) return;

            string? source = FindBundled();
            if (source == null) return;

            string target = Path.Combine(
                Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
                "plasma", "plasmoids", PackageId);

            string bundled = VersionOf(Path.Combine(source, "metadata.json"));
            string installed = VersionOf(Path.Combine(target, "metadata.json"));

            // Equal is not newer. Rewriting an identical directory on every launch would churn
            // the file times and make Plasma reload the widget for no reason.
            if (installed.Length > 0 && Compare(bundled, installed) <= 0) return;

            CopyTree(source, target);

            Log.Write("Widget", installed.Length == 0
                ? $"Installed the Plasma widget ({bundled}). Add it from Add Widgets."
                : $"Upgraded the Plasma widget from {installed} to {bundled}.");
        }
        catch (Exception ex)
        {
            Log.Write("Widget", "Could not install the Plasma widget", ex);
        }
    }

    private static bool IsPlasma()
    {
        string desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";
        return desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Where the widget shipped, whichever way Mesh Sync arrived.
    ///
    /// The published binary sits in <c>usr/bin</c> inside an AppImage and in <c>/opt/meshsync</c>
    /// from the package, so the widget is looked for relative to the binary before the shared
    /// locations - a tarball unpacked anywhere then works with no configuration.
    /// </summary>
    private static string? FindBundled()
    {
        string? binary = Path.GetDirectoryName(Environment.ProcessPath ?? "");

        var candidates = new List<string>();

        if (!string.IsNullOrEmpty(binary))
        {
            candidates.Add(Path.Combine(binary, "plasma", PackageId));
            candidates.Add(Path.Combine(binary, "..", "share", "plasma", "plasmoids", PackageId));

            // Running out of the build tree, which is where this gets tested.
            candidates.Add(Path.Combine(binary, "..", "..", "..", "..", "..", "plasma", PackageId));
        }

        candidates.Add(Path.Combine("/usr", "share", "plasma", "plasmoids", PackageId));

        foreach (string candidate in candidates)
        {
            string full = Path.GetFullPath(candidate);
            if (File.Exists(Path.Combine(full, "metadata.json"))) return full;
        }

        return null;
    }

    private static string VersionOf(string metadata)
    {
        try
        {
            if (!File.Exists(metadata)) return "";

            using var document = JsonDocument.Parse(File.ReadAllText(metadata));

            return document.RootElement.TryGetProperty("KPlugin", out var plugin) &&
                   plugin.TryGetProperty("Version", out var version)
                ? version.GetString() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Compares dotted versions numerically, so 0.10.0 is newer than 0.9.0.</summary>
    private static int Compare(string left, string right)
    {
        string[] a = left.Split('.'), b = right.Split('.');

        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length && int.TryParse(a[i], out int parsedA) ? parsedA : 0;
            int y = i < b.Length && int.TryParse(b[i], out int parsedB) ? parsedB : 0;

            if (x != y) return x.CompareTo(y);
        }

        return 0;
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(directory.Replace(source, target, StringComparison.Ordinal));

        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, target, StringComparison.Ordinal), overwrite: true);
    }
}
