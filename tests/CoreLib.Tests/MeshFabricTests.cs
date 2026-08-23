using CoreLib.Identity;
using CoreLib.Tests.Fakes;
using CoreLib.Transport;
using CoreLib.Transport.Fabric;

namespace CoreLib.Tests;

/// <summary>
/// The peer table: three devices at once, links that arrive before anyone knows who sent them,
/// and revocation.
///
/// <para>None of this could be tested before. The radio tier held one link per process in a
/// nullable field, so "a third device" had no representation to assert on.</para>
/// </summary>
public class MeshFabricTests
{
    private sealed class Mesh : IAsyncDisposable
    {
        public FakeClock Clock { get; } = new();
        public PeerSecurity Security { get; } = PeerSecurity.CreateEphemeral();
        public MeshFabric Fabric { get; }
        public FakeRouteProvider WiFi { get; }
        public FakeRouteProvider Radio { get; }

        public Mesh(BleCapability capability = BleCapability.Both)
        {
            Fabric = new MeshFabric(Security, () => capability, Clock, RouteTimings.Default);
            WiFi = new FakeRouteProvider(RouteKind.WiFi, Clock);
            Radio = new FakeRouteProvider(RouteKind.BleCentral, Clock);
            Fabric.AddProvider(WiFi);
            Fabric.AddProvider(Radio);
        }

        /// <summary>Pairs a fresh device and returns its fingerprint.</summary>
        public string Pair(string name)
        {
            var identity = DeviceIdentity.CreateEphemeral();
            Security.Peers.Trust(identity.PublicKey, name, "127.0.0.1");
            return identity.Fingerprint;
        }

        public ValueTask DisposeAsync() => Fabric.DisposeAsync();
    }

    // ── three devices ────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Two links to two peers are not a collision.</b>
    ///
    /// <para>The Android rule guarded on "a central link exists and a peripheral link exists" and
    /// then dropped one, using the fingerprint from whichever hello had just arrived - without ever
    /// checking the two links carried the <em>same</em> peer. Phone dialling laptop A while laptop
    /// B dials the phone is two good links, and it would have torn one down. Scoping the rule to
    /// one <c>PeerLink</c> makes that unrepresentable.</para>
    /// </summary>
    [Fact]
    public async Task Two_links_to_two_different_peers_are_both_kept()
    {
        await using var mesh = new Mesh();
        string a = mesh.Pair("laptop A");
        string b = mesh.Pair("laptop B");

        var outbound = mesh.Radio.NewRoute(a).Identify(a).Establish();
        var inbound = new FakeRoute(RouteKind.BlePeripheral, mesh.Clock, b, outbound: false).Identify(b).Establish();

        mesh.Fabric.LinkTo(a)!.Adopt(outbound);
        mesh.Fabric.LinkTo(b)!.Adopt(inbound);

        Assert.True(mesh.Fabric.IsConnectedTo(a));
        Assert.True(mesh.Fabric.IsConnectedTo(b));
        Assert.False(outbound.IsClosed);
        Assert.False(inbound.IsClosed);
        Assert.Equal(2, mesh.Fabric.ConnectedPeers.Count);
    }

    [Fact]
    public async Task Every_peer_gets_its_own_link_object()
    {
        await using var mesh = new Mesh();
        string a = mesh.Pair("a");
        string b = mesh.Pair("b");
        string c = mesh.Pair("c");

        Assert.Equal(3, mesh.Fabric.Links.Count);
        Assert.NotNull(mesh.Fabric.LinkTo(a));
        Assert.NotNull(mesh.Fabric.LinkTo(b));
        Assert.NotNull(mesh.Fabric.LinkTo(c));
    }

    [Fact]
    public async Task A_broadcast_reaches_every_connected_peer_and_skips_the_rest()
    {
        await using var mesh = new Mesh();
        string a = mesh.Pair("a");
        string b = mesh.Pair("b");
        mesh.Pair("c");   // paired and unreachable

        var toA = mesh.WiFi.NewRoute(a).Identify(a).Establish();
        var toB = mesh.WiFi.NewRoute(b).Identify(b).Establish();
        mesh.Fabric.LinkTo(a)!.Adopt(toA);
        mesh.Fabric.LinkTo(b)!.Adopt(toB);

        int reached = await mesh.Fabric.BroadcastAsync(SyncContent.Text, new byte[] { 7 });

        Assert.Equal(2, reached);
        Assert.Single(toA.Sent);
        Assert.Single(toB.Sent);
    }

    // ── links that arrive before identity ────────────────────────────────────

    /// <summary>
    /// The same defect as the peer-scoped one, on the path a link takes before anyone knows who
    /// sent it. This is where a device from somebody else's mesh actually lives: connected,
    /// answering, and belonging to no peer.
    /// </summary>
    [Fact]
    public async Task A_link_that_never_says_who_it_is_is_dropped_at_the_grace()
    {
        await using var mesh = new Mesh();
        mesh.Pair("my laptop");

        var stranger = new FakeRoute(RouteKind.BleCentral, mesh.Clock).AnswersButNeverIdentifies();
        mesh.Radio.Arrive(stranger);

        Assert.Equal(1, mesh.Fabric.PendingCount);
        Assert.Equal(0, mesh.Fabric.EnforceHandshakeDeadlines());

        mesh.Clock.Advance(RouteTimings.Default.HandshakeGrace + TimeSpan.FromSeconds(1));

        Assert.Equal(1, mesh.Fabric.EnforceHandshakeDeadlines());
        Assert.True(stranger.IsClosed);
        Assert.Equal(0, mesh.Fabric.PendingCount);
        Assert.False(mesh.Fabric.IsConnectedToAny);
    }

    [Fact]
    public async Task A_link_that_identifies_inside_the_grace_joins_its_peer()
    {
        await using var mesh = new Mesh();
        string a = mesh.Pair("a");

        var arriving = new FakeRoute(RouteKind.WiFi, mesh.Clock, outbound: false).Connect();
        mesh.WiFi.Arrive(arriving);
        Assert.Equal(1, mesh.Fabric.PendingCount);

        arriving.Identify(a).Establish();

        Assert.Equal(0, mesh.Fabric.PendingCount);
        Assert.True(mesh.Fabric.IsConnectedTo(a));
    }

    /// <summary>
    /// A device that identifies as something this mesh has never paired with is closed, not merely
    /// ignored. Ignoring it is what left it holding a link.
    /// </summary>
    [Fact]
    public async Task A_link_from_an_unpaired_device_is_closed_rather_than_ignored()
    {
        await using var mesh = new Mesh();
        mesh.Pair("a");

        var elsewhere = DeviceIdentity.CreateEphemeral();
        var route = new FakeRoute(RouteKind.BleCentral, mesh.Clock, elsewhere.Fingerprint, outbound: false);
        route.Identify(elsewhere.Fingerprint);

        mesh.Radio.Arrive(route);

        Assert.True(route.IsClosed);
        Assert.False(mesh.Fabric.IsConnectedToAny);
    }

    // ── revocation ───────────────────────────────────────────────────────────

    /// <summary>
    /// Forgetting a device has to mean forgetting it now. A session holds its own key, so a link
    /// left alone keeps working until it happens to drop.
    /// </summary>
    [Fact]
    public async Task Forgetting_a_device_closes_its_routes_at_once()
    {
        await using var mesh = new Mesh();
        string a = mesh.Pair("a");
        string b = mesh.Pair("b");

        var toA = mesh.WiFi.NewRoute(a).Identify(a).Establish();
        var toB = mesh.WiFi.NewRoute(b).Identify(b).Establish();
        mesh.Fabric.LinkTo(a)!.Adopt(toA);
        mesh.Fabric.LinkTo(b)!.Adopt(toB);

        mesh.Security.Peers.Forget(a);

        // Disposal of the orphaned link is asynchronous; the route is closed either way.
        await Task.Delay(50);

        Assert.Null(mesh.Fabric.LinkTo(a));
        Assert.True(toA.IsClosed);
        Assert.False(toB.IsClosed);
        Assert.True(mesh.Fabric.IsConnectedTo(b));
    }

    // ── presence, per peer ───────────────────────────────────────────────────

    [Fact]
    public async Task Presence_is_reported_for_the_peer_that_has_it_and_no_other()
    {
        await using var mesh = new Mesh();
        string a = mesh.Pair("a");
        string b = mesh.Pair("b");

        var radio = mesh.Radio.NewRoute(a).Identify(a).Establish();
        var socket = mesh.WiFi.NewRoute(b).Identify(b).Establish();
        mesh.Fabric.LinkTo(a)!.Adopt(radio);
        mesh.Fabric.LinkTo(b)!.Adopt(socket);

        var presence = mesh.Fabric.PeersWithPresence;

        Assert.Contains(a, presence);
        Assert.DoesNotContain(b, presence);
    }

    [Fact]
    public async Task Opening_is_refused_while_a_route_is_backing_off()
    {
        await using var mesh = new Mesh();
        string a = mesh.Pair("a");

        mesh.WiFi.Queue(a, mesh.WiFi.NewRoute(a));
        Assert.True(mesh.Fabric.TryOpen(a, RouteKind.WiFi));

        var link = mesh.Fabric.LinkTo(a)!;
        await link.CloseAsync(RouteKind.WiFi, "test");
        link.NoteFailure(RouteKind.WiFi, screenOn: true);

        mesh.WiFi.Queue(a, mesh.WiFi.NewRoute(a));
        Assert.False(mesh.Fabric.TryOpen(a, RouteKind.WiFi));

        mesh.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(mesh.Fabric.TryOpen(a, RouteKind.WiFi));
    }
}
