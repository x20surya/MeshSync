using System;

namespace CoreLib.Transport
{
    /// <summary>Which link is carrying a peer, when one is.</summary>
    public enum LinkKind
    {
        None,

        /// <summary>A TCP socket. Carries anything, including images and files.</summary>
        WiFi,

        /// <summary>A GATT link. Carries text, and is what exists when there is no network.</summary>
        Ble
    }

    /// <summary>
    /// One place that knows whether a peer is reachable and over which link.
    ///
    /// <para><b>Why this is a type rather than a question each screen answers.</b> The Windows
    /// dashboard used to read the TCP transport directly, so once the phone fell back to
    /// Bluetooth the window still said "waiting for a device" while the phone showed itself
    /// connected. Both transports report here and every UI reads only this, so there is one
    /// answer instead of one per caller.</para>
    ///
    /// <para><b>Why it is instance rather than static.</b> It began as a static on Windows, where
    /// one process is one device and that is safe. It is not safe anywhere else: the Linux head
    /// can construct more than one <c>Daemon</c> in a process, which is how the mesh is exercised
    /// without a second machine, and two devices sharing one link state would each report the
    /// other's connections as their own.</para>
    /// </summary>
    public sealed class LinkState
    {
        private readonly object _gate = new();
        private bool _wifi;
        private bool _ble;
        private string? _peerName;

        /// <summary>Raised whenever the effective state changes.</summary>
        public event Action? Changed;

        public bool IsConnected
        {
            get { lock (_gate) return _wifi || _ble; }
        }

        /// <summary>True when a socket is up, whatever the radio is doing.</summary>
        public bool IsWiFiConnected
        {
            get { lock (_gate) return _wifi; }
        }

        /// <summary>True when a radio link is up, whatever the network is doing.</summary>
        public bool IsBleConnected
        {
            get { lock (_gate) return _ble; }
        }

        /// <summary>Wi-Fi wins when both are up, because it is the link that carries everything.</summary>
        public LinkKind ActiveLink
        {
            get
            {
                lock (_gate)
                {
                    if (_wifi) return LinkKind.WiFi;
                    return _ble ? LinkKind.Ble : LinkKind.None;
                }
            }
        }

        public string? PeerName
        {
            get { lock (_gate) return _peerName; }
        }

        public void SetWiFi(bool connected, string? peerName = null)
        {
            bool changed;
            lock (_gate)
            {
                changed = _wifi != connected;
                _wifi = connected;

                if (connected && !string.IsNullOrEmpty(peerName) && _peerName != peerName)
                {
                    _peerName = peerName;
                    changed = true;
                }
                else if (!connected && !_ble)
                {
                    changed |= _peerName != null;
                    _peerName = null;
                }
            }

            if (changed) Raise();
        }

        /// <summary>
        /// Records the Bluetooth link, and the peer's name once it has announced one.
        ///
        /// Bluetooth used to carry no name at all, so a device paired only over Bluetooth had
        /// nothing to be called and every label fell back to "your devices". Its hello carries
        /// one now, which matters most in exactly the case Bluetooth exists for: no network.
        /// </summary>
        public void SetBle(bool connected, string? peerName = null)
        {
            bool changed;
            lock (_gate)
            {
                changed = _ble != connected;
                _ble = connected;

                if (connected && !string.IsNullOrEmpty(peerName) && _peerName != peerName)
                {
                    _peerName = peerName;
                    changed = true;
                }
                else if (!connected && !_wifi)
                {
                    changed |= _peerName != null;
                    _peerName = null;
                }
            }

            if (changed) Raise();
        }

        private void Raise()
        {
            try { Changed?.Invoke(); }
            catch { /* a broken listener must not break syncing */ }
        }
    }
}
