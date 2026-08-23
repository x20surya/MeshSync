using CoreLib.Identity;
using CoreLib.Transport.Fabric;

namespace CoreLib.Tests.Fakes;

/// <summary>
/// A route with no transport behind it.
///
/// <para>The reason the fabric is testable at all. Every rule that used to live inside a GATT
/// callback or a socket read loop is now a state transition this can drive by hand, so the
/// defects that previously needed two devices and a radio - a peer that answers pings and never
/// identifies itself, two links to two different peers arriving in the same moment - become
/// ordinary unit tests.</para>
///
/// <para><b>It enforces the invariant rather than merely modelling it.</b>
/// <see cref="Establish"/> throws unless a session has been agreed first, because there is no
/// legitimate path from a connected link to a usable one that skips the key agreement, and a fake
/// that allowed it would let the fix silently regress.</para>
/// </summary>
public sealed class FakeRoute : IPeerRoute
{
    private readonly FakeClock _clock;
    private RouteState _state;
    private string _fingerprint;

    public FakeRoute(RouteKind kind, FakeClock clock, string fingerprint = "", bool outbound = true)
    {
        Kind = kind;
        _clock = clock;
        _fingerprint = fingerprint;
        IsOutbound = outbound;
        _state = RouteState.Connecting;
        StateSinceUtc = clock.UtcNow;

        MaxPayloadBytes = kind == RouteKind.WiFi ? 32 * 1024 * 1024 : 64 * 1024;
        CarriesPresence = kind != RouteKind.WiFi;
    }

    public RouteKind Kind { get; }
    public string PeerFingerprint => _fingerprint;
    public RouteState State => _state;
    public DateTime StateSinceUtc { get; private set; }
    public PeerSession? Session { get; private set; }
    public string? LastFailure { get; private set; }
    public DateTime RetryAtUtc { get; private set; }
    public bool IsOutbound { get; }
    public int MaxPayloadBytes { get; set; }
    public bool CarriesPresence { get; set; }

    /// <summary>Everything this route was asked to send, in order.</summary>
    public List<(byte ContentType, byte[] Body)> Sent { get; } = new();

    public bool IsClosed { get; private set; }
    public string? ClosedBecause { get; private set; }

    public event Action<IPeerRoute, RouteState, RouteState>? StateChanged;
    public event Action<IPeerRoute, RoutePayload>? PayloadReceived;

    // ── driving it ───────────────────────────────────────────────────────────

    /// <summary>The transport connected. No hello has crossed; the grace starts here.</summary>
    public FakeRoute Connect()
    {
        Move(RouteState.Handshaking);
        return this;
    }

    /// <summary>The peer's hello crossed and a session was agreed.</summary>
    public FakeRoute Identify(string fingerprint, PeerSession? session = null)
    {
        _fingerprint = fingerprint;
        Session = session;
        HasSession = true;
        Move(RouteState.Handshaking);
        return this;
    }

    /// <summary>
    /// The peer answered a ping but the key agreement failed or never happened.
    ///
    /// This is the stranger: a link the radio reports as perfectly healthy, carrying nothing.
    /// </summary>
    public FakeRoute AnswersButNeverIdentifies()
    {
        HasSession = false;
        Move(RouteState.Handshaking);
        return this;
    }

    public bool HasSession { get; private set; }

    public FakeRoute Establish()
    {
        if (!HasSession)
        {
            throw new InvalidOperationException(
                "A route cannot be established without a session. That is the invariant under test.");
        }

        Move(RouteState.Established);
        return this;
    }

    public FakeRoute Drop(string reason = "the peer went away")
    {
        LastFailure = reason;
        Move(RouteState.Backoff);
        return this;
    }

    public void Deliver(PeerRecord peer, byte contentType, byte[] body) =>
        PayloadReceived?.Invoke(this, new RoutePayload
        {
            Peer = peer,
            ContentType = contentType,
            Body = body,
            Via = Kind,
        });

    private void Move(RouteState to)
    {
        var from = _state;
        if (from == to) return;

        _state = to;
        StateSinceUtc = _clock.UtcNow;
        StateChanged?.Invoke(this, from, to);
    }

    // ── the interface ────────────────────────────────────────────────────────

    public Task<bool> SendAsync(byte contentType, byte[] body, CancellationToken cancellationToken = default)
    {
        if (_state != RouteState.Established) return Task.FromResult(false);
        if (body.Length > MaxPayloadBytes) return Task.FromResult(false);

        Sent.Add((contentType, body));
        return Task.FromResult(true);
    }

    public Task CloseAsync(string reason)
    {
        IsClosed = true;
        ClosedBecause ??= reason;
        LastFailure ??= reason;
        Move(RouteState.Idle);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        IsClosed = true;
        StateChanged = null;
        PayloadReceived = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>A provider that hands out <see cref="FakeRoute"/>s a test prepared in advance.</summary>
public sealed class FakeRouteProvider : IRouteProvider
{
    private readonly FakeClock _clock;
    private readonly Dictionary<string, Queue<FakeRoute>> _queued = new(StringComparer.OrdinalIgnoreCase);

    public FakeRouteProvider(RouteKind kind, FakeClock clock)
    {
        Kind = kind;
        _clock = clock;
    }

    public RouteKind Kind { get; }
    public bool IsAvailable { get; set; } = true;

    /// <summary>Every peer this provider was asked to reach, in order.</summary>
    public List<string> OpenedFor { get; } = new();

    public event Action<IPeerRoute>? RouteArrived;

    /// <summary>The next route <see cref="Open"/> will return for that peer.</summary>
    public FakeRoute Queue(string fingerprint, FakeRoute route)
    {
        if (!_queued.TryGetValue(fingerprint, out var queue)) _queued[fingerprint] = queue = new Queue<FakeRoute>();
        queue.Enqueue(route);
        return route;
    }

    /// <summary>Simulates a peer opening a link to this device.</summary>
    public FakeRoute Arrive(FakeRoute route)
    {
        RouteArrived?.Invoke(route);
        return route;
    }

    public IPeerRoute? Open(PeerRecord peer)
    {
        OpenedFor.Add(peer.Fingerprint);

        if (_queued.TryGetValue(peer.Fingerprint, out var queue) && queue.Count > 0) return queue.Dequeue();

        // Nothing prepared: the ordinary "no address, nothing found" answer.
        return null;
    }

    public ValueTask DisposeAsync()
    {
        RouteArrived = null;
        return ValueTask.CompletedTask;
    }

    /// <summary>A fresh route of this provider's kind, on the shared clock.</summary>
    public FakeRoute NewRoute(string fingerprint = "", bool outbound = true) =>
        new(Kind, _clock, fingerprint, outbound);
}
