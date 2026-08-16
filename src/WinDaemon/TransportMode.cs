using System;
using CoreLib.Diagnostics;
using Microsoft.Win32;

namespace WinDaemon
{
    public enum TransportPreference
    {
        /// <summary>Wi-Fi for everything, Bluetooth as the fallback when there is no network.</summary>
        Both,

        /// <summary>Wi-Fi only. Nothing syncs without a network, but the radio stays off.</summary>
        WiFi,

        /// <summary>Bluetooth only. Text syncs with no network at all; images will not send.</summary>
        Ble
    }

    /// <summary>
    /// Which links the daemon is allowed to offer. Persisted, because a preference that
    /// resets on restart is worse than not having one.
    /// </summary>
    public static class TransportSettings
    {
        private const string SettingsKeyPath = @"SOFTWARE\MeshSync";
        private const string ValueName = "TransportMode";

        /// <summary>Raised when the preference changes, so the daemon can start or stop a transport.</summary>
        public static event Action<TransportPreference>? Changed;

        public static TransportPreference Current { get; private set; } = Load();

        public static void Set(TransportPreference preference)
        {
            if (preference == Current) return;

            Current = preference;
            Save(preference);
            Log.Write("Daemon", $"Transport preference set to {preference}.");

            try { Changed?.Invoke(preference); }
            catch (Exception ex) { Log.Write("Daemon", "Applying the transport preference failed", ex); }
        }

        public static bool AllowsWiFi => Current != TransportPreference.Ble;

        public static bool AllowsBle => Current != TransportPreference.WiFi;

        private static TransportPreference Load()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
                return (key?.GetValue(ValueName) as string) switch
                {
                    nameof(TransportPreference.WiFi) => TransportPreference.WiFi,
                    nameof(TransportPreference.Ble) => TransportPreference.Ble,
                    _ => TransportPreference.Both
                };
            }
            catch
            {
                return TransportPreference.Both;
            }
        }

        private static void Save(TransportPreference preference)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
                key?.SetValue(ValueName, preference.ToString());
            }
            catch (Exception ex)
            {
                Log.Write("Daemon", "Saving the transport preference failed", ex);
            }
        }
    }
}
