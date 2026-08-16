using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Bluetooth;
using Android.Content;
using CoreLib.Transport;

namespace AndroidClient.Platforms.Android
{
    public class AndroidBleTransport : BluetoothGattCallback, ITransportConnection
    {
        private readonly BluetoothManager _bluetoothManager;
        private readonly BluetoothAdapter _bluetoothAdapter;
        
        private BluetoothGattServer? _gattServer;
        private BluetoothGatt? _gattClient;
        private BluetoothGattCharacteristic? _writeCharacteristic;

        public static readonly Java.Util.UUID ServiceUuid = Java.Util.UUID.FromString("00000000-0000-0000-0000-000000004500")!;
        public static readonly Java.Util.UUID CharacteristicUuid = Java.Util.UUID.FromString("00000000-0000-0000-0000-000000004501")!;

        public event EventHandler<PayloadReceivedEventArgs>? PayloadReceived;
        public event EventHandler? ConnectionClosed;

        public bool IsConnected => _gattClient != null;

        public AndroidBleTransport()
        {
            _bluetoothManager = (BluetoothManager)global::Android.App.Application.Context.GetSystemService(Context.BluetoothService)!;
            _bluetoothAdapter = _bluetoothManager.Adapter!;
        }

        public Task StartListeningAsync(CancellationToken cancellationToken = default)
        {
            var serverCallback = new GattServerCallback(this);
            _gattServer = _bluetoothManager.OpenGattServer(global::Android.App.Application.Context, serverCallback);

            var service = new BluetoothGattService(ServiceUuid, GattServiceType.Primary);
            var characteristic = new BluetoothGattCharacteristic(
                CharacteristicUuid,
                GattProperty.Write | GattProperty.WriteNoResponse,
                GattPermission.Write);

            service.AddCharacteristic(characteristic);
            _gattServer?.AddService(service);

            Console.WriteLine("[AndroidBleTransport] Started GATT Server listening for payloads.");
            return Task.CompletedTask;
        }

        public Task ConnectAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            var device = _bluetoothAdapter.GetRemoteDevice(deviceId);
            if (device == null) throw new Exception("Device not found.");

            _gattClient = device.ConnectGatt(global::Android.App.Application.Context, false, this);
            return Task.CompletedTask;
        }

        public override void OnConnectionStateChange(BluetoothGatt? gatt, GattStatus status, ProfileState newState)
        {
            if (newState == ProfileState.Connected)
            {
                Console.WriteLine("[AndroidBleTransport] Connected to Remote GATT Server. Discovering services...");
                gatt?.DiscoverServices();
            }
            else if (newState == ProfileState.Disconnected)
            {
                ConnectionClosed?.Invoke(this, EventArgs.Empty);
            }
        }

        public override void OnServicesDiscovered(BluetoothGatt? gatt, GattStatus status)
        {
            if (status == GattStatus.Success && gatt != null)
            {
                var service = gatt.GetService(ServiceUuid);
                _writeCharacteristic = service?.GetCharacteristic(CharacteristicUuid);
            }
        }

        public Task SendPayloadAsync(byte[] encryptedPayload, CancellationToken cancellationToken = default)
        {
            if (_gattClient == null || _writeCharacteristic == null) 
                throw new InvalidOperationException("Not connected to a BLE characteristic.");

            // WARNING: In production, BLE has a strict MTU limit (often 20-512 bytes).
            // We would need to chunk this byte array if it's larger than the negotiated MTU.
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                _gattClient.WriteCharacteristic(
                    _writeCharacteristic,
                    encryptedPayload,
                    (int)GattWriteType.Default);
            }
            else
            {
#pragma warning disable CA1422 // Superseded on API 33+, still the only option below it.
                _writeCharacteristic.SetValue(encryptedPayload);
                _gattClient.WriteCharacteristic(_writeCharacteristic);
#pragma warning restore CA1422
            }

            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            _gattClient?.Disconnect();
            _gattClient?.Close();
            _gattServer?.Close();
            return Task.CompletedTask;
        }

        public new void Dispose()
        {
            DisconnectAsync().Wait();
            base.Dispose();
        }

        public void TriggerPayloadReceived(byte[] payload)
        {
            PayloadReceived?.Invoke(this, new PayloadReceivedEventArgs { EncryptedPayload = payload });
        }
    }

    class GattServerCallback : BluetoothGattServerCallback
    {
        private readonly AndroidBleTransport _parent;

        public GattServerCallback(AndroidBleTransport parent)
        {
            _parent = parent;
        }

        public override void OnCharacteristicWriteRequest(BluetoothDevice? device, int requestId, BluetoothGattCharacteristic? characteristic, bool preparedWrite, bool responseNeeded, int offset, byte[]? value)
        {
            base.OnCharacteristicWriteRequest(device, requestId, characteristic, preparedWrite, responseNeeded, offset, value);

            if (characteristic?.Uuid?.Equals(AndroidBleTransport.CharacteristicUuid) == true && value != null)
            {
                _parent.TriggerPayloadReceived(value);
            }
        }
    }
}
