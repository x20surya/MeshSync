using System.Diagnostics;
using CoreLib.Diagnostics;
using CoreLib.Identity;
using CoreLib.Transport;
using CoreLib.Transport.Fabric;
using Tmds.DBus.Protocol;

namespace DesktopCore.Bluetooth;

/// <summary>
/// One GATT link to one peer, over BlueZ, wearing the route interface.
///
/// <para><b>What moved and what did not.</b> Every line that talks to BlueZ - resolving the
/// characteristics, waiting for the MTU to settle, the framing, the hello, the heartbeat, the
/// serialised write - is the code that was proven against a phone on 2026-08-23, unchanged in
/// substance. What moved out is ownership: scanning, the cooldowns and the decision to connect
/// belonged to the same class and now belong to <see cref="LinuxBleRadio"/> and the shared
/// scheduler, so this file is one link and nothing else.</para>
///
/// <para><b>The grace is no longer this file's problem.</b> It used to drop a peer that never
/// agreed a session from inside its own heartbeat. The state machine does that now, identically
/// on all three platforms, which is the point of having one.</para>
/// </summary>
public sealed class LinuxBleLink : IPeerRoute
{
    private readonly BlueZ _bluez;
    private readonly ILinkClock _clock;
    private readonly BleReassembler _reassembler = new(BleProtocol.MaxPayloadBytes);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private readonly object _gate = new();

    private string? _inboxPath;
    private string? _outboxPath;
    private int _usablePayload = BleFragmenter.MinimumMtuPayload;
    private byte _messageCounter;
    private EphemeralKeyPair? _ephemeral;
    private PeerSession? _peer;
    private DateTime _lastHeardUtc;
    private DateTime _lastPingUtc;
    private RouteState _state;
    private string? _lastFailure;
    private int _closed;

    internal LinuxBleLink(BlueZ bluez, string devicePath, string? alias, ILinkClock clock)
    {
        _bluez = bluez;
        DevicePath = devicePath;
        RemoteDeviceName = alias;
        _clock = clock;

        _state = RouteState.Connecting;
        StateSinceUtc = clock.UtcNow;
        _lastHeardUtc = clock.UtcNow;
    }

    /// <summary>The BlueZ object path this link runs over. Also the radio's key for it.</summary>
    internal string DevicePath { get; }

    internal string? OutboxPath { get { lock (_gate) return _outboxPath; } }

    public RouteKind Kind => RouteKind.BleCentral;

    public string PeerFingerprint { get; private set; } = string.Empty;

    public RouteState State { get { lock (_gate) return _state; } }

    public DateTime StateSinceUtc { get; private set; }

    public PeerSession? Session => Volatile.Read(ref _peer);

    public string? LastFailure { get { lock (_gate) return _lastFailure; } }

    public DateTime RetryAtUtc { get; private set; }

    /// <summary>This device scanned and connected out, so the link is always outbound.</summary>
    public bool IsOutbound => true;

    public int MaxPayloadBytes => BleProtocol.MaxPayloadBytes;

    /// <summary>
    /// A radio link is proof the peer is nearby, which is the one thing a socket can never say.
    /// </summary>
    public bool CarriesPresence => true;

    public string? RemoteDeviceName { get; private set; }

    /// <summary>Announced in the hello, so the peer knows whose key to seal for.</summary>
    public string? LocalPublicKey { get; set; }

    public string? LocalDeviceName { get; set; }

    public string? LocalMeshName { get; set; }

    /// <summary>What this machine's radio can actually do, announced rather than assumed.</summary>
    public BleCapability LocalCapability { get; set; } = BleCapability.Central;

    /// <summary>Authorises the peer and agrees this link's key. Returning null drops the link.</summary>
    public Func<string, string, string, EphemeralKeyPair, PeerSession?>? OpenSession { get; set; }

    public event Action<IPeerRoute, RouteState, RouteState>? StateChanged;
    public event Action<IPeerRoute, RoutePayload>? PayloadReceived;

    /// <summary>The peer asked for Wi-Fi, for something this link cannot carry.</summary>
    public event Action<LinuxBleLink>? WiFiRequested;

    /// <summary>Raised with the peer's hello, so the registry can note its name and capability.</summary>
    public event Action<LinuxBleLink, PeerIdentifiedEventArgs>? Identified;

    /// <summary>
    /// Called with this link's outbox path the moment it is known, and <b>before</b> notifications
    /// are switched on.
    ///
    /// <para>BlueZ publishes one property-change stream for the whole bus, so the radio has to map
    /// a changed characteristic back to the link that owns it. Registering that mapping after the
    /// subscription is set up looks equivalent and is not: the peer answers a subscription
    /// immediately, and a peripheral sends its hello the instant a central subscribes. Anything
    /// arriving before the mapping exists is dropped on the floor.</para>
    ///
    /// <para>That window used to be the whole MTU-settling loop - up to a second and a half - so
    /// the peer's hello was reliably lost, no session was ever agreed, and the link was dropped at
    /// the handshake grace and the peer cooled down for five minutes. It presented as "Bluetooth
    /// does not work" with both ends logging a healthy link.</para>
    /// </summary>
    internal Action<LinuxBleLink, string>? Registered { get; set; }

    // ──────────────────────────────── bringing it up

    /// <summary>
    /// Finds the two characteristics, subscribes, and announces this device.
    ///
    /// <para>BlueZ publishes the GATT tree only once it has resolved services, which is not
    /// immediate after <c>Connect</c> returns - hence the retry rather than a single read.</para>
    /// </summary>
    internal async Task<bool> ResolveAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 20 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            var objects = await _bluez.GetObjectsAsync().ConfigureAwait(false);

            var service = objects.FirstOrDefault(o =>
                o.Path.StartsWith(DevicePath, StringComparison.Ordinal) &&
                o.Has(BlueZ.ServiceInterface) &&
                string.Equals(o.String(BlueZ.ServiceInterface, "UUID"),
                              BleProtocol.ServiceUuid.ToString("D"), StringComparison.OrdinalIgnoreCase));

            if (service != null)
            {
                string? inbox = FindCharacteristic(objects, service.Path, BleProtocol.InboxCharacteristicUuid);
                string? outbox = FindCharacteristic(objects, service.Path, BleProtocol.OutboxCharacteristicUuid);

                if (inbox != null && outbox != null)
                {
                    lock (_gate)
                    {
                        _inboxPath = inbox;
                        _outboxPath = outbox;
                    }

                    // Before subscribing, never after: the peer sends its hello the moment the
                    // subscription lands, and nothing can route it here until this mapping exists.
                    try { Registered?.Invoke(this, outbox); }
                    catch (Exception ex) { Log.Write("BleLink", "Registering the link failed", ex); }

                    await SubscribeAsync().ConfigureAwait(false);

                    // The GATT link is open; the session is not. Handshaking is exactly that
                    // state, and it is the one the shared deadline watches.
                    _lastHeardUtc = _clock.UtcNow;
                    Move(RouteState.Handshaking);

                    await SendHelloAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HeartbeatLoopAsync(_life.Token), CancellationToken.None);
                    return true;
                }
            }

            try { await Task.Delay(500, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }

        Fail("that device never published the mesh service");
        return false;
    }

    private static string? FindCharacteristic(List<BlueZObject> objects, string servicePath, Guid uuid) =>
        objects.FirstOrDefault(o =>
            o.Path.StartsWith(servicePath, StringComparison.Ordinal) &&
            o.Has(BlueZ.CharacteristicInterface) &&
            string.Equals(o.String(BlueZ.CharacteristicInterface, "UUID"),
                          uuid.ToString("D"), StringComparison.OrdinalIgnoreCase))?.Path;

    private async Task SubscribeAsync()
    {
        await _bluez.CallAsync(_outboxPath!, BlueZ.CharacteristicInterface, "StartNotify").ConfigureAwait(false);

        try
        {
            int negotiated = await ReadSettledMtuAsync().ConfigureAwait(false);

            _usablePayload = Math.Max(BleFragmenter.MinimumMtuPayload, BleProtocol.UsablePayload(negotiated));
            Log.Write("BleLink", $"Negotiated MTU {negotiated}; {_usablePayload} bytes per chunk.");
        }
        catch
        {
            _usablePayload = BleFragmenter.MinimumMtuPayload;
        }
    }

    /// <summary>The ATT default, which is what BlueZ reports before an exchange has happened.</summary>
    private const int DefaultAttMtu = 23;

    /// <summary>
    /// The link's MTU, once it has actually been negotiated.
    ///
    /// <para><b>Why this is a loop and not a read.</b> BlueZ publishes the characteristic's MTU as
    /// a property, and the ATT exchange that raises it above the 23-byte default lands some
    /// milliseconds after the subscription. Reading once wins that race on a slow peer and loses
    /// it on a fast one - and losing it is not a slow link but a broken one, because the hello is
    /// written in a single attribute write and a 20-byte window truncates it. The peer then
    /// reports "the peer announced something that is not a public key" while its own log says MTU
    /// 517, which is about as misleading as a pair of logs can be.</para>
    /// </summary>
    private async Task<int> ReadSettledMtuAsync()
    {
        int negotiated = DefaultAttMtu;

        for (int attempt = 0; attempt < 8; attempt++)
        {
            var mtu = await _bluez.GetPropertyAsync(_inboxPath!, BlueZ.CharacteristicInterface, "MTU")
                .ConfigureAwait(false);

            negotiated = (int)mtu.GetUInt16();
            if (negotiated > DefaultAttMtu) return negotiated;

            await Task.Delay(TimeSpan.FromMilliseconds(180)).ConfigureAwait(false);
        }

        // Said out loud, because a link that works and carries twenty bytes at a time looks like a
        // slow peer rather than an exchange that never happened.
        Log.Write("BleLink", "The MTU never rose above the 23-byte default; this link will be slow.");
        return negotiated;
    }

    // ──────────────────────────────── frames

    /// <summary>A notification arrived on this link's outbox.</summary>
    internal void OnValue(byte[] frame)
    {
        _lastHeardUtc = _clock.UtcNow;

        try
        {
            // Told apart by length alone, which is the whole Bluetooth framing rule: two bytes is
            // control, four is a receipt, five or more is a data chunk, and a leading zero marks
            // the one extended frame there is.
            if (BleProtocol.TryParseControl(frame, out byte control))
            {
                if (control == BleProtocol.ControlPing)
                {
                    _ = SendRawAsync(BleProtocol.BuildControl(BleProtocol.ControlPong));
                }
                else if (control == BleProtocol.ControlWakeWiFi && Session != null)
                {
                    // Only from a peer that has identified itself. Control frames ride outside the
                    // encrypted path, so anything that knew the service UUID could otherwise make
                    // this machine raise Wi-Fi at will.
                    try { WiFiRequested?.Invoke(this); }
                    catch (Exception ex) { Log.Write("BleLink", "A WiFiRequested handler threw", ex); }
                }

                return;
            }

            if (BleProtocol.TryParseExtended(frame, out byte kind, out byte[] payload))
            {
                if (kind == BleProtocol.ExtendedHello) HandleHello(payload);
                return;
            }

            if (BleProtocol.TryParseAck(frame, out _, out _)) return;

            if (frame.Length < BleFragmenter.HeaderSize) return;

            byte[]? whole = _reassembler.Accept(frame);
            if (whole == null) return;

            var session = Session;
            if (session == null)
            {
                Log.Write("BleLink", "Dropped a payload that arrived before a key was agreed.");
                return;
            }

            if (!session.TryDecrypt(whole, out var decrypted))
            {
                Log.Write("BleLink", "Dropped a payload that does not authenticate under this link's key.");
                return;
            }

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
            // A dropped or malformed frame must never take the link down with it.
            Log.Write("BleLink", "Handling a Bluetooth frame failed", ex);
        }
    }

    /// <summary>The peer went away at the BlueZ level.</summary>
    internal void OnDisconnected()
    {
        Log.Write("BleLink", "The peer disconnected.");
        Fail("the peer disconnected");
    }

    private void HandleHello(byte[] payload)
    {
        if (!BleProtocol.TryParseHelloPayload(payload, out string publicKey, out string deviceName,
                                              out string meshName, out string ephemeralKey,
                                              out var capability))
        {
            Log.Write("BleLink", "A hello arrived that could not be parsed.");
            return;
        }

        var ephemeral = _ephemeral;
        var open = OpenSession;
        if (ephemeral == null || open == null) return;

        var session = open(publicKey, deviceName, ephemeralKey, ephemeral);
        if (session == null)
        {
            // Not one of ours. Closing is the whole answer: the scheduler remembers the refusal
            // against the address, the identity and the advertised name, and the state machine
            // would have closed this at the grace anyway.
            Log.Write("BleLink", $"{deviceName} is not in this mesh.");
            Fail("not a paired device");
            return;
        }

        // A second hello on one link would otherwise leak the first key. There is no legitimate
        // reason for one, so the replacement is logged rather than silent, and the key it replaces
        // is disposed - which is what makes the traffic under it unrecoverable.
        var previous = Interlocked.Exchange(ref _peer, session);
        if (previous != null)
        {
            Log.Write("BleLink", "A second hello arrived on one link; the earlier session key was discarded.");
            previous.Dispose();
        }

        if (!string.IsNullOrWhiteSpace(deviceName)) RemoteDeviceName = deviceName;
        PeerFingerprint = session.Fingerprint;
        _lastHeardUtc = _clock.UtcNow;

        Log.Write("BleLink",
            $"Radio link up to \"{RemoteDeviceName}\" ({DeviceIdentity.Shorten(PeerFingerprint)}).");

        try
        {
            Identified?.Invoke(this, new PeerIdentifiedEventArgs
            {
                DeviceName = RemoteDeviceName ?? "",
                PublicKey = publicKey,
                Fingerprint = PeerFingerprint,
                MeshName = meshName,
                Capability = capability,
            });
        }
        catch (Exception ex) { Log.Write("BleLink", "An Identified handler threw", ex); }

        Move(RouteState.Established);
    }

    // ──────────────────────────────── liveness

    /// <summary>
    /// Pings on the protocol's cadence, and drops a link that stops answering.
    ///
    /// <para>The grace for a peer that never identifies itself is <em>not</em> here any more: the
    /// shared state machine enforces it, identically on every platform, which is what stopped two
    /// of the three heads from letting a refused device hold a link for as long as it stayed in
    /// range.</para>
    /// </summary>
    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

                if (State is RouteState.Idle or RouteState.Backoff or RouteState.Draining) return;

                if (_clock.UtcNow - _lastHeardUtc > BleProtocol.PeerTimeout)
                {
                    Log.Write("BleLink", "The peer stopped answering; dropping the link.");
                    Fail("the peer stopped answering");
                    return;
                }

                if (_clock.UtcNow - _lastPingUtc < BleProtocol.HeartbeatInterval) continue;
                _lastPingUtc = _clock.UtcNow;

                await SendRawAsync(BleProtocol.BuildControl(BleProtocol.ControlPing)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* expected on teardown */ }
        catch (Exception ex) { Log.Write("BleLink", "The heartbeat loop failed", ex); }
    }

    // ──────────────────────────────── sending

    private async Task SendHelloAsync()
    {
        if (LocalPublicKey == null) return;

        _ephemeral?.Dispose();
        _ephemeral = EphemeralKeyPair.Create();

        byte[] frame = BleProtocol.BuildExtended(BleProtocol.ExtendedHello,
            BleProtocol.BuildHelloPayload(LocalPublicKey, LocalDeviceName, LocalMeshName,
                                          _ephemeral.PublicKey, LocalCapability));

        // Written whole rather than fragmented: an extended frame is marked by a leading zero and
        // a fragmented chunk starts with its message id, so the two shapes cannot be mixed.
        if (frame.Length > _usablePayload)
        {
            Log.Write("BleLink",
                $"The hello is {frame.Length} bytes and only {_usablePayload} will fit - the peer will not learn this device's identity.");
            return;
        }

        await SendRawAsync(frame).ConfigureAwait(false);
        Log.Write("BleLink", "Announced this device over Bluetooth.");
    }

    /// <summary>Asks the peer to raise Wi-Fi, for something this link cannot carry.</summary>
    public async Task<bool> RequestWiFiAsync()
    {
        if (State != RouteState.Established) return false;

        await SendRawAsync(BleProtocol.BuildControl(BleProtocol.ControlWakeWiFi)).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> SendAsync(byte contentType, byte[] body, CancellationToken cancellationToken = default)
    {
        if (State != RouteState.Established) return false;

        var session = Session;
        if (session == null) return false;

        byte[]? payload = session.Encrypt(contentType, body);
        if (payload == null) return false;

        if (payload.Length > MaxPayloadBytes)
        {
            Log.Write("BleLink", $"{payload.Length} bytes is over the Bluetooth ceiling; Wi-Fi is needed.");
            return false;
        }

        try
        {
            byte messageId = BleProtocol.NextMessageId(ref _messageCounter);

            foreach (byte[] chunk in BleFragmenter.Fragment(payload, _usablePayload, messageId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SendRawAsync(chunk).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Write("BleLink", "Sending over Bluetooth failed", ex);
            return false;
        }
    }

    /// <summary>One GATT write. Serialised, because two at once on one characteristic is a stall.</summary>
    private async Task SendRawAsync(byte[] frame)
    {
        string? inbox;
        lock (_gate) inbox = _inboxPath;
        if (inbox == null) return;

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _bluez.CallAsync(inbox, BlueZ.CharacteristicInterface, "WriteValue", "aya{sv}", (ref MessageWriter writer) =>
            {
                writer.WriteArray(frame);
                var options = writer.WriteArrayStart(DBusType.DictEntry);
                writer.WriteArrayEnd(options);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Write("BleLink", "A Bluetooth write failed", ex);
            Fail("a write failed");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // ──────────────────────────────── teardown

    public Task CloseAsync(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return Task.CompletedTask;

        lock (_gate) _lastFailure ??= reason;
        Move(RouteState.Draining);

        try { _life.Cancel(); } catch { }

        // Disconnected rather than merely forgotten: a link left open at the BlueZ level goes on
        // holding the radio, and a device that is still connected is picked first on the next scan.
        _ = Task.Run(async () =>
        {
            try { await _bluez.CallAsync(DevicePath, BlueZ.DeviceInterface, "Disconnect").ConfigureAwait(false); }
            catch { /* Already gone is the usual reason to be here. */ }
        });

        // Disposing the key is what makes the traffic that crossed this link unrecoverable.
        Interlocked.Exchange(ref _peer, null)?.Dispose();

        lock (_gate)
        {
            _inboxPath = null;
            _outboxPath = null;
        }

        Move(RouteState.Idle);
        return Task.CompletedTask;
    }

    private void Fail(string reason)
    {
        lock (_gate) _lastFailure = reason;
        try { _life.Cancel(); } catch { }
        Move(RouteState.Backoff);
    }

    private void Move(RouteState to)
    {
        RouteState from;
        lock (_gate)
        {
            from = _state;
            if (from == to) return;

            // Idle is terminal: a disposed link does not come back, and a resurrection would
            // leave the owning PeerLink holding something dead.
            if (from == RouteState.Idle) return;

            _state = to;
            StateSinceUtc = _clock.UtcNow;
        }

        try { StateChanged?.Invoke(this, from, to); }
        catch (Exception ex) { Log.Write("BleLink", "A StateChanged handler threw", ex); }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync("this device is shutting down").ConfigureAwait(false);

        try { _ephemeral?.Dispose(); } catch { }
        try { _life.Dispose(); } catch { }
        try { _writeGate.Dispose(); } catch { }

        StateChanged = null;
        PayloadReceived = null;
        WiFiRequested = null;
        Identified = null;
    }

    [Conditional("DEBUG")]
    internal void AssertNotShared() => Debug.Assert(_reassembler != null,
        "A reassembler is one instance per link, never shared. Two peers writing into one discards each other's messages.");
}
