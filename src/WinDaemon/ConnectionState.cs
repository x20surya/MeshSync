using System;

namespace WinDaemon
{
    public enum LinkKind { None, WiFi, Ble }

    /// <summary>
    /// One place that knows whether a phone is reachable and over which link.
    ///
    /// The dashboard used to read the TCP transport directly, so once the phone fell back
    /// to Bluetooth the window still said "waiting for a device" while the phone showed
    /// itself connected. Both transports now report here and the UI reads only this.
    /// </summary>
    public static class ConnectionState
    {
        private static readonly object Gate = new();
        private static bool _wifi;
        private static bool _ble;
        private static string? _peerName;

        /// <summary>Raised whenever the effective state changes.</summary>
        public static event Action? Changed;

        public static bool IsConnected
        {
            get { lock (Gate) return _wifi || _ble; }
        }

        /// <summary>Wi-Fi wins when both are up, because it is the link that carries everything.</summary>
        public static LinkKind ActiveLink
        {
            get
            {
                lock (Gate)
                {
                    if (_wifi) return LinkKind.WiFi;
                    return _ble ? LinkKind.Ble : LinkKind.None;
                }
            }
        }

        public static string? PeerName
        {
            get { lock (Gate) return _peerName; }
        }

        public static void SetWiFi(bool connected, string? peerName = null)
        {
            bool changed;
            lock (Gate)
            {
                changed = _wifi != connected;
                _wifi = connected;
                if (connected && !string.IsNullOrEmpty(peerName)) { _peerName = peerName; changed = true; }
                else if (!connected && !_ble) { _peerName = null; }
            }

            if (changed) Raise();
        }

        public static void SetBle(bool connected)
        {
            bool changed;
            lock (Gate)
            {
                changed = _ble != connected;
                _ble = connected;
                // BLE carries no hello frame, so it contributes no name of its own.
                if (!connected && !_wifi) _peerName = null;
            }

            if (changed) Raise();
        }

        private static void Raise()
        {
            try { Changed?.Invoke(); }
            catch { /* a broken listener must not break syncing */ }
        }
    }
}
