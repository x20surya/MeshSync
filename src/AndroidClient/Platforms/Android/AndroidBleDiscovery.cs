using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.OS;
using CoreLib.Diagnostics;
using CoreLib.Transport;
using CoreLib.Transport.Ble;
using Java.Util;

namespace AndroidClient.Platforms.Android
{
    /// <summary>
    /// Finds the computer over BLE by scanning for the Mesh Sync service UUID.
    ///
    /// Matching on the service rather than on a Bluetooth address means pairing does not
    /// have to carry a MAC, so the existing QR payload keeps working and a replaced or
    /// re-paired computer is still found without re-pairing.
    /// </summary>
    public sealed class AndroidBleDiscovery : ScanCallback, IDiscoveryService, IDisposable
    {
        private static readonly ParcelUuid ServiceParcelUuid =
            ParcelUuid.FromString(BleProtocol.ServiceUuid.ToString())!;

        private readonly object _gate = new();
        private readonly Dictionary<string, DateTime> _seen = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan RediscoverAfter = TimeSpan.FromSeconds(30);

        /// <summary>Everything a round has seen, keyed by address, with the strongest reading kept.</summary>
        private readonly Dictionary<string, BleCandidate> _round = new(StringComparer.OrdinalIgnoreCase);

        private BluetoothLeScanner? _scanner;
        private bool _scanning;
        private bool _disposed;

        public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;

        /// <summary>The phone never advertises; the computer is the peripheral.</summary>
        public Task StartAdvertisingAsync(byte[] publicIdentifier) => Task.CompletedTask;

        public Task StopAdvertisingAsync() => Task.CompletedTask;

        public Task StartScanningAsync()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AndroidBleDiscovery));

            lock (_gate)
            {
                if (_scanning) return Task.CompletedTask;

                var manager = (BluetoothManager?)global::Android.App.Application.Context
                    .GetSystemService(Context.BluetoothService);
                var adapter = manager?.Adapter;

                if (adapter == null || !adapter.IsEnabled)
                {
                    Log.Write("BleScan", "Bluetooth is off, cannot scan.");
                    return Task.CompletedTask;
                }

                _scanner = adapter.BluetoothLeScanner;
                if (_scanner == null)
                {
                    Log.Write("BleScan", "This device has no BLE scanner.");
                    return Task.CompletedTask;
                }

                var filter = new ScanFilter.Builder()!
                    .SetServiceUuid(ServiceParcelUuid)!
                    .Build();

                var settings = new ScanSettings.Builder()!
                    .SetScanMode(global::Android.Bluetooth.LE.ScanMode.LowLatency)!
                    .Build();

                var filters = new List<ScanFilter>();
                if (filter != null) filters.Add(filter);

                _scanner.StartScan(filters, settings, this);
                _scanning = true;
                Log.Write("BleScan", "Scanning for the Mesh Sync service.");
            }

            return Task.CompletedTask;
        }

        public Task StopScanningAsync()
        {
            lock (_gate)
            {
                if (!_scanning) return Task.CompletedTask;

                try { _scanner?.StopScan(this); }
                catch (Exception ex) { Log.Write("BleScan", "Stopping the scan failed", ex); }

                _scanning = false;
                _seen.Clear();
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// One scan window, returning everything it saw.
        ///
        /// <para><b>This used to resolve on the first advertisement and stop.</b> Every install
        /// advertises the same service UUID, so the first packet is whoever happened to be
        /// nearest - and a foreign phone sitting closer than your own therefore won every round,
        /// was refused, and the round was over. Six minutes of scans found a stranger's phone
        /// over and over and the paired one not once, on the platform where this was fixed first.
        /// Collecting the whole window and letting the scheduler rank it is the fix.</para>
        /// </summary>
        public async Task<IReadOnlyList<BleCandidate>> ScanAsync(TimeSpan window, CancellationToken cancellationToken = default)
        {
            lock (_gate) _round.Clear();

            try
            {
                await StartScanningAsync().ConfigureAwait(false);

                // Fully qualified: Android.OS defines an OperationCanceledException too.
                try { await Task.Delay(window, cancellationToken).ConfigureAwait(false); }
                catch (System.OperationCanceledException) { }
            }
            finally
            {
                await StopScanningAsync().ConfigureAwait(false);
            }

            lock (_gate) return _round.Values.ToList();
        }

        public override void OnScanResult(ScanCallbackType callbackType, ScanResult? result)
        {
            base.OnScanResult(callbackType, result);

            var device = result?.Device;
            string? address = device?.Address;
            if (string.IsNullOrEmpty(address)) return;

            // Kept for the whole window, with the strongest reading of each device winning. The
            // scanner reports the same peer many times a second while it is in range, so this is
            // a merge rather than a stream.
            lock (_gate)
            {
                bool first = !_round.ContainsKey(address!);

                if (!first && _round[address!].Rssi >= (result?.Rssi ?? short.MinValue)) return;

                _round[address!] = new BleCandidate
                {
                    Address = address!,
                    Name = device?.Name,
                    Rssi = result?.Rssi ?? short.MinValue,
                    Beacon = BeaconOf(result),

                    // An advertisement only arrives while the device is being seen, so anything
                    // reported here is present by definition.
                    IsPresent = true,
                };

                if (!first) return;

                var now = DateTime.UtcNow;
                if (_seen.TryGetValue(address!, out var last) && now - last < RediscoverAfter) return;
                _seen[address!] = now;
            }

            Log.Write("BleScan", $"Found {device?.Name ?? "a Mesh Sync peer"} at {address} ({result?.Rssi} dBm).");

            DeviceDiscovered?.Invoke(this, new DeviceDiscoveredEventArgs
            {
                DeviceId = address!,
                DeviceName = device?.Name ?? "Mesh Sync",
                PublicIdentifer = Array.Empty<byte>()
            });
        }

        /// <summary>Pulls the mesh beacon out of an advertisement, if it carried one.</summary>
        private static byte[]? BeaconOf(ScanResult? result)
        {
            try
            {
                var data = result?.ScanRecord?.GetManufacturerSpecificData(MeshBeacon.CompanyId);
                return data != null && data.Length == MeshBeacon.Length ? data : null;
            }
            catch
            {
                // An advertisement this build does not understand is simply not one of ours.
                return null;
            }
        }

        public override void OnScanFailed(ScanFailure errorCode)
        {
            base.OnScanFailed(errorCode);
            Log.Write("BleScan", $"Scan failed: {errorCode}");
        }

        public new void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopScanningAsync().GetAwaiter().GetResult();
            DeviceDiscovered = null;
            base.Dispose();
        }
    }
}
