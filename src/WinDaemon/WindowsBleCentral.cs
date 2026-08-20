using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using CoreLib.Diagnostics;
using CoreLib.Identity;
using CoreLib.Transport;

namespace WinDaemon
{
    /// <summary>
    /// BLE GATT client. The half of Bluetooth this side never had.
    ///
    /// <para>Windows was the peripheral and the phone the central, fixed at compile time, which
    /// is why Bluetooth could only ever join a phone to a computer. This is the mirror image:
    /// scan for the Mesh Sync service, connect, subscribe to the peer's outbox and write to its
    /// inbox. Which of the two a device uses for a given peer is decided by
    /// <see cref="BleRoleRules"/> rather than by which platform it is running on.</para>
    ///
    /// <para>The wire protocol needs no changes at all: it was already written from the
    /// central's point of view - the client writes chunks, the server notifies them - so a
    /// Windows central talks to an Android peripheral using exactly the frames an Android
    /// central sends to a Windows peripheral.</para>
    /// </summary>
    public sealed class WindowsBleCentral : ITransportConnection
    {
        private readonly BleReassembler _reassembler = new(BleProtocol.MaxPayloadBytes);
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly object _gate = new();

        private BluetoothLEAdvertisementWatcher? _watcher;
        private BluetoothLEDevice? _device;
        private GattSession? _session;
        private GattCharacteristic? _inbox;
        private GattCharacteristic? _outbox;
        private TaskCompletionSource<ulong>? _found;
        private TaskCompletionSource<bool>? _pong;

        private int _usablePayload = BleFragmenter.MinimumMtuPayload;
        private byte _messageId;
        private CancellationTokenSource? _heartbeatCts;
        private DateTime _lastInboundUtc = DateTime.MinValue;
        private volatile bool _ready;
        private bool _disposed;

        public event EventHandler<PayloadReceivedEventArgs>? PayloadReceived;
        public event EventHandler? ConnectionClosed;

        /// <summary>Raised once the peer has said which device it is.</summary>
        public event EventHandler<PeerIdentifiedEventArgs>? PeerIdentified;

        /// <summary>The peer has something Bluetooth cannot carry and is asking for Wi-Fi.</summary>
        public event EventHandler? WiFiRequested;

        public bool IsConnected => _ready;

        /// <summary>This device's base64 public key, announced over the link.</summary>
        public string? LocalPublicKey { get; set; }

        /// <summary>Friendly name announced alongside the key, so the peer has something to show.</summary>
        public string? LocalDeviceName { get; set; }

        /// <summary>What this device calls the mesh, so a peer with no name of its own can adopt it.</summary>
        public string? LocalMeshName { get; set; }

        /// <summary>Name the peer announced, or null if it has not said.</summary>
        public string? RemoteDeviceName { get; private set; }

        /// <summary>
        /// Authorises a peer and agrees the key this link is encrypted with. Returning null
        /// drops the link.
        /// </summary>
        public Func<string, string, string, EphemeralKeyPair, PeerSession?>? OpenSession { get; set; }

        /// <summary>
        /// This link's ephemeral keypair. One per instance is correct here, unlike the server
        /// half, because a central is built fresh for every connection attempt.
        /// </summary>
        private readonly EphemeralKeyPair _ephemeral = EphemeralKeyPair.Create();

        private PeerSession? _peer;

        /// <summary>The agreed key for the live link, or null before the peer's hello arrives.</summary>
        public PeerSession? Peer => Volatile.Read(ref _peer);

        public string? RemotePublicKey { get; private set; }

        public string RemoteFingerprint { get; private set; } = string.Empty;

        // ──────────────────────────────── finding a peer

        /// <summary>
        /// Scans until a device advertising the Mesh Sync service turns up.
        ///
        /// Matching on the service rather than on a Bluetooth address is what lets pairing
        /// carry a public key and nothing else - the same reason the phone's scanner does it
        /// that way.
        /// </summary>
        public async Task<ulong?> FindPeerAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            var found = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _found = found;

            var watcher = new BluetoothLEAdvertisementWatcher
            {
                // Active, so the scan response is collected too. A service UUID that does not
                // fit in the advertisement itself is carried there instead.
                ScanningMode = BluetoothLEScanningMode.Active
            };

            watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(BleProtocol.ServiceUuid);
            watcher.Received += OnAdvertisementReceived;

            lock (_gate) _watcher = watcher;

            try
            {
                watcher.Start();
                Log.Write("BleCentral", "Scanning for the Mesh Sync service.");

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linked.CancelAfter(timeout);
                using var registration = linked.Token.Register(() => found.TrySetResult(0));

                ulong address = await found.Task.ConfigureAwait(false);
                return address == 0 ? null : address;
            }
            catch (Exception ex)
            {
                Log.Write("BleCentral", "Scanning failed", ex);
                return null;
            }
            finally
            {
                try
                {
                    watcher.Received -= OnAdvertisementReceived;
                    watcher.Stop();
                }
                catch { }

                lock (_gate)
                {
                    _watcher = null;
                    _found = null;
                }
            }
        }

        private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender,
                                             BluetoothLEAdvertisementReceivedEventArgs args)
        {
            TaskCompletionSource<ulong>? found;
            lock (_gate) found = _found;

            found?.TrySetResult(args.BluetoothAddress);
        }

        // ──────────────────────────────── connecting

        /// <summary>
        /// Connects to a peer found by <see cref="FindPeerAsync"/>. The id is the Bluetooth
        /// address in hex, matching what the scanner reports.
        /// </summary>
        public async Task ConnectAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (!ulong.TryParse(deviceId, System.Globalization.NumberStyles.HexNumber, null, out ulong address))
                throw new ArgumentException($"'{deviceId}' is not a Bluetooth address.", nameof(deviceId));

            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                ?? throw new InvalidOperationException($"No Bluetooth device at {deviceId}.");

            lock (_gate) _device = device;

            device.ConnectionStatusChanged += OnConnectionStatusChanged;

            // Windows drops an idle BLE connection unless it is told to hold it. This is what
            // makes Bluetooth a standing link on this side rather than something that lapses
            // between clipboard items.
            var session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
            if (session != null)
            {
                session.MaintainConnection = true;
                lock (_gate) _session = session;

                _usablePayload = BleProtocol.UsablePayload((int)session.MaxPduSize);
                session.MaxPduSizeChanged += OnMaxPduSizeChanged;
            }

            var services = await device.GetGattServicesForUuidAsync(BleProtocol.ServiceUuid, BluetoothCacheMode.Uncached);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
                throw new InvalidOperationException($"The peer does not expose the Mesh Sync service ({services.Status}).");

            var service = services.Services[0];

            var inboxes = await service.GetCharacteristicsForUuidAsync(BleProtocol.InboxCharacteristicUuid, BluetoothCacheMode.Uncached);
            var outboxes = await service.GetCharacteristicsForUuidAsync(BleProtocol.OutboxCharacteristicUuid, BluetoothCacheMode.Uncached);

            if (inboxes.Status != GattCommunicationStatus.Success || inboxes.Characteristics.Count == 0 ||
                outboxes.Status != GattCommunicationStatus.Success || outboxes.Characteristics.Count == 0)
            {
                throw new InvalidOperationException("The Mesh Sync service is missing a characteristic.");
            }

            var inbox = inboxes.Characteristics[0];
            var outbox = outboxes.Characteristics[0];

            outbox.ValueChanged += OnValueChanged;

            var subscribed = await outbox.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);

            if (subscribed != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"Could not subscribe to the peer's outbox ({subscribed}).");

            lock (_gate)
            {
                _inbox = inbox;
                _outbox = outbox;
            }

            _ready = true;
            _lastInboundUtc = DateTime.UtcNow;

            Log.Write("BleCentral", $"Link ready. Usable payload {_usablePayload} bytes per write.");

            // Subscribing successfully proves nothing on its own - the same lesson the phone
            // learned. A service published by a process that has since exited still accepts a
            // subscription, and both ends then believe they are talking while nothing crosses.
            if (!await ExchangeGreetingAsync(cancellationToken).ConfigureAwait(false))
            {
                _ready = false;
                throw new InvalidOperationException(
                    "The peer accepted the connection but did not answer; its Bluetooth service is stale.");
            }

            Log.Write("BleCentral", "Peer answered - link confirmed.");

            _heartbeatCts = new CancellationTokenSource();
            _ = HeartbeatLoopAsync(_heartbeatCts.Token);

            try { await SendHelloAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Write("BleCentral", "Could not announce this device's identity", ex); }
        }

        private void OnMaxPduSizeChanged(GattSession sender, object args)
        {
            _usablePayload = BleProtocol.UsablePayload((int)sender.MaxPduSize);
            Log.Write("BleCentral", $"MTU changed, usable payload now {_usablePayload}.");
        }

        private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected) return;

            Log.Write("BleCentral", "Peer disconnected.");
            Drop();
        }

        // ──────────────────────────────── receiving

        private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            byte[] chunk;
            try { chunk = args.CharacteristicValue.ToArray(); }
            catch { return; }

            HandleChunk(chunk);
        }

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
                        case BleProtocol.ControlPong:
                            _pong?.TrySetResult(true);
                            break;

                        case BleProtocol.ControlWakeWiFi:
                            // Only from a peer that has identified itself. Control frames ride
                            // outside the encrypted path, so a service that merely advertises
                            // the right UUID could otherwise make this machine dial out.
                            if (RemoteFingerprint.Length == 0)
                            {
                                Log.Write("BleCentral", "Ignoring a Wi-Fi request from a peer that has not identified itself.");
                                break;
                            }

                            Log.Write("BleCentral", "The peer asked for Wi-Fi.");
                            WiFiRequested?.Invoke(this, EventArgs.Empty);
                            break;

                        default:
                            Log.Write("BleCentral", $"Ignoring unknown control kind 0x{controlKind:X2}.");
                            break;
                    }

                    return;
                }

                // Tell the peer this one landed so it can release the next. Without it its
                // second notification overwrites the first before it is transmitted.
                if (chunk.Length >= BleFragmenter.HeaderSize)
                {
                    byte messageId = chunk[0];
                    int sequence = chunk[1] | (chunk[2] << 8);
                    _ = SendAckAsync(messageId, sequence);
                }

                byte[]? payload = _reassembler.Accept(chunk);
                if (payload == null) return;

                Log.Write("BleCentral", $"Reassembled a {payload.Length} byte payload.");
                PayloadReceived?.Invoke(this, new PayloadReceivedEventArgs
                {
                    EncryptedPayload = payload,
                    Fingerprint = RemoteFingerprint
                });
            }
            catch (Exception ex)
            {
                Log.Write("BleCentral", "Handling a notification failed", ex);
            }
        }

        private void HandleExtended(byte kind, byte[] payload)
        {
            if (kind != BleProtocol.ExtendedHello)
            {
                Log.Write("BleCentral", $"Ignoring unknown extended control kind 0x{kind:X2}.");
                return;
            }

            if (!BleProtocol.TryParseHelloPayload(payload, out string publicKey, out string peerName,
                                                  out string peerMesh, out string peerEphemeral) ||
                !DeviceIdentity.IsValidPublicKey(publicKey))
            {
                Log.Write("BleCentral", "The peer announced something that is not a public key.");
                return;
            }

            if (peerName.Length > 0) RemoteDeviceName = peerName;

            var open = OpenSession;
            if (open != null)
            {
                var agreed = open(publicKey, peerName, peerEphemeral, _ephemeral);
                if (agreed == null)
                {
                    Log.Write("BleCentral", peerEphemeral.Length == 0
                        ? "The peer offered no ephemeral key; it is running an older build - dropping the link."
                        : "The peer is not a device this one has paired with - dropping the link.");
                    Drop();
                    return;
                }

                Interlocked.Exchange(ref _peer, agreed)?.Dispose();
            }

            RemotePublicKey = publicKey;
            RemoteFingerprint = DeviceIdentity.FingerprintOf(publicKey);

            Log.Write("BleCentral", $"Peer identified as {DeviceIdentity.Shorten(RemoteFingerprint)}.");

            try
            {
                PeerIdentified?.Invoke(this, new PeerIdentifiedEventArgs
                {
                    PublicKey = publicKey,
                    Fingerprint = RemoteFingerprint,
                    DeviceName = RemoteDeviceName ?? "",
                    MeshName = peerMesh
                });
            }
            catch (Exception ex) { Log.Write("BleCentral", "PeerIdentified handler threw", ex); }
        }

        // ──────────────────────────────── sending

        public async Task SendPayloadAsync(byte[] encryptedPayload, CancellationToken cancellationToken = default)
        {
            if (encryptedPayload == null) throw new ArgumentNullException(nameof(encryptedPayload));
            if (!_ready) throw new InvalidOperationException("The BLE link is not ready.");

            if (encryptedPayload.Length > BleProtocol.MaxPayloadBytes)
                throw new ArgumentException(
                    $"Payload of {encryptedPayload.Length} bytes is too large for BLE; use Wi-Fi for this.",
                    nameof(encryptedPayload));

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                byte messageId = BleProtocol.NextMessageId(ref _messageId);
                var chunks = BleFragmenter.Fragment(encryptedPayload, _usablePayload, messageId);

                foreach (var chunk in chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await WriteAsync(chunk).ConfigureAwait(false);
                }

                Log.Write("BleCentral", $"Sent {encryptedPayload.Length} bytes as {chunks.Count} chunks of at most {_usablePayload}.");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// Writes one frame to the peer's inbox.
        ///
        /// With response, so awaiting it is the flow control: the peer has processed the write
        /// before the next one goes out. The phone has to wait for a callback to get the same
        /// guarantee, because Android permits one outstanding write at a time.
        /// </summary>
        private async Task WriteAsync(byte[] frame)
        {
            GattCharacteristic? inbox;
            lock (_gate) inbox = _inbox;

            if (inbox == null) throw new InvalidOperationException("The BLE link is not ready.");

            using var writer = new DataWriter();
            writer.WriteBytes(frame);

            var status = await inbox.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse);
            if (status != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"A BLE write failed ({status}).");
        }

        private async Task SendControlAsync(byte kind)
        {
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try { await WriteAsync(BleProtocol.BuildControl(kind)).ConfigureAwait(false); }
            finally { _sendLock.Release(); }
        }

        /// <summary>Asks the peer to bring Wi-Fi up, for something Bluetooth cannot carry.</summary>
        public async Task<bool> RequestWiFiAsync()
        {
            if (!_ready) return false;

            try
            {
                Log.Write("BleCentral", "Asking the peer to raise Wi-Fi.");
                await SendControlAsync(BleProtocol.ControlWakeWiFi).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("BleCentral", "Could not ask the peer for Wi-Fi", ex);
                return false;
            }
        }

        private async Task SendHelloAsync()
        {
            string? key = LocalPublicKey;
            if (string.IsNullOrWhiteSpace(key)) return;

            var frame = BleProtocol.BuildExtended(BleProtocol.ExtendedHello,
                BleProtocol.BuildHelloPayload(key, LocalDeviceName, LocalMeshName, _ephemeral.PublicKey));

            // Written whole rather than fragmented - see BuildHelloPayload for why the two
            // shapes cannot be mixed - so an oversized one is reported rather than silently lost.
            if (frame.Length > _usablePayload)
            {
                Log.Write("BleCentral",
                    $"The hello is {frame.Length} bytes and only {_usablePayload} will fit - the peer will not learn this device's identity.");
                return;
            }

            await _sendLock.WaitAsync().ConfigureAwait(false);
            try { await WriteAsync(frame).ConfigureAwait(false); }
            finally { _sendLock.Release(); }
        }

        private async Task SendAckAsync(byte messageId, int sequence)
        {
            try
            {
                await _sendLock.WaitAsync().ConfigureAwait(false);
                try { await WriteAsync(BleProtocol.BuildAck(messageId, sequence)).ConfigureAwait(false); }
                finally { _sendLock.Release(); }
            }
            catch (Exception ex)
            {
                Log.Write("BleCentral", $"Could not acknowledge chunk {sequence}", ex);
            }
        }

        // ──────────────────────────────── liveness

        private async Task<bool> ExchangeGreetingAsync(CancellationToken cancellationToken)
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                _pong = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                try { await SendControlAsync(BleProtocol.ControlPing).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    Log.Write("BleCentral", $"Greeting attempt {attempt} could not be written", ex);
                    continue;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));

                var finished = await Task.WhenAny(
                    _pong.Task,
                    Task.Delay(Timeout.Infinite, timeout.Token)).ConfigureAwait(false);

                if (finished == _pong.Task) return true;

                Log.Write("BleCentral", $"Greeting attempt {attempt} went unanswered.");
            }

            return false;
        }

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
                        Log.Write("BleCentral",
                            $"No answer from the peer for {BleProtocol.PeerTimeout.TotalSeconds:F0}s - dropping the link.");
                        Drop();
                        return;
                    }

                    try { await SendControlAsync(BleProtocol.ControlPing).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        Log.Write("BleCentral", "Heartbeat write failed - dropping the link", ex);
                        Drop();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { /* expected on teardown */ }
        }

        // ──────────────────────────────── lifetime

        /// <summary>Marks the link dead and tells the owner once.</summary>
        private void Drop()
        {
            if (!_ready) return;

            _ready = false;
            _reassembler.Reset();

            // Destroyed with the link, which is what makes what crossed it unrecoverable.
            try { Interlocked.Exchange(ref _peer, null)?.Dispose(); } catch { }

            try { ConnectionClosed?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { Log.Write("BleCentral", "ConnectionClosed handler threw", ex); }
        }

        public Task DisconnectAsync()
        {
            _ready = false;

            try { _heartbeatCts?.Cancel(); } catch { }
            _heartbeatCts = null;
            _reassembler.Reset();

            BluetoothLEDevice? device;
            GattSession? session;
            GattCharacteristic? outbox;

            lock (_gate)
            {
                device = _device;
                session = _session;
                outbox = _outbox;
                _device = null;
                _session = null;
                _inbox = null;
                _outbox = null;
            }

            if (outbox != null) outbox.ValueChanged -= OnValueChanged;

            if (session != null)
            {
                session.MaxPduSizeChanged -= OnMaxPduSizeChanged;
                // Releasing the request is what actually lets the radio drop the connection.
                try { session.MaintainConnection = false; } catch { }
                try { session.Dispose(); } catch { }
            }

            if (device != null)
            {
                device.ConnectionStatusChanged -= OnConnectionStatusChanged;
                try { device.Dispose(); } catch { }
            }

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { DisconnectAsync().GetAwaiter().GetResult(); } catch { }

            try
            {
                BluetoothLEAdvertisementWatcher? watcher;
                lock (_gate) watcher = _watcher;

                if (watcher != null)
                {
                    watcher.Received -= OnAdvertisementReceived;
                    watcher.Stop();
                }
            }
            catch { }

            try { Interlocked.Exchange(ref _peer, null)?.Dispose(); } catch { }
            try { _ephemeral.Dispose(); } catch { }

            PayloadReceived = null;
            ConnectionClosed = null;
            PeerIdentified = null;
            WiFiRequested = null;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WindowsBleCentral));
        }
    }
}
