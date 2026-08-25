using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreLib.Diagnostics;
using CoreLib.Identity;
using CoreLib.Transport.Fabric;

namespace CoreLib.Transport.Ble
{
    /// <summary>
    /// One radio, many peers.
    ///
    /// <para><b>Why a scheduler and not a loop per peer.</b> N peers cannot each own a scan.
    /// Android silently throttles an app past about five scan start/stop cycles in thirty seconds -
    /// the scan simply returns nothing, with no error and no callback - and an active scan contends
    /// with every live link for the same antenna. So one object owns the adapter and the peers ask
    /// it for what they want.</para>
    ///
    /// <para><b>What changed from every head's version.</b> All three stopped scanning the moment
    /// one link existed: <c>_bleCentral?.IsConnected != true &amp;&amp; _bleTransport?.IsConnected != true</c>
    /// on Windows, the same shape on Linux, no gate at all on Android. The question is about the
    /// peers, not the app, so the second and third device in a mesh were never reached over the
    /// radio - and nothing said why.</para>
    ///
    /// <para>Advertising is never gated on having a link. A peer that cannot advertise depends on
    /// this device staying findable.</para>
    /// </summary>
    public sealed class BleRadioScheduler : IAsyncDisposable
    {
        private readonly IBleRadio _radio;
        private readonly ILinkClock _clock;
        private readonly RouteTimings _timings;
        private readonly BleCooldowns _cooldowns;

        private readonly object _gate = new();
        private readonly Dictionary<string, CentralLink> _central = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<IPeerRoute, BleCandidate> _attempts = new();
        private readonly SemaphoreSlim _signal = new(0);

        private HashSet<string> _wanted = new(StringComparer.OrdinalIgnoreCase);
        private bool _advertising;
        private BleAdvertisement _advertisement = new();
        private DateTime _lastRotationUtc;
        private long _rounds;
        private int _barrenRounds;
        private long _recoveries;

        /// <summary>
        /// True while a human is inviting a device in and there is nothing paired to arbitrate a
        /// role with.
        ///
        /// <para>Without it a fresh install never scans - the wanted set is empty, because there
        /// is no peer to owe a link to - so it can only ever be joined and never join. On an
        /// adapter that cannot advertise that leaves it neither scanning nor advertising, which
        /// is the same deadlock reached from the other direction.</para>
        /// </summary>
        private bool _probing;
        private int _lastSeen;
        private int _lastOurs;
        private bool _disposed;

        public BleRadioScheduler(IBleRadio radio, ILinkClock? clock = null, RouteTimings? timings = null,
                                 BleCooldowns? cooldowns = null)
        {
            _radio = radio ?? throw new ArgumentNullException(nameof(radio));
            _clock = clock ?? SystemClock.Instance;
            _timings = timings ?? RouteTimings.Default;
            _cooldowns = cooldowns ?? new BleCooldowns(_clock, _timings.RefusalCooldown);
            _lastRotationUtc = _clock.UtcNow;

            CentralRoutes = new BleProvider(RouteKind.BleCentral);
            InboundRoutes = new BleProvider(RouteKind.BlePeripheral);

            _radio.InboundRoute += OnInbound;
        }

        /// <summary>Links this device opened. Fed into the fabric as they come up.</summary>
        public IRouteProvider CentralRoutes { get; }

        /// <summary>Links a peer opened to this device's advertised service.</summary>
        public IRouteProvider InboundRoutes { get; }

        public BleCooldowns Cooldowns => _cooldowns;

        /// <summary>
        /// Whether a candidate's advertisement says it belongs to this mesh.
        ///
        /// <para>Left accepting everything until there is a mesh key to check against, which is the
        /// behaviour every version before this had. Once a beacon is being published, this is what
        /// makes a refusal cost nothing instead of a connect, an MTU exchange and a hello.</para>
        /// </summary>
        public Func<BleCandidate, bool> BeaconFilter { get; set; } = _ => true;

        /// <summary>
        /// How to order what a round found: a verified beacon before a silent advertisement.
        ///
        /// <para>Signal strength alone put a foreign phone sitting closer than your own at the
        /// front of every round. Ranking by mesh first means a device that has proved which mesh
        /// it is in is tried before one that has not, whatever the RSSI.</para>
        /// </summary>
        public Func<BleCandidate, int> BeaconRank { get; set; } = _ => 0;

        public long Rounds => Interlocked.Read(ref _rounds);

        /// <summary>What the last round saw, for the health surface: "4 seen, 1 ours".</summary>
        public (int Seen, int Ours) LastRound
        {
            get { lock (_gate) return (_lastSeen, _lastOurs); }
        }

        public int LiveCentralLinks { get { lock (_gate) return _central.Count; } }

        public bool IsAdvertising { get { lock (_gate) return _advertising; } }

        public string Status => _radio.Status;

        /// <summary>
        /// The peers this device is owed an outbound link to.
        ///
        /// Called every reconcile pass. An empty set stops the scan entirely - not because a link
        /// exists, but because nothing is missing one.
        /// </summary>
        /// <summary>
        /// How many rounds in a row have found nothing at all while something was wanted.
        ///
        /// The escalation signal: a radio that has been asked for peers and has seen no
        /// advertisement of any kind for several full windows is more likely to be wedged than
        /// to be in an empty room.
        /// </summary>
        public int BarrenRounds { get { lock (_gate) return _barrenRounds; } }

        /// <summary>How many times the adapter has been restarted to get out of that.</summary>
        public long Recoveries => Interlocked.Read(ref _recoveries);

        /// <summary>
        /// Scan even with nothing wanted, because a human is pairing something in.
        ///
        /// Fed from <c>RoutePolicy.ShouldScan</c>, which is where the rule lives.
        /// </summary>
        public void SetProbing(bool probing)
        {
            bool changed;
            lock (_gate)
            {
                changed = _probing != probing;
                _probing = probing;
            }

            if (changed) Signal();
        }

        public void SetWanted(IReadOnlySet<string> peers)
        {
            bool changed;
            lock (_gate)
            {
                var next = new HashSet<string>(peers ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
                changed = !next.SetEquals(_wanted);
                _wanted = next;
            }

            if (changed) Signal();
        }

        /// <summary>Publishes or withdraws the service. Idempotent, and never gated on having a link.</summary>
        public async Task SetAdvertisingAsync(bool wanted, BleAdvertisement? advertisement = null)
        {
            BleAdvertisement toPublish;
            bool change;

            lock (_gate)
            {
                if (advertisement != null) _advertisement = advertisement;
                toPublish = _advertisement;
                change = wanted != _advertising;
                if (change) _advertising = wanted;
            }

            if (!change)
            {
                // Re-publish when the beacon itself changed, so a rotated epoch or a newly minted
                // mesh key reaches the air without waiting for a restart.
                if (wanted && advertisement != null)
                {
                    try { await _radio.StartAdvertisingAsync(toPublish).ConfigureAwait(false); }
                    catch (Exception ex) { Log.Write("Ble", "Refreshing the advertisement failed", ex); }
                }

                return;
            }

            try
            {
                if (wanted) await _radio.StartAdvertisingAsync(toPublish).ConfigureAwait(false);
                else await _radio.StopAdvertisingAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write("Ble", wanted ? "Could not start advertising" : "Could not stop advertising", ex);
                lock (_gate) _advertising = !wanted;
            }
        }

        public void Signal()
        {
            try { _signal.Release(); } catch (SemaphoreFullException) { }
        }

        // ──────────────────────────────── the loop

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            Log.Write("Ble", "Radio scheduler started.");

            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                TimeSpan wait;

                try { wait = await RunRoundAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.Write("Ble", "A scan round failed", ex);
                    wait = _timings.ReconcileInterval;
                }

                try { await _signal.WaitAsync(wait, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

            Log.Write("Ble", "Radio scheduler stopped.");
        }

        /// <summary>One round. Returns how long to wait before the next.</summary>
        internal async Task<TimeSpan> RunRoundAsync(CancellationToken cancellationToken)
        {
            await RotateIfCrowdedAsync().ConfigureAwait(false);

            List<string> wanted;
            int live;
            lock (_gate)
            {
                wanted = _wanted.Where(f => !_central.ContainsKey(f)).ToList();
                live = _central.Count;
            }

            bool probing;
            lock (_gate) probing = _probing;

            // Nothing missing a link and nobody pairing: stop scanning altogether. Advertising
            // continues, because a peer that cannot advertise depends on this device staying
            // findable.
            if (wanted.Count == 0 && !probing)
            {
                lock (_gate) _barrenRounds = 0;
                return _timings.ReconcileInterval;
            }

            if (!_radio.IsAvailable) return _timings.ReconcileInterval;

            if (live >= _timings.MaxBleCentralLinks)
            {
                // At the ceiling. Waiting for the rotation window is the whole answer; scanning
                // now would find peers there is nowhere to put.
                return _timings.RotationInterval;
            }

            Interlocked.Increment(ref _rounds);

            IReadOnlyList<BleCandidate> seen;

            // A round that never comes back must not be the end of Bluetooth.
            //
            // The radio talks to the platform, and a platform call that goes unanswered awaits for
            // ever: no error, no exception, nothing in the log. Observed on Linux, where one
            // unanswered BlueZ call left the last scan line three hours old and the adapter still
            // discovering, because the round's own cleanup never ran either - while Wi-Fi, the
            // clipboard and the notifications all carried on, so nothing about the symptom pointed
            // here.
            //
            // Cancelled through a linked token rather than abandoned with WaitAsync, so the radio
            // gets to run its own finally and hand the antenna back.
            using var round = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            round.CancelAfter(_timings.ScanRoundBudget);

            try
            {
                seen = await _radio.ScanAsync(_timings.ScanWindow, round.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;   // Shutting down, which is not a failure.
            }
            catch (OperationCanceledException)
            {
                Log.Write("Ble",
                    $"A scan round did not finish within {_timings.ScanRoundBudget.TotalSeconds:0}s " +
                    "and was abandoned; the radio will be asked again.");
                return _timings.ScanInterval;
            }
            catch (Exception ex)
            {
                Log.Write("Ble", "Scanning failed", ex);
                return _timings.ScanInterval;
            }

            var usable = seen
                .Where(c => c.IsPresent)

                // Filtered here rather than refused inside the connect. The same device is found
                // on every round while its link is up, and treating that as a failed attempt put
                // a peer this radio is successfully talking to into the refusal cooldown.
                .Where(c => !_radio.HasLinkTo(c.Address))
                .Where(c => !_cooldowns.ShouldSkip(c))
                .Where(c => BeaconFilter(c))
                .OrderBy(c => BeaconRank(c))
                .ThenByDescending(c => c.Rssi)
                .ToList();

            // Counted before the "already linked" filter, not after. "Ours" means how many of the
            // devices in range belong to this mesh - a peer whose link is already up is still one
            // of ours, and reporting "1 seen, 0 ours" beside an established link to that very
            // device is precisely the sort of misleading line this surface exists to avoid.
            int ours = seen.Count(c => c.IsPresent && BeaconFilter(c));

            lock (_gate)
            {
                _lastSeen = seen.Count;
                _lastOurs = ours;
            }

            if (usable.Count == 0)
            {
                // Say which filter emptied the round, not just that it is empty.
                //
                // This line used to report "none of them in this mesh" whenever `usable` came out
                // empty - for any of the four reasons. `ours` is counted three lines above and was
                // not used, so a peer that was merely cooling off after a refusal, or one whose
                // link was already up, was reported as belonging to somebody else's mesh. That is
                // the worst possible lie to tell here, because it sends the reader to the mesh key
                // and the pairing when the radio is working exactly as intended.
                int linked = seen.Count(c => c.IsPresent && BeaconFilter(c) && _radio.HasLinkTo(c.Address));
                int cooling = ours - linked;

                Log.Write("Ble", seen.Count == 0
                    ? "Nothing in range advertising the Mesh Sync service."
                    : ours == 0
                        ? $"{seen.Count} device(s) advertising the service, none of them in this mesh."
                        : $"{seen.Count} seen, {ours} in this mesh: {linked} already linked, " +
                          $"{cooling} cooling off after a refusal.");

                // Only a round that saw *nothing at all* counts towards recovery. A round that
                // saw devices and refused them is the radio working exactly as intended.
                if (seen.Count == 0) await NoteBarrenRoundAsync().ConfigureAwait(false);
                else lock (_gate) _barrenRounds = 0;

                return _timings.ScanInterval;
            }

            lock (_gate) _barrenRounds = 0;

            // Room for several, not one. A round that connects to a single candidate and ends is
            // how one foreign device sitting closer than your own took every round.
            int room = Math.Max(0, _timings.MaxBleCentralLinks - LiveCentralLinks);
            int opened = 0;

            foreach (var candidate in usable.Take(room))
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (await TryConnectAsync(candidate, cancellationToken).ConfigureAwait(false)) opened++;
            }

            return opened > 0 ? _timings.ReconcileInterval : _timings.ScanInterval;
        }

        /// <summary>
        /// Restarts the adapter's advertising after several rounds that saw nothing at all.
        ///
        /// <para>Rung four of the failsafe ladder. A radio that has been asked for peers and has
        /// seen no advertisement of any kind for several full windows is more likely to be wedged
        /// than to be in an empty room - and the failure that motivates it is real: killing a
        /// process orphans its GATT registration, and a peer then keeps discovering the orphan,
        /// connects, subscribes, both ends report success, and nothing crosses. Quitting
        /// gracefully recovers; a crash needs the adapter toggled.</para>
        ///
        /// <para>Cheap and safe: stopping and starting an advertisement costs nothing when it was
        /// working, and the count resets the moment a round sees anything.</para>
        /// </summary>
        private async Task NoteBarrenRoundAsync()
        {
            int barren;
            bool advertising;

            lock (_gate)
            {
                barren = ++_barrenRounds;
                advertising = _advertising;
            }

            if (barren < BarrenRoundsBeforeRecovery || !advertising) return;

            lock (_gate) _barrenRounds = 0;
            Interlocked.Increment(ref _recoveries);

            Log.Write("Ble",
                $"{barren} scan rounds saw nothing at all while peers were wanted; republishing the service.");

            try
            {
                await _radio.StopAdvertisingAsync().ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                await _radio.StartAdvertisingAsync(_advertisement).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write("Ble", "Republishing the service failed", ex);
            }
        }

        /// <summary>
        /// Three full windows of complete silence. Long enough that an empty room does not trigger
        /// it constantly, short enough that a wedged radio is not left all night.
        /// </summary>
        private const int BarrenRoundsBeforeRecovery = 3;

        private async Task<bool> TryConnectAsync(BleCandidate candidate, CancellationToken cancellationToken)
        {
            IPeerRoute? route;

            try
            {
                route = await _radio.ConnectAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Write("Ble", $"Connecting to {candidate.Address} failed", ex);
                _cooldowns.Refuse(candidate.Address, null, candidate.Name);
                return false;
            }

            if (route == null)
            {
                // The radio declined to open one. It has said why; this records the refusal so
                // the next round spends its window on something else.
                _cooldowns.Refuse(candidate.Address, null, candidate.Name);
                return false;
            }

            lock (_gate) _attempts[route] = candidate;

            route.StateChanged += OnAttemptState;
            route.PayloadReceived += OnRoutePayload;

            // Caught up by hand, because the link can already be established by the time the
            // handler is attached: a peripheral sends its hello the instant a central subscribes,
            // and the connect above does not return until it has subscribed. Missing that
            // transition left the link out of the budget entirely - the cap was never enforced
            // and rotation never ran, both silently, with the health surface reporting 0 links
            // beside a route that had been up for minutes.
            OnAttemptState(route, RouteState.Connecting, route.State);

            // Handed straight to the fabric, which holds it under the handshake deadline until it
            // says who it is. That window is where a device from another mesh used to live for as
            // long as it stayed in range.
            ((BleProvider)CentralRoutes).Publish(route);
            return true;
        }

        private void OnInbound(IPeerRoute route)
        {
            if (route == null) return;

            route.StateChanged += OnInboundState;
            route.PayloadReceived += OnRoutePayload;

            // Same catch-up as the outbound half, for the same reason.
            OnInboundState(route, RouteState.Connecting, route.State);

            ((BleProvider)InboundRoutes).Publish(route);
        }

        // ──────────────────────────────── learning from outcomes

        private void OnAttemptState(IPeerRoute route, RouteState from, RouteState to)
        {
            if (to == RouteState.Established)
            {
                lock (_gate)
                {
                    _attempts.Remove(route);
                    _central[route.PeerFingerprint] = new CentralLink(route, _clock.UtcNow);
                }

                Log.Write("Ble",
                    $"Radio link up to {DeviceIdentity.Shorten(route.PeerFingerprint)}. {LiveCentralLinks} of {_timings.MaxBleCentralLinks}.");
                return;
            }

            if (to is not (RouteState.Backoff or RouteState.Idle)) return;

            BleCandidate candidate;
            bool wasAttempt;

            lock (_gate)
            {
                wasAttempt = _attempts.Remove(route, out candidate);

                if (_central.TryGetValue(route.PeerFingerprint, out var held) && ReferenceEquals(held.Route, route))
                {
                    _central.Remove(route.PeerFingerprint);
                }
            }

            route.StateChanged -= OnAttemptState;
            route.PayloadReceived -= OnRoutePayload;

            if (wasAttempt && from != RouteState.Established)
            {
                // It never became usable. Remember it against everything known, or the next round
                // finds the same device and spends the whole grace on it again.
                _cooldowns.Refuse(candidate.Address, route.PeerFingerprint, candidate.Name);

                Log.Write("Ble",
                    $"{candidate.Name ?? candidate.Address} produced no session; ignoring it for {_cooldowns.Duration.TotalMinutes:F0} minutes.");
            }

            Signal();
        }

        private void OnInboundState(IPeerRoute route, RouteState from, RouteState to)
        {
            if (to is not (RouteState.Backoff or RouteState.Idle)) return;

            route.StateChanged -= OnInboundState;
            route.PayloadReceived -= OnRoutePayload;
            Signal();
        }

        /// <summary>
        /// Notes that a link carried something.
        ///
        /// Rotation orders by last payload rather than by last connect, so a link that only ever
        /// heartbeats yields its slot to one that is being used.
        /// </summary>
        private void OnRoutePayload(IPeerRoute route, RoutePayload payload)
        {
            lock (_gate)
            {
                if (_central.TryGetValue(route.PeerFingerprint, out var held) && ReferenceEquals(held.Route, route))
                {
                    _central[route.PeerFingerprint] = held with { LastUsefulUtc = _clock.UtcNow };
                }
            }
        }

        // ──────────────────────────────── rotation

        /// <summary>
        /// Gives a waiting peer a slot when there are more of them than there are slots.
        ///
        /// <para>Four concurrent central links covers a phone, a laptop and a desktop with headroom
        /// and sits inside every platform ceiling - a GATT central holds around seven on Android.
        /// A fifth paired device that needs the radio has to be handled deliberately, because the
        /// alternative is that it silently never connects, which is the failure this whole change
        /// is about.</para>
        ///
        /// <para>A link is never cut mid-transfer: only one that is <see cref="RouteState.Established"/>
        /// and has carried nothing for a whole rotation window is eligible.</para>
        /// </summary>
        internal async Task<bool> RotateIfCrowdedAsync()
        {
            CentralLink? victim = null;

            lock (_gate)
            {
                if (_clock.UtcNow - _lastRotationUtc < _timings.RotationInterval) return false;

                int waiting = _wanted.Count(f => !_central.ContainsKey(f));
                if (waiting == 0 || _central.Count < _timings.MaxBleCentralLinks) return false;

                _lastRotationUtc = _clock.UtcNow;

                // Cast to the nullable before FirstOrDefault: CentralLink is a struct, so the
                // plain overload answers default(CentralLink) rather than null, and a null check
                // against it passes with a null Route inside. Found by the test below.
                victim = _central.Values
                    .Where(l => l.Route.State == RouteState.Established)
                    .Where(l => _clock.UtcNow - l.LastUsefulUtc >= _timings.RotationInterval)
                    .OrderBy(l => l.LastUsefulUtc)
                    .Select(l => (CentralLink?)l)
                    .FirstOrDefault();

                if (victim.HasValue) _central.Remove(victim.Value.Route.PeerFingerprint);
            }

            if (!victim.HasValue) return false;

            Log.Write("Ble",
                $"{_timings.MaxBleCentralLinks} radio links and more peers waiting; rotating {DeviceIdentity.Shorten(victim.Value.Route.PeerFingerprint)} out.");

            try { await victim.Value.Route.CloseAsync("rotated out to give a waiting peer a slot").ConfigureAwait(false); }
            catch (Exception ex) { Log.Write("Ble", "Rotating a link out failed", ex); }

            return true;
        }

        private readonly record struct CentralLink(IPeerRoute Route, DateTime LastUsefulUtc);

        // ──────────────────────────────── the two providers

        /// <summary>
        /// A provider that never opens anything on request.
        ///
        /// <para>A radio link is not opened per peer on demand the way a socket is: the scheduler
        /// scans and hands over what it finds. <see cref="Open"/> answering null is the ordinary
        /// case rather than a failure, so it must not deepen a backoff.</para>
        /// </summary>
        private sealed class BleProvider : IRouteProvider
        {
            public BleProvider(RouteKind kind) => Kind = kind;

            public RouteKind Kind { get; }

            public bool IsAvailable { get; set; } = true;

            public event Action<IPeerRoute>? RouteArrived;

            public IPeerRoute? Open(PeerRecord peer) => null;

            public void Publish(IPeerRoute route)
            {
                try { RouteArrived?.Invoke(route); }
                catch (Exception ex) { Log.Write("Ble", "A RouteArrived handler threw", ex); }
            }

            public ValueTask DisposeAsync()
            {
                RouteArrived = null;
                return ValueTask.CompletedTask;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            _radio.InboundRoute -= OnInbound;
            Signal();

            List<IPeerRoute> routes;
            lock (_gate)
            {
                routes = _central.Values.Select(l => l.Route).Concat(_attempts.Keys).ToList();
                _central.Clear();
                _attempts.Clear();
            }

            foreach (var route in routes)
            {
                route.StateChanged -= OnAttemptState;
                route.StateChanged -= OnInboundState;
                route.PayloadReceived -= OnRoutePayload;
                try { await route.CloseAsync("this device is shutting down").ConfigureAwait(false); } catch { }
            }

            await CentralRoutes.DisposeAsync().ConfigureAwait(false);
            await InboundRoutes.DisposeAsync().ConfigureAwait(false);
            await _radio.DisposeAsync().ConfigureAwait(false);

            _signal.Dispose();
        }
    }
}
