using CoreLib.Transport;
using Microsoft.Win32;

namespace WinDaemon
{
    /// <summary>
    /// Keeps the transport preference in HKCU, where this daemon keeps its other settings.
    ///
    /// <para>What the preference <em>means</em> - which tiers it allows, when it is applied, what
    /// happens on a bad read - is <see cref="TransportSettings"/>, shared with the Linux and Mac
    /// head. Only the shelf differs between platforms, and this is the shelf. Failures are left
    /// to throw: the shared type wraps both calls and falls back to offering both links, which is
    /// the state every device shipped in before there was a preference at all.</para>
    /// </summary>
    public sealed class RegistryTransportPreferenceStore : ITransportPreferenceStore
    {
        private const string SettingsKeyPath = @"SOFTWARE\MeshSync";
        private const string ValueName = "TransportMode";

        public TransportPreference Load()
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);

            return (key?.GetValue(ValueName) as string) switch
            {
                nameof(TransportPreference.WiFi) => TransportPreference.WiFi,
                nameof(TransportPreference.Ble) => TransportPreference.Ble,
                _ => TransportPreference.Both
            };
        }

        public void Save(TransportPreference preference)
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
            key?.SetValue(ValueName, preference.ToString());
        }
    }
}
