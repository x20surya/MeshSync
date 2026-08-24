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
    /// Everything this device knows about reaching one peer.
    ///
    /// <para><b>What it replaces.</b> A per-peer dictionary on the Wi&#8209;Fi tier and, on the
    /// radio tier, two nullable fields per head - <c>_bleLink</c> and <c>_blePeripheralLink</c>,
    /// <c>_bleCentral</c> and <c>_bleTransport</c>, <c>Ble</c> and <c>BleServer</c>. One link per
    /// process, whatever the peer count, and every question about them asked of the app rather than
    /// of a device.</para>
    ///
    /// <para><b>Why the collision rule lives here and nowhere else.</b> Two links to one peer must
    /// be settled; two links to two peers must not be touched. Scoping the rule to a single
    /// <see cref="PeerLink"/> makes the second case unrepresentable. The Android version guarded on
    /// "a central link exists and a peripheral link exists" without comparing fingerprints, so with
    /// three devices - phone dialling laptop A while laptop B dials the phone - it would tear down a
    /// perfectly good link.</para>
    /// </summary>
    public sealed class PeerLink : IAsyncDisposable
    {
        private readonly ILinkClock _clock;
        private readonly RouteTimings _timings;
        private readonly string _localFingerprint;
        private readonly Func<BleCapability> _localCapability;
        private readonly Random _jitter = new();

        private readonly object _gate = new();
        private readonly Dictionary<RouteKind, IPeerRoute> _routes = new();
        private readonly Dictionary<RouteKind, int> _failures = new();
        private readonly Dictionary<RouteKind, DateTime> _retryAt = new();
        private readonly Dictionary<RouteKind, string> _lastFailure = new();
        private readonly Dictionary<RouteKind, DateTime> _lastLost = new();
        private readonly Dictionary<RouteKind, DateTime> _lastAttempt = new();

        private PeerRecord _peer;
        private bool _disposed;

        public PeerLink(PeerRecord peer, string localFingerprint, Func<BleCapability> localCapability,
                        ILinkClock? clock = null, RouteTimings? timings = null)
        {
            _peer = peer ?? throw new ArgumentNullException(nameof(peer));
            _localFingerprint = localFingerprint ?? "";
            _localCapability = localCapability ?? (() => BleCapability.Central);
            _clock = clock ?? SystemClock.Instance;
            _timings = timings ?? RouteTimings.Default;
        }

        public string Fingerprint => _peer.Fingerprint;

        public PeerRecord Peer
        {
            get { lock (_gate) return _peer; }
            internal set { lock (_gate) _peer = value; }
        }

        /// <summary>A route to this peer became usable.</summary>
        public event Action<PeerLink, IPeerRoute>? RouteEstablished;

        /// <summary>A route to this peer went away, with the reason.</summary>
        public event Action<PeerLink, RouteKind, string>? RouteLost;

        public event Action<PeerLink, RoutePayload>? PayloadReceived;

        // ──────────────────────────────── what is true right now

        public bool IsConnected => LiveRoutes.Count > 0;

        /// <summary>True when something is carrying presence for this peer specifically.</summary>
        public bool HasPresence => LiveRoutes.Any(r => r.CarriesPresence);

        /// <summary>Wi&#8209;Fi wins when both are up, because it is the link that carries everything.</summary>
        public LinkKind ActiveLink
        {
            get
            {
                var live = LiveRoutes;
                if (live.Any(r => r.Kind == RouteKind.WiFi)) return LinkKind.WiFi;
                return live.Count > 0 ? LinkKind.Ble : LinkKind.None;
            }
        }

        public IReadOnlyList<IPeerRoute> LiveRoutes
        {
            get { lock (_gate) return _routes.Values.Where(r => r.State == RouteState.Established).ToList(); }
        }

        public IReadOnlyList<IPeerRoute> AllRoutes
        {
            get { lock (_gate) return _routes.Values.ToList(); }
        }

        public IPeerRoute? RouteOf(RouteKind kind)
        {
            lock (_gate) return _routes.TryGetValue(kind, out var route) ? route : null;
        }

        public bool Has(RouteKind kind)
        {
            lock (_gate) return _routes.ContainsKey(kind);
        }

        /// <summary>
        /// True when this device is the one that should dial a socket to this peer.
        ///
        /// <para>The same comparison that settles a collision, asked before there is one. Both ends
        /// compute complementary answers from fingerprints they already hold.</para>
        /// </summary>
        public bool ShouldDialWiFi => string.CompareOrdinal(_localFingerprint, Fingerprint) < 0;

        /// <summary>
        /// False while a failed route is serving out its backoff, and - for a socket this device
        /// would lose the race for - while the other end still has first refusal.
        ///
        /// <para>See <see cref="RouteTimings.DialGrace"/>. Racing a collision this device is
        /// guaranteed to lose costs a socket, a handshake and a retry, every time.</para>
        /// </summary>
        public bool MayOpen(RouteKind kind)
        {
            lock (_gate)
            {
                if (_routes.ContainsKey(kind)) return false;

                var now = _clock.UtcNow;

                if (_retryAt.TryGetValue(kind, out var until) && now < until) return false;

                // The hard rate limit. Deliberately not cleared by a success, unlike the backoff
                // above - see RouteTimings.MinDialInterval for why that distinction is the whole
                // point.
                if (_lastAttempt.TryGetValue(kind, out var attempted) &&
                    now - attempted < _timings.MinDialInterval)
                {
                    return false;
                }

                // Only after something has actually been lost. The first attempt is always free:
                // both ends dial, which is the design, and at that point there is no established
                // link for either to lose. Delaying it would cost every fresh start a beat for a
                // collision that may not happen.
                //
                // It is the *retry* that has to be deferred, because by then this end knows the
                // other one is there and knows it will lose the race.
                if (kind == RouteKind.WiFi && !ShouldDialWiFi &&
                    _lastLost.TryGetValue(kind, out var lost) && now - lost < _timings.DialGrace)
                {
                    return false;
                }

                return true;
            }
        }

        /// <summary>
        /// Records that a route of this kind is being opened now.
        ///
        /// Called whatever the attempt goes on to do, because the point is to bound how often this
        /// device reaches for the radio or the network - not how often it succeeds.
        /// </summary>
        public void NoteOpening(RouteKind kind)
        {
            lock (_gate) _lastAttempt[kind] = _clock.UtcNow;
        }

        public DateTime RetryAt(RouteKind kind)
        {
            lock (_gate) return _retryAt.TryGetValue(kind, out var until) ? until : DateTime.MinValue;
        }

        /// <summary>
        /// Why this kind of route last went away, kept after the route object is gone.
        ///
        /// <para>A retired route takes its own <c>LastFailure</c> with it, so without this the
        /// health surface can say a peer is unreachable and never say why - which is most of what
        /// makes the connection layer hard to diagnose in the first place.</para>
        /// </summary>
        public string? FailureOf(RouteKind kind)
        {
            lock (_gate) return _lastFailure.TryGetValue(kind, out var reason) ? reason : null;
        }

        /// <summary>Route kinds that have failed and are waiting out a backoff.</summary>
        public IReadOnlyList<RouteKind> Backoffs
        {
            get
            {
                lock (_gate)
                {
                    return _retryAt.Keys.Where(k => !_routes.ContainsKey(k)).ToList();
                }
            }
        }

        // ──────────────────────────────── taking routes in

        /// <summary>
        /// Takes ownership of a route, whether this device opened it or accepted it.
        ///
        /// <para>Inbound and outbound arrive here identically, which is the point: every head had
        /// two separate code paths for the two halves of each tier, and the halves disagreed about
        /// when to give up.</para>
        /// </summary>
        public void Adopt(IPeerRoute route)
        {
            if (route == null) throw new ArgumentNullException(nameof(route));

            IPeerRoute? loser = null;
            string reason = "";

            lock (_gate)
            {
                if (_disposed) { loser = route; reason = "the peer link is gone"; }
                else if (_routes.TryGetValue(route.Kind, out var existing) && !ReferenceEquals(existing, route))
                {
                    var winner = SettleSameKind(existing, route);
                    loser = ReferenceEquals(winner, existing) ? route : existing;
                    reason = "a second link of the same kind to one peer";
                    _routes[route.Kind] = winner;
                }
                else
                {
                    _routes[route.Kind] = route;
                }
            }

            if (ReferenceEquals(loser, route))
            {
                _ = CloseQuietlyAsync(route, reason);
                return;
            }

            Attach(route);

            if (loser != null)
            {
                Log.Write("Fabric", $"{Describe(route)}: {reason}; dropping the other.");
                Detach(loser);
                _ = CloseQuietlyAsync(loser, reason);
            }

            OnRouteState(route, RouteState.Idle, route.State);
        }

        private void Attach(IPeerRoute route)
        {
            route.StateChanged += OnRouteState;
            route.PayloadReceived += OnRoutePayload;
        }

        private void Detach(IPeerRoute route)
        {
            route.StateChanged -= OnRouteState;
            route.PayloadReceived -= OnRoutePayload;
        }

        /// <summary>
        /// Which of two links of the same kind survives.
        ///
        /// <para>Both devices listen and both dial, so each can open one to the other in the same
        /// moment. The survivor is the link dialled by the lower fingerprint - a value both ends
        /// already hold, so they converge with no round trip and neither has to be in charge.</para>
        /// </summary>
        private IPeerRoute SettleSameKind(IPeerRoute existing, IPeerRoute incoming)
        {
            // <b>Direction decides, and nothing else may.</b>
            //
            // This used to answer "keep the incoming one" whenever the existing route had not
            // finished its handshake yet - which looks harmless and is not, because the two ends
            // then stop agreeing. The peer settles on direction alone and keeps the link it
            // dialled; this end sees a half-open existing route, keeps the *other* one, and each
            // side kills the link the other is holding. Both redial, and it repeats.
            //
            // On a phone and a laptop that produced a collision every fifteen seconds for as long
            // as they were both running, with a route logging "established" and "lost" in the same
            // millisecond - established locally, already killed remotely.
            //
            // So the rule is the fingerprint comparison and only that. It is deterministic, both
            // ends compute complementary answers from values they already hold, and neither needs
            // to know what state the other's route is in.
            if (existing.IsOutbound != incoming.IsOutbound)
            {
                bool keepOutbound = ShouldDialWiFi;
                return existing.IsOutbound == keepOutbound ? existing : incoming;
            }

            // Both the same direction, so one is a link the peer has already abandoned. Believe
            // the newer one.
            return incoming;
        }

        // ──────────────────────────────── the radio's two halves

        /// <summary>
        /// Drops whichever radio half should not exist, when both are live to this one peer.
        ///
        /// <para>Only reachable when both links carry the same fingerprint, because both live in
        /// this object. Both ends compute the complement from the same two fingerprints, so exactly
        /// one link is dropped rather than both or neither.</para>
        ///
        /// <para>A duplicate is not cosmetic: echo suppression is on the sending side, so the
        /// receiver has no defence and every clipboard item crosses twice.</para>
        /// </summary>
        public void ResolveRadioCollision()
        {
            IPeerRoute? doomed = null;

            lock (_gate)
            {
                if (!_routes.TryGetValue(RouteKind.BleCentral, out var central) ||
                    !_routes.TryGetValue(RouteKind.BlePeripheral, out var peripheral)) return;

                if (central.State != RouteState.Established || peripheral.State != RouteState.Established) return;

                var keep = BleLinkArbiter.KeepFor(_localFingerprint, _localCapability(), Fingerprint);

                doomed = keep == BleRole.Central ? peripheral : central;
                _routes.Remove(doomed.Kind);
            }

            if (doomed == null) return;

            Log.Write("Fabric",
                $"Two radio links to {DeviceIdentity.Shorten(Fingerprint)}; keeping the one " +
                (doomed.Kind == RouteKind.BlePeripheral ? "this device opened." : "the peer opened."));

            Detach(doomed);
            _ = CloseQuietlyAsync(doomed, "the arbiter chose the other radio half");
        }

        // ──────────────────────────────── the handshake deadline

        /// <summary>
        /// Closes any route that has been connected too long without agreeing a session.
        ///
        /// <para><b>This is the fix for the standing link being held by a stranger.</b> The Android
        /// central and the Windows peripheral both returned from a failed key agreement leaving the
        /// link up and reporting connected, so a device that answered pings but never identified
        /// itself parked a loop for as long as it stayed in range. Called every reconcile pass, for
        /// every route kind, on every head.</para>
        /// </summary>
        public int EnforceHandshakeDeadline()
        {
            var expired = new List<IPeerRoute>();
            var now = _clock.UtcNow;

            lock (_gate)
            {
                foreach (var route in _routes.Values.ToList())
                {
                    if (route.State != RouteState.Handshaking) continue;
                    if (now - route.StateSinceUtc < _timings.HandshakeGrace) continue;

                    expired.Add(route);
                    _routes.Remove(route.Kind);
                    NoteFailureLocked(route.Kind, screenOn: false, "no session inside the handshake grace");
                }
            }

            foreach (var route in expired)
            {
                Log.Write("Fabric",
                    $"{Describe(route)} never agreed a session within {_timings.HandshakeGrace.TotalSeconds:F0}s; dropping it.");
                Detach(route);
                _ = CloseQuietlyAsync(route, "no session inside the handshake grace");
                Raise(RouteLost, route.Kind, "no session inside the handshake grace");
            }

            return expired.Count;
        }

        // ──────────────────────────────── closing

        public async Task CloseAsync(RouteKind kind, string reason)
        {
            IPeerRoute? route;
            lock (_gate)
            {
                if (!_routes.Remove(kind, out route)) return;
            }

            Detach(route!);
            await CloseQuietlyAsync(route!, reason).ConfigureAwait(false);
            Raise(RouteLost, kind, reason);
        }

        public async Task CloseAllAsync(string reason)
        {
            List<IPeerRoute> routes;
            lock (_gate)
            {
                routes = _routes.Values.ToList();
                _routes.Clear();
            }

            foreach (var route in routes)
            {
                Detach(route);
                await CloseQuietlyAsync(route, reason).ConfigureAwait(false);
                Raise(RouteLost, route.Kind, reason);
            }
        }

        // ──────────────────────────────── backoff

        /// <summary>Records a failed attempt and sets when the next one may happen.</summary>
        public void NoteFailure(RouteKind kind, bool screenOn)
        {
            lock (_gate) NoteFailureLocked(kind, screenOn);
        }

        /// <summary>Clears the backoff for a kind, after a success or a peers-changed event.</summary>
        public void NoteSuccess(RouteKind kind)
        {
            lock (_gate)
            {
                _failures.Remove(kind);
                _retryAt.Remove(kind);
                _lastFailure.Remove(kind);
            }
        }

        private void NoteFailureLocked(RouteKind kind, bool screenOn, string? reason = null)
        {
            int failures = _failures.TryGetValue(kind, out var count) ? count + 1 : 1;
            _failures[kind] = failures;
            _retryAt[kind] = _clock.UtcNow + BackoffFor(failures, screenOn);

            if (!string.IsNullOrWhiteSpace(reason)) _lastFailure[kind] = reason!;
            _lastLost[kind] = _clock.UtcNow;
        }

        /// <summary>
        /// Exponential, capped, and jittered so a phone and a laptop waking together do not retry
        /// in lockstep. The ceiling depends on whether anybody is present: a fixed brisk retry ran
        /// all night whenever the other device was off, which is the drain the tier exists to avoid.
        /// </summary>
        internal TimeSpan BackoffFor(int failures, bool screenOn)
        {
            if (failures <= 0) return _timings.MinBackoff;

            double seconds = _timings.MinBackoff.TotalSeconds * Math.Pow(2, Math.Min(failures - 1, 6));
            seconds = Math.Min(seconds, _timings.MaxBackoff.TotalSeconds);

            var ceiling = screenOn ? _timings.ActiveCeiling : _timings.IdleCeiling;
            seconds = Math.Min(seconds, ceiling.TotalSeconds);

            double jitter;
            lock (_jitter) jitter = 0.8 + _jitter.NextDouble() * 0.4;

            return TimeSpan.FromSeconds(seconds * jitter);
        }

        // ──────────────────────────────── sending

        /// <summary>
        /// Sends over the best route this peer has for a payload of this size.
        ///
        /// <para>Wi&#8209;Fi first because it carries everything; the radio when it is what exists.
        /// A payload too large for every live route is refused here rather than truncated, and the
        /// caller is the one that decides whether to raise Wi&#8209;Fi for it - which it can now do
        /// for this peer alone.</para>
        /// </summary>
        public async Task<bool> SendAsync(byte contentType, byte[] body, CancellationToken cancellationToken = default)
        {
            foreach (var route in Preferred(body?.Length ?? 0))
            {
                try
                {
                    if (await route.SendAsync(contentType, body!, cancellationToken).ConfigureAwait(false)) return true;
                }
                catch (Exception ex)
                {
                    // One unreachable route must not stop the next being tried.
                    Log.Write("Fabric", $"Sending over {Describe(route)} failed", ex);
                }
            }

            return false;
        }

        /// <summary>Live routes that could carry this many bytes, best first.</summary>
        public IReadOnlyList<IPeerRoute> Preferred(int payloadBytes)
        {
            return LiveRoutes
                .Where(r => payloadBytes <= r.MaxPayloadBytes)
                .OrderBy(r => r.Kind == RouteKind.WiFi ? 0 : 1)
                .ToList();
        }

        /// <summary>True when nothing live could carry a payload this size, but something is up.</summary>
        public bool NeedsWiFiFor(int payloadBytes) =>
            IsConnected && Preferred(payloadBytes).Count == 0;

        // ──────────────────────────────── route events

        private void OnRouteState(IPeerRoute route, RouteState from, RouteState to)
        {
            if (to == RouteState.Established)
            {
                NoteSuccess(route.Kind);
                Log.Write("Fabric", $"{Describe(route)} established.");
                Raise(RouteEstablished, route);
                ResolveRadioCollision();
                return;
            }

            // Any terminal state, from any state before it - not only from Established.
            //
            // <para><b>A route that failed before it was ever usable used to stay in the table for
            // ever.</b> It was adopted while Handshaking, dropped to Backoff a moment later, and
            // this method returned early without removing it - so <c>Has(kind)</c> answered true
            // about a dead object and the supervisor never opened another. One failed handshake
            // wedged that route kind to that peer until the process restarted.</para>
            if (to is not (RouteState.Backoff or RouteState.Idle)) return;

            bool removed;
            lock (_gate)
            {
                removed = _routes.TryGetValue(route.Kind, out var held) && ReferenceEquals(held, route);
                if (removed) _routes.Remove(route.Kind);
            }

            // Already gone: this is the loser of a collision, which was removed from the table
            // deliberately before being closed. Nothing was lost, so nothing is announced.
            if (!removed) return;

            string reason = route.LastFailure ?? "the link closed";

            lock (_gate)
            {
                _lastFailure[route.Kind] = reason;

                // <b>A link that has just died must not be reopened in the same millisecond.</b>
                //
                // Both devices dial, so both can win the race to connect and one link is dropped
                // as the loser. The end whose link was dropped sees an ordinary loss - and with
                // no backoff it redialled instantly, collided again, and lost again. Two devices
                // on a desk produced 136 collisions in two minutes and were still going.
                //
                // The old dial loop tolerated glare because it ran on a 15-second timer. A
                // supervisor that reconciles the moment anything changes does not have that
                // accidental rate limit, so the backoff has to be real. One second with jitter is
                // enough to break the loop and short enough that a genuine drop reconnects at once.
                NoteFailureLocked(route.Kind, screenOn: true, reason);
            }

            Log.Write("Fabric", $"{Describe(route)} lost: {reason}");
            Detach(route);
            Raise(RouteLost, route.Kind, reason);
        }

        private void OnRoutePayload(IPeerRoute route, RoutePayload payload)
        {
            try { PayloadReceived?.Invoke(this, payload); }
            catch (Exception ex) { Log.Write("Fabric", "A payload handler threw", ex); }
        }

        private void Raise(Action<PeerLink, IPeerRoute>? handler, IPeerRoute route)
        {
            try { handler?.Invoke(this, route); }
            catch (Exception ex) { Log.Write("Fabric", "A route handler threw", ex); }
        }

        private void Raise(Action<PeerLink, RouteKind, string>? handler, RouteKind kind, string reason)
        {
            try { handler?.Invoke(this, kind, reason); }
            catch (Exception ex) { Log.Write("Fabric", "A route handler threw", ex); }
        }

        private static async Task CloseQuietlyAsync(IPeerRoute route, string reason)
        {
            try { await route.CloseAsync(reason).ConfigureAwait(false); }
            catch (Exception ex) { Log.Write("Fabric", "Closing a route failed", ex); }

            try { await route.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Write("Fabric", "Disposing a route failed", ex); }
        }

        private string Describe(IPeerRoute route) =>
            $"{route.Kind} to {DeviceIdentity.Shorten(Fingerprint)}";

        public async ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }

            await CloseAllAsync("this device is shutting down").ConfigureAwait(false);

            RouteEstablished = null;
            RouteLost = null;
            PayloadReceived = null;
        }
    }
}
