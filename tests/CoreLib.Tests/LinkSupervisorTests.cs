using CoreLib.Identity;
using CoreLib.Tests.Fakes;
using CoreLib.Transport;
using CoreLib.Transport.Fabric;

namespace CoreLib.Tests;

/// <summary>
/// The reconcile pass, and the watchdog over it.
///
/// <para>The watchdog case is the one worth reading. <c>Console.In.ReadLineAsync</c> is a
/// synchronized reader whose async methods run the blocking read inline, so an await that never
/// yielded stopped the thread D-Bus needed and killed the entire Bluetooth tier - while failing
/// nothing and logging nothing. A loop that is alive but wedged looks exactly like a loop that is
/// working. This is the test for that class of failure.</para>
/// </summary>
public class LinkSupervisorTests
{
    private static readonly RouteTimings Brisk = RouteTimings.Default with
    {
        ReconcileInterval = TimeSpan.FromMilliseconds(20),
        SupervisorWatchdog = TimeSpan.FromMilliseconds(120),
    };

    private sealed class Rig : IAsyncDisposable
    {
        public FakeClock Clock { get; } = new();
        public PeerSecurity Security { get; } = PeerSecurity.CreateEphemeral();
        public MeshFabric Fabric { get; }
        public LinkSupervisor Supervisor { get; }
        public FakeRouteProvider WiFi { get; }
        public FakeRouteProvider Radio { get; }
        public LocalConditions Conditions { get; set; }

        public Rig(RouteTimings? timings = null, Func<LocalConditions>? conditions = null)
        {
            var used = timings ?? Brisk;
            Fabric = new MeshFabric(Security, () => BleCapability.Central, Clock, used);
            WiFi = new FakeRouteProvider(RouteKind.WiFi, Clock);
            Radio = new FakeRouteProvider(RouteKind.BleCentral, Clock);
            Fabric.AddProvider(WiFi);
            Fabric.AddProvider(Radio);

            Conditions = new LocalConditions
            {
                LocalFingerprint = Security.Identity.Fingerprint,
                ScreenOn = true,
                LocalCapability = BleCapability.Central,
            };

            Supervisor = new LinkSupervisor(Fabric, conditions ?? (() => Conditions), Clock, used);
        }

        private int _paired;

        public string Pair(string name)
        {
            var identity = DeviceIdentity.CreateEphemeral();

            // A port each, because two paired devices at one address:port means one of the two
            // records is stale - and the fabric now declines to dial the stale one.
            Security.Peers.Trust(identity.PublicKey, name, $"127.0.0.1:{45001 + _paired++}");
            return identity.Fingerprint;
        }

        public async ValueTask DisposeAsync()
        {
            await Supervisor.DisposeAsync();
            await Fabric.DisposeAsync();
        }
    }

    // ── reconciling ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_pass_opens_the_routes_policy_wants()
    {
        await using var rig = new Rig();
        string a = rig.Pair("a");
        string b = rig.Pair("b");

        rig.WiFi.Queue(a, rig.WiFi.NewRoute(a));
        rig.WiFi.Queue(b, rig.WiFi.NewRoute(b));

        await rig.Supervisor.ReconcileAsync(CancellationToken.None);

        Assert.Contains(a, rig.WiFi.OpenedFor);
        Assert.Contains(b, rig.WiFi.OpenedFor);
    }

    /// <summary>
    /// Closing one route never touches another peer's. That difference is the whole of the defect
    /// where a radio link to one device dropped the socket to every other.
    /// </summary>
    [Fact]
    public async Task A_pass_closes_only_the_route_policy_stopped_wanting()
    {
        await using var rig = new Rig();
        string a = rig.Pair("a");
        string b = rig.Pair("b");

        var toA = rig.WiFi.NewRoute(a).Identify(a).Establish();
        var toB = rig.WiFi.NewRoute(b).Identify(b).Establish();
        rig.Fabric.LinkTo(a)!.Adopt(toA);
        rig.Fabric.LinkTo(b)!.Adopt(toB);

        // Screen off, and only A has something carrying presence. B still needs its socket.
        var radio = rig.Radio.NewRoute(a).Identify(a).Establish();
        rig.Fabric.LinkTo(a)!.Adopt(radio);
        rig.Conditions = rig.Conditions with { ScreenOn = false };

        await rig.Supervisor.ReconcileAsync(CancellationToken.None);
        await Task.Delay(50);

        Assert.True(toA.IsClosed);
        Assert.False(toB.IsClosed);
        Assert.True(rig.Fabric.IsConnectedTo(a));   // still reachable, over the radio
        Assert.True(rig.Fabric.IsConnectedTo(b));
    }

    /// <summary>
    /// Every unserved peer reaches the radio, not just the first. The old condition was "is any
    /// link up", which stopped the scan the moment one peer was reached.
    /// </summary>
    [Fact]
    public async Task Every_unserved_peer_reaches_the_radio_not_just_the_first()
    {
        await using var rig = new Rig();
        string a = rig.Pair("a");
        string b = rig.Pair("b");
        string c = rig.Pair("c");

        IReadOnlySet<string>? wanted = null;
        rig.Supervisor.WantedCentralPeersChanged = set => wanted = set;

        var toA = rig.Radio.NewRoute(a).Identify(a).Establish();
        rig.Fabric.LinkTo(a)!.Adopt(toA);

        await rig.Supervisor.ReconcileAsync(CancellationToken.None);

        Assert.NotNull(wanted);
        Assert.DoesNotContain(a, wanted!);
        Assert.Contains(b, wanted!);
        Assert.Contains(c, wanted!);
    }

    [Fact]
    public async Task Advertising_is_asked_for_only_when_the_radio_can_do_it()
    {
        await using var rig = new Rig();
        rig.Pair("a");

        bool? asked = null;
        rig.Supervisor.AdvertisingWanted = want => asked = want;

        await rig.Supervisor.ReconcileAsync(CancellationToken.None);
        Assert.False(asked);   // this rig reports Central only

        rig.Conditions = rig.Conditions with { LocalCapability = BleCapability.Both };
        await rig.Supervisor.ReconcileAsync(CancellationToken.None);
        Assert.True(asked);
    }

    /// <summary>A pass is a set comparison, so running it twice must change nothing.</summary>
    [Fact]
    public async Task A_pass_is_idempotent()
    {
        await using var rig = new Rig();
        string a = rig.Pair("a");

        var toA = rig.WiFi.NewRoute(a).Identify(a).Establish();
        rig.Fabric.LinkTo(a)!.Adopt(toA);

        await rig.Supervisor.ReconcileAsync(CancellationToken.None);
        await rig.Supervisor.ReconcileAsync(CancellationToken.None);
        await rig.Supervisor.ReconcileAsync(CancellationToken.None);

        Assert.False(toA.IsClosed);
        Assert.Single(rig.Fabric.LinkTo(a)!.AllRoutes);
    }

    [Fact]
    public async Task A_pass_enforces_the_handshake_deadline()
    {
        await using var rig = new Rig();
        rig.Pair("a");

        var stranger = new FakeRoute(RouteKind.BleCentral, rig.Clock).AnswersButNeverIdentifies();
        rig.Radio.Arrive(stranger);

        rig.Clock.Advance(Brisk.HandshakeGrace + TimeSpan.FromSeconds(1));
        await rig.Supervisor.ReconcileAsync(CancellationToken.None);

        Assert.True(stranger.IsClosed);
    }

    // ── the watchdog ─────────────────────────────────────────────────────────

    /// <summary>
    /// A pass that never comes back is abandoned, counted and followed by another.
    ///
    /// <para>The wedge here is a synchronous call inside the pass that never returns - the same
    /// shape as the console read that stopped the Bluetooth tier dead. Nothing throws and nothing
    /// logs an error, which is exactly why a timestamp and a race are the only things that catch
    /// it.</para>
    /// </summary>
    [Fact]
    public async Task A_pass_that_never_returns_is_abandoned_and_counted()
    {
        using var wedge = new ManualResetEventSlim(false);

        await using var rig = new Rig(conditions: () =>
        {
            wedge.Wait(TimeSpan.FromSeconds(10));
            return new LocalConditions { LocalFingerprint = "local" };
        });

        long before = rig.Supervisor.Restarts;

        await rig.Supervisor.RunOnePassAsync(CancellationToken.None);

        Assert.Equal(before + 1, rig.Supervisor.Restarts);
        Assert.Equal(0, rig.Supervisor.Passes);

        // Let the abandoned pass unwind rather than leaving a pool thread parked for the suite.
        wedge.Set();
    }

    [Fact]
    public async Task A_pass_that_returns_is_counted_and_timestamped()
    {
        await using var rig = new Rig();
        rig.Pair("a");

        var before = rig.Supervisor.LastPassUtc;
        rig.Clock.Advance(TimeSpan.FromSeconds(5));

        await rig.Supervisor.RunOnePassAsync(CancellationToken.None);

        Assert.Equal(1, rig.Supervisor.Passes);
        Assert.Equal(0, rig.Supervisor.Restarts);
        Assert.True(rig.Supervisor.LastPassUtc > before);
    }

    /// <summary>A handler that throws must not take the supervisor down with it.</summary>
    [Fact]
    public async Task A_throwing_handler_does_not_stop_the_pass()
    {
        await using var rig = new Rig();
        rig.Pair("a");

        rig.Supervisor.WantedCentralPeersChanged = _ => throw new InvalidOperationException("boom");

        await rig.Supervisor.RunOnePassAsync(CancellationToken.None);

        Assert.Equal(1, rig.Supervisor.Passes);
        Assert.Equal(0, rig.Supervisor.Restarts);
    }
}
