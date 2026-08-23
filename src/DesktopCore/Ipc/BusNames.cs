namespace DesktopCore.Ipc;

/// <summary>
/// Every name and path on the Mesh Sync bus surface, in one place.
///
/// <para><b>Why a fingerprint cannot be a path element as it stands.</b> A D-Bus object path
/// element may contain only <c>[A-Za-z0-9_]</c>, and a fingerprint is written
/// <c>AC83-492B-684F-4263</c>. The hyphens have to become underscores on the way out and back,
/// and doing that in more than one place is how a device ends up unreachable at a path that
/// looks right. Everything that needs the mapping comes here for it.</para>
/// </summary>
public static class BusNames
{
    /// <summary>The well-known name this device owns while it is running.</summary>
    public const string Service = "dev.meshsync.Daemon";

    public const string Root = "/dev/meshsync/Daemon";
    public const string DevicesRoot = Root + "/devices";
    public const string PendingRoot = Root + "/pending";

    public const string DevicesPrefix = DevicesRoot + "/";
    public const string PendingPrefix = PendingRoot + "/";

    public const string DaemonInterface = "dev.meshsync.Daemon1";
    public const string DeviceInterface = "dev.meshsync.Device1";
    public const string PairingInterface = "dev.meshsync.Pairing1";

    public const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    public const string ObjectManagerInterface = "org.freedesktop.DBus.ObjectManager";

    /// <summary>A fingerprint as a path element.</summary>
    public static string ToElement(string fingerprint) => fingerprint.Replace('-', '_');

    /// <summary>A path element back to the fingerprint it names.</summary>
    public static string FromElement(string element) => element.Replace('_', '-');

    public static string DevicePath(string fingerprint) => DevicesPrefix + ToElement(fingerprint);

    public static string PendingPath(string fingerprint) => PendingPrefix + ToElement(fingerprint);

    /// <summary>
    /// The fingerprint a path names, or null when the path is not a direct child of the prefix.
    ///
    /// Rejects grandchildren deliberately: the tree is two levels deep by design, and answering
    /// for a deeper path would export an object nothing here knows how to serve.
    /// </summary>
    public static string? FingerprintIn(string path, string prefix)
    {
        if (path.Length <= prefix.Length || !path.StartsWith(prefix, StringComparison.Ordinal)) return null;

        string element = path[prefix.Length..];
        return element.Contains('/') ? null : FromElement(element);
    }
}
