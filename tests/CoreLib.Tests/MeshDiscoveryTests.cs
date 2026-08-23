using CoreLib.Identity;
using CoreLib.Tests.Fakes;
using CoreLib.Transport;
using CoreLib.Transport.Ble;

namespace CoreLib.Tests;

/// <summary>
/// Which advertisements are worth connecting to, and what this device puts on the air.
///
/// <para>This is the answer to "no two meshes should collide", and the saving is the whole point:
/// a device from another mesh costs one comparison instead of a connect, an MTU exchange, a hello,
/// and this device's name and mesh name given away to a stranger before either end has authorised
/// anything.</para>
/// </summary>
public class MeshDiscoveryTests
{
    private static (MeshDiscovery Discovery, PeerSecurity Security, FakeClock Clock) Rig()
    {
        var clock = new FakeClock();
        var security = PeerSecurity.CreateEphemeral();
        return (new MeshDiscovery(security, clock), security, clock);
    }

    private static BleCandidate Candidate(byte[]? beacon, string address = "aa:aa") => new()
    {
        Address = address,
        Rssi = -50,
        Beacon = beacon,
        IsPresent = true,
    };

    private static void Pair(PeerSecurity security)
    {
        var peer = DeviceIdentity.CreateEphemeral();
        security.Peers.Trust(peer.PublicKey, "peer");
    }

    // ── with a key ───────────────────────────────────────────────────────────

    [Fact]
    public void A_device_in_this_mesh_is_accepted_and_one_from_another_is_not()
    {
        var (discovery, security, clock) = Rig();
        Pair(security);
        var key = discovery.MintIfDue()!;

        var ours = MeshBeacon.Build(key, clock.UtcNow);
        var theirs = MeshBeacon.Build(Enumerable.Repeat((byte)0x77, MeshBeacon.KeyLength).ToArray(), clock.UtcNow);

        Assert.True(discovery.Accepts(Candidate(ours)));
        Assert.False(discovery.Accepts(Candidate(theirs)));
    }

    /// <summary>
    /// Once this device knows which mesh it is in, a device that says nothing is somebody else's
    /// business. Before that it is worth trying, because it may simply predate the beacon.
    /// </summary>
    [Fact]
    public void A_silent_advertisement_is_tried_only_while_this_device_has_no_key()
    {
        var (discovery, security, _) = Rig();
        Pair(security);

        Assert.True(discovery.Accepts(Candidate(null)));

        discovery.MintIfDue();

        Assert.False(discovery.Accepts(Candidate(null)));
    }

    [Fact]
    public void A_device_with_nothing_paired_still_tries_everything()
    {
        var (discovery, _, _) = Rig();

        Assert.True(discovery.Accepts(Candidate(null)));
        Assert.True(discovery.Accepts(Candidate(new byte[] { 1, 2, 3, 4, 5, 6 })));
    }

    // ── minting ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A fresh install does not mint. A key made before the first pairing would be replaced by the
    /// inviter's anyway, and it would put a beacon on the air for a mesh of one.
    /// </summary>
    [Fact]
    public void A_device_with_no_peers_does_not_mint()
    {
        var (discovery, security, _) = Rig();

        Assert.Null(discovery.MintIfDue());
        Assert.False(security.Peers.HasMeshKey);
    }

    [Fact]
    public void A_device_with_peers_mints_once_and_then_keeps_it()
    {
        var (discovery, security, _) = Rig();
        Pair(security);

        var first = discovery.MintIfDue();
        var second = discovery.MintIfDue();

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.True(security.Peers.HasMeshKey);
    }

    // ── the advertisement ────────────────────────────────────────────────────

    [Fact]
    public void The_advertisement_says_whether_this_device_can_also_scan()
    {
        var (discovery, security, clock) = Rig();
        Pair(security);
        var key = discovery.MintIfDue()!;

        var both = discovery.CurrentAdvertisement(BleCapability.Both);
        Assert.True(MeshBeacon.Verify(key, both.Beacon, clock.UtcNow, out var flags));
        Assert.True(flags.HasFlag(MeshBeaconFlags.CanBeCentral));

        var peripheralOnly = discovery.CurrentAdvertisement(BleCapability.Peripheral);
        Assert.True(MeshBeacon.Verify(key, peripheralOnly.Beacon, clock.UtcNow, out var lonely));
        Assert.False(lonely.HasFlag(MeshBeaconFlags.CanBeCentral));
    }

    [Fact]
    public void The_advertisement_never_carries_the_device_name()
    {
        var (discovery, security, _) = Rig();
        Pair(security);
        discovery.MintIfDue();

        Assert.False(discovery.CurrentAdvertisement(BleCapability.Both).IncludeDeviceName);
    }

    [Fact]
    public void A_device_with_no_key_and_no_pairing_window_advertises_no_beacon()
    {
        var (discovery, _, _) = Rig();

        Assert.Empty(discovery.CurrentAdvertisement(BleCapability.Both).Beacon);
    }

    // ── pairing over the radio ───────────────────────────────────────────────

    /// <summary>
    /// The inviter advertises a tag derived from the code on its own screen; the joiner computes
    /// the same tag from the code it scanned and finds exactly that device.
    ///
    /// <para>This is what makes pairing work with no network at all - and it is the last step of
    /// this project that did not honour its own central claim, because the QR code pinned an
    /// address.</para>
    /// </summary>
    [Fact]
    public void A_joiner_finds_the_inviter_over_the_radio_and_nobody_else()
    {
        var inviter = Rig();
        var joiner = Rig();

        inviter.Security.Pairing.Open();
        var advertisement = inviter.Discovery.CurrentAdvertisement(BleCapability.Both);
        Assert.NotEmpty(advertisement.Beacon);

        // The joiner has just scanned the inviter's code.
        joiner.Security.Pairing.Open();
        joiner.Discovery.InvitedPublicKey = inviter.Security.Identity.PublicKey;

        Assert.True(joiner.Discovery.Accepts(Candidate(advertisement.Beacon)));

        // Somebody else's pairing screen, open at the same moment in the same room.
        var elsewhere = Rig();
        elsewhere.Security.Pairing.Open();

        Assert.False(joiner.Discovery.Accepts(Candidate(elsewhere.Discovery.CurrentAdvertisement(BleCapability.Both).Beacon)));
    }

    [Fact]
    public void A_pairing_beacon_says_so_in_its_flags()
    {
        var (discovery, security, clock) = Rig();
        security.Pairing.Open();

        var advertisement = discovery.CurrentAdvertisement(BleCapability.Both);
        var secret = MeshBeacon.PairingSecretFrom(security.Identity.PublicKey);

        Assert.True(MeshBeacon.Verify(secret, advertisement.Beacon, clock.UtcNow, out var flags));
        Assert.True(flags.HasFlag(MeshBeaconFlags.PairingOpen));
    }

    /// <summary>
    /// An established mesh that opens its pairing window keeps advertising under its mesh key, so
    /// its own devices go on finding it while a new one is being added.
    /// </summary>
    [Fact]
    public void Opening_the_window_on_an_established_mesh_keeps_the_mesh_beacon()
    {
        var (discovery, security, clock) = Rig();
        Pair(security);
        var key = discovery.MintIfDue()!;

        security.Pairing.Open();
        var advertisement = discovery.CurrentAdvertisement(BleCapability.Both);

        Assert.True(MeshBeacon.Verify(key, advertisement.Beacon, clock.UtcNow, out var flags));
        Assert.True(flags.HasFlag(MeshBeaconFlags.PairingOpen));
    }

    // ── adoption ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_key_offered_by_a_peer_is_adopted_when_it_is_the_lower_one()
    {
        var (discovery, security, _) = Rig();
        Pair(security);
        security.Peers.AdoptMeshKey(Enumerable.Repeat((byte)0x50, MeshBeacon.KeyLength).ToArray());

        Assert.True(discovery.Adopt(Enumerable.Repeat((byte)0x10, MeshBeacon.KeyLength).ToArray()));
        Assert.False(discovery.Adopt(Enumerable.Repeat((byte)0x90, MeshBeacon.KeyLength).ToArray()));

        Assert.Equal(0x10, security.Peers.MeshKey![0]);
    }
}
