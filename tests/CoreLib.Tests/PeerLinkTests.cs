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

    /// <summary>
    /// <b>A hard ceiling on how often this device reaches for the network, whatever else happens.</b>
    ///
    /// <para>The backoff after a failure is cleared by the next success, which is right for a
    /// backoff and useless as a rate limit: in a glare loop every cycle establishes something
    /// briefly, the backoff resets, and the next attempt goes straight out. Two devices on a desk
    /// opened 285 sockets to each other in under three minutes that way.</para>
    /// </summary>
    [Fact]
    public void Opening_is_rate_limited_even_across_a_success()
    {
        var clock = new FakeClock();
        var peerIdentity = DeviceIdentity.CreateEphemeral();
        var peer = new PeerRecord { PublicKey = peerIdentity.PublicKey, Name = "peer" };

        // The dialling side, so the losing side's grace cannot be what is under test here.
        var link = new PeerLink(peer, "!" + peer.Fingerprint, () => BleCapability.Both, clock, Timings);

        link.NoteOpening(RouteKind.WiFi);
        Assert.False(link.MayOpen(RouteKind.WiFi));

        // A route comes up and goes away again, which clears the ordinary backoff.
        var route = new FakeRoute(RouteKind.WiFi, clock).Identify(peer.Fingerprint).Establish();
        link.Adopt(route);
        link.NoteSuccess(RouteKind.WiFi);
        route.Drop("gone");
        link.NoteSuccess(RouteKind.WiFi);

        // The rate limit still holds.
        Assert.False(link.MayOpen(RouteKind.WiFi));

        clock.Advance(Timings.MinDialInterval + TimeSpan.FromSeconds(1));
        Assert.True(link.MayOpen(RouteKind.WiFi));
    }

    // ── the two ends must agree ──────────────────────────────────────────────

    /// <summary>
    /// <b>Both ends of a collision must keep the same physical link.</b>
    ///
    /// <para>This is the property the whole rule exists for, and it was quietly broken: the
    /// settlement returned the incoming route whenever the existing one had not finished its
    /// handshake yet. One end then keeps the link it dialled while the other keeps the opposite
    /// one, so each kills the link the other is holding, both redial, and it repeats.</para>
    ///
    /// <para>Found on a phone and a laptop: a collision every fifteen seconds for as long as both
    /// were running, and a route logging "established" and "lost" in the same millisecond -
    /// established locally, already killed remotely.</para>
    ///
    /// <para>Modelled from both sides at once, with the lower end's route deliberately still
    /// handshaking, which is exactly the state that used to flip the answer.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]    // the higher end's existing route has not finished its handshake
    [InlineData(false)]   // both are established
    public void Both_ends_of_a_collision_keep_the_same_link(bool existingStillHandshaking)
    {
        var clock = new FakeClock();

        var lowIdentity = DeviceIdentity.CreateEphemeral();
        var highIdentity = DeviceIdentity.CreateEphemeral();

        // Deterministic ordering, whatever the two random fingerprints happen to be.
        var (low, high) = string.CompareOrdinal(lowIdentity.Fingerprint, highIdentity.Fingerprint) < 0
            ? (lowIdentity, highIdentity)
            : (highIdentity, lowIdentity);

        var lowSide = new PeerLink(new PeerRecord { PublicKey = high.PublicKey, Name = "high" },
                                   low.Fingerprint, () => BleCapability.Both, clock, Timings);
        var highSide = new PeerLink(new PeerRecord { PublicKey = low.PublicKey, Name = "low" },
                                    high.Fingerprint, () => BleCapability.Both, clock, Timings);

        Assert.True(lowSide.ShouldDialWiFi);
        Assert.False(highSide.ShouldDialWiFi);

        // One socket each way. "lowDialled" is one physical link seen from both ends; so is
        // "highDialled".
        var lowSeesItsOwnDial = new FakeRoute(RouteKind.WiFi, clock).Identify(high.Fingerprint).Establish();
        var lowSeesTheirDial = new FakeRoute(RouteKind.WiFi, clock, outbound: false).Identify(high.Fingerprint).Establish();

        var highSeesItsOwnDial = new FakeRoute(RouteKind.WiFi, clock).Identify(low.Fingerprint).Establish();
        var highSeesTheirDial = new FakeRoute(RouteKind.WiFi, clock, outbound: false).Identify(low.Fingerprint);

        if (!existingStillHandshaking) highSeesTheirDial.Establish();

        lowSide.Adopt(lowSeesItsOwnDial);
        lowSide.Adopt(lowSeesTheirDial);

        // The high end adopts the peer's dial first, then its own - the order that used to flip it.
        highSide.Adopt(highSeesTheirDial);
        highSide.Adopt(highSeesItsOwnDial);

        // The low end dialled, so both must be holding that same physical link: outbound at the
        // low end, inbound at the high end.
        Assert.True(lowSide.RouteOf(RouteKind.WiFi)!.IsOutbound,
            "the end that wins the race must keep the link it dialled");
        Assert.False(highSide.RouteOf(RouteKind.WiFi)!.IsOutbound,
            "the other end must keep the link its peer dialled - the same physical socket");
    }

    // ── what hardware found ──────────────────────────────────────────────────

    /// <summary>
    /// <b>A route that failed before it was ever usable must leave the table.</b>
    ///
    /// <para>It is adopted while <c>Handshaking</c> and can drop to <c>Backoff</c> a moment later -
    /// a refused hello, a socket closed by the peer, a failed MTU exchange. The state handler
    /// returned early unless the route had been <c>Established</c>, so the dead object stayed in
    /// the table, <c>Has(kind)</c> answered true about it, and the supervisor never opened
    /// another. One failed handshake wedged that route kind to that peer until a restart.</para>
    /// </summary>
    [Fact]
    public void A_route_that_fails_before_it_is_established_leaves_the_table()
    {
        var (link, clock, _, _) = Link();

        var route = new FakeRoute(RouteKind.WiFi, clock).Connect();
        link.Adopt(route);
        Assert.True(link.Has(RouteKind.WiFi));

        // Never established - refused at the hello.
        route.Drop("not a paired device");

        Assert.False(link.Has(RouteKind.WiFi));

        // And once the backoff lapses, another may be opened.
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(link.MayOpen(RouteKind.WiFi));
    }

    /// <summary>
    /// <b>A link that has just died is not reopened in the same millisecond.</b>
    ///
    /// <para>Both devices dial, so both can win the race and one link is dropped as the loser. The
    /// end whose link was dropped sees an ordinary loss - and with no backoff it redialled
    /// instantly, collided again, and lost again. A phone and a laptop on one desk produced 136
    /// collisions in two minutes and were still going.</para>
    ///
    /// <para>The old dial loop tolerated glare because it ran on a fifteen-second timer. A
    /// supervisor that reconciles the moment anything changes has no such accidental rate limit,
    /// so the backoff has to be real.</para>
    /// </summary>
    [Fact]
    public void Losing_an_established_route_backs_off_before_the_next_attempt()
    {
        var (link, clock, _, peerFingerprint) = Link();

        var route = new FakeRoute(RouteKind.WiFi, clock).Identify(peerFingerprint).Establish();
        link.Adopt(route);

        route.Drop("the peer dropped it as a collision loser");

        Assert.False(link.Has(RouteKind.WiFi));
        Assert.False(link.MayOpen(RouteKind.WiFi));   // not this instant

        clock.Advance(Timings.MaxBackoff + TimeSpan.FromSeconds(1));
        Assert.True(link.MayOpen(RouteKind.WiFi));    // but soon
    }

    /// <summary>
    /// The loser of a collision is removed from the table before it is closed, so its own closing
    /// must not be announced as a loss - the peer never stopped being reachable.
    /// </summary>
    [Fact]
    public void Dropping_a_collision_loser_is_not_announced_as_a_lost_route()
    {
        var (link, clock, _, peerFingerprint) = Link();

        int lost = 0;
        link.RouteLost += (_, _, _) => lost++;

        var first = new FakeRoute(RouteKind.WiFi, clock, peerFingerprint).Identify(peerFingerprint).Establish();
        var second = new FakeRoute(RouteKind.WiFi, clock, peerFingerprint, outbound: false)
            .Identify(peerFingerprint).Establish();

        link.Adopt(first);
        link.Adopt(second);

        Assert.Equal(0, lost);
        Assert.Single(link.LiveRoutes);
    }

    /// <summary>
    /// <b>The end that would lose a collision gives the other one first refusal - but only on the
    /// retry.</b>
    ///
    /// <para>Both ends dial, and that is the design: either may be the only one that can open the
    /// socket. But both already agree the surviving link is the one dialled by the lower
    /// fingerprint, so once a collision has actually happened the higher end has nothing to gain
    /// by racing again immediately.</para>
    ///
    /// <para>A phone and a laptop on one desk produced a collision every two seconds indefinitely:
    /// each one re-established the link, which cleared the very backoff meant to damp it.</para>
    /// </summary>
    [Fact]
    public void The_end_that_would_lose_the_race_defers_its_retry_and_not_its_first_try()
    {
        var clock = new FakeClock();
        var peerIdentity = DeviceIdentity.CreateEphemeral();
        var peer = new PeerRecord { PublicKey = peerIdentity.PublicKey, Name = "peer" };

        // Deterministically the higher of the two, so this device loses any collision.
        // Lowercase, because a fingerprint is lowercase hex and 'Z' would sort *below* it.
        string higher = "z" + peer.Fingerprint;
        var loser = new PeerLink(peer, higher, () => BleCapability.Both, clock, Timings);

        Assert.False(loser.ShouldDialWiFi);

        // The first attempt is free: nothing has been lost, so there is no collision to avoid.
        Assert.True(loser.MayOpen(RouteKind.WiFi));

        var route = new FakeRoute(RouteKind.WiFi, clock).Identify(peer.Fingerprint).Establish();
        loser.Adopt(route);
        route.Drop("dropped as the collision loser");

        // Now it knows the other end is there, and that it would lose again.
        Assert.False(loser.MayOpen(RouteKind.WiFi));

        clock.Advance(Timings.DialGrace + TimeSpan.FromSeconds(1));
        Assert.True(loser.MayOpen(RouteKind.WiFi));
    }

    /// <summary>The end that wins the collision keeps dialling as soon as its backoff lapses.</summary>
    [Fact]
    public void The_end_that_wins_the_race_is_not_made_to_wait()
    {
        var clock = new FakeClock();
        var peerIdentity = DeviceIdentity.CreateEphemeral();
        var peer = new PeerRecord { PublicKey = peerIdentity.PublicKey, Name = "peer" };

        string lower = "!" + peer.Fingerprint;   // '!' sorts below any lowercase hex digit
        var winner = new PeerLink(peer, lower, () => BleCapability.Both, clock, Timings);

        Assert.True(winner.ShouldDialWiFi);

        var route = new FakeRoute(RouteKind.WiFi, clock).Identify(peer.Fingerprint).Establish();
        winner.Adopt(route);
        route.Drop("the peer went away");

        // Its own backoff still applies - a dead link is not reopened instantly - but the extra
        // grace for the losing side does not.
        clock.Advance(Timings.MaxBackoff + TimeSpan.FromSeconds(1));
        Assert.True(winner.MayOpen(RouteKind.WiFi));
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

    /// <summary>
    /// A success clears the backoff.
    ///
    /// <para>Pinned to the dialling side deliberately. The other side carries a second, separate
    /// wait - first refusal for the end that wins a collision - and that one is <em>not</em>
    /// cleared by a success, because a route establishing is exactly the moment the old code
    /// cleared the damping and dialled straight into another collision.</para>
    /// </summary>
    [Fact]
    public void A_success_clears_the_backoff()
    {
        var clock = new FakeClock();
        var peerIdentity = DeviceIdentity.CreateEphemeral();
        var peer = new PeerRecord { PublicKey = peerIdentity.PublicKey, Name = "peer" };

        var link = new PeerLink(peer, "!" + peer.Fingerprint, () => BleCapability.Both, clock, Timings);
        Assert.True(link.ShouldDialWiFi);

        link.NoteFailure(RouteKind.WiFi, screenOn: true);
        Assert.False(link.MayOpen(RouteKind.WiFi));

        link.NoteSuccess(RouteKind.WiFi);
        Assert.True(link.MayOpen(RouteKind.WiFi));
    }

    /// <summary>
    /// A route establishing must not clear the losing side's grace.
    ///
    /// <para>That is the loop this whole mechanism exists to break: the loser's link was dropped,
    /// the winner's inbound link established a moment later, the success cleared the damping, and
    /// the loser dialled straight into another collision. Every two seconds, indefinitely.</para>
    /// </summary>
    [Fact]
    public async Task A_success_does_not_clear_the_losing_sides_grace()
    {
        var clock = new FakeClock();
        var peerIdentity = DeviceIdentity.CreateEphemeral();
        var peer = new PeerRecord { PublicKey = peerIdentity.PublicKey, Name = "peer" };

        var loser = new PeerLink(peer, "z" + peer.Fingerprint, () => BleCapability.Both, clock, Timings);
        Assert.False(loser.ShouldDialWiFi);

        var mine = new FakeRoute(RouteKind.WiFi, clock).Identify(peer.Fingerprint).Establish();
        loser.Adopt(mine);
        mine.Drop("dropped as the collision loser");

        // The peer's own link arrives immediately afterwards and establishes.
        var theirs = new FakeRoute(RouteKind.WiFi, clock, outbound: false).Identify(peer.Fingerprint);
        loser.Adopt(theirs);
        theirs.Establish();

        // That success must not have re-armed the dial.
        await loser.CloseAsync(RouteKind.WiFi, "the peer went away");
        Assert.False(loser.MayOpen(RouteKind.WiFi));
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
