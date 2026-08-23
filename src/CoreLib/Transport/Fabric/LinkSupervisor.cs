using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreLib.Diagnostics;

namespace CoreLib.Transport.Fabric
{
    /// <summary>
    /// Makes what is true match what policy wants, over and over, and notices when it stops.
    ///
    /// <para><b>What it replaces.</b> Five loops - the Android Bluetooth and Wi&#8209;Fi loops, the
    /// Windows dial and central loops, the Linux dial loop - each owning a slice of state and
    /// signalling the others through semaphores. Each was correct about its own slice and none of
    /// them could see the whole, which is how a device ended up scanning for a peer it already had
    /// a link to and dropping a socket to one it did not.</para>
    ///
    /// <para><b>Why a pass is idempotent.</b> Reconciling is comparing two sets and acting on the
    /// difference, so running it twice changes nothing and running it late changes nothing
    /// permanently. That is what lets anything at all signal it - a screen event, a peers change, a
    /// route closing - without a caller having to know what state the loop was in.</para>
    /// </summary>
    public sealed class LinkSupervisor : IAsyncDisposable
    {
        private readonly MeshFabric _fabric;
        private readonly Func<LocalConditions> _conditions;
        private readonly ILinkClock _clock;
        private readonly RouteTimings _timings;
        private readonly SemaphoreSlim _signal = new(0);

        private long _lastPassUtcTicks;
        private long _passes;
        private long _restarts;
        private volatile bool _disposed;

        public LinkSupervisor(MeshFabric fabric, Func<LocalConditions> conditions,
                              ILinkClock? clock = null, RouteTimings? timings = null)
        {
            _fabric = fabric ?? throw new ArgumentNullException(nameof(fabric));
            _conditions = conditions ?? throw new ArgumentNullException(nameof(conditions));
            _clock = clock ?? SystemClock.Instance;
            _timings = timings ?? fabric.Timings;
            _lastPassUtcTicks = _clock.UtcNow.Ticks;
        }

        /// <summary>Asked for the set of peers the radio should be trying to reach.</summary>
        public Action<IReadOnlySet<string>>? WantedCentralPeersChanged { get; set; }

        /// <summary>Asked to publish or withdraw the GATT service.</summary>
        public Action<bool>? AdvertisingWanted { get; set; }

        public long Passes => Interlocked.Read(ref _passes);

        /// <summary>How many times a pass had to be abandoned because it never came back.</summary>
        public long Restarts => Interlocked.Read(ref _restarts);

        public DateTime LastPassUtc => new(Interlocked.Read(ref _lastPassUtcTicks), DateTimeKind.Utc);

        /// <summary>Runs a pass now rather than at the next interval. One pending nudge is enough.</summary>
        public void Signal()
        {
            try { _signal.Release(); } catch (SemaphoreFullException) { }
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            Log.Write("Fabric", "Link supervisor started.");

            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                await RunOnePassAsync(cancellationToken).ConfigureAwait(false);

                Drain();

                try { await _signal.WaitAsync(_timings.ReconcileInterval, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

            Log.Write("Fabric", "Link supervisor stopped.");
        }

        /// <summary>
        /// One pass, raced against the watchdog.
        ///
        /// <para><b>Why the race exists.</b> <c>Console.In.ReadLineAsync</c> is a synchronized
        /// reader whose async methods run the blocking read inline, so an await that never yields
        /// stopped the thread D-Bus needed and killed the entire Bluetooth tier - while logging
        /// nothing and failing nothing. A loop that is alive but wedged is indistinguishable from a
        /// loop that is working, right up until you look at the radio. A timestamp and a race are
        /// the whole cost of catching that class of failure.</para>
        /// </summary>
        internal async Task RunOnePassAsync(CancellationToken cancellationToken)
        {
            using var pass = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // On the pool rather than inline, so a pass that blocks cannot also stop the watchdog
            // that is meant to notice.
            var work = Task.Run(() => ReconcileAsync(pass.Token), pass.Token);

            Task finished;
            try
            {
                finished = await Task.WhenAny(work, Task.Delay(_timings.SupervisorWatchdog, cancellationToken))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            if (!ReferenceEquals(finished, work))
            {
                Interlocked.Increment(ref _restarts);
                Log.Write("Fabric",
                    $"A reconcile pass did not finish within {_timings.SupervisorWatchdog.TotalSeconds:F0}s. Abandoning it and starting another.");
                try { pass.Cancel(); } catch { }
                return;
            }

            try { await work.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Write("Fabric", "A reconcile pass failed", ex); }

            Interlocked.Exchange(ref _lastPassUtcTicks, _clock.UtcNow.Ticks);
            Interlocked.Increment(ref _passes);
        }

        /// <summary>
        /// Compares what policy wants against what exists, and acts on the difference.
        /// </summary>
        internal Task ReconcileAsync(CancellationToken cancellationToken)
        {
            var local = _conditions();
            var peers = _fabric.Security.Peers.Peers;

            // Presence is read from the fabric rather than taken from the caller, so the policy
            // always sees the state this pass is about to act on.
            local = local with { PeersWithPresence = _fabric.PeersWithPresence };

            var plan = RoutePolicy.Plan(peers, local, _clock.UtcNow);

            // The deadline sweep runs first, so a route that is about to be dropped does not count
            // as satisfying a want and leave the peer unserved for another interval.
            _fabric.EnforceHandshakeDeadlines();

            foreach (var link in _fabric.Links)
            {
                if (cancellationToken.IsCancellationRequested) break;

                foreach (var route in link.AllRoutes)
                {
                    if (plan.Routes.Contains(new RouteKey(link.Fingerprint, route.Kind))) continue;

                    // Wanted no longer. Closing one route never touches another peer's, which is
                    // the difference between this and a device-wide DisconnectAll.
                    _ = link.CloseAsync(route.Kind, "policy no longer wants this route");
                }
            }

            foreach (var want in plan.Routes)
            {
                if (cancellationToken.IsCancellationRequested) break;

                // A peripheral route is not opened: the peer connects to us. Wanting it means an
                // arriving link is adopted rather than refused.
                if (want.Kind == RouteKind.BlePeripheral) continue;

                var link = _fabric.LinkTo(want.Fingerprint);
                if (link == null || link.Has(want.Kind)) continue;

                _fabric.TryOpen(want.Fingerprint, want.Kind);
            }

            var wanted = plan.BleCentralPeers
                .Where(f => _fabric.LinkTo(f)?.Has(RouteKind.BleCentral) != true)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Invoke(() => WantedCentralPeersChanged?.Invoke(wanted), "WantedCentralPeersChanged");
            Invoke(() => AdvertisingWanted?.Invoke(plan.ShouldAdvertise), "AdvertisingWanted");

            return Task.CompletedTask;
        }

        private void Drain()
        {
            while (_signal.CurrentCount > 0)
            {
                if (!_signal.Wait(0)) break;
            }
        }

        private static void Invoke(Action action, string what)
        {
            try { action(); }
            catch (Exception ex) { Log.Write("Fabric", $"{what} handler threw", ex); }
        }

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            Signal();
            _signal.Dispose();
            WantedCentralPeersChanged = null;
            AdvertisingWanted = null;
            return ValueTask.CompletedTask;
        }
    }
}
