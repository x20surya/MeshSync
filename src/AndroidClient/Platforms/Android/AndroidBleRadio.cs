using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreLib.Diagnostics;
using CoreLib.Transport;
using CoreLib.Transport.Ble;
using CoreLib.Transport.Fabric;

namespace AndroidClient.Platforms.Android
{
    /// <summary>
    /// This phone's Bluetooth adapter, behind the shared radio interface.
    ///
    /// <para><b>What this replaces on Android specifically.</b> The scan resolved on the first
    /// advertisement it saw and connected to it - so a device from another mesh sitting closer
    /// than your own won every round. There was no cooldown of any kind, so it won the next round
    /// too, and the one after. And the loop that drove it was never gated on whether this phone
    /// should be dialling anybody at all: <c>ShouldDialAnyPeer</c> was never called from here.</para>
    ///
    /// <para>All three of those are now the scheduler's business, and identical on every platform.
    /// What stays here is the scan window, the connect, and an honest answer about what the radio
    /// can do.</para>
    /// </summary>
    public sealed class AndroidBleRadio : IBleRadio
    {
        private readonly ILinkClock _clock;
        private readonly object _gate = new();
        private readonly List<AndroidBleTransport> _links = new();

        private AndroidBlePeripheral? _peripheral;
        private DateTime _lastScanUtc = DateTime.MinValue;
        private bool _scanning;
        private bool _disposed;

        public AndroidBleRadio(ILinkClock? clock = null) =>
            // Qualified: Android.OS has a SystemClock of its own.
            _clock = clock ?? CoreLib.Transport.Fabric.SystemClock.Instance;

        /// <summary>
        /// Android silently throttles an app that starts and stops BLE scans more than about five
        /// times in thirty seconds - the scan simply returns nothing, with no error and no
        /// callback. The scheduler's interval is comfortably outside that, and this is the floor
        /// that holds even if it is signalled repeatedly.
        /// </summary>
        private static readonly TimeSpan ScanFloor = TimeSpan.FromSeconds(12);

        /// <summary>
        /// What this phone's radio can do, probed once and remembered.
        ///
        /// <para>Advertising is a hardware capability on Android and scanning is not:
        /// <c>BluetoothAdapter.BluetoothLeAdvertiser</c> is null on devices without peripheral
        /// support. Set from whether the peripheral half actually <em>started</em>, never from
        /// what the adapter claimed - and declaring `BLUETOOTH_ADVERTISE` is not requesting it,
        /// so a refused runtime grant shows up here as central-only rather than as a phone that
        /// silently never becomes findable.</para>
        /// </summary>
        public BleCapability Capability { get; set; } = BleCapability.Central;

        public bool IsAvailable { get; set; } = true;

        public string Status => !IsAvailable ? "off" : _scanning ? "scanning" : "idle";

        public event Action<IPeerRoute>? InboundRoute;

        /// <summary>Called with each new outbound link so the manager can give it its identity.</summary>
        public Action<AndroidBleTransport>? Prepare { get; set; }

        /// <summary>The peripheral half, once it has started. Null on a phone that cannot advertise.</summary>
        public AndroidBlePeripheral? Peripheral
        {
            get { lock (_gate) return _peripheral; }
            set { lock (_gate) _peripheral = value; }
        }

        /// <summary>Hands the fabric a link a peer opened to this phone's advertised service.</summary>
        public void PublishInbound(IPeerRoute route)
        {
            try { InboundRoute?.Invoke(route); }
            catch (Exception ex) { Log.Write("BleRadio", "An InboundRoute handler threw", ex); }
        }

        // ──────────────────────────────── advertising

        /// <summary>True when a link to that Bluetooth address is already held.</summary>
        public bool HasLinkTo(string address)
        {
            lock (_gate)
            {
                foreach (var link in _links)
                {
                    if (string.Equals(link.PeerFingerprint, address, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Publishing is owned by <see cref="AndroidBlePeripheral"/>, which registers the GATT
        /// service and the advertisement together.
        ///
        /// <para>The beacon reaches the air through <see cref="AndroidBlePeripheral.Beacon"/>,
        /// which Android does let a caller set: <c>AddManufacturerData</c> sits beside the service
        /// UUID inside the 31-byte legacy advertisement.</para>
        /// </summary>
        public Task StartAdvertisingAsync(BleAdvertisement advertisement, CancellationToken cancellationToken = default)
        {
            var peripheral = Peripheral;
            if (peripheral == null) return Task.CompletedTask;

            try { peripheral.Beacon = advertisement.Beacon; }
            catch (Exception ex) { Log.Write("BleRadio", "Could not set the advertised beacon", ex); }

            return Task.CompletedTask;
        }

        public Task StopAdvertisingAsync() => Task.CompletedTask;

        // ──────────────────────────────── scanning

        public async Task<IReadOnlyList<BleCandidate>> ScanAsync(TimeSpan window, CancellationToken cancellationToken = default)
        {
            if (_disposed || !IsAvailable) return Array.Empty<BleCandidate>();

            var since = _clock.UtcNow - _lastScanUtc;
            if (since < ScanFloor)
            {
                var wait = ScanFloor - since;
                Log.Write("BleRadio",
                    $"Holding off the scan for {wait.TotalSeconds:F0}s to stay under Android's scan throttle.");

                try { await Task.Delay(wait, cancellationToken).ConfigureAwait(false); }
                catch (System.OperationCanceledException) { return Array.Empty<BleCandidate>(); }
            }

            _lastScanUtc = _clock.UtcNow;
            _scanning = true;

            AndroidBleDiscovery? discovery = null;
            try
            {
                discovery = new AndroidBleDiscovery();
                return await discovery.ScanAsync(window, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write("BleRadio", "Scanning failed", ex);
                return Array.Empty<BleCandidate>();
            }
            finally
            {
                _scanning = false;
                discovery?.Dispose();
            }
        }

        // ──────────────────────────────── connecting

        public async Task<IPeerRoute?> ConnectAsync(BleCandidate candidate, CancellationToken cancellationToken = default)
        {
            if (_disposed || !IsAvailable) return null;

            var link = new AndroidBleTransport(_clock);
            Prepare?.Invoke(link);

            lock (_gate) _links.Add(link);
            link.StateChanged += OnLinkState;

            try
            {
                await link.ConnectAsync(candidate.Address, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write("BleRadio", $"Connecting to {candidate.Name ?? candidate.Address} failed: {ex.Message}");

                // Handed back anyway, so its Backoff state reaches the scheduler and the device
                // is cooled down rather than picked again on the very next round.
                await link.CloseAsync($"connecting failed: {ex.Message}").ConfigureAwait(false);
            }

            return link;
        }

        private void OnLinkState(IPeerRoute route, RouteState from, RouteState to)
        {
            if (to is not (RouteState.Idle or RouteState.Backoff)) return;
            if (route is not AndroidBleTransport link) return;

            link.StateChanged -= OnLinkState;
            lock (_gate) _links.Remove(link);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            IsAvailable = false;

            List<AndroidBleTransport> links;
            lock (_gate)
            {
                links = _links.ToList();
                _links.Clear();
            }

            foreach (var link in links)
            {
                try { await link.DisposeAsync().ConfigureAwait(false); } catch { }
            }

            InboundRoute = null;
            Prepare = null;
        }
    }
}
