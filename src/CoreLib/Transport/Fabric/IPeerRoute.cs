using System;
using System.Threading;
using System.Threading.Tasks;
using CoreLib.Identity;

namespace CoreLib.Transport.Fabric
{
    /// <summary>A payload that arrived on one route, already opened.</summary>
    public sealed class RoutePayload
    {
        public required PeerRecord Peer { get; init; }
        public required byte ContentType { get; init; }
        public required byte[] Body { get; init; }
        public required RouteKind Via { get; init; }
    }

    /// <summary>
    /// One way of reaching one peer.
    ///
    /// <para><b>What it replaces.</b> Three transports with three different lifecycles, three
    /// definitions of connected, three retry loops and three ideas of when to give up - two of
    /// which never gave up at all. A route is created, it moves through
    /// <see cref="RouteState"/>, and it is closed. Nothing above it needs to know whether it is a
    /// socket or a radio.</para>
    ///
    /// <para><b>Who closes it.</b> Only the owning <see cref="PeerLink"/>. A route never retires
    /// itself and never retries itself; it reports a state and the supervisor decides. That is
    /// what stops a transport and a daemon disagreeing about whether a link exists, which is how
    /// a refused peer stayed "connected" on two of the three heads.</para>
    /// </summary>
    public interface IPeerRoute : IAsyncDisposable
    {
        RouteKind Kind { get; }

        /// <summary>Who this route reaches. Empty until the peer has identified itself.</summary>
        string PeerFingerprint { get; }

        RouteState State { get; }

        /// <summary>When <see cref="State"/> last changed. The handshake deadline is measured from it.</summary>
        DateTime StateSinceUtc { get; }

        /// <summary>
        /// The key agreed for this connection, or null before the peer's hello has crossed.
        ///
        /// Never cached against the peer, never shared with another route, and disposed with the
        /// route - which is what makes what crossed it unrecoverable.
        /// </summary>
        PeerSession? Session { get; }

        /// <summary>Why the route last left <see cref="RouteState.Established"/>, for the health surface.</summary>
        string? LastFailure { get; }

        /// <summary>Not retried before this. Set when entering <see cref="RouteState.Backoff"/>.</summary>
        DateTime RetryAtUtc { get; }

        /// <summary>
        /// True when this device opened the link rather than accepting it.
        ///
        /// Needed to settle two simultaneous Wi&#8209;Fi links: the survivor is the one dialled by
        /// the lower fingerprint, and that can only be applied if a route knows which end it is.
        /// </summary>
        bool IsOutbound { get; }

        /// <summary>Largest payload this route will carry: 32 MB on a socket, 64 KB on a radio.</summary>
        int MaxPayloadBytes { get; }

        /// <summary>
        /// True when holding this route open is itself proof the peer is nearby.
        ///
        /// Bluetooth says yes and Wi&#8209;Fi says no, and that single bit is what decides whether
        /// a peer needs a socket raised for it. It is per route, so a radio link to one device can
        /// no longer suppress the socket to another.
        /// </summary>
        bool CarriesPresence { get; }

        /// <summary>Seals and sends. False when the route is not <see cref="RouteState.Established"/>.</summary>
        Task<bool> SendAsync(byte contentType, byte[] body, CancellationToken cancellationToken = default);

        /// <summary>Closes the route, letting in-flight sends finish. Idempotent.</summary>
        Task CloseAsync(string reason);

        event Action<IPeerRoute, RouteState, RouteState>? StateChanged;

        event Action<IPeerRoute, RoutePayload>? PayloadReceived;
    }

    /// <summary>
    /// Opens routes of one kind, and hands over the ones that arrive unasked.
    ///
    /// <para><b>Why inbound needs its own event.</b> Half of every tier is a link somebody else
    /// opened - an accepted socket, a central subscribing to our GATT server - and every head
    /// handled those on a completely separate code path from the ones it dialled. They arrive
    /// through <see cref="RouteArrived"/> and become ordinary routes in an ordinary
    /// <see cref="PeerLink"/>, so there is one lifecycle rather than two.</para>
    /// </summary>
    public interface IRouteProvider : IAsyncDisposable
    {
        RouteKind Kind { get; }

        /// <summary>False when the medium is unavailable: no radio, no network, preference off.</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Begins opening a route. Returns null when there is nothing to try yet - no stored
        /// address, or a scan that has not found the peer - which is a normal answer and not a
        /// failure to back off from.
        /// </summary>
        IPeerRoute? Open(PeerRecord peer);

        /// <summary>A peer opened a link to this device.</summary>
        event Action<IPeerRoute>? RouteArrived;
    }
}
