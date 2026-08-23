using CoreLib.Identity;
using CoreLib.Tests.Fakes;
using CoreLib.Transport;
using CoreLib.Transport.Fabric;

namespace CoreLib.Tests;

/// <summary>
/// What the connection layer can say about itself from outside the process.
///
/// <para>Invariant I9. Diagnosing this used to mean three log files on three devices, one of them
/// through <c>adb logcat</c>, and inferring state no head could actually report.</para>
/// </summary>
public class MeshHealthTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public FakeClock Clock { get; } = new();
        public PeerSecurity Security { get; } = PeerSecurity.CreateEphemeral();
        public MeshFabric Fabric { get; }
        public FakeRouteProvider WiFi { get; }
        public FakeRouteProvider Radio { get; }

        public Rig()
        {
            Fabric = new MeshFabric(Security, () => BleCapability.Both, Clock);
            WiFi = new FakeRouteProvider(RouteKind.WiFi, Clock);
            Radio = new FakeRouteProvider(RouteKind.BleCentral, Clock);
            Fabric.AddProvider(WiFi);
            Fabric.AddProvider(Radio);
        }

        public string Pair(string name)
        {
            var identity = DeviceIdentity.CreateEphemeral();
            Security.Peers.Trust(identity.PublicKey, name);
            return identity.Fingerprint;
        }

        public MeshHealth Snapshot() =>
            MeshHealth.Of(Fabric, Clock, Clock.UtcNow, passes: 412, restarts: 0,
                          radioStatus: "scanning", radioLinks: 1, radioBudget: 4,
                          advertising: true, lastScan: (4, 1));

        public ValueTask DisposeAsync() => Fabric.DisposeAsync();
    }

    [Fact]
    public async Task Every_peer_appears_with_every_route_it_has()
    {
        await using var rig = new Rig();
        string phone = rig.Pair("S21 FE");
        string desktop = rig.Pair("MSI-SURYANSHU");

        var radio = rig.Radio.NewRoute(phone).Identify(phone).Establish();
        var socket = rig.WiFi.NewRoute(desktop).Identify(desktop).Establish();
        rig.Fabric.LinkTo(phone)!.Adopt(radio);
        rig.Fabric.LinkTo(desktop)!.Adopt(socket);

        var health = rig.Snapshot();

        Assert.Equal(2, health.Peers.Count);
        Assert.All(health.Peers, p => Assert.True(p.IsConnected));

        var phoneHealth = health.Peers.Single(p => p.Name == "S21 FE");
        Assert.Equal(RouteKind.BleCentral, phoneHealth.Routes.Single().Kind);
        Assert.Equal(RouteState.Established, phoneHealth.Routes.Single().State);
    }

    /// <summary>
    /// Two devices with the same name are two rows, because the answer is per peer now. Windows
    /// could mark only one device connected and guessed which by comparing names, which broke on
    /// exactly this.
    /// </summary>
    [Fact]
    public async Task Two_devices_called_the_same_thing_are_still_two_peers()
    {
        await using var rig = new Rig();
        string first = rig.Pair("Laptop");
        string second = rig.Pair("Laptop");

        var live = rig.WiFi.NewRoute(first).Identify(first).Establish();
        rig.Fabric.LinkTo(first)!.Adopt(live);

        var health = rig.Snapshot();

        Assert.Equal(2, health.Peers.Count);
        Assert.Single(health.Peers, p => p.IsConnected);
        Assert.Equal(first, health.Peers.Single(p => p.IsConnected).Fingerprint);
        Assert.Equal(second, health.Peers.Single(p => !p.IsConnected).Fingerprint);
    }

    /// <summary>The line that turns six minutes of guessing into one glance.</summary>
    [Fact]
    public async Task The_table_says_what_the_last_scan_actually_found()
    {
        await using var rig = new Rig();
        rig.Pair("Framework 13");

        string table = rig.Snapshot().ToTable();

        Assert.Contains("last round 4 seen, 1 ours", table);
        Assert.Contains("1/4 links", table);
        Assert.Contains("advertising", table);
    }

    [Fact]
    public async Task A_route_backing_off_says_why_and_when_it_will_try_again()
    {
        await using var rig = new Rig();
        string peer = rig.Pair("Framework 13");

        var route = rig.WiFi.NewRoute(peer).Identify(peer).Establish();
        rig.Fabric.LinkTo(peer)!.Adopt(route);
        route.Drop("connect timed out");
        rig.Fabric.LinkTo(peer)!.NoteFailure(RouteKind.WiFi, screenOn: true);

        // The route object is gone, and the reason is not: a peer that is unreachable with no
        // explanation is most of what makes this layer hard to diagnose.
        var health = MeshHealth.Of(rig.Fabric, rig.Clock, rig.Clock.UtcNow, 1, 0);

        Assert.Single(health.Peers);
        Assert.Contains("connect timed out", health.ToTable());
        Assert.Contains("retry in", health.ToTable());
    }

    [Fact]
    public async Task A_link_that_has_not_identified_itself_is_counted_separately()
    {
        await using var rig = new Rig();
        rig.Pair("mine");

        rig.Radio.Arrive(new FakeRoute(RouteKind.BleCentral, rig.Clock).AnswersButNeverIdentifies());

        var health = rig.Snapshot();

        Assert.Equal(1, health.Handshaking);
        Assert.Contains("connected and not yet identified", health.ToTable());
    }

    [Fact]
    public async Task An_abandoned_supervisor_pass_is_visible()
    {
        await using var rig = new Rig();

        var health = MeshHealth.Of(rig.Fabric, rig.Clock, rig.Clock.UtcNow, passes: 10, restarts: 3);

        Assert.Equal(3, health.SupervisorRestarts);
        Assert.Contains("3 restarts", health.ToTable());
    }

    [Fact]
    public async Task A_mesh_with_nothing_paired_says_so_rather_than_printing_an_empty_table()
    {
        await using var rig = new Rig();

        Assert.Contains("(nothing paired)", rig.Snapshot().ToTable());
    }

    [Fact]
    public async Task The_beacon_state_is_reported()
    {
        await using var rig = new Rig();
        rig.Pair("a");

        Assert.False(rig.Snapshot().HasMeshKey);
        Assert.Contains("beacon off", rig.Snapshot().ToTable());

        rig.Security.Peers.MintMeshKeyIfMissing();

        Assert.True(rig.Snapshot().HasMeshKey);
        Assert.Contains("beacon on", rig.Snapshot().ToTable());
    }
}
