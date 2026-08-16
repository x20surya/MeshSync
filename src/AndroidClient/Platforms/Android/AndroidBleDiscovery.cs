using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.OS;
using CoreLib.Diagnostics;
using CoreLib.Transport;
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

        /// <summary>Scans until a peer turns up or the timeout expires. Returns its address.</summary>
        public async Task<string?> FindPeerAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var found = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnFound(object? sender, DeviceDiscoveredEventArgs e) => found.TrySetResult(e.DeviceId);

            DeviceDiscovered += OnFound;
            try
            {
                await StartScanningAsync().ConfigureAwait(false);

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linked.CancelAfter(timeout);
                using var registration = linked.Token.Register(() => found.TrySetResult(string.Empty));

                string address = await found.Task.ConfigureAwait(false);
                return string.IsNullOrEmpty(address) ? null : address;
            }
            finally
            {
                DeviceDiscovered -= OnFound;
                await StopScanningAsync().ConfigureAwait(false);
            }
        }

        public override void OnScanResult(ScanCallbackType callbackType, ScanResult? result)
        {
            base.OnScanResult(callbackType, result);

            var device = result?.Device;
            string? address = device?.Address;
            if (string.IsNullOrEmpty(address)) return;

            // The scanner reports the same peer many times a second while it is in range.
            lock (_gate)
            {
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
