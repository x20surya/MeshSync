using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth.Advertisement;
using CoreLib.Transport;

namespace WinDaemon
{
    public class WindowsBleDiscovery : IDiscoveryService
    {
        private BluetoothLEAdvertisementPublisher? _publisher;
        private BluetoothLEAdvertisementWatcher? _watcher;
        
        // Use a unique manufacturer ID for our custom payload (e.g. 0xFFFF for testing)
        private const ushort ManufacturerId = 0xFFFF;

        public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;

        public Task StartAdvertisingAsync(byte[] publicIdentifier)
        {
            _publisher = new BluetoothLEAdvertisementPublisher();

            // Add our custom payload to the manufacturer data
            var manufacturerData = new BluetoothLEManufacturerData
            {
                CompanyId = ManufacturerId
            };
            
            // Write the public identifier into the payload
            var writer = new Windows.Storage.Streams.DataWriter();
            writer.WriteBytes(publicIdentifier);
            manufacturerData.Data = writer.DetachBuffer();

            _publisher.Advertisement.ManufacturerData.Add(manufacturerData);
            _publisher.Start();

            Console.WriteLine("[WinBle] Started advertising BLE presence.");
            return Task.CompletedTask;
        }

        public Task StopAdvertisingAsync()
        {
            _publisher?.Stop();
            return Task.CompletedTask;
        }

        public Task StartScanningAsync()
        {
            _watcher = new BluetoothLEAdvertisementWatcher();
            _watcher.Received += OnAdvertisementReceived;
            _watcher.Start();

            Console.WriteLine("[WinBle] Started scanning for BLE devices.");
            return Task.CompletedTask;
        }

        private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            // Look for our specific manufacturer data
            foreach (var data in args.Advertisement.ManufacturerData)
            {
                if (data.CompanyId == ManufacturerId)
                {
                    // Found a mesh device!
                    byte[] payload = data.Data.ToArray();
                    
                    DeviceDiscovered?.Invoke(this, new DeviceDiscoveredEventArgs
                    {
                        DeviceId = args.BluetoothAddress.ToString("X"),
                        DeviceName = args.Advertisement.LocalName,
                        PublicIdentifer = payload
                    });
                }
            }
        }

        public Task StopScanningAsync()
        {
            if (_watcher != null)
            {
                _watcher.Received -= OnAdvertisementReceived;
                _watcher.Stop();
            }
            return Task.CompletedTask;
        }
    }
}
