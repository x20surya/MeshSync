using System;
using CoreLib.Diagnostics;

namespace CoreLib.Transport
{
    /// <summary>Which links the device is allowed to offer.</summary>
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
    /// Where a transport preference is kept between runs.
    ///
    /// <para>The preference itself is the same idea on every platform; only the shelf differs.
    /// Windows has the registry, Linux and macOS have a file next to the peer registry. Keeping
    /// the storage behind this interface is what lets the rule about what the preference
    /// <em>means</em> live in one place instead of being decided twice.</para>
    /// </summary>
    public interface ITransportPreferenceStore
    {
        TransportPreference Load();

        void Save(TransportPreference preference);
    }

    /// <summary>
    /// Which links this device is allowed to offer. Persisted, because a preference that
    /// resets on restart is worse than not having one.
    /// </summary>
    public sealed class TransportSettings
    {
        private readonly ITransportPreferenceStore? _store;
        private readonly object _gate = new();
        private TransportPreference _current;

        /// <summary>Raised when the preference changes, so a daemon can start or stop a transport.</summary>
        public event Action<TransportPreference>? Changed;

        /// <summary>
        /// Loads the stored preference, falling back to <see cref="TransportPreference.Both"/>.
        ///
        /// A device with no store, or one whose store cannot be read, offers both links. That is
        /// the state every device shipped in before there was a preference at all, so it is the
        /// one that cannot surprise anybody.
        /// </summary>
        public TransportSettings(ITransportPreferenceStore? store = null)
        {
            _store = store;

            try { _current = store?.Load() ?? TransportPreference.Both; }
            catch (Exception ex)
            {
                Log.Write("Daemon", "Reading the transport preference failed", ex);
                _current = TransportPreference.Both;
            }
        }

        public TransportPreference Current
        {
            get { lock (_gate) return _current; }
        }

        public bool AllowsWiFi => Current != TransportPreference.Ble;

        public bool AllowsBle => Current != TransportPreference.WiFi;

        public void Set(TransportPreference preference)
        {
            lock (_gate)
            {
                if (preference == _current) return;
                _current = preference;
            }

            try { _store?.Save(preference); }
            catch (Exception ex) { Log.Write("Daemon", "Saving the transport preference failed", ex); }

            Log.Write("Daemon", $"Transport preference set to {preference}.");

            try { Changed?.Invoke(preference); }
            catch (Exception ex) { Log.Write("Daemon", "Applying the transport preference failed", ex); }
        }
    }
}
