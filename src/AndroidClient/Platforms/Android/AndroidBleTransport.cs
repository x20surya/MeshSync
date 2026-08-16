using System;
using System.Threading;
using System.Threading.Tasks;
using Android.Bluetooth;
using Android.Content;
using Android.OS;
using CoreLib.Diagnostics;
using CoreLib.Transport;
using Java.Util;

namespace AndroidClient.Platforms.Android
{
    /// <summary>
    /// BLE GATT client. The phone is the central and connects to the computer's peripheral,
    /// mirroring the TCP topology so the roles stay consistent across both transports.
    ///
    /// Payloads are fragmented over the negotiated MTU by <see cref="BleFragmenter"/>. The
    /// previous version wrote a whole payload in a single SetValue call, which silently
    /// truncated at the MTU - its own comment said chunking would be needed.
    /// </summary>
    public sealed class AndroidBleTransport : BluetoothGattCallback, ITransportConnection
    {
        private static readonly UUID ServiceUuid = UUID.FromString(BleProtocol.ServiceUuid.ToString())!;
        private static readonly UUID InboxUuid = UUID.FromString(BleProtocol.InboxCharacteristicUuid.ToString())!;
        private static readonly UUID OutboxUuid = UUID.FromString(BleProtocol.OutboxCharacteristicUuid.ToString())!;

        /// <summary>Standard descriptor that switches notifications on for a characteristic.</summary>
        private static readonly UUID ClientConfigDescriptorUuid =
            UUID.FromString("00002902-0000-1000-8000-00805f9b34fb")!;

        private readonly BleReassembler _reassembler = new(BleProtocol.MaxPayloadBytes);
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly SemaphoreSlim _writeComplete = new(0, 1);

        private BluetoothGatt? _gatt;
        private BluetoothGattCharacteristic? _inbox;
        private TaskCompletionSource<bool>? _readySignal;
        private int _usablePayload = BleFragmenter.MinimumMtuPayload;
        private byte _messageId;
        private volatile bool _ready;
        private bool _disposed;

        public event EventHandler<PayloadReceivedEventArgs>? PayloadReceived;
        public event EventHandler? ConnectionClosed;

        /// <summary>Only true once services are discovered and notifications are live.</summary>
        public bool IsConnected => _ready;

        public Task StartListeningAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The Android BLE transport is the GATT client. It connects to the computer.");

        public async Task ConnectAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            var manager = (BluetoothManager?)global::Android.App.Application.Context
                .GetSystemService(Context.BluetoothService);
            var adapter = manager?.Adapter
                ?? throw new InvalidOperationException("This device has no Bluetooth adapter.");

            if (!adapter.IsEnabled) throw new InvalidOperationException("Bluetooth is switched off.");

            var device = adapter.GetRemoteDevice(deviceId)
                ?? throw new InvalidOperationException($"No Bluetooth device at {deviceId}.");

            _readySignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // TransportLe: without it Android may try BR/EDR and fail on a BLE-only service.
            _gatt = device.ConnectGatt(global::Android.App.Application.Context, false, this, BluetoothTransports.Le);
            if (_gatt == null) throw new InvalidOperationException("Could not open a GATT connection.");

            using var registration = cancellationToken.Register(() => _readySignal.TrySetCanceled());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var timeoutRegistration = timeout.Token.Register(() =>
                _readySignal.TrySetException(new TimeoutException("The BLE link did not become ready in time.")));

            await _readySignal.Task.ConfigureAwait(false);
            Log.Write("BleClient", $"Link ready. Usable payload {_usablePayload} bytes per write.");

            // Ready is not the same as usable. Android reports the subscription write as
            // successful even when it lands on a service published by a process that has
            // since exited, so both ends believe they are talking and nothing crosses.
            // Requiring an answer before declaring the transport connected turns that
            // silent half-open state into an ordinary failed attempt the loop can retry.
            if (!await ExchangeGreetingAsync(cancellationToken).ConfigureAwait(false))
            {
                _ready = false;
                throw new InvalidOperationException(
                    "The computer accepted the connection but did not answer; its Bluetooth service is stale.");
            }

            Log.Write("BleClient", "Computer answered - link confirmed.");
        }

        // ──────────────────────────────── GATT callbacks

        public override void OnConnectionStateChange(BluetoothGatt? gatt, GattStatus status, ProfileState newState)
        {
            if (newState == ProfileState.Connected)
            {
                Log.Write("BleClient", "Connected. Negotiating MTU.");

                // Android caches a device's service and handle table across connections. If
                // the computer restarts, it republishes the service with fresh handles while
                // the phone keeps writing to the stale ones, so both sides look connected and
                // nothing whatsoever gets through. Clearing the cache forces a real rediscovery.
                if (gatt != null) RefreshServiceCache(gatt);

                // Ask before discovering services: a later MTU change would not resize
                // characteristics already in flight.
                if (gatt?.RequestMtu(BleProtocol.PreferredMtu) != true) gatt?.DiscoverServices();
            }
            else if (newState == ProfileState.Disconnected)
            {
                Log.Write("BleClient", $"Disconnected (status {status}).");
                _ready = false;
                _reassembler.Reset();
                _readySignal?.TrySetException(new InvalidOperationException($"BLE disconnected: {status}"));
                ConnectionClosed?.Invoke(this, EventArgs.Empty);
            }
        }

        public override void OnMtuChanged(BluetoothGatt? gatt, int mtu, GattStatus status)
        {
            // A refused negotiation is not fatal; it just means small chunks.
            _usablePayload = status == GattStatus.Success
                ? BleProtocol.UsablePayload(mtu)
                : BleFragmenter.MinimumMtuPayload;

            Log.Write("BleClient", $"MTU {mtu} ({status}), usable payload {_usablePayload}.");
            gatt?.DiscoverServices();
        }

        public override void OnServicesDiscovered(BluetoothGatt? gatt, GattStatus status)
        {
            if (status != GattStatus.Success || gatt == null)
            {
                _readySignal?.TrySetException(new InvalidOperationException($"Service discovery failed: {status}"));
                return;
            }

            var service = gatt.GetService(ServiceUuid);
            if (service == null)
            {
                _readySignal?.TrySetException(new InvalidOperationException("The peer does not expose the Mesh Sync service."));
                return;
            }

            _inbox = service.GetCharacteristic(InboxUuid);
            var outbox = service.GetCharacteristic(OutboxUuid);

            if (_inbox == null || outbox == null)
            {
                _readySignal?.TrySetException(new InvalidOperationException("The Mesh Sync service is missing a characteristic."));
                return;
            }

            // Notifications need both a local subscription and the remote descriptor write.
            gatt.SetCharacteristicNotification(outbox, true);

            var descriptor = outbox.GetDescriptor(ClientConfigDescriptorUuid);
            if (descriptor == null)
            {
                _readySignal?.TrySetException(new InvalidOperationException("The outbox has no client config descriptor."));
                return;
            }

            // Notifications, with a receipt written back per chunk. Indications looked like
            // the right answer but went unconfirmed on this stack and the peer dropped the
            // link, so flow control lives in our own protocol instead.
            var enable = BluetoothGattDescriptor.EnableNotificationValue!.ToArray();

            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                gatt.WriteDescriptor(descriptor, enable);
            }
            else
            {
#pragma warning disable CA1422 // Superseded on API 33+, still the only option below it.
                descriptor.SetValue(enable);
                gatt.WriteDescriptor(descriptor);
#pragma warning restore CA1422
            }
        }

        public override void OnDescriptorWrite(BluetoothGatt? gatt, BluetoothGattDescriptor? descriptor, GattStatus status)
        {
            if (descriptor?.Uuid?.Equals(ClientConfigDescriptorUuid) != true) return;

            if (status == GattStatus.Success)
            {
                _ready = true;
                _lastInboundUtc = DateTime.UtcNow;
                _heartbeatCts = new CancellationTokenSource();
                _ = HeartbeatLoopAsync(_heartbeatCts.Token);
                _readySignal?.TrySetResult(true);
            }
            else
            {
                _readySignal?.TrySetException(new InvalidOperationException($"Could not enable notifications: {status}"));
            }
        }

        public override void OnCharacteristicChanged(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, byte[]? value)
        {
            if (characteristic?.Uuid?.Equals(OutboxUuid) != true || value == null) return;
            HandleChunk(value);
        }

        // Pre-API-33 delivery path; the framework calls one or the other by version.
#pragma warning disable CS0672, CA1422
        public override void OnCharacteristicChanged(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(33)) return;
            if (characteristic?.Uuid?.Equals(OutboxUuid) != true) return;

            var value = characteristic.GetValue();
            if (value != null) HandleChunk(value);
        }
#pragma warning restore CS0672, CA1422

        private void HandleChunk(byte[] chunk)
        {
            try
            {
                _lastInboundUtc = DateTime.UtcNow;

                // A pong, not data. Its only job is to prove the peer is still answering.
                if (BleProtocol.TryParseControl(chunk, out _))
                {
                    _pong?.TrySetResult(true);
                    return;
                }

                // Tell the server this one landed so it can release the next. Without it the
                // server's second notification overwrites the first before it is transmitted.
                if (chunk.Length >= BleFragmenter.HeaderSize)
                {
                    byte messageId = chunk[0];
                    int sequence = chunk[1] | (chunk[2] << 8);
                    _ = SendAckAsync(messageId, sequence);
                }

                byte[]? payload = _reassembler.Accept(chunk);
                if (payload == null) return;

                Log.Write("BleClient", $"Reassembled a {payload.Length} byte payload.");
                PayloadReceived?.Invoke(this, new PayloadReceivedEventArgs { EncryptedPayload = payload });
            }
            catch (Exception ex)
            {
                Log.Write("BleClient", "Handling a notification failed", ex);
            }
        }

        public override void OnCharacteristicWrite(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, GattStatus status)
        {
            // Android allows exactly one outstanding write, so the sender waits for this.
            if (characteristic?.Uuid?.Equals(InboxUuid) == true)
            {
                try { _writeComplete.Release(); } catch (SemaphoreFullException) { }
            }
        }

        /// <summary>
        /// Clears Android's cached service and handle table for this device.
        ///
        /// BluetoothGatt.refresh() is public in AOSP but not part of the SDK, so reflection
        /// is the only route. It is the long-standing remedy for a peripheral whose service
        /// definition changes between connections, which is exactly what happens when the
        /// desktop app restarts and republishes its GATT service.
        /// </summary>
        private static void RefreshServiceCache(BluetoothGatt gatt)
        {
            try
            {
                var method = gatt.Class.GetMethod("refresh");
                if (method == null)
                {
                    Log.Write("BleClient", "No refresh() on BluetoothGatt; service cache left alone.");
                    return;
                }

                var result = method.Invoke(gatt);
                Log.Write("BleClient", $"Cleared the cached service table (refresh returned {result}).");
            }
            catch (Exception ex)
            {
                // Not fatal: it only matters when the peer's services changed.
                Log.Write("BleClient", "Could not clear the cached service table", ex);
            }
        }

        // ──────────────────────────────── liveness

        private DateTime _lastInboundUtc = DateTime.MinValue;
        private CancellationTokenSource? _heartbeatCts;
        private TaskCompletionSource<bool>? _pong;

        /// <summary>
        /// Pings the computer and waits for its reply, proving the link carries traffic in
        /// both directions before the transport is reported as connected.
        /// </summary>
        private async Task<bool> ExchangeGreetingAsync(CancellationToken cancellationToken)
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                _pong = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                try { await SendControlAsync(BleProtocol.ControlPing).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    Log.Write("BleClient", $"Greeting attempt {attempt} could not be written", ex);
                    continue;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));

                var finished = await Task.WhenAny(
                    _pong.Task,
                    Task.Delay(Timeout.Infinite, timeout.Token)).ConfigureAwait(false);

                if (finished == _pong.Task) return true;

                Log.Write("BleClient", $"Greeting attempt {attempt} went unanswered.");
            }

            return false;
        }

        /// <summary>
        /// Pings the computer and gives up on the link if it stops answering.
        ///
        /// A GATT connection outlives the process that published the service, so restarting
        /// the desktop app leaves the phone connected to nothing: writes still appear to
        /// succeed, but the computer never re-announces its subscription and can no longer
        /// notify back. Only an end-to-end exchange catches that; the radio reports the link
        /// as perfectly healthy throughout.
        /// </summary>
        private async Task HeartbeatLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && _ready)
                {
                    await Task.Delay(BleProtocol.HeartbeatInterval, token).ConfigureAwait(false);
                    if (!_ready || token.IsCancellationRequested) return;

                    if (DateTime.UtcNow - _lastInboundUtc > BleProtocol.PeerTimeout)
                    {
                        Log.Write("BleClient",
                            $"No answer from the computer for {BleProtocol.PeerTimeout.TotalSeconds:F0}s - dropping the link.");
                        _ready = false;
                        ConnectionClosed?.Invoke(this, EventArgs.Empty);
                        return;
                    }

                    try { await SendControlAsync(BleProtocol.ControlPing).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        Log.Write("BleClient", "Heartbeat write failed - dropping the link", ex);
                        _ready = false;
                        ConnectionClosed?.Invoke(this, EventArgs.Empty);
                        return;
                    }
                }
            }
            catch (System.OperationCanceledException) { /* expected on teardown */ }
        }

        private async Task SendControlAsync(byte kind)
        {
            var gatt = _gatt;
            var inbox = _inbox;
            if (gatt == null || inbox == null) return;

            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await WriteChunkAsync(gatt, inbox, BleProtocol.BuildControl(kind), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// Writes a four-byte receipt for a chunk. Uses the send lock so it cannot interleave
        /// with an outbound payload, since Android permits one outstanding write at a time.
        /// </summary>
        private async Task SendAckAsync(byte messageId, int sequence)
        {
            var gatt = _gatt;
            var inbox = _inbox;
            if (gatt == null || inbox == null) return;

            try
            {
                await _sendLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    await WriteChunkAsync(gatt, inbox, BleProtocol.BuildAck(messageId, sequence),
                        CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _sendLock.Release();
                }
            }
            catch (Exception ex)
            {
                Log.Write("BleClient", $"Could not acknowledge chunk {sequence}", ex);
            }
        }

        // ──────────────────────────────── sending

        public async Task SendPayloadAsync(byte[] encryptedPayload, CancellationToken cancellationToken = default)
        {
            if (encryptedPayload == null) throw new ArgumentNullException(nameof(encryptedPayload));

            var gatt = _gatt;
            var inbox = _inbox;
            if (gatt == null || inbox == null || !_ready) throw new InvalidOperationException("The BLE link is not ready.");

            if (encryptedPayload.Length > BleProtocol.MaxPayloadBytes)
                throw new ArgumentException(
                    $"Payload of {encryptedPayload.Length} bytes is too large for BLE; use Wi-Fi for this.",
                    nameof(encryptedPayload));

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                byte messageId = unchecked(++_messageId);
                var chunks = BleFragmenter.Fragment(encryptedPayload, _usablePayload, messageId);

                foreach (var chunk in chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await WriteChunkAsync(gatt, inbox, chunk, cancellationToken).ConfigureAwait(false);
                }

                Log.Write("BleClient", $"Sent {encryptedPayload.Length} bytes as {chunks.Count} chunks of at most {_usablePayload}.");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task WriteChunkAsync(BluetoothGatt gatt, BluetoothGattCharacteristic inbox,
                                           byte[] chunk, CancellationToken cancellationToken)
        {
            // Drain any stale completion so the wait below matches this write.
            while (_writeComplete.CurrentCount > 0) await _writeComplete.WaitAsync(0).ConfigureAwait(false);

            bool queued;
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                queued = gatt.WriteCharacteristic(inbox, chunk, (int)GattWriteType.Default) == (int)GattStatus.Success;
            }
            else
            {
#pragma warning disable CA1422 // Superseded on API 33+, still the only option below it.
                inbox.WriteType = GattWriteType.Default;
                inbox.SetValue(chunk);
                queued = gatt.WriteCharacteristic(inbox);
#pragma warning restore CA1422
            }

            if (!queued) throw new InvalidOperationException("The BLE stack refused the write.");

            // Wait for the stack's completion callback rather than guessing with a delay.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            // Fully qualified: Android.OS also defines OperationCanceledException.
            try { await _writeComplete.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (System.OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("A BLE chunk write was not acknowledged.");
            }
        }

        // ──────────────────────────────── lifetime

        public async Task DisconnectAsync()
        {
            _ready = false;
            try { _heartbeatCts?.Cancel(); } catch { }
            _heartbeatCts = null;
            _reassembler.Reset();

            var gatt = _gatt;
            _gatt = null;
            _inbox = null;

            if (gatt == null) return;

            try
            {
                gatt.Disconnect();

                // Close() immediately after Disconnect() leaves the underlying link up for a
                // moment. Reconnecting into that half-torn-down link is what let the phone
                // reattach to a connection the computer's newly published service was never
                // bound to: both ends reported success and nothing crossed. Letting the
                // disconnect land first forces a genuinely new connection.
                await Task.Delay(TimeSpan.FromMilliseconds(600)).ConfigureAwait(false);

                gatt.Close();
                await Task.Delay(TimeSpan.FromMilliseconds(400)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write("BleClient", "Closing the GATT client failed", ex);
            }
        }

        public new void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Blocking here would stall the reconnect loop for a second on every retry, and
            // the teardown does not need to be observed.
            try { DisconnectAsync().GetAwaiter().GetResult(); } catch { }

            PayloadReceived = null;
            ConnectionClosed = null;

            base.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AndroidBleTransport));
        }
    }
}
