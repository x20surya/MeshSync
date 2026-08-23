using CoreLib.Identity;
using CoreLib.Tests.Fakes;
using CoreLib.Transport;
using CoreLib.Transport.Fabric;

namespace CoreLib.Tests;

/// <summary>
/// One peer's routes: the handshake deadline, the two collision rules, and which route carries
/// what.
///
/// <para>The first case is the one this refactor exists for.</para>
/// </summary>
public class PeerLinkTests
{
    private static readonly RouteTimings Timings = RouteTimings.Default;

    private static (PeerLink Link, FakeClock Clock, string LocalFingerprint, string PeerFingerprint) Link(
        BleCapability capability = BleCapability.Both)
    {
        var clock = new FakeClock();
        var local = DeviceIdentity.CreateEphemeral();
        var peerIdentity = DeviceIdentity.CreateEphemeral();
        var peer = new PeerRecord { PublicKey = peerIdentity.PublicKey, Name = "peer" };

        var link = new PeerLink(peer, local.Fingerprint, () => capability, clock, Timings);
        return (link, clock, local.Fingerprint, peer.Fingerprint);
    }

    // ── the handshake deadline ───────────────────────────────────────────────

    /// <summary>
    /// <b>The stranger lock, as a unit test.</b>
    ///
    /// <para>Every install advertises the same service UUID, so a scan finds other people's
    /// meshes. On Android the central connected, negotiated an MTU, subscribed, pinged, was
    /// answered - ping is answered before identity by design - and then failed the key agreement
    /// and <em>returned without dropping the link</em>. <c>_ready</c> stayed true, so
    /// <c>BleConnected</c> stayed true, so the Bluetooth loop parked on a semaphore with no
    /// timeout and <c>WiFiWanted()</c> concluded Wi-Fi was unnecessary. The Windows peripheral
    /// reached the same state from the other side.</para>
    ///
    /// <para>The state machine makes it unrepresentable: there is no path into
    /// <c>Established</c> that skips a session, and <c>Handshaking</c> has a deadline.</para>
    /// </summary>
    [Fact]
    public void A_peer_that_answers_but_never_identifies_is_dropped_at_the_grace()
    {
        var (link, clock, _, _) = Link();

        var stranger = new FakeRoute(RouteKind.BleCentral, clock, "somebody-elses-mesh").AnswersButNeverIdentifies();
        link.Adopt(stranger);

        Assert.False(link.IsConnected);
        Assert.Equal(0, link.EnforceHandshakeDeadline());

        clock.Advance(Timings.HandshakeGrace + TimeSpan.FromSeconds(1));

        Assert.Equal(1, link.EnforceHandshakeDeadline());
        Assert.True(stranger.IsClosed);
        Assert.False(link.Has(RouteKind.BleCentral));
        Assert.False(link.MayOpen(RouteKind.BleCentral));   // and it backs off rather than retrying at once
    }

    /// <summary>A route cannot be reported usable without a session. The fake enforces it too.</summary>
    [Fact]
    public void A_route_cannot_be_established_without_a_session()
    {
        var (_, clock, _, peerFingerprint) = Link();
        var route = new FakeRoute(RouteKind.WiFi, clock).AnswersButNeverIdentifies();

        Assert.Throws<InvalidOperationException>(() => route.Establish());

        route.Identify(peerFingerprint);
        route.Establish();
        Assert.Equal(RouteState.Established, route.State);
    }

    [Fact]
    public void A_route_that_agrees_a_session_inside_the_grace_survives()
    {
        var (link, clock, _, peerFingerprint) = Link();

        var route = new FakeRoute(RouteKind.WiFi, clock).Connect();
        link.Adopt(route);

        clock.Advance(Timings.HandshakeGrace - TimeSpan.FromSeconds(1));
        route.Identify(peerFingerprint).Establish();

        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(0, link.EnforceHandshakeDeadline());
        Assert.True(link.IsConnected);
    }

    // ── two links to one peer ────────────────────────────────────────────────

    /// <summary>
    /// Both radio halves live to one peer is a genuine duplicate, and it is not cosmetic: echo
    /// suppression is on the sending side, so the receiver has no defence and every clipboard item
    /// crosses twice.
    /// </summary>
    [Fact]
    public void Two_radio_halves_to_one_peer_are_settled_by_the_arbiter()
    {
        var (link, clock, localFingerprint, peerFingerprint) = Link();

        var central = new FakeRoute(RouteKind.BleCentral, clock).Identify(peerFingerprint);
        var peripheral = new FakeRoute(RouteKind.BlePeripheral, clock, outbound: false).Identify(peerFingerprint);

        link.Adopt(central);
        link.Adopt(peripheral);
        central.Establish();
        peripheral.Establish();

        link.ResolveRadioCollision();

        var keep = BleLinkArbiter.KeepFor(localFingerprint, BleCapability.Both, peerFingerprint);
        var survivor = keep == BleRole.Central ? RouteKind.BleCentral : RouteKind.BlePeripheral;
        var casualty = keep == BleRole.Central ? RouteKind.BlePeripheral : RouteKind.BleCentral;

        Assert.True(link.Has(survivor));
        Assert.False(link.Has(casualty));
        Assert.Single(link.LiveRoutes);
    }

    /// <summary>
    /// Two devices dialling each other in the same moment. The link opened by the lower
    /// fingerprint survives, computed from values both ends already hold, so they converge with no
    /// round trip.
    /// </summary>
    [Fact]
    public void Two_sockets_keep_the_one_the_lower_fingerprint_dialled()
    {
        var (link, clock, localFingerprint, peerFingerprint) = Link();

        var outbound = new FakeRoute(RouteKind.WiFi, clock, peerFingerprint).Identify(peerFingerprint).Establish();
        var inbound = new FakeRoute(RouteKind.WiFi, clock, peerFingerprint, outbound: false)
            .Identify(peerFingerprint).Establish();

        link.Adopt(outbound);
        link.Adopt(inbound);

        bool weShouldDial = string.CompareOrdinal(localFingerprint, peerFingerprint) < 0;
        var survivor = link.RouteOf(RouteKind.WiFi);

        Assert.NotNull(survivor);
        Assert.Equal(weShouldDial, survivor!.IsOutbound);
        Assert.Single(link.LiveRoutes);
    }

    // ── which route carries what ─────────────────────────────────────────────

    [Fact]
    public async Task A_send_prefers_wifi_when_both_are_up()
    {
        var (link, clock, _, peerFingerprint) = Link();

        var wifi = new FakeRoute(RouteKind.WiFi, clock).Identify(peerFingerprint).Establish();
        var radio = new FakeRoute(RouteKind.BleCentral, clock).Identify(peerFingerprint).Establish();

        link.Adopt(wifi);
        link.Adopt(radio);

        Assert.True(await link.SendAsync(SyncContent.Text, new byte[] { 1, 2, 3 }));

        Assert.Single(wifi.Sent);
        Assert.Empty(radio.Sent);
        Assert.Equal(LinkKind.WiFi, link.ActiveLink);
    }

    [Fact]
    public async Task A_payload_too_large_for_the_radio_is_not_sent_over_it()
    {
        var (link, clock, _, peerFingerprint) = Link();

        var radio = new FakeRoute(RouteKind.BleCentral, clock).Identify(peerFingerprint).Establish();
        link.Adopt(radio);

        var image = new byte[BleProtocol.MaxPayloadBytes + 1];

        Assert.False(await link.SendAsync(SyncContent.Image, image));
        Assert.Empty(radio.Sent);
        Assert.True(link.NeedsWiFiFor(image.Length));
        Assert.False(link.NeedsWiFiFor(16));
    }

    /// <summary>Presence belongs to a route kind, which is what makes Wi-Fi demand answerable per peer.</summary>
    [Fact]
    public void Only_the_radio_carries_presence()
    {
        var (link, clock, _, peerFingerprint) = Link();

        var wifi = new FakeRoute(RouteKind.WiFi, clock).Identify(peerFingerprint).Establish();
        link.Adopt(wifi);
        Assert.True(link.IsConnected);
        Assert.False(link.HasPresence);

        var radio = new FakeRoute(RouteKind.BleCentral, clock).Identify(peerFingerprint).Establish();
        link.Adopt(radio);
        Assert.True(link.HasPresence);
    }

    // ── backoff ──────────────────────────────────────────────────────────────

    [Fact]
    public void Backoff_grows_and_is_capped_by_the_ceiling()
    {
        var (link, _, _, _) = Link();

        var first = link.BackoffFor(1, screenOn: true);
        var later = link.BackoffFor(6, screenOn: true);

        Assert.True(first < later, "backoff should grow with repeated failures");
        Assert.True(later <= Timings.ActiveCeiling * 1.2, "the active ceiling should bound it");

        var idle = link.BackoffFor(6, screenOn: false);
        Assert.True(idle <= Timings.IdleCeiling * 1.2, "the idle ceiling should bound it");
        Assert.True(idle > later, "a device nobody is looking at should retry more slowly");
    }

    [Fact]
    public void A_success_clears_the_backoff()
    {
        var (link, _, _, _) = Link();

        link.NoteFailure(RouteKind.WiFi, screenOn: true);
        Assert.False(link.MayOpen(RouteKind.WiFi));

        link.NoteSuccess(RouteKind.WiFi);
        Assert.True(link.MayOpen(RouteKind.WiFi));
    }

    [Fact]
    public async Task Closing_one_route_leaves_the_other_alone()
    {
        var (link, clock, _, peerFingerprint) = Link();

        var wifi = new FakeRoute(RouteKind.WiFi, clock).Identify(peerFingerprint).Establish();
        var radio = new FakeRoute(RouteKind.BleCentral, clock).Identify(peerFingerprint).Establish();
        link.Adopt(wifi);
        link.Adopt(radio);

        await link.CloseAsync(RouteKind.WiFi, "policy no longer wants it");

        Assert.True(wifi.IsClosed);
        Assert.False(radio.IsClosed);
        Assert.True(link.IsConnected);
        Assert.Equal(LinkKind.Ble, link.ActiveLink);
    }
}
