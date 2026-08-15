using System;
using System.Linq;
using System.Threading.Tasks;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.OS;
using CoreLib.Transport;

namespace AndroidClient.Platforms.Android
{
    public class AndroidBleDiscovery : ScanCallback, IDiscoveryService
    {
        private readonly BluetoothManager _bluetoothManager;
        private readonly BluetoothAdapter _bluetoothAdapter;
        
        private BluetoothLeAdvertiser? _advertiser;
        private BluetoothLeScanner? _scanner;
        
        private AdvertiseCallback? _advertiseCallback;

        // Use a unique manufacturer ID for our custom payload (e.g. 0xFFFF for testing)
        private const int ManufacturerId = 0xFFFF;

        public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;

        public AndroidBleDiscovery()
        {
            _bluetoothManager = (BluetoothManager)global::Android.App.Application.Context.GetSystemService(Context.BluetoothService)!;
            _bluetoothAdapter = _bluetoothManager.Adapter!;
        }

        public Task StartAdvertisingAsync(byte[] publicIdentifier)
        {
            if (_bluetoothAdapter == null || !_bluetoothAdapter.IsEnabled)
                throw new InvalidOperationException("Bluetooth is not enabled.");

            _advertiser = _bluetoothAdapter.BluetoothLeAdvertiser;
            if (_advertiser == null)
                throw new NotSupportedException("BLE Advertising not supported on this device.");

            var settings = new AdvertiseSettings.Builder()!
                .SetAdvertiseMode(AdvertiseMode.LowLatency)!
                .SetConnectable(true)!
                .SetTimeout(0)!
                .SetTxPowerLevel(AdvertiseTx.PowerHigh)!
                .Build();

            var data = new AdvertiseData.Builder()!
                .AddManufacturerData(ManufacturerId, publicIdentifier)!
                .Build();

            _advertiseCallback = new CustomAdvertiseCallback();
            _advertiser.StartAdvertising(settings, data, _advertiseCallback);

            return Task.CompletedTask;
        }

        public Task StopAdvertisingAsync()
        {
            if (_advertiser != null && _advertiseCallback != null)
            {
                _advertiser.StopAdvertising(_advertiseCallback);
            }
            return Task.CompletedTask;
        }

        public Task StartScanningAsync()
        {
            if (_bluetoothAdapter == null || !_bluetoothAdapter.IsEnabled)
                throw new InvalidOperationException("Bluetooth is not enabled.");

            _scanner = _bluetoothAdapter.BluetoothLeScanner;
            if (_scanner == null)
                throw new NotSupportedException("BLE Scanning not supported.");

            var scanFilter = new ScanFilter.Builder()!
                .SetManufacturerData(ManufacturerId, new byte[] { }) // Filter intentionally left broad, Android matches on ManufacturerId
                .Build();

            var scanSettings = new ScanSettings.Builder()!
                .SetScanMode(global::Android.Bluetooth.LE.ScanMode.LowLatency)!
                .Build();

            _scanner.StartScan(new[] { scanFilter }, scanSettings, this);

            return Task.CompletedTask;
        }

        public Task StopScanningAsync()
        {
            _scanner?.StopScan(this);
            return Task.CompletedTask;
        }

        public override void OnScanResult(ScanCallbackType callbackType, ScanResult? result)
        {
            base.OnScanResult(callbackType, result);

            if (result?.ScanRecord == null) return;

            var manufacturerData = result.ScanRecord.GetManufacturerSpecificData(ManufacturerId);
            if (manufacturerData != null)
            {
                byte[] payload = manufacturerData;
                
                DeviceDiscovered?.Invoke(this, new DeviceDiscoveredEventArgs
                {
                    DeviceId = result.Device?.Address ?? "Unknown",
                    DeviceName = result.Device?.Name ?? "Android BLE Node",
                    PublicIdentifer = payload
                });
            }
        }
    }

    class CustomAdvertiseCallback : AdvertiseCallback
    {
        public override void OnStartSuccess(AdvertiseSettings? settingsInEffect)
        {
            base.OnStartSuccess(settingsInEffect);
            Console.WriteLine("[AndroidBle] Successfully started advertising.");
        }

        public override void OnStartFailure(AdvertiseFailure errorCode)
        {
            base.OnStartFailure(errorCode);
            Console.WriteLine($"[AndroidBle] Failed to advertise. Error: {errorCode}");
        }
    }
}
