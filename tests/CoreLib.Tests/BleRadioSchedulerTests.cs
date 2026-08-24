using CoreLib.Identity;
using CoreLib.Tests.Fakes;
using CoreLib.Transport;
using CoreLib.Transport.Ble;
using CoreLib.Transport.Fabric;

namespace CoreLib.Tests;

/// <summary>
/// One radio, many peers: when to scan, who to try, what to remember, and who yields a slot.
///
/// <para>These are the findings from <c>HANDOFF.md</c> turned into cases. Not one of them could be
/// asserted on before, because every head owned its own scan loop and none of the loops had a seam
/// to test through.</para>
/// </summary>
public class BleRadioSchedulerTests
{
    private static readonly RouteTimings Timings = RouteTimings.Default;

    private static (BleRadioScheduler Scheduler, FakeBleRadio Radio, FakeClock Clock) Rig(
        BleCapability capability = BleCapability.Both, RouteTimings? timings = null)
    {
        var clock = new FakeClock();
        var radio = new FakeBleRadio(clock, capability);
        var scheduler = new BleRadioScheduler(radio, clock, timings ?? Timings);
        return (scheduler, radio, clock);
    }

    private static string Fingerprint() => DeviceIdentity.CreateEphemeral().Fingerprint;

    // ── when to scan ─────────────────────────────────────────────────────────

    /// <summary>
    /// Scanning stops when nothing is missing a link, not when something has one.
    ///
    /// The old condition was <c>_bleCentral?.IsConnected != true</c> and its equivalents, so the
    /// second and third device in a mesh were never reached over the radio at all.
    /// </summary>
    [Fact]
    public async Task Nothing_wanted_means_no_scan_at_all()
    {
        var (scheduler, radio, _) = Rig();
        await using var _s = scheduler;

        radio.Place("aa:aa");
        scheduler.SetWanted(new HashSet<string>());

        await scheduler.RunRoundAsync(CancellationToken.None);

        Assert.Empty(radio.ScanWindows);
        Assert.Empty(radio.ConnectAttempts);
    }

    [Fact]
    public async Task A_peer_still_owed_a_link_keeps_the_scan_running()
    {
        var (scheduler, radio, _) = Rig();
        await using var _s = scheduler;

        radio.Place("aa:aa", rssi: -40);
        scheduler.SetWanted(new HashSet<string> { Fingerprint() });

        await scheduler.RunRoundAsync(CancellationToken.None);

        Assert.Single(radio.ScanWindows);
        Assert.Equal(Timings.ScanWindow, radio.ScanWindows[0]);
        Assert.Single(radio.ConnectAttempts);
    }

    /// <summary>
    /// A round connects to everything it has room for, not to one candidate.
    ///
    /// A round that picks a single candidate and ends is how a foreign phone sitting closer than
    /// your own won every round: connected, refused, round over, repeat.
    /// </summary>
    [Fact]
    public async Task A_round_fills_the_free_slots_rather_than_taking_one_candidate()
    {
        var (scheduler, radio, _) = Rig();
        await using var _s = scheduler;

        radio.Place("aa:aa", rssi: -40).Place("bb:bb", rssi: -55).Place("cc:cc", rssi: -70);
        scheduler.SetWanted(new HashSet<string> { Fingerprint(), Fingerprint(), Fingerprint() });

        await scheduler.RunRoundAsync(CancellationToken.None);

        Assert.Equal(3, radio.ConnectAttempts.Count);

        // Strongest signal first: the nearest device is the likeliest to still be there.
        Assert.Equal("aa:aa", radio.ConnectAttempts[0].Address);
        Assert.Equal("cc:cc", radio.ConnectAttempts[2].Address);
    }

    /// <summary>
    /// BlueZ keeps a device object for every LE address it has ever seen, and most are ghosts that
    /// still carry the service UUID they advertised at the time. RSSI is published only while a
    /// device is being seen now, which is what tells the two apart.
    /// </summary>
    [Fact]
    public async Task A_remembered_device_that_is_not_here_now_is_not_dialled()
    {
        var (scheduler, radio, _) = Rig();
        await using var _s = scheduler;

        radio.Place("ghost", rssi: -40, present: false);
        scheduler.SetWanted(new HashSet<string> { Fingerprint() });

        await scheduler.RunRoundAsync(CancellationToken.None);

        Assert.Empty(radio.ConnectAttempts);
    }

    // ── remembering refusals ─────────────────────────────────────────────────

    /// <summary>
    /// A refusal that is not remembered is a reconnection four seconds later, forever.
    ///
    /// A laptop here held a Bluetooth link to a phone in somebody else's mesh for as long as both
    /// were in range.
    /// </summary>
    [Fact]
    public async Task A_device_that_produced_no_session_is_not_tried_again_at_once()
    {
        var (scheduler, radio, clock) = Rig();
        await using var _s = scheduler;

        radio.Place("stranger", rssi: -30, name: "Someone else's phone");
        scheduler.SetWanted(new HashSet<string> { Fingerprint() });

        await scheduler.RunRoundAsync(CancellationToken.None);
        Assert.Single(radio.ConnectAttempts);

        // It connects, answers, and never says who it is. The fabric drops it at the grace.
        radio.Opened["stranger"].AnswersButNeverIdentifies().Drop("no session inside the grace");

        await scheduler.RunRoundAsync(CancellationToken.None);
        Assert.Single(radio.ConnectAttempts);   // still one: skipped without connecting

        clock.Advance(Timings.RefusalCooldown + TimeSpan.FromSeconds(1));
        await scheduler.RunRoundAsync(CancellationToken.None);

        Assert.Equal(2, radio.ConnectAttempts.Count);
    }

    /// <summary>
    /// The cooldown has to survive an LE address rotation, and the advertised name is the only key
    /// that both survives one and is known before connecting.
    /// </summary>
    [Fact]
    public async Task A_refused_device_that_rotates_its_address_is_still_skipped()
    {
        var (scheduler, radio, _) = Rig();
        await using var _s = scheduler;

        radio.Place("address-one", rssi: -30, name: "Galaxy S21");
        scheduler.SetWanted(new HashSet<string> { Fingerprint() });

        await scheduler.RunRoundAsync(CancellationToken.None);
        radio.Opened["address-one"].AnswersButNeverIdentifies().Drop("no session");

        // Same phone, new privacy address, same advertised name.
        radio.ClearRange();
        radio.Place("address-two", rssi: -30, name: "Galaxy S21");

        await scheduler.RunRoundAsync(CancellationToken.None);

        Assert.Single(radio.ConnectAttempts);
    }

    [Fact]
    public async Task A_confirmed_pairing_clears_the_refusals()
    {
        var (scheduler, radio, _) = Rig();
        await using var _s = scheduler;

        radio.Place("newly-paired", rssi: -30, name: "Laptop");
        scheduler.SetWanted(new HashSet<string> { Fingerprint() });

        await scheduler.RunRoundAsync(CancellationToken.None);
        radio.Opened["newly-paired"].AnswersButNeverIdentifies().Drop("not paired yet");

        // The user confirms it a moment later. Making them wait out five minutes for that reads as
        // the confirmation not having worked.
        scheduler.Cooldowns.Clear();

        await scheduler.RunRoundAsync(CancellationToken.None);

        Assert.Equal(2, radio.ConnectAttempts.Count);
    }

    // ── the beacon filter ────────────────────────────────────────────────────

    /// <summary>
    /// With a beacon to check, a device from another mesh costs nothing: no connect, no MTU
    /// exchange, no hello, and no device or mesh name given away to a stranger.
    /// </summary>
    [Fact]
    public async Task A_device_whose_beacon_is_not_ours_is_never_connected_to()
    {
        var (scheduler, radio, _) = Rig();
        await using var _s = scheduler;

        scheduler.BeaconFilter = c => c.Beacon is { Length: > 0 } b && b[0] == 0x01;

        radio.Place("theirs", rssi: -20, beacon: new byte[] { 0x99 });
        radio.Place("ours", rssi: -80, beacon: new byte[] { 0x01 });
        scheduler.SetWanted(new HashSet<string> { Fingerprint() });

        await scheduler.RunRoundAsync(CancellationToken.None);

        Assert.Single(radio.ConnectAttempts);
        Assert.Equal("ours", radio.ConnectAttempts[0].Address);
        Assert.Equal((2, 1), scheduler.LastRound);
    }

    /// <summary>
    /// A device that has proved which mesh it is in is tried before one that has not, whatever
    /// the signal strength. Ranking by RSSI alone put a foreign phone sitting closer than your
    /// own at the front of every round.
    /// </summary>
    [Fact]
    public async Task A_verified_beacon_outranks_a_stronger_silent_advertisement()
    {
        var (scheduler, radio, _) = Rig();
        await using var _s = scheduler;

        scheduler.BeaconRank = c => c.Beacon is { Length: > 0 } ? 0 : 1;

        radio.Place("silent-but-close", rssi: -20);
        radio.Place("ours-but-far", rssi: -85, beacon: new byte[] { 0x01 });
        scheduler.SetWanted(new HashSet<string> { Fingerprint(), Fingerprint() });

        await scheduler.RunRoundAsync(CancellationToken.None);

        Assert.Equal("ours-but-far", radio.ConnectAttempts[0].Address);
        Assert.Equal("silent-but-close", radio.ConnectAttempts[1].Address);
    }

    // ── advertising ──────────────────────────────────────────────────────────

    /// <summary>
    /// Advertising is never gated on having a link. A peer that cannot advertise depends on this
    /// device staying findable, so withdrawing the service because something else connected would
    /// strand it.
    /// </summary>
    [Fact]
    public async Task Advertising_survives_every_link_being_up()
    {
        var (scheduler, radio, _) = Rig();
        await using var _s = scheduler;

        await scheduler.SetAdvertisingAsync(true, new BleAdvertisement());
        Assert.True(radio.Advertising);

        scheduler.SetWanted(new HashSet<string>());
        await scheduler.RunRoundAsync(CancellationToken.None);

        Assert.True(radio.Advertising);
        Assert.Equal(0, radio.StopAdvertisingCalls);
    }

    [Fact]
    public async Task A_new_beacon_is_republished_without_a_restart()
    {
        var (scheduler, radio, _) = Rig();
        await using var _s = scheduler;

        await scheduler.SetAdvertisingAsync(true, new BleAdvertisement { Beacon = new byte[] { 1 } });
        await scheduler.SetAdvertisingAsync(true, new BleAdvertisement { Beacon = new byte[] { 2 } });

        Assert.Equal(2, radio.Published.Count);
        Assert.Equal(new byte[] { 2 }, radio.Published[1].Beacon);
    }

    /// <summary>
    /// <b>A link that is already established when it is handed over still counts.</b>
    ///
    /// <para>A peripheral sends its hello the instant a central subscribes, and a real connect
    /// does not return until it has subscribed - so the route can reach <c>Established</c> before
    /// the scheduler attaches its handler. Missing that transition left the link out of the budget
    /// entirely: the cap was never enforced, rotation never ran, and the health surface reported
    /// no links beside a route that had been up for minutes.</para>
    /// </summary>
    [Fact]
    public async Task A_link_established_before_the_handover_is_still_counted()
    {
        var (scheduler, radio, _) = Rig();
        await using var _s = scheduler;

        string peer = Fingerprint();

        radio.Place("fast peer", rssi: -40);
        radio.LiveOnArrival["fast peer"] = peer;
        scheduler.SetWanted(new HashSet<string> { peer });

        await scheduler.RunRoundAsync(CancellationToken.None);

        Assert.Equal(RouteState.Established, radio.Opened["fast peer"].State);
        Assert.Equal(1, scheduler.LiveCentralLinks);
    }

    // ── the link budget ──────────────────────────────────────────────────────

    [Fact]
    public async Task The_link_budget_is_not_exceeded()
    {
        var timings = Timings with { MaxBleCentralLinks = 2 };
        var (scheduler, radio, _) = Rig(timings: timings);
        await using var _s = scheduler;

        radio.Place("a", rssi: -40).Place("b", rssi: -50).Place("c", rssi: -60);
        scheduler.SetWanted(new HashSet<string> { Fingerprint(), Fingerprint(), Fingerprint() });

        await scheduler.RunRoundAsync(CancellationToken.None);

        Assert.Equal(2, radio.ConnectAttempts.Count);
    }

    /// <summary>
    /// Peer five rotates in rather than silently never connecting.
    ///
    /// The alternative - the four most recently active peers holding their links until one drops -
    /// leaves a device you have not touched today unreachable with no network, which it cannot fix
    /// without a link.
    /// </summary>
    [Fact]
    public async Task A_waiting_peer_rotates_the_least_useful_link_out()
    {
        var timings = Timings with { MaxBleCentralLinks = 1 };
        var (scheduler, radio, clock) = Rig(timings: timings);
        await using var _s = scheduler;

        string held = Fingerprint();
        string waiting = Fingerprint();

        radio.Place("held", rssi: -40);
        scheduler.SetWanted(new HashSet<string> { held });
        await scheduler.RunRoundAsync(CancellationToken.None);

        var route = radio.Opened["held"];
        route.Identify(held).Establish();
        Assert.Equal(1, scheduler.LiveCentralLinks);

        // Another peer starts wanting a link, and the held one has carried nothing for a window.
        scheduler.SetWanted(new HashSet<string> { held, waiting });
        clock.Advance(timings.RotationInterval + TimeSpan.FromSeconds(1));

        Assert.True(await scheduler.RotateIfCrowdedAsync());
        Assert.True(route.IsClosed);
        Assert.Equal(0, scheduler.LiveCentralLinks);
    }

    /// <summary>A link that is carrying traffic keeps its slot, whatever else is waiting.</summary>
    [Fact]
    public async Task A_link_that_is_being_used_is_not_rotated_out()
    {
        var timings = Timings with { MaxBleCentralLinks = 1 };
        var (scheduler, radio, clock) = Rig(timings: timings);
        await using var _s = scheduler;

        string held = Fingerprint();
        var peer = new PeerRecord { PublicKey = "x", Name = "held" };

        radio.Place("held", rssi: -40);
        scheduler.SetWanted(new HashSet<string> { held });
        await scheduler.RunRoundAsync(CancellationToken.None);

        var route = radio.Opened["held"];
        route.Identify(held).Establish();

        scheduler.SetWanted(new HashSet<string> { held, Fingerprint() });
        clock.Advance(timings.RotationInterval + TimeSpan.FromSeconds(1));

        // Something crossed just now, so this link is the useful one.
        route.Deliver(peer, SyncContent.Text, new byte[] { 1 });

        Assert.False(await scheduler.RotateIfCrowdedAsync());
        Assert.False(route.IsClosed);
    }

    // ── pairing, and the failsafe ladder ─────────────────────────────────────

    /// <summary>
    /// A device with nothing paired still has to scan while somebody is pairing it.
    ///
    /// The wanted set is empty - there is no peer to owe a link to - so without this a fresh
    /// install could only ever be joined and never join.
    /// </summary>
    [Fact]
    public async Task Probing_scans_with_nothing_wanted()
    {
        var (scheduler, radio, _) = Rig();
        await using var _s = scheduler;

        radio.Place("the inviter", rssi: -40);
        scheduler.SetWanted(new HashSet<string>());

        await scheduler.RunRoundAsync(CancellationToken.None);
        Assert.Empty(radio.ScanWindows);

        scheduler.SetProbing(true);
        await scheduler.RunRoundAsync(CancellationToken.None);

        Assert.Single(radio.ScanWindows);
        Assert.Single(radio.ConnectAttempts);
    }

    /// <summary>
    /// Rung four: several rounds that saw nothing at all while peers were wanted republish the
    /// service.
    ///
    /// <para>The failure that motivates it is real. Killing a process orphans its GATT
    /// registration, and a peer then keeps discovering the orphan: it connects, subscribes, both
    /// ends report success, and nothing crosses. Quitting gracefully recovers; a crash needs the
    /// adapter toggled.</para>
    /// </summary>
    [Fact]
    public async Task Rounds_that_see_nothing_at_all_republish_the_service()
    {
        var (scheduler, radio, _) = Rig();
        await using var _s = scheduler;

        await scheduler.SetAdvertisingAsync(true, new BleAdvertisement());
        scheduler.SetWanted(new HashSet<string> { Fingerprint() });

        // An empty room: wanted, scanned, nothing seen.
        for (int round = 0; round < 3; round++) await scheduler.RunRoundAsync(CancellationToken.None);

        Assert.Equal(1, scheduler.Recoveries);
        Assert.Equal(1, radio.StopAdvertisingCalls);
        Assert.True(radio.Advertising, "the service should be back up after a recovery");
    }

    /// <summary>
    /// A round that saw devices and refused them is the radio working exactly as intended, and
    /// must not count towards recovery.
    /// </summary>
    [Fact]
    public async Task Refusing_other_meshes_is_not_a_reason_to_restart_the_radio()
    {
        var (scheduler, radio, _) = Rig();
        await using var _s = scheduler;

        scheduler.BeaconFilter = _ => false;
        await scheduler.SetAdvertisingAsync(true, new BleAdvertisement());

        radio.Place("somebody else", rssi: -30, beacon: new byte[] { 0x99 });
        scheduler.SetWanted(new HashSet<string> { Fingerprint() });

        for (int round = 0; round < 5; round++) await scheduler.RunRoundAsync(CancellationToken.None);

        Assert.Equal(0, scheduler.Recoveries);
        Assert.Equal(0, scheduler.BarrenRounds);
    }

    [Fact]
    public async Task A_round_that_finds_something_clears_the_barren_count()
    {
        var (scheduler, radio, _) = Rig();
        await using var _s = scheduler;

        await scheduler.SetAdvertisingAsync(true, new BleAdvertisement());
        scheduler.SetWanted(new HashSet<string> { Fingerprint() });

        await scheduler.RunRoundAsync(CancellationToken.None);
        await scheduler.RunRoundAsync(CancellationToken.None);
        Assert.Equal(2, scheduler.BarrenRounds);

        radio.Place("here at last", rssi: -50);
        await scheduler.RunRoundAsync(CancellationToken.None);

        Assert.Equal(0, scheduler.BarrenRounds);
        Assert.Equal(0, scheduler.Recoveries);
    }

    // ── inbound ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_peer_that_connects_to_us_arrives_as_an_ordinary_route()
    {
        var (scheduler, radio, _) = Rig();
        await using var _s = scheduler;

        IPeerRoute? arrived = null;
        scheduler.InboundRoutes.RouteArrived += r => arrived = r;

        var route = radio.Arrive();

        Assert.NotNull(arrived);
        Assert.Same(route, arrived);
        Assert.Equal(RouteKind.BlePeripheral, arrived!.Kind);
    }
}
