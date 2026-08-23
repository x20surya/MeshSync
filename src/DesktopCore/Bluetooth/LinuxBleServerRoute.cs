using CoreLib.Diagnostics;
using CoreLib.Identity;
using CoreLib.Transport;
using CoreLib.Transport.Fabric;

namespace DesktopCore.Bluetooth;

/// <summary>
/// The link a peer opened to this machine's advertised service, wearing the route interface.
///
/// <para>Thin, because <see cref="LinuxBleServer"/> already holds the link. What this adds is the
/// state machine: a link that has not agreed a session sits in
/// <see cref="RouteState.Handshaking"/> and is closed on the shared deadline, rather than being
/// reported as connected the moment a central subscribes.</para>
///
/// <para><b>It is not reached on any Linux machine today.</b> BlueZ rejects the exported GATT tree,
/// so <c>LinuxBlePeripheral</c> registers, fails and stands aside - and capability-first
/// arbitration then correctly makes this device always the central. That is a supported
/// arrangement, not a missing half, and this exists so the day the tree is accepted the fabric
/// needs no further change.</para>
/// </summary>
public sealed class LinuxBleServerRoute : IPeerRoute
{
    private readonly LinuxBleServer _server;
    private readonly ILinkClock _clock;
    private readonly object _gate = new();

    private RouteState _state;
    private string? _lastFailure;
    private int _closed;

    public LinuxBleServerRoute(LinuxBleServer server, ILinkClock? clock = null)
    {
        _server = server;
        _clock = clock ?? SystemClock.Instance;
        _state = RouteState.Handshaking;
        StateSinceUtc = _clock.UtcNow;

        _server.PeerIdentified += OnIdentified;
        _server.PayloadReceived += OnPayload;
        _server.ConnectionClosed += OnClosed;
    }

    public RouteKind Kind => RouteKind.BlePeripheral;

    public string PeerFingerprint => _server.RemoteFingerprint;

    public RouteState State { get { lock (_gate) return _state; } }

    public DateTime StateSinceUtc { get; private set; }

    public PeerSession? Session => _server.Peer;

    public string? LastFailure { get { lock (_gate) return _lastFailure; } }

    public DateTime RetryAtUtc { get; private set; }

    /// <summary>The peer opened it, so it is inbound by definition.</summary>
    public bool IsOutbound => false;

    public int MaxPayloadBytes => BleProtocol.MaxPayloadBytes;

    public bool CarriesPresence => true;

    public event Action<IPeerRoute, RouteState, RouteState>? StateChanged;
    public event Action<IPeerRoute, RoutePayload>? PayloadReceived;

    /// <summary>Raised with the peer's hello, so the registry can note its name and capability.</summary>
    public event Action<LinuxBleServerRoute, PeerIdentifiedEventArgs>? Identified;

    /// <summary>Runs the peripheral's liveness check, which has no loop of its own.</summary>
    public void CheckHeartbeat()
    {
        _server.CheckHeartbeat();

        if (!_server.IsConnected && State == RouteState.Established) Fail("the central stopped answering");
    }

    private void OnIdentified(object? sender, PeerIdentifiedEventArgs e)
    {
        try { Identified?.Invoke(this, e); }
        catch (Exception ex) { Log.Write("BleServer", "An Identified handler threw", ex); }

        if (_server.IsConnected) Move(RouteState.Established);
    }

    private void OnPayload(object? sender, PayloadReceivedEventArgs e)
    {
        var session = _server.Peer;
        if (session == null)
        {
            Log.Write("BleServer", "Dropped a payload that arrived before a key was agreed.");
            return;
        }

        if (!session.TryDecrypt(e.EncryptedPayload, out var decrypted))
        {
            Log.Write("BleServer", "Dropped a payload that does not authenticate under this link's key.");
            return;
        }

        try
        {
            PayloadReceived?.Invoke(this, new RoutePayload
            {
                Peer = decrypted.Peer,
                ContentType = decrypted.ContentType,
                Body = decrypted.Body,
                Via = RouteKind.BlePeripheral,
            });
        }
        catch (Exception ex) { Log.Write("BleServer", "Payload handling failed", ex); }
    }

    private void OnClosed(object? sender, EventArgs e) => Fail("the central disconnected");

    public async Task<bool> SendAsync(byte contentType, byte[] body, CancellationToken cancellationToken = default)
    {
        if (State != RouteState.Established) return false;

        var session = _server.Peer;
        if (session == null) return false;

        byte[]? payload = session.Encrypt(contentType, body);
        if (payload == null) return false;

        if (payload.Length > MaxPayloadBytes)
        {
            Log.Write("BleServer", $"{payload.Length} bytes is over the Bluetooth ceiling; Wi-Fi is needed.");
            return false;
        }

        try
        {
            await _server.SendPayloadAsync(payload).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("BleServer", "Sending over Bluetooth failed", ex);
            return false;
        }
    }

    /// <summary>Asks the central to raise Wi-Fi, for something this link cannot carry.</summary>
    public bool RequestWiFi() => _server.RequestWiFi();

    public Task CloseAsync(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return Task.CompletedTask;

        lock (_gate) _lastFailure ??= reason;
        Move(RouteState.Draining);

        try { _server.Disconnect(); }
        catch (Exception ex) { Log.Write("BleServer", "Dropping the inbound link failed", ex); }

        Move(RouteState.Idle);
        return Task.CompletedTask;
    }

    private void Fail(string reason)
    {
        lock (_gate) _lastFailure = reason;
        Move(RouteState.Backoff);
    }

    private void Move(RouteState to)
    {
        RouteState from;
        lock (_gate)
        {
            from = _state;
            if (from == to) return;
            if (from == RouteState.Idle) return;

            _state = to;
            StateSinceUtc = _clock.UtcNow;
        }

        try { StateChanged?.Invoke(this, from, to); }
        catch (Exception ex) { Log.Write("BleServer", "A StateChanged handler threw", ex); }
    }

    public ValueTask DisposeAsync()
    {
        _server.PeerIdentified -= OnIdentified;
        _server.PayloadReceived -= OnPayload;
        _server.ConnectionClosed -= OnClosed;

        StateChanged = null;
        PayloadReceived = null;
        Identified = null;

        return ValueTask.CompletedTask;
    }
}
