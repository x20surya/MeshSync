using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth.Advertisement;
using CoreLib.Diagnostics;
using CoreLib.Transport;
using CoreLib.Transport.Ble;
using CoreLib.Transport.Fabric;

namespace WinDaemon
{
    /// <summary>
    /// This machine's Bluetooth adapter, behind the shared radio interface.
    ///
    /// <para><b>What moved.</b> Scanning used to live inside <c>WindowsBleCentral</c>, so one
    /// object was both the scan and the link and the daemon could therefore hold exactly one of
    /// each. The scan is here now and a link is minted per candidate, which is the whole of what
    /// lets this machine reach more than one peer over the radio.</para>
    ///
    /// <para><b>What did not.</b> Every line that talks to WinRT - the watcher, the GATT session,
    /// the notification sizing, the receipts - is the code that was proven against a phone,
    /// unchanged in substance.</para>
    /// </summary>
    public sealed class WindowsBleRadio : IBleRadio
    {
        private readonly ILinkClock _clock;
        private readonly object _gate = new();
        /// <summary>
        /// Live links, keyed by the address they were opened to.
        ///
        /// <para>Keyed on the address rather than the peer, deliberately: a scan reports addresses
        /// and cannot know which peer one belongs to until a link to it exists, so the address is
        /// the only thing both sides of the question share.</para>
        /// </summary>
        private readonly Dictionary<string, WindowsBleCentral> _links = new(StringComparer.OrdinalIgnoreCase);

        private BluetoothLEAdvertisementWatcher? _watcher;
        private bool _disposed;

        public WindowsBleRadio(ILinkClock? clock = null) => _clock = clock ?? SystemClock.Instance;

        /// <summary>
        /// What this machine can do.
        ///
        /// <para>Set from whether the GATT server actually published, never from the adapter
        /// merely existing. Claiming both halves and then failing to advertise makes the arbiter
        /// answer "you advertise", and the device then neither advertises nor scans.</para>
        /// </summary>
        public BleCapability Capability { get; set; } = BleCapability.Central;

        public bool IsAvailable { get; set; } = true;

        public string Status => !IsAvailable ? "off" : _watcher != null ? "scanning" : "idle";

        public event Action<IPeerRoute>? InboundRoute;

        /// <summary>Called with each new outbound link so the daemon can give it its identity.</summary>
        public Action<WindowsBleCentral>? Prepare { get; set; }

        /// <summary>Hands the fabric a link a peer opened to this machine's advertised service.</summary>
        public void PublishInbound(IPeerRoute route)
        {
            try { InboundRoute?.Invoke(route); }
            catch (Exception ex) { Log.Write("BleRadio", "An InboundRoute handler threw", ex); }
        }

        // ──────────────────────────────── advertising

        /// <summary>
        /// Publishing is owned by <see cref="WindowsBleTransport"/>, which holds the GATT service
        /// provider and advertises it.
        ///
        /// <para>The mesh beacon does not fit alongside a 128-bit service UUID in a Windows GATT
        /// advertisement, and a scanner treats a missing beacon as "unknown, try after anything
        /// that verified" rather than as a refusal - which is exactly why the beacon was made a
        /// ranking and not a gate. A Windows machine is still found by its service UUID, still
        /// authorised by its hello, and simply does not get the fast path.</para>
        /// </summary>
        public Task StartAdvertisingAsync(BleAdvertisement advertisement, CancellationToken cancellationToken = default)
        {
            _ = advertisement;
            return Task.CompletedTask;
        }

        public Task StopAdvertisingAsync() => Task.CompletedTask;

        // ──────────────────────────────── scanning

        /// <summary>
        /// One discovery window, stopped in a <c>finally</c>.
        ///
        /// <para>Active, so the scan response is collected too: a service UUID that does not fit
        /// in the advertisement itself is carried there instead. Stopping between rounds is
        /// load-bearing - an active scan running alongside a live link contends with it for the
        /// same antenna.</para>
        /// </summary>
        public async Task<IReadOnlyList<BleCandidate>> ScanAsync(TimeSpan window, CancellationToken cancellationToken = default)
        {
            if (_disposed || !IsAvailable) return Array.Empty<BleCandidate>();

            var seen = new Dictionary<ulong, BleCandidate>();

            var watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };

            watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(BleProtocol.ServiceUuid);

            void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
            {
                try
                {
                    lock (seen)
                    {
                        seen[args.BluetoothAddress] = new BleCandidate
                        {
                            Address = args.BluetoothAddress.ToString("X"),
                            Name = string.IsNullOrWhiteSpace(args.Advertisement.LocalName) ? null : args.Advertisement.LocalName,
                            Rssi = args.RawSignalStrengthInDBm,
                            Beacon = BeaconOf(args.Advertisement),

                            // An advertisement only arrives while the device is being seen, so
                            // anything reported here is present by definition - unlike BlueZ,
                            // which keeps an object for every address it has ever seen.
                            IsPresent = true,
                        };
                    }
                }
                catch (Exception ex) { Log.Write("BleRadio", "An advertisement could not be read", ex); }
            }

            watcher.Received += OnReceived;
            lock (_gate) _watcher = watcher;

            try
            {
                watcher.Start();

                try { await Task.Delay(window, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            catch (Exception ex)
            {
                Log.Write("BleRadio", "Scanning failed", ex);
            }
            finally
            {
                try
                {
                    watcher.Received -= OnReceived;
                    watcher.Stop();
                }
                catch { }

                lock (_gate) _watcher = null;
            }

            lock (seen) return seen.Values.ToList();
        }

        /// <summary>True when a link to that Bluetooth address is already held.</summary>
        public bool HasLinkTo(string address)
        {
            lock (_gate) return _links.ContainsKey(address);
        }

        /// <summary>Pulls the mesh beacon out of an advertisement, from wherever it was carried.</summary>
        private static byte[]? BeaconOf(BluetoothLEAdvertisement advertisement)
        {
            try
            {
                foreach (var section in advertisement.ManufacturerData)
                {
                    if (section.CompanyId != MeshBeacon.CompanyId) continue;

                    var bytes = section.Data.ToArray();
                    if (bytes.Length == MeshBeacon.Length) return bytes;
                }
            }
            catch
            {
                // An advertisement this build does not understand is simply not one of ours.
            }

            return null;
        }

        // ──────────────────────────────── connecting

        public async Task<IPeerRoute?> ConnectAsync(BleCandidate candidate, CancellationToken cancellationToken = default)
        {
            if (_disposed || !IsAvailable) return null;

            lock (_gate)
            {
                // Belt to the scheduler's braces. It filters candidates it knows are linked, but
                // the filter and the connect are not one atomic step, and a second GATT link to a
                // device this machine is already talking to is not a small thing to leak.
                if (_links.ContainsKey(candidate.Address))
                {
                    Log.Write("BleRadio",
                        $"Already holding a link to {candidate.Name ?? candidate.Address}; not opening a second.");
                    return null;
                }
            }

            var link = new WindowsBleCentral(_clock);
            Prepare?.Invoke(link);

            lock (_gate) _links[candidate.Address] = link;
            link.StateChanged += OnLinkState;

            try
            {
                await link.ConnectAsync(candidate.Address, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write("BleRadio", $"Connecting to {candidate.Name ?? candidate.Address} failed: {ex.Message}");

                // Handed back anyway, so its Backoff state reaches the scheduler and the device is
                // cooled down rather than picked again on the very next round.
                await link.CloseAsync($"connecting failed: {ex.Message}").ConfigureAwait(false);
            }

            return link;
        }

        private void OnLinkState(IPeerRoute route, RouteState from, RouteState to)
        {
            if (to is not (RouteState.Idle or RouteState.Backoff)) return;
            if (route is not WindowsBleCentral link) return;

            link.StateChanged -= OnLinkState;

            lock (_gate)
            {
                foreach (var pair in _links.Where(p => ReferenceEquals(p.Value, link)).ToList())
                {
                    _links.Remove(pair.Key);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            IsAvailable = false;

            try
            {
                BluetoothLEAdvertisementWatcher? watcher;
                lock (_gate) watcher = _watcher;
                watcher?.Stop();
            }
            catch { }

            List<WindowsBleCentral> links;
            lock (_gate)
            {
                links = _links.Values.ToList();
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
