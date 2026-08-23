using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using CoreLib.Diagnostics;
using CoreLib.Identity;
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

        /// <summary>Raised once the phone has said which device it is.</summary>
        public event EventHandler<PeerIdentifiedEventArgs>? PeerIdentified;

        /// <summary>
        /// The peer has something Bluetooth cannot carry and is asking for Wi-Fi.
        ///
        /// Either end can be the one holding an image now that roles are negotiated, so the
        /// request has to be answerable in both directions rather than only sent by this side.
        /// </summary>
        public event EventHandler? WiFiRequested;

        public bool IsConnected => HasLivePeer;

        /// <summary>
        /// This device's base64 public key, announced over the link so the phone knows which
        /// key to seal for. Bluetooth used to carry no identity at all, which is why the tier
        /// only worked when exactly one device was paired.
        /// </summary>
        public string? LocalPublicKey { get; set; }

        /// <summary>Friendly name announced alongside the key, so the peer has something to show.</summary>
        public string? LocalDeviceName { get; set; }

        /// <summary>What this device calls the mesh, so a peer with no name of its own can adopt it.</summary>
        public string? LocalMeshName { get; set; }

        /// <summary>
        /// What this machine's radio can do, announced rather than left for the peer to assume.
        /// This half is running, so it can certainly advertise.
        /// </summary>
        public BleCapability LocalCapability { get; set; } = BleCapability.Both;

        /// <summary>Name the peer announced, or null if it has not said.</summary>
        public string? RemoteDeviceName { get; private set; }

        /// <summary>
        /// Authorises a peer and agrees the key this link is encrypted with. Returning null
        /// drops what it sent; leaving it null accepts anyone and agrees nothing, which is what
        /// this tier used to do unconditionally.
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

        /// <summary>Public key the phone announced, or null if it has not yet.</summary>
        public string? RemotePublicKey { get; private set; }

        /// <summary>Fingerprint of <see cref="RemotePublicKey"/>, or empty.</summary>
        public string RemoteFingerprint { get; private set; } = string.Empty;

        /// <summary>
        /// Which subscribed central is which paired device, keyed by the session's device id.
        ///
        /// Needed because notifying the characteristic fans out to <em>every</em> subscriber,
        /// and each payload is sealed for one peer. With a single phone that was invisible;
        /// with two it would send each of them the other's traffic and collect a receipt from
        /// whichever answered first.
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _peerByDevice =
            new(StringComparer.OrdinalIgnoreCase);

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
                    // Notify, with our own receipts on top. Indicate would be acknowledged by
                    // the ATT layer, but on this stack the confirmations never arrived and
                    // Windows tore the link down with GATT status 19.
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

            // Said out loud, because this server holds one reassembler, one ephemeral keypair and
            // one session. A second central subscribing does not merely go unserved: its writes
            // land in the same reassembler, so a sequence gap from one discards the other's
            // in-flight message and neither peer is told. Until this half is genuinely per
            // subscriber, an honest log line beats silent corruption.
            //
            // It costs nothing in practice yet: capability-first arbitration makes a peer that
            // can advertise take the peripheral half, so two centrals choosing this machine at
            // once is the uncommon case rather than the normal one.
            if (count > 1)
            {
                Log.Write("BleServer",
                    $"{count} centrals are subscribed and this server serves one at a time. " +
                    "The others will not be answered until it drops.");
            }

            if (_hasSubscriber && !had)
            {
                // A fresh link means a fresh ephemeral, or every central this server ever
                // serves would share one and the forward secrecy would only be per process.
                RollEphemeral();

                HoldTheLink(sender);

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
        /// Asks Windows to keep the radio link up.
        ///
        /// <para><b>Why this is needed even though the peer is pinging.</b> Windows drops an idle
        /// BLE connection unless something has asked it not to. That was already known for the
        /// central role - <c>WindowsBleCentral</c> sets the same flag - but the server half never
        /// did, and a GATT server does not hold its own link either. The phone's heartbeat does
        /// not save it: those are ATT writes, and Windows tears the connection down anyway.</para>
        ///
        /// <para>Measured on hardware before this existed: the link died at almost exactly 30
        /// seconds, every time, reported to the phone as <c>status 19</c> - the peer terminated
        /// it. The phone reconnected within a second and the cycle repeated for as long as it
        /// was watched. Nothing was lost, because a reconnect is fast and the clipboard is
        /// retried, but "the standing link" was reconnecting a hundred and twenty times an hour
        /// and paying the radio cost of it.</para>
        /// </summary>
        private static void HoldTheLink(GattLocalCharacteristic outbox)
        {
            try
            {
                foreach (var client in outbox.SubscribedClients)
                {
                    var session = client.Session;
                    if (session == null) continue;

                    session.MaintainConnection = true;
                    Log.Write("BleServer", "Holding the Bluetooth link open.");
                }
            }
            catch (Exception ex)
            {
                // Not fatal: without it the link churns rather than fails, which is how it
                // behaved before this was added.
                Log.Write("BleServer", "Could not ask Windows to hold the link open", ex);
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

                // Clamped: Windows reports MTU minus the ATT header, which overshoots the
                // spec's 512-octet attribute ceiling and silently drops the notification.
                return smallest <= BleFragmenter.HeaderSize
                    ? BleFragmenter.MinimumMtuPayload
                    : Math.Min(smallest, BleProtocol.MaxAttributeValueBytes);
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

                // Traffic arriving is proof of a live peer, whatever the subscription event
                // did or did not tell us. Without this the window sat on "waiting for a
                // device" while clipboard items were visibly arriving.
                NotePeerAlive();

                // Which session this came in on, so a reply goes back to the device that asked
                // rather than to every subscriber.
                string? deviceId = null;
                try { deviceId = args.Session?.DeviceId?.Id; } catch { }

                // Checked first, and before the receipt and the reassembler: an extended
                // control frame is marked by a leading zero, which is the one value a data
                // chunk's message id can never be.
                if (BleProtocol.TryParseExtended(chunk, out byte extendedKind, out byte[] extendedPayload))
                {
                    await HandleExtendedAsync(extendedKind, extendedPayload, deviceId);
                    return;
                }

                if (BleProtocol.TryParseControl(chunk, out byte controlKind))
                {
                    switch (controlKind)
                    {
                        case BleProtocol.ControlPing:
                            await SendControlAsync(BleProtocol.ControlPong, ClientFor(deviceId));
                            break;

                        case BleProtocol.ControlWakeWiFi:
                            // A central can ask this side for Wi-Fi too, now that either end
                            // may be the one holding something Bluetooth cannot carry - but
                            // only one that has identified itself. Control frames ride outside
                            // the encrypted path, so anything that knows the service UUID could
                            // otherwise make this machine raise a network on demand.
                            if (RemoteFingerprint.Length == 0)
                            {
                                Log.Write("BleServer", "Ignoring a Wi-Fi request from a peer that has not identified itself.");
                                break;
                            }

                            Log.Write("BleServer", "The peer asked for Wi-Fi.");
                            WiFiRequested?.Invoke(this, EventArgs.Empty);
                            break;

                        default:
                            Log.Write("BleServer", $"Ignoring unknown control kind 0x{controlKind:X2}.");
                            break;
                    }

                    return;
                }

                // A receipt for something we sent, not inbound data.
                if (BleProtocol.TryParseAck(chunk, out byte ackMessageId, out int ackSequence))
                {
                    NoteAck(ackMessageId, ackSequence);
                    return;
                }

                byte[]? payload = _reassembler.Accept(chunk);
                if (payload == null) return;

                Log.Write("BleServer", $"Reassembled a {payload.Length} byte payload.");
                PayloadReceived?.Invoke(this, new PayloadReceivedEventArgs
                {
                    EncryptedPayload = payload,
                    Fingerprint = RemoteFingerprint
                });
            }
            catch (Exception ex)
            {
                Log.Write("BleServer", "Handling a write failed", ex);
            }
        }

        public Task SendPayloadAsync(byte[] encryptedPayload, CancellationToken cancellationToken = default) =>
            SendPayloadToAsync(null, encryptedPayload, cancellationToken);

        /// <summary>
        /// Sends to one paired device, or to the single subscriber when there is only one.
        ///
        /// Addressed rather than broadcast, because each payload is sealed with a key only its
        /// recipient holds. Notifying the characteristic reaches every subscriber, which with
        /// two phones would hand each of them traffic it cannot read and would let either one
        /// answer the receipt the sender is waiting on.
        /// </summary>
        public async Task SendPayloadToAsync(string? fingerprint, byte[] encryptedPayload,
                                             CancellationToken cancellationToken = default)
        {
            if (encryptedPayload == null) throw new ArgumentNullException(nameof(encryptedPayload));
            // Checks the live subscriber list rather than the cached flag: after a restart
            // the flag is false even though the phone is still subscribed at the OS level.
            if (_outbox == null || _outbox.SubscribedClients.Count == 0)
                throw new InvalidOperationException("No BLE subscriber to notify.");

            if (encryptedPayload.Length > BleProtocol.MaxPayloadBytes)
                throw new ArgumentException(
                    $"Payload of {encryptedPayload.Length} bytes is too large for BLE; use Wi-Fi for this.",
                    nameof(encryptedPayload));

            var target = SubscriberFor(fingerprint);

            int chunkSize = MaxNotificationSize();
            byte messageId;

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Never zero: that value marks an extended control frame, and wrapping through
                // it would make one clipboard item in every 256 parse as an identity exchange.
                messageId = BleProtocol.NextMessageId(ref _messageId);
                var chunks = BleFragmenter.Fragment(encryptedPayload, chunkSize, messageId);

                for (int index = 0; index < chunks.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Arm before sending, so a fast receipt cannot arrive before we wait.
                    ArmAck(messageId, index);

                    await NotifyAsync(chunks[index], target).ConfigureAwait(false);

                    // A single chunk needs no receipt: there is nothing behind it to overwrite it.
                    if (chunks.Count == 1) break;

                    if (!await WaitForAckAsync(cancellationToken).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException(
                            $"Chunk {index + 1} of {chunks.Count} was not acknowledged by the phone.");
                    }
                }

                Log.Write("BleServer", $"Sent {encryptedPayload.Length} bytes as {chunks.Count} chunks of at most {chunkSize}.");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        // ──────────────────────────────── liveness

        private DateTime _lastInboundUtc = DateTime.MinValue;

        /// <summary>
        /// True when a peer subscribed, or when one has written to us recently. The second
        /// case covers a restarted process: the phone's GATT link survives and it keeps
        /// writing, but it subscribed to the previous service instance so no subscription
        /// event ever arrives for this one.
        /// </summary>
        private bool HasLivePeer =>
            _hasSubscriber || DateTime.UtcNow - _lastInboundUtc < BleProtocol.PeerTimeout;

        private void NotePeerAlive()
        {
            bool wasConnected = IsConnected;
            _lastInboundUtc = DateTime.UtcNow;

            if (!wasConnected)
            {
                Log.Write("BleServer", "Peer traffic seen without a subscription event; treating the link as live.");
                ClientConnected?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Asks a peer to bring Wi-Fi up, for something Bluetooth cannot carry.
        ///
        /// It is a request, not a guarantee: the peer may be out of Wi-Fi range or have it
        /// switched off, so the caller has to be prepared for the socket never arriving.
        /// </summary>
        public async Task<bool> RequestWiFiAsync(string? fingerprint = null)
        {
            if (_outbox == null || _outbox.SubscribedClients.Count == 0) return false;

            Log.Write("BleServer", "Asking the peer to raise Wi-Fi.");
            return await SendControlAsync(BleProtocol.ControlWakeWiFi, SubscriberFor(fingerprint)).ConfigureAwait(false);
        }

        /// <summary>
        /// Handles a frame too long for the two-byte control shape.
        ///
        /// Only the identity exchange uses it so far. An unknown kind is ignored rather than
        /// treated as a fault, so a newer peer can add one without breaking this one.
        /// </summary>
        private async Task HandleExtendedAsync(byte kind, byte[] payload, string? deviceId)
        {
            if (kind != BleProtocol.ExtendedHello)
            {
                Log.Write("BleServer", $"Ignoring unknown extended control kind 0x{kind:X2}.");
                return;
            }

            if (!BleProtocol.TryParseHelloPayload(payload, out string publicKey, out string peerName,
                                                  out string peerMesh, out string peerEphemeral,
                                                  out var peerCapability) ||
                !DeviceIdentity.IsValidPublicKey(publicKey))
            {
                Log.Write("BleServer", "The peer announced something that is not a public key.");
                return;
            }

            if (peerName.Length > 0) RemoteDeviceName = peerName;

            var open = OpenSession;
            if (open != null)
            {
                var agreed = open(publicKey, peerName, peerEphemeral, _ephemeral);
                if (agreed == null)
                {
                    Log.Write("BleServer", peerEphemeral.Length == 0
                        ? "Refusing a Bluetooth peer that offered no ephemeral key; it is running an older build."
                        : "Refusing a Bluetooth peer this device has not paired with.");
                    RemotePublicKey = null;
                    RemoteFingerprint = string.Empty;
                    return;
                }

                Interlocked.Exchange(ref _peer, agreed)?.Dispose();
            }

            RemotePublicKey = publicKey;
            RemoteFingerprint = DeviceIdentity.FingerprintOf(publicKey);

            // Remembered against the session, so later payloads go to this device and not to
            // every subscriber.
            if (!string.IsNullOrWhiteSpace(deviceId)) _peerByDevice[deviceId!] = RemoteFingerprint;

            Log.Write("BleServer", $"Peer identified as {DeviceIdentity.Shorten(RemoteFingerprint)}.");

            // Answered in kind *before* anything else goes out.
            //
            // This ordering is load-bearing now and was not before. The session key is agreed
            // from both ephemeral keys, and ours only reaches the peer in this hello - so any
            // payload sent ahead of it arrives at a peer that cannot possibly open it. It was
            // harmless when the key came from the peer's identity alone and was known before the
            // connection existed; with forward secrecy it means the first thing this device says
            // after every reconnect is dropped. Observed on hardware doing exactly that, once
            // per reconnect, to the address announcement below.
            await SendHelloAsync(ClientFor(deviceId)).ConfigureAwait(false);

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
            catch (Exception ex) { Log.Write("BleServer", "PeerIdentified handler threw", ex); }
        }

        /// <summary>Announces this device's identity and this link's ephemeral key.</summary>
        private async Task SendHelloAsync(GattSubscribedClient? target = null)
        {
            string? key = LocalPublicKey;
            if (string.IsNullOrWhiteSpace(key) || _outbox == null) return;

            var frame = BleProtocol.BuildExtended(BleProtocol.ExtendedHello,
                BleProtocol.BuildHelloPayload(key, LocalDeviceName, LocalMeshName, _ephemeral.PublicKey,
                                              LocalCapability));

            // A hello is written whole rather than fragmented, because an extended control
            // frame is told apart by a leading zero and a fragment starts with its message id.
            // At a negotiated MTU there is ample room; saying so beats a frame that vanishes.
            int room = MaxNotificationSize();
            if (frame.Length > room)
            {
                Log.Write("BleServer",
                    $"The hello is {frame.Length} bytes and only {room} will fit - the peer will not learn this device's identity.");
                return;
            }

            try
            {
                await NotifyAsync(frame, target).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write("BleServer", "Could not announce this device's identity", ex);
            }
        }

        private async Task<bool> SendControlAsync(byte kind, GattSubscribedClient? target = null)
        {
            // Never behind the send lock: a ping has to be answerable while a multi-chunk
            // payload is mid-flight, or the phone's heartbeat times out during every large
            // transfer and drops a link that is working perfectly.
            try
            {
                await NotifyAsync(BleProtocol.BuildControl(kind), target).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("BleServer", $"Could not send control frame 0x{kind:X2}", ex);
                return false;
            }
        }

        /// <summary>
        /// Notifies one frame, to a specific subscriber when we know which, and to all of them
        /// when we do not - which is the case only until a peer has introduced itself.
        /// </summary>
        private async Task NotifyAsync(byte[] frame, GattSubscribedClient? target)
        {
            var outbox = _outbox;
            if (outbox == null) throw new InvalidOperationException("No BLE subscriber to notify.");

            using var writer = new DataWriter();
            writer.WriteBytes(frame);
            var buffer = writer.DetachBuffer();

            if (target != null) await outbox.NotifyValueAsync(buffer, target);
            else await outbox.NotifyValueAsync(buffer);
        }

        /// <summary>The subscribed central belonging to one paired device, if it is connected.</summary>
        private GattSubscribedClient? SubscriberFor(string? fingerprint)
        {
            if (string.IsNullOrWhiteSpace(fingerprint)) return null;

            try
            {
                var clients = _outbox?.SubscribedClients;
                if (clients == null) return null;

                foreach (var client in clients)
                {
                    string? deviceId = client.Session?.DeviceId?.Id;
                    if (deviceId == null) continue;

                    if (_peerByDevice.TryGetValue(deviceId, out var known) &&
                        string.Equals(known, fingerprint, StringComparison.OrdinalIgnoreCase))
                    {
                        return client;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("BleServer", "Could not resolve which subscriber to notify", ex);
            }

            return null;
        }

        private GattSubscribedClient? ClientFor(string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return null;

            try
            {
                var clients = _outbox?.SubscribedClients;
                if (clients == null) return null;

                foreach (var client in clients)
                {
                    if (string.Equals(client.Session?.DeviceId?.Id, deviceId, StringComparison.OrdinalIgnoreCase))
                        return client;
                }
            }
            catch { }

            return null;
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

        public Task ConnectAsync(string deviceId, CancellationToken cancellationToken = default) =>
            // This is the server half. Dialling out is what WindowsBleCentral is for, and which
            // of the two applies to a given peer is decided by BleRoleRules rather than by platform.
            throw new NotSupportedException("The Windows BLE transport is the GATT server. Use WindowsBleCentral to dial out.");

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

            try { Interlocked.Exchange(ref _peer, null)?.Dispose(); } catch { }
            try { _ephemeral.Dispose(); } catch { }

            PayloadReceived = null;
            ConnectionClosed = null;
            ClientConnected = null;
            PeerIdentified = null;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WindowsBleTransport));
        }
    }
}
