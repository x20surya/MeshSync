using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreLib.Diagnostics;
using CoreLib.Identity;

namespace CoreLib.Transport.Fabric
{
    /// <summary>
    /// Every way this device can reach every device it is paired with.
    ///
    /// <para><b>The one thing a head talks to.</b> A platform supplies route providers - a socket
    /// acceptor, a radio - and reads state back. It no longer owns a link field, a dial loop or an
    /// idea of what "connected" means, which is the rule <c>AGENTS.md</c> already states and that
    /// the radio tier has never obeyed.</para>
    ///
    /// <para><b>Why unidentified routes live here rather than in a peer.</b> A link exists before
    /// anyone knows who is on the other end - an accepted socket, a central that has subscribed and
    /// not yet said anything. It cannot belong to a <see cref="PeerLink"/> until its hello crosses,
    /// and it must still be subject to the handshake deadline while it waits, because that window
    /// is exactly where a device that never identifies itself used to live forever.</para>
    /// </summary>
    public sealed class MeshFabric : IAsyncDisposable
    {
        private readonly PeerSecurity _security;
        private readonly ILinkClock _clock;
        private readonly RouteTimings _timings;
        private readonly Func<BleCapability> _localCapability;

        private readonly object _gate = new();
        private readonly Dictionary<string, PeerLink> _links = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<RouteKind, IRouteProvider> _providers = new();
        /// <summary>
        /// Routes that exist and have not said who they are on yet.
        ///
        /// <para>The value is the peer this device <em>meant</em> to reach, empty for a link that
        /// arrived unasked. Without it a route dialled but not yet identified counted as no route
        /// at all, so every reconcile pass opened another one - observed as several sockets to one
        /// peer inside a second, on the very first end-to-end run of the fabric.</para>
        /// </summary>
        private readonly Dictionary<IPeerRoute, string> _pending = new();

        private bool _disposed;

        public MeshFabric(PeerSecurity security,
                          Func<BleCapability>? localCapability = null,
                          ILinkClock? clock = null,
                          RouteTimings? timings = null)
        {
            _security = security ?? throw new ArgumentNullException(nameof(security));
            _localCapability = localCapability ?? (() => BleCapability.Central);
            _clock = clock ?? SystemClock.Instance;
            _timings = timings ?? RouteTimings.Default;

            SyncPeers();
            _security.Peers.Changed += SyncPeers;
        }

        public PeerSecurity Security => _security;

        public RouteTimings Timings => _timings;

        /// <summary>A decrypted payload arrived from a paired device.</summary>
        public event Action<PeerLink, RoutePayload>? PayloadReceived;

        /// <summary>A peer became reachable, over the route named.</summary>
        public event Action<PeerLink, IPeerRoute>? PeerConnected;

        /// <summary>A route to a peer went away. The peer may still be reachable another way.</summary>
        public event Action<PeerLink, RouteKind, string>? PeerDisconnected;

        /// <summary>Anything at all changed about reachability, for a UI to redraw on.</summary>
        public event Action? Changed;

        // ──────────────────────────────── the peer table

        public IReadOnlyList<PeerLink> Links
        {
            get { lock (_gate) return _links.Values.ToList(); }
        }

        public PeerLink? LinkTo(string? fingerprint)
        {
            if (string.IsNullOrWhiteSpace(fingerprint)) return null;
            lock (_gate) return _links.TryGetValue(fingerprint!, out var link) ? link : null;
        }

        public bool IsConnectedTo(string? fingerprint) => LinkTo(fingerprint)?.IsConnected == true;

        public bool HasPresenceFor(string? fingerprint) => LinkTo(fingerprint)?.HasPresence == true;

        public IReadOnlyList<string> ConnectedPeers =>
            Links.Where(l => l.IsConnected).Select(l => l.Fingerprint).ToList();

        public bool IsConnectedToAny => Links.Any(l => l.IsConnected);

        /// <summary>Peers with something carrying presence, for the route policy.</summary>
        public IReadOnlySet<string> PeersWithPresence
        {
            get
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var link in Links) if (link.HasPresence) set.Add(link.Fingerprint);
                return set;
            }
        }

        /// <summary>
        /// Rebuilds the peer table from the registry.
        ///
        /// <para>A forgotten device's links are closed rather than left to notice on their own. A
        /// session holds its own key, so without this a revoked peer keeps working until its link
        /// happens to drop - and revoking has to mean revoking now.</para>
        /// </summary>
        private void SyncPeers()
        {
            List<PeerLink> orphaned = new();

            lock (_gate)
            {
                if (_disposed) return;

                var current = _security.Peers.Peers.ToDictionary(p => p.Fingerprint, StringComparer.OrdinalIgnoreCase);

                foreach (var pair in _links.ToList())
                {
                    if (current.ContainsKey(pair.Key)) { pair.Value.Peer = current[pair.Key]; continue; }
                    _links.Remove(pair.Key);
                    orphaned.Add(pair.Value);
                }

                foreach (var peer in current.Values)
                {
                    if (_links.ContainsKey(peer.Fingerprint)) continue;

                    var link = new PeerLink(peer, _security.Identity.Fingerprint, _localCapability, _clock, _timings);
                    link.PayloadReceived += OnPayload;
                    link.RouteEstablished += OnEstablished;
                    link.RouteLost += OnLost;
                    _links[peer.Fingerprint] = link;
                }
            }

            foreach (var link in orphaned)
            {
                Log.Write("Fabric", $"{DeviceIdentity.Shorten(link.Fingerprint)} was forgotten; dropping its links.");
                _ = link.DisposeAsync();
            }

            if (orphaned.Count > 0) RaiseChanged();
        }

        // ──────────────────────────────── providers

        public void AddProvider(IRouteProvider provider)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));

            lock (_gate) _providers[provider.Kind] = provider;
            provider.RouteArrived += OnRouteArrived;
        }

        public IRouteProvider? ProviderFor(RouteKind kind)
        {
            lock (_gate) return _providers.TryGetValue(kind, out var provider) ? provider : null;
        }

        /// <summary>
        /// Asks the provider for a route to one peer, if policy allows one now.
        ///
        /// Returns false for the ordinary cases - already open, still in backoff, nothing to dial -
        /// which are answers rather than failures and must not deepen the backoff.
        /// </summary>
        public bool TryOpen(string fingerprint, RouteKind kind)
        {
            var link = LinkTo(fingerprint);
            if (link == null || !link.MayOpen(kind)) return false;

            // A route already on its way to this peer is a route. Counting only adopted ones made
            // the supervisor dial again on every pass for as long as a handshake was in flight.
            if (IsPending(fingerprint, kind)) return false;

            var provider = ProviderFor(kind);
            if (provider?.IsAvailable != true) return false;

            // An address another peer is already established on is not this peer's address,
            // whatever the registry still says. Dialling it reaches that peer, and the link that
            // arrives settles at the far end as a second link of the same kind - dropping the one
            // that works. Caught here, before a socket exists, because by the time the mistake is
            // visible from the hello the damage is done at the other end.
            if (kind == RouteKind.WiFi && HeldByAnotherPeer(link, out string holder))
            {
                Log.Write("Fabric",
                    $"Not dialling {DeviceIdentity.Shorten(fingerprint)} at {link.Peer.LastAddress}: " +
                    $"{DeviceIdentity.Shorten(holder)} is established there. Forgetting the address.");

                _security.Peers.ForgetAddress(fingerprint);
                link.NoteFailure(kind, screenOn: true);
                return false;
            }

            // Recorded before the attempt, not after, so a provider that blocks or throws still
            // counts against the rate limit.
            link.NoteOpening(kind);

            IPeerRoute? route;
            try { route = provider.Open(link.Peer); }
            catch (Exception ex)
            {
                Log.Write("Fabric", $"Opening a {kind} route to {DeviceIdentity.Shorten(fingerprint)} threw", ex);
                return false;
            }

            if (route == null) return false;

            if (string.IsNullOrWhiteSpace(route.PeerFingerprint)) Hold(route, fingerprint);
            else link.Adopt(route);

            return true;
        }

        /// <summary>
        /// True when some other peer already has an established socket at this peer's stored
        /// address, which makes that address demonstrably stale.
        ///
        /// <para>Compared whole, port included, rather than by host: two devices sharing one
        /// machine on different ports is a supported arrangement and the way the mesh is
        /// exercised without a second piece of hardware.</para>
        /// </summary>
        private bool HeldByAnotherPeer(PeerLink link, out string holder)
        {
            holder = "";
            string? address = link.Peer.LastAddress;
            if (string.IsNullOrWhiteSpace(address)) return false;

            foreach (var other in Links)
            {
                if (ReferenceEquals(other, link)) continue;
                if (!other.Has(RouteKind.WiFi)) continue;
                if (!string.Equals(other.Peer.LastAddress, address, StringComparison.OrdinalIgnoreCase)) continue;

                holder = other.Fingerprint;
                return true;
            }

            return false;
        }

        // ──────────────────────────────── routes that arrive unasked

        private void OnRouteArrived(IPeerRoute route)
        {
            if (route == null) return;

            if (string.IsNullOrWhiteSpace(route.PeerFingerprint)) { Hold(route); return; }

            var link = LinkTo(route.PeerFingerprint);
            if (link == null)
            {
                // Not a paired device. Refusal happens in the transport's key agreement; this is
                // the belt to that braces, and it must close rather than merely ignore.
                Log.Write("Fabric",
                    $"A {route.Kind} route arrived from {DeviceIdentity.Shorten(route.PeerFingerprint)}, which is not paired. Dropping it.");
                _ = DropAsync(route, "not a paired device");
                return;
            }

            link.Adopt(route);
        }

        /// <summary>True when a route to this peer of this kind is already being opened.</summary>
        public bool IsPending(string fingerprint, RouteKind kind)
        {
            lock (_gate)
            {
                foreach (var pair in _pending)
                {
                    if (pair.Value.Length == 0) continue;
                    if (pair.Key.Kind != kind) continue;
                    if (!string.Equals(pair.Value, fingerprint, StringComparison.OrdinalIgnoreCase)) continue;
                    if (pair.Key.State is RouteState.Backoff or RouteState.Idle) continue;

                    return true;
                }

                return false;
            }
        }

        /// <summary>Parks a route that has not said who it is, under the handshake deadline.</summary>
        private void Hold(IPeerRoute route, string intended = "")
        {
            lock (_gate)
            {
                if (_disposed) { _ = DropAsync(route, "the fabric is gone"); return; }
                _pending[route] = intended;
            }

            route.StateChanged += OnPendingState;
            OnPendingState(route, RouteState.Idle, route.State);
        }

        private void OnPendingState(IPeerRoute route, RouteState from, RouteState to)
        {
            if (string.IsNullOrWhiteSpace(route.PeerFingerprint))
            {
                if (to is RouteState.Backoff or RouteState.Idle or RouteState.Draining) Release(route, adopt: false);
                return;
            }

            Release(route, adopt: true);
        }

        private void Release(IPeerRoute route, bool adopt)
        {
            bool held;
            string? intended;
            lock (_gate) held = _pending.Remove(route, out intended);
            if (!held) return;

            route.StateChanged -= OnPendingState;

            if (!adopt) { _ = DropAsync(route, route.LastFailure ?? "the link closed before identifying"); return; }

            // A dial is aimed at an address, and who is actually there is only known now. When it
            // is not the device the dial was for, the intent is not simply dropped - see
            // NoteAnsweredByAnother for what that cost before this existed.
            if (!string.IsNullOrWhiteSpace(intended) &&
                !string.Equals(intended, route.PeerFingerprint, StringComparison.OrdinalIgnoreCase) &&
                !AcceptMisdirected(intended!, route))
            {
                return;
            }

            var link = LinkTo(route.PeerFingerprint);
            if (link == null)
            {
                Log.Write("Fabric",
                    $"{DeviceIdentity.Shorten(route.PeerFingerprint)} identified over {route.Kind} and is not paired. Dropping it.");
                _ = DropAsync(route, "not a paired device");
                return;
            }

            link.Adopt(route);
        }

        /// <summary>
        /// Handles a dial that reached a different paired device than the one it was for.
        ///
        /// <para><b>Two peers sharing one stored address used to kill the link between them.</b>
        /// A phone acting as a hotspot handed one device an address, and days later handed the
        /// same address to another; both records survived in the registry. Every reconcile pass
        /// dialled that address for the peer that no longer held it, the peer that did held it
        /// answered, and the route was adopted under whoever answered - taking the healthy link
        /// to that device as a same-kind collision and dropping it. The intended peer still had
        /// no route, so the next pass dialled the same address again. A working link was torn
        /// down and rebuilt every fifteen seconds, indefinitely, and the log read as if the two
        /// devices simply could not hold a connection.</para>
        ///
        /// <para>So the address is forgotten - it demonstrably belongs to the other device - and
        /// the intended peer's route of that kind is put into backoff rather than retried at
        /// once. The route that did arrive is still adopted: it is an authenticated link to a
        /// device this one is paired with, and refusing it would be throwing away the one useful
        /// thing the dial produced.</para>
        /// </summary>
        private bool AcceptMisdirected(string intended, IPeerRoute route)
        {
            var link = LinkTo(intended);

            Log.Write("Fabric",
                $"Dialled {DeviceIdentity.Shorten(intended)} at {link?.Peer.LastAddress ?? "an unknown address"} and " +
                $"{DeviceIdentity.Shorten(route.PeerFingerprint)} answered. Forgetting that address: it is the other device's now.");

            _security.Peers.ForgetAddress(intended);
            link?.NoteFailure(route.Kind, screenOn: true);

            // <b>A misdirected dial may add a route, never replace one.</b> The peer that answered
            // is already reachable over this kind, so adopting would settle as a second link of
            // the same kind and drop the working one in favour of a link this device only opened
            // by mistake. Forgetting the address stops the next pass; this stops the one that has
            // already happened. Both are needed - the first attempt is what killed the link.
            var answering = LinkTo(route.PeerFingerprint);
            if (answering?.Has(route.Kind) != true) return true;

            _ = DropAsync(route, "a misdirected dial reached a peer that is already linked");
            return false;
        }

        // ──────────────────────────────── the deadline sweep

        /// <summary>
        /// Closes every route that has been connected too long without agreeing a session, held or
        /// adopted. Returns how many were dropped.
        ///
        /// <para>Run every reconcile pass. This single sweep is what makes the standing link
        /// impossible to hold hostage: a device that connects, answers pings and never identifies
        /// itself is closed on a clock rather than tolerated until it walks away.</para>
        /// </summary>
        public int EnforceHandshakeDeadlines()
        {
            int dropped = 0;
            List<IPeerRoute> stale = new();
            var now = _clock.UtcNow;

            lock (_gate)
            {
                foreach (var route in _pending.Keys.ToList())
                {
                    if (route.State is not (RouteState.Handshaking or RouteState.Connecting)) continue;
                    if (now - route.StateSinceUtc < _timings.HandshakeGrace) continue;

                    _pending.Remove(route);
                    stale.Add(route);
                }
            }

            foreach (var route in stale)
            {
                route.StateChanged -= OnPendingState;
                Log.Write("Fabric",
                    $"A {route.Kind} link never said who it was within {_timings.HandshakeGrace.TotalSeconds:F0}s; dropping it. It belongs to another mesh.");
                _ = DropAsync(route, "never identified itself");
                dropped++;
            }

            foreach (var link in Links) dropped += link.EnforceHandshakeDeadline();

            if (dropped > 0) RaiseChanged();
            return dropped;
        }

        public int PendingCount { get { lock (_gate) return _pending.Count; } }

        // ──────────────────────────────── sending

        /// <summary>
        /// Sends to every connected device, sealed separately for each.
        ///
        /// <para>There is no such thing as one ciphertext for the whole mesh: the key belongs to the
        /// connection, so a fan-out is genuinely N encryptions. That is the cost of a paired device
        /// being unable to read traffic meant for another pair.</para>
        /// </summary>
        public async Task<int> BroadcastAsync(byte contentType, byte[] body, CancellationToken cancellationToken = default)
        {
            var targets = Links.Where(l => l.IsConnected).ToList();
            if (targets.Count == 0) return 0;

            var sends = targets.Select(l => l.SendAsync(contentType, body, cancellationToken));
            var results = await Task.WhenAll(sends).ConfigureAwait(false);
            return results.Count(ok => ok);
        }

        public Task<bool> SendToAsync(string fingerprint, byte contentType, byte[] body,
                                      CancellationToken cancellationToken = default)
        {
            var link = LinkTo(fingerprint);
            return link == null ? Task.FromResult(false) : link.SendAsync(contentType, body, cancellationToken);
        }

        /// <summary>Peers that are reachable but cannot carry a payload this size on what is up.</summary>
        public IReadOnlyList<PeerLink> NeedingWiFiFor(int payloadBytes) =>
            Links.Where(l => l.NeedsWiFiFor(payloadBytes)).ToList();

        // ──────────────────────────────── plumbing

        private void OnPayload(PeerLink link, RoutePayload payload)
        {
            try { PayloadReceived?.Invoke(link, payload); }
            catch (Exception ex) { Log.Write("Fabric", "A payload handler threw", ex); }
        }

        private void OnEstablished(PeerLink link, IPeerRoute route)
        {
            try { PeerConnected?.Invoke(link, route); }
            catch (Exception ex) { Log.Write("Fabric", "A PeerConnected handler threw", ex); }

            RaiseChanged();
        }

        private void OnLost(PeerLink link, RouteKind kind, string reason)
        {
            try { PeerDisconnected?.Invoke(link, kind, reason); }
            catch (Exception ex) { Log.Write("Fabric", "A PeerDisconnected handler threw", ex); }

            RaiseChanged();
        }

        private void RaiseChanged()
        {
            try { Changed?.Invoke(); }
            catch { /* a broken listener must not break syncing */ }
        }

        private static async Task DropAsync(IPeerRoute route, string reason)
        {
            try { await route.CloseAsync(reason).ConfigureAwait(false); }
            catch (Exception ex) { Log.Write("Fabric", "Closing a route failed", ex); }

            try { await route.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Write("Fabric", "Disposing a route failed", ex); }
        }

        public async ValueTask DisposeAsync()
        {
            List<PeerLink> links;
            List<IPeerRoute> pending;
            List<IRouteProvider> providers;

            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;

                links = _links.Values.ToList();
                pending = _pending.Keys.ToList();
                providers = _providers.Values.ToList();
                _links.Clear();
                _pending.Clear();
                _providers.Clear();
            }

            _security.Peers.Changed -= SyncPeers;

            foreach (var provider in providers)
            {
                provider.RouteArrived -= OnRouteArrived;
                try { await provider.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { Log.Write("Fabric", "Disposing a route provider failed", ex); }
            }

            foreach (var route in pending)
            {
                route.StateChanged -= OnPendingState;
                await DropAsync(route, "this device is shutting down").ConfigureAwait(false);
            }

            foreach (var link in links) await link.DisposeAsync().ConfigureAwait(false);

            PayloadReceived = null;
            PeerConnected = null;
            PeerDisconnected = null;
            Changed = null;
        }
    }
}
