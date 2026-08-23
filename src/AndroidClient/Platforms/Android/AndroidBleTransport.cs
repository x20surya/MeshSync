using System;
using System.Threading;
using System.Threading.Tasks;
using Android.Bluetooth;
using Android.Content;
using Android.OS;
using CoreLib.Diagnostics;
using CoreLib.Transport;
using CoreLib.Transport.Fabric;
using Java.Util;

namespace AndroidClient.Platforms.Android
{
    /// <summary>
    /// BLE GATT client: the half where this phone scans for a peer and connects out to it.
    ///
    /// <para>Paired with <see cref="AndroidBlePeripheral"/>, which is the half where a peer
    /// connects here instead. Which one applies to a given device is decided by
    /// <see cref="BleRoleRules"/> rather than by platform - it used to be fixed, the phone
    /// always central and the computer always peripheral, which is why Bluetooth could join a
    /// phone to a computer and nothing else.</para>
    ///
    /// Payloads are fragmented over the negotiated MTU by <see cref="BleFragmenter"/>. The
    /// previous version wrote a whole payload in a single SetValue call, which silently
    /// truncated at the MTU - its own comment said chunking would be needed.
    /// </summary>
    public sealed class AndroidBleTransport : BluetoothGattCallback, IPeerRoute
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

        private readonly ILinkClock _clock;
        private RouteState _state;
        private string? _lastFailure;
        private int _closed;

        public AndroidBleTransport(ILinkClock? clock = null)
        {
            // Qualified: Android.OS has a SystemClock of its own and this project's implicit
            // usings pull it in, exactly as WinForms does with LinkState on the other head.
            _clock = clock ?? CoreLib.Transport.Fabric.SystemClock.Instance;
            _state = RouteState.Connecting;
            StateSinceUtc = _clock.UtcNow;
        }

        public event Action<IPeerRoute, RouteState, RouteState>? StateChanged;
        public event Action<IPeerRoute, RoutePayload>? PayloadReceived;

        public RouteKind Kind => RouteKind.BleCentral;

        /// <summary>This phone scanned and connected out, so the link is always outbound.</summary>
        public bool IsOutbound => true;

        public RouteState State => _state;

        public DateTime StateSinceUtc { get; private set; }

        public string PeerFingerprint => RemoteFingerprint;

        public CoreLib.Identity.PeerSession? Session => Peer;

        public string? LastFailure => _lastFailure;

        public DateTime RetryAtUtc { get; private set; }

        public int MaxPayloadBytes => BleProtocol.MaxPayloadBytes;

        /// <summary>A radio link is proof the peer is nearby, which a socket can never say.</summary>
        public bool CarriesPresence => true;

        /// <summary>What this phone's radio can do, announced rather than assumed.</summary>
        public BleCapability LocalCapability { get; set; } = BleCapability.Central;

        /// <summary>
        /// The computer has something Bluetooth cannot carry and is asking for Wi-Fi.
        ///
        /// It cannot dial us - the phone is the client on both tiers - so this is the only
        /// way an image copied on the computer reaches the phone while Bluetooth is the
        /// standing link.
        /// </summary>
        public event Action<AndroidBleTransport>? WiFiRequested;

        /// <summary>Raised with the peer's hello, so the registry can note its name and capability.</summary>
        public event Action<AndroidBleTransport, PeerIdentifiedEventArgs>? Identified;

        /// <summary>
        /// This device's base64 public key, announced over the link so the computer knows
        /// which key to seal for and whether to allow us at all.
        ///
        /// Bluetooth carried no identity exchange, which meant the tier only worked when
        /// exactly one device was paired - there was no other way to know whose key to use.
        /// </summary>
        public string? LocalPublicKey { get; set; }

        /// <summary>Friendly name announced alongside the key, so the peer has something to show.</summary>
        public string? LocalDeviceName { get; set; }

        /// <summary>What this device calls the mesh, so a peer with no name of its own can adopt it.</summary>
        public string? LocalMeshName { get; set; }

        /// <summary>Name the peer announced, or null if it has not said.</summary>
        public string? RemoteDeviceName { get; private set; }

        /// <summary>Public key the computer announced, or null if it has not yet.</summary>
        public string? RemotePublicKey { get; private set; }

        /// <summary>Fingerprint of <see cref="RemotePublicKey"/>, or empty.</summary>
        public string RemoteFingerprint { get; private set; } = string.Empty;

        /// <summary>
        /// Authorises the peer and agrees the key this link is encrypted with. Returning null
        /// drops the link.
        /// </summary>
        public Func<string, string, string, CoreLib.Identity.EphemeralKeyPair, CoreLib.Identity.PeerSession?>? OpenSession { get; set; }

        /// <summary>
        /// This link's ephemeral keypair. One per instance is correct here: the reconnect loop
        /// builds a fresh transport for every attempt, so the object's life is the link's life.
        /// </summary>
        private readonly CoreLib.Identity.EphemeralKeyPair _ephemeral = CoreLib.Identity.EphemeralKeyPair.Create();

        private CoreLib.Identity.PeerSession? _peer;

        /// <summary>The agreed key for the live link, or null before the peer's hello arrives.</summary>
        public CoreLib.Identity.PeerSession? Peer => Volatile.Read(ref _peer);

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

            // The GATT link is open; the session is not. Handshaking is exactly that state, and
            // it is the one the shared deadline watches - which is what stops a device that
            // answers pings and never identifies itself from holding this phone's standing link.
            Move(RouteState.Handshaking);

            Log.Write("BleClient", $"Link ready. Usable payload {_usablePayload} bytes per write.");

            // Ready is not the same as usable. Android reports the subscription write as
            // successful even when it lands on a service published by a process that has
            // since exited, so both ends believe they are talking and nothing crosses.
            // Requiring an answer before declaring the transport connected turns that
            // silent half-open state into an ordinary failed attempt the loop can retry.
            if (!await ExchangeGreetingAsync(cancellationToken).ConfigureAwait(false))
            {
                _ready = false;
                Fail("the peer accepted the connection and did not answer; its service is stale");
                throw new InvalidOperationException(
                    "The computer accepted the connection but did not answer; its Bluetooth service is stale.");
            }

            Log.Write("BleClient", "Computer answered - link confirmed.");

            // Sent once the link is proven, so the computer knows whose key to seal for. Not
            // fatal if it fails: with a single paired device both ends can still infer each
            // other, which is how this tier worked before it could say.
            try { await SendHelloAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Write("BleClient", "Could not announce this device's identity", ex); }
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
                _readySignal?.TrySetException(new InvalidOperationException($"BLE disconnected: {status}"));
                Fail($"the peer disconnected ({status})");
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

                // Checked first, and before the receipt and the reassembler: an extended
                // control frame is marked by a leading zero, which is the one value a data
                // chunk's message id can never be.
                if (BleProtocol.TryParseExtended(chunk, out byte extendedKind, out byte[] extendedPayload))
                {
                    HandleExtended(extendedKind, extendedPayload);
                    return;
                }

                // Control, not data. The kind used to be discarded, which was harmless while
                // a pong was the only thing the computer ever sent - it is not any more.
                if (BleProtocol.TryParseControl(chunk, out byte controlKind))
                {
                    switch (controlKind)
                    {
                        case BleProtocol.ControlPong:
                            _pong?.TrySetResult(true);
                            break;

                        case BleProtocol.ControlWakeWiFi:
                            // Only from a peer that has identified itself. Control frames ride
                            // outside the encrypted path, so a service that merely advertises
                            // the right UUID could otherwise make this phone raise Wi-Fi at
                            // will, which is a battery attack that needs no key at all.
                            if (RemoteFingerprint.Length == 0)
                            {
                                Log.Write("BleClient", "Ignoring a Wi-Fi request from a peer that has not identified itself.");
                                break;
                            }

                            Log.Write("BleClient", "The computer asked for Wi-Fi.");
                            WiFiRequested?.Invoke(this);
                            break;

                        default:
                            // Tolerated, not fatal: a newer peer may send kinds we predate.
                            Log.Write("BleClient", $"Ignoring unknown control kind 0x{controlKind:X2}.");
                            break;
                    }

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

                var session = Peer;
                if (session == null)
                {
                    Log.Write("BleClient", "Dropped a payload that arrived before a key was agreed.");
                    return;
                }

                if (!session.TryDecrypt(payload, out var decrypted))
                {
                    Log.Write("BleClient", "Dropped a payload that does not authenticate under this link's key.");
                    return;
                }

                Log.Write("BleClient", $"Reassembled a {payload.Length} byte payload.");
                PayloadReceived?.Invoke(this, new RoutePayload
                {
                    Peer = decrypted.Peer,
                    ContentType = decrypted.ContentType,
                    Body = decrypted.Body,
                    Via = RouteKind.BleCentral,
                });
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
                            $"No answer from the peer for {BleProtocol.PeerTimeout.TotalSeconds:F0}s - dropping the link.");
                        Fail("the peer stopped answering");
                        return;
                    }

                    try { await SendControlAsync(BleProtocol.ControlPing).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        Log.Write("BleClient", "Heartbeat write failed - dropping the link", ex);
                        Fail("a heartbeat write failed");
                        return;
                    }
                }
            }
            catch (System.OperationCanceledException) { /* expected on teardown */ }
        }

        /// <summary>
        /// Handles a frame too long for the two-byte control shape. Only the identity exchange
        /// uses it so far; an unknown kind is ignored rather than treated as a fault, so a
        /// newer peer can add one without breaking this one.
        /// </summary>
        private void HandleExtended(byte kind, byte[] payload)
        {
            if (kind != BleProtocol.ExtendedHello)
            {
                Log.Write("BleClient", $"Ignoring unknown extended control kind 0x{kind:X2}.");
                return;
            }

            if (!BleProtocol.TryParseHelloPayload(payload, out string publicKey, out string peerName,
                                                  out string peerMesh, out string peerEphemeral,
                                                  out var peerCapability) ||
                !CoreLib.Identity.DeviceIdentity.IsValidPublicKey(publicKey))
            {
                Log.Write("BleClient", "The peer announced something that is not a public key.");
                return;
            }

            if (peerName.Length > 0) RemoteDeviceName = peerName;

            var open = OpenSession;
            if (open != null)
            {
                var agreed = open(publicKey, peerName, peerEphemeral, _ephemeral);
                if (agreed == null)
                {
                    // Dropped, not merely logged. This returning and leaving the link up is the
                    // whole of the defect this refactor is named after: _ready stayed true, so
                    // BleConnected stayed true, so the Bluetooth loop parked on a semaphore with
                    // no timeout and WiFiWanted() concluded Wi-Fi was unnecessary. A phone with
                    // no working link to anything, indefinitely, reporting "Connected over
                    // Bluetooth". The state machine now makes that unrepresentable, and this
                    // closes it here as well so the grace does not have to.
                    Log.Write("BleClient", peerEphemeral.Length == 0
                        ? "The computer offered no ephemeral key; it is running an older build."
                        : "The computer is not a device this phone has paired with.");

                    RemotePublicKey = null;
                    RemoteFingerprint = string.Empty;
                    Fail("not a paired device");
                    return;
                }

                Interlocked.Exchange(ref _peer, agreed)?.Dispose();
            }

            RemotePublicKey = publicKey;
            RemoteFingerprint = CoreLib.Identity.DeviceIdentity.FingerprintOf(publicKey);

            Log.Write("BleClient", $"Computer identified as {CoreLib.Identity.DeviceIdentity.Shorten(RemoteFingerprint)}.");

            try
            {
                Identified?.Invoke(this, new PeerIdentifiedEventArgs
                {
                    PublicKey = publicKey,
                    Fingerprint = RemoteFingerprint,
                    DeviceName = RemoteDeviceName ?? "",
                    MeshName = peerMesh,
                    Capability = peerCapability,
                });
            }
            catch (Exception ex) { Log.Write("BleClient", "An Identified handler threw", ex); }

            Move(RouteState.Established);
        }

        /// <summary>Announces this device's public key over the link.</summary>
        private async Task SendHelloAsync()
        {
            string? key = LocalPublicKey;
            if (string.IsNullOrWhiteSpace(key)) return;

            var gatt = _gatt;
            var inbox = _inbox;
            if (gatt == null || inbox == null) return;

            var frame = BleProtocol.BuildExtended(BleProtocol.ExtendedHello,
                BleProtocol.BuildHelloPayload(key, LocalDeviceName, LocalMeshName, _ephemeral.PublicKey,
                                              LocalCapability));

            // Written whole rather than fragmented - see BuildHelloPayload for why the two
            // shapes cannot be mixed - so an oversized one is reported rather than silently lost.
            if (frame.Length > _usablePayload)
            {
                Log.Write("BleClient",
                    $"The hello is {frame.Length} bytes and only {_usablePayload} will fit - the peer will not learn this device's identity.");
                return;
            }

            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await WriteChunkAsync(gatt, inbox, frame, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
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

        public async Task<bool> SendAsync(byte contentType, byte[] body, CancellationToken cancellationToken = default)
        {
            if (State != RouteState.Established) return false;

            var session = Peer;
            if (session == null) return false;

            var gatt = _gatt;
            var inbox = _inbox;
            if (gatt == null || inbox == null || !_ready) return false;

            byte[]? encryptedPayload = session.Encrypt(contentType, body);
            if (encryptedPayload == null) return false;

            if (encryptedPayload.Length > BleProtocol.MaxPayloadBytes)
            {
                Log.Write("BleClient", $"{encryptedPayload.Length} bytes is over the Bluetooth ceiling; Wi-Fi is needed.");
                return false;
            }

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Never zero: that value marks an extended control frame, and wrapping through
                // it would make one clipboard item in every 256 parse as an identity exchange.
                byte messageId = BleProtocol.NextMessageId(ref _messageId);
                var chunks = BleFragmenter.Fragment(encryptedPayload, _usablePayload, messageId);

                foreach (var chunk in chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await WriteChunkAsync(gatt, inbox, chunk, cancellationToken).ConfigureAwait(false);
                }

                Log.Write("BleClient", $"Sent {encryptedPayload.Length} bytes as {chunks.Count} chunks of at most {_usablePayload}.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("BleClient", "Sending over Bluetooth failed", ex);
                return false;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>Asks the peer to raise Wi-Fi, for something this link cannot carry.</summary>
        public async Task<bool> RequestWiFiAsync()
        {
            if (State != RouteState.Established) return false;

            try
            {
                await SendControlAsync(BleProtocol.ControlWakeWiFi).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("BleClient", "Could not ask the peer for Wi-Fi", ex);
                return false;
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

        /// <summary>Marks the link failed and tells the owner once, through the state machine.</summary>
        private void Fail(string reason)
        {
            _lastFailure = reason;
            _ready = false;
            _reassembler.Reset();

            // Destroyed with the link, which is what makes what crossed it unrecoverable.
            try { Interlocked.Exchange(ref _peer, null)?.Dispose(); } catch { }

            Move(RouteState.Backoff);
        }

        private void Move(RouteState to)
        {
            var from = _state;
            if (from == to) return;

            // Idle is terminal: a disposed link does not come back, and a resurrection would
            // leave the owning PeerLink holding something dead.
            if (from == RouteState.Idle) return;

            _state = to;
            StateSinceUtc = _clock.UtcNow;

            try { StateChanged?.Invoke(this, from, to); }
            catch (Exception ex) { Log.Write("BleClient", "A StateChanged handler threw", ex); }
        }

        public async Task CloseAsync(string reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;

            _lastFailure ??= reason;
            Move(RouteState.Draining);

            await TeardownAsync().ConfigureAwait(false);

            Move(RouteState.Idle);
        }

        private async Task TeardownAsync()
        {
            _ready = false;
            try { _heartbeatCts?.Cancel(); } catch { }
            _heartbeatCts = null;
            _reassembler.Reset();

            try { Interlocked.Exchange(ref _peer, null)?.Dispose(); } catch { }

            var gatt = _gatt;
            _gatt = null;
            _inbox = null;

            if (gatt == null) return;

            try
            {
                gatt.Disconnect();

                // Close() immediately after Disconnect() leaves the underlying link up for a
                // moment. Reconnecting into that half-torn-down link is what let the phone
                // reattach to a connection the peer's newly published service was never bound
                // to: both ends reported success and nothing crossed. Letting the disconnect
                // land first forces a genuinely new connection.
                await Task.Delay(TimeSpan.FromMilliseconds(600)).ConfigureAwait(false);

                gatt.Close();
                await Task.Delay(TimeSpan.FromMilliseconds(400)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write("BleClient", "Closing the GATT client failed", ex);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            await CloseAsync("this device is shutting down").ConfigureAwait(false);

            try { _ephemeral.Dispose(); } catch { }

            StateChanged = null;
            PayloadReceived = null;
            WiFiRequested = null;
            Identified = null;

            base.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AndroidBleTransport));
        }
    }
}
