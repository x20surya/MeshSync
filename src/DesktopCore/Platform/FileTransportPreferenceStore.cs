using CoreLib.Diagnostics;
using CoreLib.Transport;

namespace DesktopCore.Platform;

/// <summary>
/// Keeps the transport preference in a file beside the peer registry.
///
/// <para>The Windows daemon uses <c>HKCU\SOFTWARE\MeshSync</c>. There is no registry here, and the
/// preference is one word, so it is one word in a file - which has the useful property of living
/// in the same directory as <c>device.key</c> and <c>peers.json</c>, so a second device started
/// with <c>--data</c> gets its own preference along with its own identity.</para>
/// </summary>
public sealed class FileTransportPreferenceStore : ITransportPreferenceStore
{
    private readonly string _path;

    public FileTransportPreferenceStore(string dataDirectory) =>
        _path = Path.Combine(dataDirectory, "transport");

    public TransportPreference Load()
    {
        try
        {
            if (!File.Exists(_path)) return TransportPreference.Both;

            return File.ReadAllText(_path).Trim() switch
            {
                nameof(TransportPreference.WiFi) => TransportPreference.WiFi,
                nameof(TransportPreference.Ble) => TransportPreference.Ble,
                _ => TransportPreference.Both
            };
        }
        catch (Exception ex)
        {
            Log.Write("Daemon", "Could not read the transport preference", ex);
            return TransportPreference.Both;
        }
    }

    public void Save(TransportPreference preference)
    {
        // Written through a temporary file so an interrupted write cannot leave a half-line that
        // reads as neither one preference nor the other on the next start.
        string temporary = _path + ".tmp";

        File.WriteAllText(temporary, preference.ToString());
        File.Move(temporary, _path, overwrite: true);
    }
}
