using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.OS;
using CoreLib.Diagnostics;
using CoreLib.Identity;
using CoreLib.Transport;
using CoreLib.Transport.Ble;
using Java.Util;

namespace AndroidClient.Platforms.Android
{
    /// <summary>
    /// BLE GATT server. The half of Bluetooth the phone never had.
    ///
    /// <para>The phone was the central and the computer the peripheral, fixed at compile time,
    /// which is why Bluetooth could only ever join a phone to a computer - two phones would
    /// both sit scanning for something neither was broadcasting. This advertises the Mesh Sync
    /// service and serves the two characteristics, so a phone can be the one that is found.</para>
    ///
    /// <para>Whether it is used for a given peer is decided by <see cref="BleRoleRules"/>, and
    /// capability comes before fingerprint there: advertising is hardware-dependent on Android,
    /// so a phone that cannot do it has to be the central whatever its identity sorts to.</para>
    ///
    /// <para>The wire protocol needs no changes: it was already written from the central's
    /// point of view - the client writes chunks, the server notifies them - so this serves a
    /// Windows central using exactly the frames the Windows peripheral serves to this phone.</para>
    /// </summary>
    public sealed class AndroidBlePeripheral : BluetoothGattServerCallback, ITransportConnection
    {
        private static readonly UUID ServiceUuid = UUID.FromString(BleProtocol.ServiceUuid.ToString())!;
        private static readonly UUID InboxUuid = UUID.FromString(BleProtocol.InboxCharacteristicUuid.ToString())!;
        private static readonly UUID OutboxUuid = UUID.FromString(BleProtocol.OutboxCharacteristicUuid.ToString())!;

        /// <summary>Standard descriptor a central writes to switch notifications on.</summary>
        private static readonly UUID ClientConfigDescriptorUuid =
            UUID.FromString("00002902-0000-1000-8000-00805f9b34fb")!;

        private static readonly ParcelUuid ServiceParcelUuid =
            ParcelUuid.FromString(BleProtocol.ServiceUuid.ToString())!;

        private readonly BleReassembler _reassembler = new(BleProtocol.MaxPayloadBytes);
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        /// <summary>Android permits one outstanding notification; this is its completion.</summary>
        private readonly SemaphoreSlim _notifySent = new(0, 1);

        private readonly object _gate = new();

        private BluetoothGattServer? _server;
        private BluetoothGattCharacteristic? _inbox;
        private BluetoothGattCharacteristic? _outbox;
        private BluetoothLeAdvertiser? _advertiser;
        private AdvertiseCallbackHandler? _advertiseCallback;
        private BluetoothDevice? _subscriber;

        private int _usablePayload = BleFragmenter.MinimumMtuPayload;
        private byte _messageId;
        private DateTime _lastInboundUtc = DateTime.MinValue;
        private volatile bool _hasSubscriber;
        private bool _disposed;

        public event EventHandler<PayloadReceivedEventArgs>? PayloadReceived;
        public event EventHandler? ConnectionClosed;

        /// <summary>Raised when a central subscribes, mirroring the Windows peripheral.</summary>
        public event EventHandler? ClientConnected;

        /// <summary>Raised once the peer has said which device it is.</summary>
        public event EventHandler<PeerIdentifiedEventArgs>? PeerIdentified;

        /// <summary>The peer has something Bluetooth cannot carry and is asking for Wi-Fi.</summary>
        public event EventHandler? WiFiRequested;

        /// <summary>
        /// True when a central has subscribed, or has written recently.
        ///
        /// The second case is what covers a peer whose subscription belongs to a previous
        /// instance of this service: its writes still arrive, and treating the link as dead
        /// while clipboard items visibly land is the failure that took longest to spot the
        /// first time round.
        /// </summary>
        public bool IsConnected =>
            _hasSubscriber || DateTime.UtcNow - _lastInboundUtc < BleProtocol.PeerTimeout;

        /// <summary>This device's base64 public key, announced over the link.</summary>
        public string? LocalPublicKey { get; set; }

        /// <summary>Friendly name announced alongside the key, so the peer has something to show.</summary>
        public string? LocalDeviceName { get; set; }

        /// <summary>What this device calls the mesh, so a peer with no name of its own can adopt it.</summary>
        public string? LocalMeshName { get; set; }

        /// <summary>
        /// What this phone's radio can do, announced rather than left for the peer to assume.
        /// This half is running, so it can certainly advertise.
        /// </summary>
        public BleCapability LocalCapability { get; set; } = BleCapability.Both;

        /// <summary>Name the peer announced, or null if it has not said.</summary>
        public string? RemoteDeviceName { get; private set; }

        /// <summary>
        /// Authorises a peer and agrees the key this link is encrypted with. Returning null
        /// drops the link.
        /// </summary>
        public Func<string, string, string, EphemeralKeyPair, PeerSession?>? OpenSession { get; set; }

        /// <summary>
        /// This connection's ephemeral keypair. Rolled whenever a central subscribes, so
        /// successive connections to this long-lived server do not share one - a peripheral
        /// outlives the links it serves, unlike a central, which is built per attempt.
        /// </summary>
        private EphemeralKeyPair _ephemeral = EphemeralKeyPair.Create();

        private PeerSession? _peer;

        /// <summary>The agreed key for the live link, or null before the peer's hello arrives.</summary>
        public PeerSession? Peer => Volatile.Read(ref _peer);

        private void RollEphemeral()
        {
            var fresh = EphemeralKeyPair.Create();
            Interlocked.Exchange(ref _ephemeral, fresh)?.Dispose();
            Interlocked.Exchange(ref _peer, null)?.Dispose();
        }

        public string? RemotePublicKey { get; private set; }

        public string RemoteFingerprint { get; private set; } = string.Empty;

        // ──────────────────────────────── starting

        /// <summary>Publishes the service and starts advertising it.</summary>
        public Task StartListeningAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            var context = global::Android.App.Application.Context;
            var manager = (BluetoothManager?)context.GetSystemService(Context.BluetoothService);
            var adapter = manager?.Adapter;

            if (manager == null || adapter == null || !adapter.IsEnabled)
            {
                Log.Write("BlePeripheral", "Bluetooth is off, cannot advertise.");
                return Task.CompletedTask;
            }

            // Null on hardware without peripheral support. Checked here as well as in the role
            // rule, because the rule works from a capability that was probed earlier and this
            // is the moment it actually has to be true.
            var advertiser = adapter.BluetoothLeAdvertiser;
            if (advertiser == null)
            {
                Log.Write("BlePeripheral", "This device cannot advertise; it can only be a central.");
                return Task.CompletedTask;
            }

            var server = manager.OpenGattServer(context, this);
            if (server == null)
            {
                Log.Write("BlePeripheral", "Could not open a GATT server.");
                return Task.CompletedTask;
            }

            var service = new BluetoothGattService(ServiceUuid, GattServiceType.Primary);

            // Written by the central. WriteNoResponse as well as Write, so a peer may choose
            // either - the Windows central uses the acknowledged form for flow control.
            var inbox = new BluetoothGattCharacteristic(
                InboxUuid,
                GattProperty.Write | GattProperty.WriteNoResponse,
                GattPermission.Write);

            // Notified to the central, with our own receipts on top. Indications would be
            // acknowledged by the ATT layer and look like the answer, but on this stack their
            // confirmations never arrived and the link was torn down with GATT status 19.
            var outbox = new BluetoothGattCharacteristic(
                OutboxUuid,
                GattProperty.Notify,
                GattPermission.Read);

            var config = new BluetoothGattDescriptor(
                ClientConfigDescriptorUuid,
                GattDescriptorPermission.Read | GattDescriptorPermission.Write);

            outbox.AddDescriptor(config);
            service.AddCharacteristic(inbox);
            service.AddCharacteristic(outbox);
            server.AddService(service);

            lock (_gate)
            {
                _server = server;
                _inbox = inbox;
                _outbox = outbox;
                _advertiser = advertiser;
            }

            lock (_gate) _settings = new AdvertiseSettings.Builder()!
                .SetAdvertiseMode(AdvertiseMode.LowLatency)!
                .SetTxPowerLevel(AdvertiseTx.PowerHigh)!
                .SetConnectable(true)!
                .Build();

            Advertise();
            return Task.CompletedTask;
        }

        private AdvertiseSettings? _settings;
        private byte[] _beacon = Array.Empty<byte>();

        /// <summary>
        /// The six-byte mesh beacon to publish beside the service UUID, or empty for none.
        ///
        /// <para>Setting it re-advertises, so a rotated epoch or a newly adopted mesh key reaches
        /// the air without waiting for a restart.</para>
        /// </summary>
        public byte[] Beacon
        {
            get { lock (_gate) return _beacon; }
            set
            {
                lock (_gate)
                {
                    if (_beacon.AsSpan().SequenceEqual(value ?? Array.Empty<byte>())) return;
                    _beacon = value ?? Array.Empty<byte>();
                }

                Advertise();
            }
        }

        /// <summary>
        /// Publishes the advertisement, with the beacon when there is one.
        ///
        /// <para><b>The budget is exact.</b> Flags take three bytes, the 128-bit service UUID
        /// eighteen, and the manufacturer-data section ten - which is the whole thirty-one a
        /// legacy advertisement carries. Including the device name as well overflows it and the
        /// whole advertisement is rejected, so the name is deliberately left out; a machine name
        /// readable by anyone in the room is also the leak the beacon exists to close.</para>
        /// </summary>
        private void Advertise()
        {
            BluetoothLeAdvertiser? advertiser;
            AdvertiseSettings? settings;
            byte[] beacon;
            AdvertiseCallbackHandler? previous;

            lock (_gate)
            {
                advertiser = _advertiser;
                settings = _settings;
                beacon = _beacon;
                previous = _advertiseCallback;
            }

            if (advertiser == null || settings == null) return;

            if (previous != null)
            {
                try { advertiser.StopAdvertising(previous); }
                catch (Exception ex) { Log.Write("BlePeripheral", "Could not stop the previous advertisement", ex); }
            }

            var builder = new AdvertiseData.Builder()!
                .SetIncludeDeviceName(false)!
                .AddServiceUuid(ServiceParcelUuid)!;

            if (beacon.Length > 0) builder = builder.AddManufacturerData(MeshBeacon.CompanyId, beacon)!;

            var callback = new AdvertiseCallbackHandler();
            lock (_gate) _advertiseCallback = callback;

            try
            {
                advertiser.StartAdvertising(settings, builder.Build(), callback);

                Log.Write("BlePeripheral", beacon.Length > 0
                    ? "Advertising the Mesh Sync service with this mesh's beacon."
                    : "Advertising the Mesh Sync service.");
            }
            catch (Exception ex)
            {
                // Declaring BLUETOOTH_ADVERTISE is not requesting it: it is a runtime grant on
                // Android 12+, and the advertiser throws a SecurityException naming the
                // permission. Swallowing that is how a phone silently never becomes findable.
                Log.Write("BlePeripheral", "Could not start advertising", ex);
            }
        }

        /// <summary>Reports advertising failures, which are otherwise completely silent.</summary>
        private sealed class AdvertiseCallbackHandler : AdvertiseCallback
        {
            public override void OnStartFailure(AdvertiseFailure errorCode)
            {
                base.OnStartFailure(errorCode);
                Log.Write("BlePeripheral", $"Advertising failed to start: {errorCode}");
            }

            public override void OnStartSuccess(AdvertiseSettings? settingsInEffect)
            {
                base.OnStartSuccess(settingsInEffect);
                Log.Write("BlePeripheral", "Advertising started.");
            }
        }

        public Task ConnectAsync(string deviceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This is the GATT server. The peer connects to it.");

        // ──────────────────────────────── server callbacks

        public override void OnConnectionStateChange(BluetoothDevice? device, ProfileState status, ProfileState newState)
        {
            base.OnConnectionStateChange(device, status, newState);

            if (newState == ProfileState.Connected)
            {
                Log.Write("BlePeripheral", $"A central connected ({device?.Address}).");
                lock (_gate) _subscriber = device;
            }
            else if (newState == ProfileState.Disconnected)
            {
                Log.Write("BlePeripheral", "The central disconnected.");
                Drop();
            }
        }

        public override void OnMtuChanged(BluetoothDevice? device, int mtu)
        {
            base.OnMtuChanged(device, mtu);

            _usablePayload = BleProtocol.UsablePayload(mtu);
            Log.Write("BlePeripheral", $"MTU {mtu}, usable payload {_usablePayload}.");
        }

        public override void OnDescriptorWriteRequest(BluetoothDevice? device, int requestId,
            BluetoothGattDescriptor? descriptor, bool preparedWrite, bool responseNeeded, int offset, byte[]? value)
        {
            base.OnDescriptorWriteRequest(device, requestId, descriptor, preparedWrite, responseNeeded, offset, value);

            bool isSubscription = descriptor?.Uuid?.Equals(ClientConfigDescriptorUuid) == true;

            Respond(device, requestId, responseNeeded, offset, value);

            if (!isSubscription || device == null) return;

            bool enabling = value != null && value.Length >= 2 && (value[0] != 0 || value[1] != 0);

            if (enabling)
            {
                // Said out loud when a second central arrives. This server holds one reassembler,
                // one ephemeral keypair and one session, so a second subscriber's writes land in
                // the same reassembler - a sequence gap from one discards the other's in-flight
                // message and neither peer is told. Until this half is genuinely per subscriber,
                // an honest log line beats silent corruption.
                lock (_gate)
                {
                    if (_subscriber != null && device.Address != null &&
                        !string.Equals(_subscriber.Address, device.Address, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Write("BlePeripheral",
                            "A second central subscribed and this server serves one at a time. " +
                            "The earlier link is being replaced.");
                    }

                    _subscriber = device;
                }
                _hasSubscriber = true;
                _lastInboundUtc = DateTime.UtcNow;

                // A fresh link means a fresh ephemeral, or every central this server ever
                // serves would share one and the forward secrecy would only be per process.
                RollEphemeral();

                Log.Write("BlePeripheral", "A central subscribed.");
                ClientConnected?.Invoke(this, EventArgs.Empty);

                // Announced without waiting to be asked, so the peer knows whose key to seal
                // for from the moment the link is usable.
                _ = SendHelloAsync();
            }
            else
            {
                Log.Write("BlePeripheral", "The central unsubscribed.");
                Drop();
            }
        }

        public override void OnCharacteristicWriteRequest(BluetoothDevice? device, int requestId,
            BluetoothGattCharacteristic? characteristic, bool preparedWrite, bool responseNeeded,
            int offset, byte[]? value)
        {
            base.OnCharacteristicWriteRequest(device, requestId, characteristic, preparedWrite, responseNeeded, offset, value);

            Respond(device, requestId, responseNeeded, offset, value);

            if (characteristic?.Uuid?.Equals(InboxUuid) != true || value == null) return;

            // Traffic arriving is proof of a live peer, whatever the subscription event did or
            // did not tell us.
            if (device != null) { lock (_gate) _subscriber = device; }

            HandleChunk(value);
        }

        /// <summary>
        /// Answers a write the peer asked to have acknowledged.
        ///
        /// A peer that asked for a response and never gets one blocks: Android permits a single
        /// outstanding write, so the next chunk is never sent and the transfer stalls rather
        /// than failing. Worth doing even when the request is one we go on to ignore.
        /// </summary>
        private void Respond(BluetoothDevice? device, int requestId, bool responseNeeded, int offset, byte[]? value)
        {
            if (!responseNeeded || device == null) return;

            try
            {
                lock (_gate)
                {
                    _server?.SendResponse(device, requestId, GattStatus.Success, offset, value ?? Array.Empty<byte>());
                }
            }
            catch (Exception ex)
            {
                Log.Write("BlePeripheral", "Responding to a write failed", ex);
            }
        }

        public override void OnNotificationSent(BluetoothDevice? device, GattStatus status)
        {
            base.OnNotificationSent(device, status);

            // Android permits one outstanding notification, so the sender waits for this.
            try { _notifySent.Release(); } catch (SemaphoreFullException) { }
        }

        // ──────────────────────────────── receiving

        private void HandleChunk(byte[] chunk)
        {
            try
            {
                _lastInboundUtc = DateTime.UtcNow;

                // Checked first, and before the receipt and the reassembler: an extended
                // control frame is marked by a leading zero, which is the one value a data
                // chunk's message id can never be.
                if (BleProtocol.TryParseExtended(chunk, out byte extendedKind, out byte[] extendedPayload))
                {
                    HandleExtended(extendedKind, extendedPayload);
                    return;
                }

                if (BleProtocol.TryParseControl(chunk, out byte controlKind))
                {
                    switch (controlKind)
                    {
                        case BleProtocol.ControlPing:
                            _ = SendControlAsync(BleProtocol.ControlPong);
                            break;

                        case BleProtocol.ControlWakeWiFi:
                            // Only from a peer that has identified itself. Control frames ride
                            // outside the encrypted path, so anything that knows the service
                            // UUID could otherwise make this phone raise Wi-Fi on demand.
                            if (RemoteFingerprint.Length == 0)
                            {
                                Log.Write("BlePeripheral", "Ignoring a Wi-Fi request from a peer that has not identified itself.");
                                break;
                            }

                            Log.Write("BlePeripheral", "The peer asked for Wi-Fi.");
                            WiFiRequested?.Invoke(this, EventArgs.Empty);
                            break;

                        default:
                            Log.Write("BlePeripheral", $"Ignoring unknown control kind 0x{controlKind:X2}.");
                            break;
                    }

                    return;
                }

                // A receipt for something we notified, not inbound data.
                if (BleProtocol.TryParseAck(chunk, out byte ackMessageId, out int ackSequence))
                {
                    NoteAck(ackMessageId, ackSequence);
                    return;
                }

                byte[]? payload = _reassembler.Accept(chunk);
                if (payload == null) return;

                Log.Write("BlePeripheral", $"Reassembled a {payload.Length} byte payload.");
                PayloadReceived?.Invoke(this, new PayloadReceivedEventArgs
                {
                    EncryptedPayload = payload,
                    Fingerprint = RemoteFingerprint
                });
            }
            catch (Exception ex)
            {
                Log.Write("BlePeripheral", "Handling a write failed", ex);
            }
        }

        private void HandleExtended(byte kind, byte[] payload)
        {
            if (kind != BleProtocol.ExtendedHello)
            {
                Log.Write("BlePeripheral", $"Ignoring unknown extended control kind 0x{kind:X2}.");
                return;
            }

            if (!BleProtocol.TryParseHelloPayload(payload, out string publicKey, out string peerName,
                                                  out string peerMesh, out string peerEphemeral,
                                                  out var peerCapability) ||
                !DeviceIdentity.IsValidPublicKey(publicKey))
            {
                Log.Write("BlePeripheral", "The peer announced something that is not a public key.");
                return;
            }

            if (peerName.Length > 0) RemoteDeviceName = peerName;

            var open = OpenSession;
            if (open != null)
            {
                var agreed = open(publicKey, peerName, peerEphemeral, _ephemeral);
                if (agreed == null)
                {
                    Log.Write("BlePeripheral", peerEphemeral.Length == 0
                        ? "Refusing a Bluetooth peer that offered no ephemeral key; it is running an older build."
                        : "Refusing a Bluetooth peer this device has not paired with.");
                    RemotePublicKey = null;
                    RemoteFingerprint = string.Empty;
                    Drop();
                    return;
                }

                Interlocked.Exchange(ref _peer, agreed)?.Dispose();
            }

            RemotePublicKey = publicKey;
            RemoteFingerprint = DeviceIdentity.FingerprintOf(publicKey);

            Log.Write("BlePeripheral", $"Peer identified as {DeviceIdentity.Shorten(RemoteFingerprint)}.");

            // Answered in kind, as the Windows server has always done.
            //
            // The hello sent when a central subscribes can be lost: the central's ATT exchange
            // lands some milliseconds *after* the subscription, so a peripheral that answers
            // immediately can put a 300-byte notification through a 23-byte MTU and have it
            // truncated. The central then holds a link it can never agree a session on, drops it
            // at the handshake grace, and cools this device down for five minutes - while this
            // side logs a peer identified perfectly happily.
            //
            // Sending again here costs one notification and closes the race, because by the time
            // a central's own hello arrives its MTU has certainly settled.
            _ = SendHelloAsync();

            try
            {
                PeerIdentified?.Invoke(this, new PeerIdentifiedEventArgs
                {
                    PublicKey = publicKey,
                    Fingerprint = RemoteFingerprint,
                    DeviceName = RemoteDeviceName ?? "",
                    MeshName = peerMesh,
                    Capability = peerCapability,
                });
            }
            catch (Exception ex) { Log.Write("BlePeripheral", "PeerIdentified handler threw", ex); }
        }

        // ──────────────────────────────── sending

        public async Task SendPayloadAsync(byte[] encryptedPayload, CancellationToken cancellationToken = default)
        {
            if (encryptedPayload == null) throw new ArgumentNullException(nameof(encryptedPayload));
            if (!IsConnected) throw new InvalidOperationException("No BLE subscriber to notify.");

            if (encryptedPayload.Length > BleProtocol.MaxPayloadBytes)
                throw new ArgumentException(
                    $"Payload of {encryptedPayload.Length} bytes is too large for BLE; use Wi-Fi for this.",
                    nameof(encryptedPayload));

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                byte messageId = BleProtocol.NextMessageId(ref _messageId);
                var chunks = BleFragmenter.Fragment(encryptedPayload, _usablePayload, messageId);

                for (int index = 0; index < chunks.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Armed before sending, so a fast receipt cannot arrive before we wait.
                    ArmAck(messageId, index);

                    await NotifyAsync(chunks[index], cancellationToken).ConfigureAwait(false);

                    // A single chunk needs no receipt: there is nothing behind it to overwrite it.
                    if (chunks.Count == 1) break;

                    if (!await WaitForAckAsync(cancellationToken).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException(
                            $"Chunk {index + 1} of {chunks.Count} was not acknowledged by the peer.");
                    }
                }

                Log.Write("BlePeripheral", $"Sent {encryptedPayload.Length} bytes as {chunks.Count} chunks of at most {_usablePayload}.");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// Notifies one chunk and waits for the stack to say it went out.
        ///
        /// Two acknowledgements are in play and they answer different questions: this one says
        /// the local radio has sent it, and the receipt written back says the peer has it. Only
        /// the second protects against a notification being overwritten in flight.
        /// </summary>
        private async Task NotifyAsync(byte[] frame, CancellationToken cancellationToken)
        {
            BluetoothGattServer? server;
            BluetoothGattCharacteristic? outbox;
            BluetoothDevice? device;

            lock (_gate)
            {
                server = _server;
                outbox = _outbox;
                device = _subscriber;
            }

            if (server == null || outbox == null || device == null)
                throw new InvalidOperationException("No BLE subscriber to notify.");

            // Drain any stale completion so the wait below matches this notification.
            while (_notifySent.CurrentCount > 0) await _notifySent.WaitAsync(0).ConfigureAwait(false);

            bool queued;
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                queued = server.NotifyCharacteristicChanged(device, outbox, false, frame) == (int)GattStatus.Success;
            }
            else
            {
#pragma warning disable CA1422 // Superseded on API 33+, still the only option below it.
                outbox.SetValue(frame);
                queued = server.NotifyCharacteristicChanged(device, outbox, false);
#pragma warning restore CA1422
            }

            if (!queued) throw new InvalidOperationException("The BLE stack refused the notification.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            // Fully qualified: Android.OS also defines OperationCanceledException.
            try { await _notifySent.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (System.OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("A BLE notification was not reported as sent.");
            }
        }

        private async Task SendControlAsync(byte kind)
        {
            // Never behind the send lock: a ping has to be answerable while a multi-chunk
            // payload is mid-flight, or the peer's heartbeat times out during every large
            // transfer and drops a link that is working perfectly.
            try
            {
                await NotifyAsync(BleProtocol.BuildControl(kind), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write("BlePeripheral", $"Could not send control frame 0x{kind:X2}", ex);
            }
        }

        /// <summary>Asks the peer to bring Wi-Fi up, for something Bluetooth cannot carry.</summary>
        public async Task<bool> RequestWiFiAsync()
        {
            if (!IsConnected) return false;

            Log.Write("BlePeripheral", "Asking the peer to raise Wi-Fi.");

            try
            {
                await NotifyAsync(BleProtocol.BuildControl(BleProtocol.ControlWakeWiFi), CancellationToken.None)
                    .ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("BlePeripheral", "Could not ask the peer for Wi-Fi", ex);
                return false;
            }
        }

        /// <summary>
        /// Waits for the MTU exchange to raise the usable payload, up to a second and a half.
        ///
        /// <para>A peer that genuinely only does 23 costs that once per connection and then works,
        /// fragmenting as it always did - it is only the hello, which cannot be fragmented, that
        /// needs the room.</para>
        /// </summary>
        private async Task WaitForMtuAsync(int needed)
        {
            for (int attempt = 0; attempt < 8 && _usablePayload < needed; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(180)).ConfigureAwait(false);
            }
        }

        private async Task SendHelloAsync()
        {
            string? key = LocalPublicKey;
            if (string.IsNullOrWhiteSpace(key)) return;

            var frame = BleProtocol.BuildExtended(BleProtocol.ExtendedHello,
                BleProtocol.BuildHelloPayload(key, LocalDeviceName, LocalMeshName, _ephemeral.PublicKey,
                                              LocalCapability));

            // Waited for, not assumed. The MTU exchange lands some milliseconds after the
            // subscription, and OnMtuChanged is what tells this server about it - so a hello sent
            // the instant a central subscribes is sized against the 23-byte ATT default and simply
            // refused, because it cannot be fragmented (an extended frame is marked by a leading
            // zero and a chunk starts with its message id, so the two shapes cannot be mixed).
            //
            // The central then holds a link it can never agree a session on, drops it at the
            // handshake grace and cools this device down for five minutes, while this side logs a
            // peer identified perfectly happily. Observed on an S21 FE against a laptop: "the
            // hello is 273 bytes and only 20 will fit", twice per connection, every time.
            //
            // The central half already learned this lesson - see LinuxBleLink.ReadSettledMtuAsync,
            // which waits for the same exchange from the other side.
            if (frame.Length > _usablePayload) await WaitForMtuAsync(frame.Length).ConfigureAwait(false);

            // Attempted even when it looks too big, because this side's idea of the size can be
            // wrong in the direction that matters. `OnMtuChanged` is the only thing that updates
            // it on a GATT *server*, and on this hardware it does not always fire at all - the
            // link was genuinely at 517 while this value sat at the 23-byte default, so refusing
            // here meant the peer never learned who we are and dropped us at its handshake grace.
            //
            // Guessing small and sending is recoverable: the notification either fits, or it does
            // not arrive and the link times out exactly as it did when we refused. Guessing small
            // and *not* sending is not recoverable at all.
            if (frame.Length > _usablePayload)
            {
                Log.Write("BlePeripheral",
                    $"The hello is {frame.Length} bytes and this side believes only {_usablePayload} will fit; " +
                    "sending it anyway, because that belief is only updated by an MTU callback that does not always arrive.");
            }

            try
            {
                await NotifyAsync(frame, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write("BlePeripheral", "Could not announce this device's identity", ex);
            }
        }

        // ──────────────────────────────── chunk receipts

        private readonly object _ackGate = new();
        private TaskCompletionSource<bool>? _pendingAck;
        private byte _awaitedMessageId;
        private int _awaitedSequence = -1;

        private void ArmAck(byte messageId, int sequence)
        {
            lock (_ackGate)
            {
                _awaitedMessageId = messageId;
                _awaitedSequence = sequence;
                _pendingAck = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private void NoteAck(byte messageId, int sequence)
        {
            lock (_ackGate)
            {
                if (_pendingAck == null) return;
                if (messageId != _awaitedMessageId || sequence != _awaitedSequence) return;

                _pendingAck.TrySetResult(true);
            }
        }

        private async Task<bool> WaitForAckAsync(CancellationToken cancellationToken)
        {
            Task<bool> pending;
            lock (_ackGate)
            {
                if (_pendingAck == null) return false;
                pending = _pendingAck.Task;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(BleProtocol.AckTimeout);

            var completed = await Task.WhenAny(pending, Task.Delay(Timeout.Infinite, timeout.Token))
                .ConfigureAwait(false);

            return completed == pending && pending.Result;
        }

        // ──────────────────────────────── lifetime

        private void Drop()
        {
            if (!_hasSubscriber) return;

            _hasSubscriber = false;
            _reassembler.Reset();

            lock (_gate) _subscriber = null;

            // Destroyed with the link, which is what makes what crossed it unrecoverable.
            try { Interlocked.Exchange(ref _peer, null)?.Dispose(); } catch { }

            try { ConnectionClosed?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { Log.Write("BlePeripheral", "ConnectionClosed handler threw", ex); }
        }

        public Task DisconnectAsync()
        {
            _hasSubscriber = false;
            _reassembler.Reset();

            BluetoothLeAdvertiser? advertiser;
            AdvertiseCallbackHandler? callback;

            lock (_gate)
            {
                advertiser = _advertiser;
                callback = _advertiseCallback;
                _advertiseCallback = null;
                _subscriber = null;
            }

            try
            {
                if (advertiser != null && callback != null)
                {
                    advertiser.StopAdvertising(callback);
                    callback.Dispose();
                }
            }
            catch (Exception ex) { Log.Write("BlePeripheral", "Stopping advertising failed", ex); }

            return Task.CompletedTask;
        }

        public new void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { DisconnectAsync().GetAwaiter().GetResult(); } catch { }

            BluetoothGattServer? server;
            lock (_gate)
            {
                server = _server;
                _server = null;
                _inbox = null;
                _outbox = null;
                _advertiser = null;
            }

            // Closing the server is what releases the published service. Leaving it registered
            // is the desktop side's orphaned-service failure, and there is no reason to invent
            // the same problem here.
            try { server?.Close(); } catch (Exception ex) { Log.Write("BlePeripheral", "Closing the GATT server failed", ex); }
            try { server?.Dispose(); } catch { }

            try { Interlocked.Exchange(ref _peer, null)?.Dispose(); } catch { }
            try { _ephemeral.Dispose(); } catch { }

            PayloadReceived = null;
            ConnectionClosed = null;
            ClientConnected = null;
            PeerIdentified = null;
            WiFiRequested = null;

            base.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AndroidBlePeripheral));
        }
    }
}
