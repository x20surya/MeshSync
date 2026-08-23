using CoreLib.Identity;
using CoreLib.Transport;
using CoreLib.Transport.Fabric;

namespace CoreLib.Tests;

/// <summary>
/// The connection policy, as a table.
///
/// <para>These rules used to live in five loops across three heads - <c>WiFiWanted()</c>,
/// <c>ShouldDialAnyPeerOverBluetooth()</c>, <c>ShouldDialOverBluetooth()</c> and two dial loops -
/// and not one of them could be asserted on without a radio in the room. Three of the cases below
/// fail against the behaviour that shipped.</para>
/// </summary>
public class RoutePolicyTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static (PeerRecord Record, string Fingerprint) Device(string seed)
    {
        var identity = DeviceIdentity.CreateEphemeral();
        _ = seed;
        return (new PeerRecord { PublicKey = identity.PublicKey, Name = seed }, identity.Fingerprint);
    }

    private static LocalConditions Conditions(string localFingerprint) => new()
    {
        LocalFingerprint = localFingerprint,
        ScreenOn = false,
        HasUsableNetwork = true,
        LocalCapability = BleCapability.Both,
    };

    // ── Wi-Fi demand is per peer ─────────────────────────────────────────────

    /// <summary>
    /// The defect this whole refactor is named after, stated as an assertion.
    ///
    /// <c>WiFiWanted()</c> was one boolean for the device and ended in <c>!BleConnected</c>, so a
    /// radio link to the laptop made the phone conclude Wi-Fi was unnecessary and call
    /// <c>DisconnectAll()</c> - dropping the socket to the desktop, a device the radio link could
    /// not reach and never claimed to.
    /// </summary>
    [Fact]
    public void A_radio_link_to_one_peer_does_not_suppress_wifi_to_another()
    {
        var (laptop, laptopFp) = Device("laptop");
        var (desktop, desktopFp) = Device("desktop");
        var local = DeviceIdentity.CreateEphemeral();

        var conditions = Conditions(local.Fingerprint) with
        {
            PeersWithPresence = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { laptopFp },
        };

        var plan = RoutePolicy.Plan(new[] { laptop, desktop }, conditions, Now);

        Assert.DoesNotContain(new RouteKey(laptopFp, RouteKind.WiFi), plan.Routes);
        Assert.Contains(new RouteKey(desktopFp, RouteKind.WiFi), plan.Routes);
    }

    [Fact]
    public void Screen_on_wants_wifi_for_every_peer()
    {
        var (a, aFp) = Device("a");
        var (b, bFp) = Device("b");
        var local = DeviceIdentity.CreateEphemeral();

        var conditions = Conditions(local.Fingerprint) with
        {
            ScreenOn = true,
            PeersWithPresence = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { aFp, bFp },
        };

        var plan = RoutePolicy.Plan(new[] { a, b }, conditions, Now);

        Assert.Contains(new RouteKey(aFp, RouteKind.WiFi), plan.Routes);
        Assert.Contains(new RouteKey(bFp, RouteKind.WiFi), plan.Routes);
    }

    [Fact]
    public void A_send_holding_wifi_holds_it_for_that_peer_alone()
    {
        var (a, aFp) = Device("a");
        var (b, bFp) = Device("b");
        var local = DeviceIdentity.CreateEphemeral();

        var conditions = Conditions(local.Fingerprint) with
        {
            PeersWithPresence = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { aFp, bFp },
            WiFiHolds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { aFp },
        };

        var plan = RoutePolicy.Plan(new[] { a, b }, conditions, Now);

        Assert.Contains(new RouteKey(aFp, RouteKind.WiFi), plan.Routes);
        Assert.DoesNotContain(new RouteKey(bFp, RouteKind.WiFi), plan.Routes);
    }

    [Fact]
    public void A_wake_request_keeps_wifi_up_until_it_lapses()
    {
        var (a, aFp) = Device("a");
        var local = DeviceIdentity.CreateEphemeral();

        var conditions = Conditions(local.Fingerprint) with
        {
            PeersWithPresence = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { aFp },
            WiFiWakeUntilUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
            {
                [aFp] = Now.AddSeconds(30),
            },
        };

        Assert.Contains(new RouteKey(aFp, RouteKind.WiFi),
            RoutePolicy.Plan(new[] { a }, conditions, Now).Routes);

        Assert.DoesNotContain(new RouteKey(aFp, RouteKind.WiFi),
            RoutePolicy.Plan(new[] { a }, conditions, Now.AddSeconds(31)).Routes);
    }

    [Fact]
    public void No_usable_network_means_no_wifi_routes_at_all()
    {
        var (a, aFp) = Device("a");
        var local = DeviceIdentity.CreateEphemeral();

        var conditions = Conditions(local.Fingerprint) with { ScreenOn = true, HasUsableNetwork = false };
        var plan = RoutePolicy.Plan(new[] { a }, conditions, Now);

        Assert.DoesNotContain(new RouteKey(aFp, RouteKind.WiFi), plan.Routes);
    }

    // ── the transport preference ─────────────────────────────────────────────

    [Fact]
    public void Bluetooth_only_asks_for_no_sockets()
    {
        var (a, aFp) = Device("a");
        var local = DeviceIdentity.CreateEphemeral();

        var conditions = Conditions(local.Fingerprint) with { ScreenOn = true, Transport = TransportPreference.Ble };
        var plan = RoutePolicy.Plan(new[] { a }, conditions, Now);

        Assert.DoesNotContain(new RouteKey(aFp, RouteKind.WiFi), plan.Routes);
        Assert.Contains(plan.Routes, r => r.Kind != RouteKind.WiFi);
    }

    [Fact]
    public void Wifi_only_asks_for_no_radio_and_does_not_advertise()
    {
        var (a, aFp) = Device("a");
        var local = DeviceIdentity.CreateEphemeral();

        var conditions = Conditions(local.Fingerprint) with { ScreenOn = true, Transport = TransportPreference.WiFi };
        var plan = RoutePolicy.Plan(new[] { a }, conditions, Now);

        Assert.Contains(new RouteKey(aFp, RouteKind.WiFi), plan.Routes);
        Assert.Empty(plan.BleCentralPeers);
        Assert.False(plan.ShouldAdvertise);
        Assert.False(RoutePolicy.ShouldScan(plan, new[] { a }, conditions));
    }

    // ── roles, capability first ──────────────────────────────────────────────

    [Fact]
    public void A_device_that_cannot_advertise_takes_the_central_half_for_every_peer()
    {
        var (a, aFp) = Device("a");
        var (b, bFp) = Device("b");
        var local = DeviceIdentity.CreateEphemeral();

        var conditions = Conditions(local.Fingerprint) with { LocalCapability = BleCapability.Central };
        var plan = RoutePolicy.Plan(new[] { a, b }, conditions, Now);

        Assert.Contains(aFp, plan.BleCentralPeers);
        Assert.Contains(bFp, plan.BleCentralPeers);
        Assert.False(plan.ShouldAdvertise);
    }

    [Fact]
    public void A_peer_that_cannot_advertise_makes_this_device_the_peripheral()
    {
        var (a, aFp) = Device("a");
        var local = DeviceIdentity.CreateEphemeral();

        var conditions = Conditions(local.Fingerprint) with
        {
            LocalCapability = BleCapability.Both,
            PeerCapabilities = new Dictionary<string, BleCapability>(StringComparer.OrdinalIgnoreCase)
            {
                [aFp] = BleCapability.Central,
            },
        };

        var plan = RoutePolicy.Plan(new[] { a }, conditions, Now);

        Assert.Contains(new RouteKey(aFp, RouteKind.BlePeripheral), plan.Routes);
        Assert.DoesNotContain(aFp, plan.BleCentralPeers);
        Assert.True(plan.ShouldAdvertise);
    }

    // ── scanning covers every peer, not the first one ────────────────────────

    /// <summary>
    /// The ceiling that made a three-device mesh impossible over the radio.
    ///
    /// Every head stopped scanning the moment one link existed - <c>_bleCentral?.IsConnected != true
    /// &amp;&amp; _bleTransport?.IsConnected != true</c> on Windows, the same shape on Linux, no gate
    /// at all on Android. The question is about the peers, not the app.
    /// </summary>
    [Fact]
    public void Every_peer_owed_a_central_link_is_listed_not_just_the_first()
    {
        var local = DeviceIdentity.CreateEphemeral();

        // Capability decides here, so all three peers take the peripheral half and this device is
        // owed a central link to each. That is the arrangement a laptop meeting three phones has.
        var peers = new List<PeerRecord>();
        var fingerprints = new List<string>();

        for (int i = 0; i < 3; i++)
        {
            var (peer, fingerprint) = Device($"peer{i}");
            peers.Add(peer);
            fingerprints.Add(fingerprint);
        }

        var conditions = Conditions(local.Fingerprint) with { LocalCapability = BleCapability.Central };
        var plan = RoutePolicy.Plan(peers, conditions, Now);

        Assert.Equal(3, plan.BleCentralPeers.Count);
        foreach (var fingerprint in fingerprints) Assert.Contains(fingerprint, plan.BleCentralPeers);
    }

    [Fact]
    public void Scanning_stops_only_when_no_peer_is_owed_a_link()
    {
        var (a, _) = Device("a");
        var local = DeviceIdentity.CreateEphemeral();
        var conditions = Conditions(local.Fingerprint) with { LocalCapability = BleCapability.Central };

        var wanted = RoutePolicy.Plan(new[] { a }, conditions, Now);
        Assert.True(RoutePolicy.ShouldScan(wanted, new[] { a }, conditions));

        var nothingWanted = wanted with { BleCentralPeers = new HashSet<string>() };
        Assert.False(RoutePolicy.ShouldScan(nothingWanted, new[] { a }, conditions));
    }

    /// <summary>
    /// A device with nothing paired still has to be findable while somebody is pairing it.
    ///
    /// There is no peer to arbitrate a role with, so the ordinary rule answers no - and on an
    /// adapter that cannot advertise that leaves the device neither scanning nor advertising,
    /// which is a deadlock rather than a degraded state.
    /// </summary>
    [Fact]
    public void A_device_with_nothing_paired_scans_while_the_pairing_window_is_open()
    {
        var local = DeviceIdentity.CreateEphemeral();
        var conditions = Conditions(local.Fingerprint) with
        {
            LocalCapability = BleCapability.Central,
            PairingOpen = true,
        };

        var plan = RoutePolicy.Plan(Array.Empty<PeerRecord>(), conditions, Now);

        Assert.Empty(plan.BleCentralPeers);
        Assert.True(RoutePolicy.ShouldScan(plan, Array.Empty<PeerRecord>(), conditions));

        var shut = conditions with { PairingOpen = false };
        Assert.False(RoutePolicy.ShouldScan(plan, Array.Empty<PeerRecord>(), shut));
    }

    /// <summary>
    /// Advertising is never gated on having a link, only on the preference and the radio.
    ///
    /// A peer that cannot advertise depends on this device staying findable, so withdrawing the
    /// service because something else is already connected would strand it.
    /// </summary>
    [Fact]
    public void Advertising_is_never_gated_on_having_a_link()
    {
        var (a, aFp) = Device("a");
        var local = DeviceIdentity.CreateEphemeral();

        var conditions = Conditions(local.Fingerprint) with
        {
            LocalCapability = BleCapability.Both,
            PeersWithPresence = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { aFp },
        };

        Assert.True(RoutePolicy.Plan(new[] { a }, conditions, Now).ShouldAdvertise);
    }

    /// <summary>
    /// A head with nothing paired yet passes an empty or null set, and a pass must not throw.
    ///
    /// The supervisor runs this on a timer whether or not anything is set up, so a first run
    /// before the registry has loaded is an ordinary case rather than an error.
    /// </summary>
    [Fact]
    public void No_peers_at_all_produces_an_empty_plan_rather_than_throwing()
    {
        var local = DeviceIdentity.CreateEphemeral();
        var conditions = Conditions(local.Fingerprint) with { ScreenOn = true };

        Assert.Empty(RoutePolicy.Plan(Array.Empty<PeerRecord>(), conditions, Now).Routes);
        Assert.Empty(RoutePolicy.Plan(null!, conditions, Now).Routes);
    }
}
