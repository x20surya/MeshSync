using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using CoreLib.Diagnostics;
using CoreLib.Transport;

namespace WinDaemon
{
    /// <summary>
    /// BLE GATT server. Carries clipboard text when there is no Wi-Fi to carry it, which is
    /// the whole point of the tier: no router, no hotspot, no network of any kind.
    ///
    /// Payloads are fragmented over the negotiated MTU by <see cref="BleFragmenter"/>, which
    /// is what the previous version of this file was missing - its own comment noted that a
    /// payload larger than the MTU would simply not work.
    ///
    /// Inbox is written by the phone; Outbox is notified to it.
    /// </summary>
    public sealed class WindowsBleTransport : ITransportConnection
    {
        private readonly BleReassembler _reassembler = new(BleProtocol.MaxPayloadBytes);
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        private GattServiceProvider? _serviceProvider;
        private GattLocalCharacteristic? _inbox;
        private GattLocalCharacteristic? _outbox;
        private byte _messageId;
        private volatile bool _hasSubscriber;
        private bool _disposed;

        public event EventHandler<PayloadReceivedEventArgs>? PayloadReceived;
        public event EventHandler? ConnectionClosed;
        public event EventHandler? ClientConnected;

        /// <summary>True once a phone has subscribed to notifications, which is our liveness signal.</summary>
        public bool IsConnected => _hasSubscriber;

        public async Task StartListeningAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            var serviceResult = await GattServiceProvider.CreateAsync(BleProtocol.ServiceUuid);
            if (serviceResult.Error != BluetoothError.Success)
            {
                Log.Write("BleServer", $"Could not create the GATT service: {serviceResult.Error}");
                return;
            }

            _serviceProvider = serviceResult.ServiceProvider;

            var inboxResult = await _serviceProvider.Service.CreateCharacteristicAsync(
                BleProtocol.InboxCharacteristicUuid,
                new GattLocalCharacteristicParameters
                {
                    CharacteristicProperties = GattCharacteristicProperties.Write |
                                               GattCharacteristicProperties.WriteWithoutResponse,
                    WriteProtectionLevel = GattProtectionLevel.Plain
                });

            if (inboxResult.Error != BluetoothError.Success)
            {
                Log.Write("BleServer", $"Could not create the inbox characteristic: {inboxResult.Error}");
                return;
            }

            _inbox = inboxResult.Characteristic;
            _inbox.WriteRequested += OnWriteRequested;

            var outboxResult = await _serviceProvider.Service.CreateCharacteristicAsync(
                BleProtocol.OutboxCharacteristicUuid,
                new GattLocalCharacteristicParameters
                {
                    CharacteristicProperties = GattCharacteristicProperties.Notify,
                    ReadProtectionLevel = GattProtectionLevel.Plain
                });

            if (outboxResult.Error != BluetoothError.Success)
            {
                Log.Write("BleServer", $"Could not create the outbox characteristic: {outboxResult.Error}");
                return;
            }

            _outbox = outboxResult.Characteristic;
            _outbox.SubscribedClientsChanged += OnSubscribedClientsChanged;

            _serviceProvider.StartAdvertising(new GattServiceProviderAdvertisingParameters
            {
                IsDiscoverable = true,
                IsConnectable = true
            });

            Log.Write("BleServer", "Advertising the Mesh Sync GATT service.");
        }

        private void OnSubscribedClientsChanged(GattLocalCharacteristic sender, object args)
        {
            bool had = _hasSubscriber;
            int count = sender.SubscribedClients.Count;
            _hasSubscriber = count > 0;

            if (_hasSubscriber && !had)
            {
                Log.Write("BleServer", $"Phone subscribed. Notification size {MaxNotificationSize()} bytes.");
                ClientConnected?.Invoke(this, EventArgs.Empty);
            }
            else if (!_hasSubscriber && had)
            {
                Log.Write("BleServer", "Phone unsubscribed.");
                _reassembler.Reset();
                ConnectionClosed?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Windows reports the usable notification size per subscribed client, which already
        /// accounts for the ATT header, so it is used directly rather than derived from an MTU.
        /// </summary>
        private int MaxNotificationSize()
        {
            try
            {
                var clients = _outbox?.SubscribedClients;
                if (clients == null || clients.Count == 0) return BleFragmenter.MinimumMtuPayload;

                int smallest = int.MaxValue;
                foreach (var client in clients)
                {
                    smallest = Math.Min(smallest, (int)client.MaxNotificationSize);
                }

                return smallest <= BleFragmenter.HeaderSize
                    ? BleFragmenter.MinimumMtuPayload
                    : smallest;
            }
            catch
            {
                return BleFragmenter.MinimumMtuPayload;
            }
        }

        private async void OnWriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
        {
            using var deferral = args.GetDeferral();

            try
            {
                var request = await args.GetRequestAsync();
                if (request == null) return;

                byte[] chunk = request.Value.ToArray();

                if (request.Option == GattWriteOption.WriteWithResponse) request.Respond();

                byte[]? payload = _reassembler.Accept(chunk);
                if (payload == null) return;

                Log.Write("BleServer", $"Reassembled a {payload.Length} byte payload.");
                PayloadReceived?.Invoke(this, new PayloadReceivedEventArgs { EncryptedPayload = payload });
            }
            catch (Exception ex)
            {
                Log.Write("BleServer", "Handling a write failed", ex);
            }
        }

        public async Task SendPayloadAsync(byte[] encryptedPayload, CancellationToken cancellationToken = default)
        {
            if (encryptedPayload == null) throw new ArgumentNullException(nameof(encryptedPayload));
            if (_outbox == null || !_hasSubscriber) throw new InvalidOperationException("No BLE subscriber.");

            if (encryptedPayload.Length > BleProtocol.MaxPayloadBytes)
                throw new ArgumentException(
                    $"Payload of {encryptedPayload.Length} bytes is too large for BLE; use Wi-Fi for this.",
                    nameof(encryptedPayload));

            int chunkSize = MaxNotificationSize();
            byte messageId;

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                messageId = unchecked(++_messageId);
                var chunks = BleFragmenter.Fragment(encryptedPayload, chunkSize, messageId);

                foreach (var chunk in chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    using var writer = new DataWriter();
                    writer.WriteBytes(chunk);
                    await _outbox.NotifyValueAsync(writer.DetachBuffer());

                    // No application-level flow control, so pace the burst.
                    if (chunks.Count > 1) await Task.Delay(BleProtocol.InterChunkDelayMs, cancellationToken).ConfigureAwait(false);
                }

                Log.Write("BleServer", $"Sent {encryptedPayload.Length} bytes as {chunks.Count} chunks of at most {chunkSize}.");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public Task ConnectAsync(string deviceId, CancellationToken cancellationToken = default) =>
            // The computer is always the peripheral in this topology; the phone connects to it.
            throw new NotSupportedException("The Windows BLE transport is the GATT server. The phone initiates the connection.");

        public Task DisconnectAsync()
        {
            try { _serviceProvider?.StopAdvertising(); }
            catch (Exception ex) { Log.Write("BleServer", "Stopping advertising failed", ex); }

            _hasSubscriber = false;
            _reassembler.Reset();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_inbox != null) _inbox.WriteRequested -= OnWriteRequested;
            if (_outbox != null) _outbox.SubscribedClientsChanged -= OnSubscribedClientsChanged;

            try { _serviceProvider?.StopAdvertising(); } catch { }

            _serviceProvider = null;
            _inbox = null;
            _outbox = null;

            PayloadReceived = null;
            ConnectionClosed = null;
            ClientConnected = null;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WindowsBleTransport));
        }
    }
}
