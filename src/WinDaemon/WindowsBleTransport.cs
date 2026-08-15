using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using CoreLib.Transport;

namespace WinDaemon
{
    public class WindowsBleTransport : ITransportConnection
    {
        private BluetoothLEDevice? _device;
        private GattSession? _session;
        private GattCharacteristic? _writeCharacteristic;
        private GattServiceProvider? _serviceProvider;
        
        public static readonly Guid ServiceUuid = Guid.Parse("00000000-0000-0000-0000-000000004500");
        public static readonly Guid CharacteristicUuid = Guid.Parse("00000000-0000-0000-0000-000000004501");

        public event EventHandler<PayloadReceivedEventArgs>? PayloadReceived;
        public event EventHandler? ConnectionClosed;

        public bool IsConnected => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

        public async Task StartListeningAsync(CancellationToken cancellationToken = default)
        {
            var result = await GattServiceProvider.CreateAsync(ServiceUuid);
            if (result.Error == BluetoothError.Success)
            {
                _serviceProvider = result.ServiceProvider;

                var charParameters = new GattLocalCharacteristicParameters
                {
                    CharacteristicProperties = GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse,
                    WriteProtectionLevel = GattProtectionLevel.Plain
                };

                var charResult = await _serviceProvider.Service.CreateCharacteristicAsync(CharacteristicUuid, charParameters);
                if (charResult.Error == BluetoothError.Success)
                {
                    charResult.Characteristic.WriteRequested += OnWriteRequested;
                    
                    var advParameters = new GattServiceProviderAdvertisingParameters
                    {
                        IsDiscoverable = true,
                        IsConnectable = true
                    };
                    _serviceProvider.StartAdvertising(advParameters);
                    Console.WriteLine("[WinBleTransport] Started GATT Server listening for payloads.");
                }
            }
        }

        private async void OnWriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
        {
            using (args.GetDeferral())
            {
                var request = await args.GetRequestAsync();
                if (request == null) return;

                byte[] payload = request.Value.ToArray();
                PayloadReceived?.Invoke(this, new PayloadReceivedEventArgs { EncryptedPayload = payload });

                if (request.Option == GattWriteOption.WriteWithResponse)
                {
                    request.Respond();
                }
            }
        }

        public async Task ConnectAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            // deviceId here is the Bluetooth MAC address in Hex
            ulong macAddress = Convert.ToUInt64(deviceId, 16);
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(macAddress);

            if (_device != null)
            {
                var services = await _device.GetGattServicesForUuidAsync(ServiceUuid);
                if (services.Status == GattCommunicationStatus.Success && services.Services.Count > 0)
                {
                    var characteristics = await services.Services[0].GetCharacteristicsForUuidAsync(CharacteristicUuid);
                    if (characteristics.Status == GattCommunicationStatus.Success && characteristics.Characteristics.Count > 0)
                    {
                        _writeCharacteristic = characteristics.Characteristics[0];
                        Console.WriteLine("[WinBleTransport] Connected to Remote GATT Server.");
                    }
                }
            }
        }

        public async Task SendPayloadAsync(byte[] encryptedPayload, CancellationToken cancellationToken = default)
        {
            if (_writeCharacteristic == null) throw new InvalidOperationException("Not connected to a BLE characteristic.");

            // WARNING: In production, BLE has a strict MTU limit (often 20-512 bytes). 
            // We would need to chunk this byte array if it's larger than the negotiated MTU.
            var writer = new Windows.Storage.Streams.DataWriter();
            writer.WriteBytes(encryptedPayload);

            var status = await _writeCharacteristic.WriteValueAsync(writer.DetachBuffer());
            if (status != GattCommunicationStatus.Success)
            {
                throw new Exception($"Failed to write BLE characteristic. Status: {status}");
            }
        }

        public Task DisconnectAsync()
        {
            _serviceProvider?.StopAdvertising();
            _device?.Dispose();
            ConnectionClosed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            DisconnectAsync().Wait();
        }
    }
}
