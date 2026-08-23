using CoreLib.Transport;

namespace CoreLib.Tests;

/// <summary>
/// The rule that stops two devices opening two Bluetooth links to each other.
///
/// <para>This is the mesh interference, written down. Every install advertises the same service
/// UUID and every device also scans for it, so without asking who should dial, two devices in
/// range each connect to the other, both links come up carrying the same peer, and the clipboard
/// crosses twice. Windows prevented it upfront, Android repaired it afterwards, and the Linux
/// head did neither - so the decision moved here, where all three call the same code.</para>
/// </summary>
public class BleLinkArbiterTests
{
    private const string Lower = "0000000000000000000000000000000000000000000000000000000000000001";
    private const string Middle = "8888888888888888888888888888888888888888888888888888888888888888";
    private const string Higher = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    /// <summary>
    /// The property the whole thing rests on: of two devices, exactly one dials.
    ///
    /// Both compute it from fingerprints they have already exchanged, so they converge with no
    /// round trip. If this ever returns true on both ends, the duplicate link is back.
    /// </summary>
    [Theory]
    [InlineData(BleCapability.Both, BleCapability.Both)]
    [InlineData(BleCapability.Both, BleCapability.Central)]
    [InlineData(BleCapability.Central, BleCapability.Both)]
    [InlineData(BleCapability.Peripheral, BleCapability.Central)]
    [InlineData(BleCapability.Central, BleCapability.Peripheral)]
    public void Exactly_one_of_the_two_devices_dials(BleCapability ours, BleCapability theirs)
    {
        bool weDial = BleLinkArbiter.ShouldDialPeer(Lower, ours, Higher, theirs);
        bool theyDial = BleLinkArbiter.ShouldDialPeer(Higher, theirs, Lower, ours);

        Assert.NotEqual(weDial, theyDial);
    }

    /// <summary>
    /// And of two live links, exactly one is dropped.
    ///
    /// The dial gate makes a collision rare but cannot make it impossible: two devices can dial
    /// inside the same moment, before either has a link to notice. Both ends must then pick the
    /// same survivor, or they drop both links and start again, forever.
    /// </summary>
    [Fact]
    public void A_collision_is_resolved_the_same_way_by_both_ends()
    {
        var ours = BleLinkArbiter.KeepFor(Lower, BleCapability.Both, Higher);
        var theirs = BleLinkArbiter.KeepFor(Higher, BleCapability.Both, Lower);

        Assert.Equal(BleRoleRules.Opposite(ours), theirs);
        Assert.NotEqual(BleRole.None, ours);
    }

    [Fact]
    public void A_device_with_no_paired_peers_does_not_scan()
    {
        Assert.False(BleLinkArbiter.ShouldDialAnyPeer(Lower, BleCapability.Both, Array.Empty<string>()));
    }

    [Fact]
    public void A_null_peer_list_does_not_scan()
    {
        Assert.False(BleLinkArbiter.ShouldDialAnyPeer(Lower, BleCapability.Both, null!));
    }

    /// <summary>One peer that needs dialling is enough to justify the scan.</summary>
    [Fact]
    public void One_peer_worth_dialling_is_enough()
    {
        // Lower advertises to Higher, so that peer alone would not start a scan - but Lower is
        // the central for anything below it, which Middle is not and this third fingerprint is.
        var peers = new[] { Higher, Middle };

        Assert.True(BleLinkArbiter.ShouldDialAnyPeer(Higher, BleCapability.Both, peers.Append(Lower)));
    }

    /// <summary>
    /// A device that cannot advertise always dials, whatever its fingerprint says.
    ///
    /// Advertising is a hardware capability rather than a given, and two devices that agree the
    /// one which cannot advertise should do the advertising have agreed on nothing.
    /// </summary>
    [Fact]
    public void A_device_that_cannot_advertise_always_dials()
    {
        Assert.True(BleLinkArbiter.ShouldDialPeer(Lower, BleCapability.Central, Higher, BleCapability.Both));
        Assert.True(BleLinkArbiter.ShouldDialPeer(Higher, BleCapability.Central, Lower, BleCapability.Both));
    }

    /// <summary>A device that can only advertise never dials, for the same reason.</summary>
    [Fact]
    public void A_device_that_can_only_advertise_never_dials()
    {
        Assert.False(BleLinkArbiter.ShouldDialPeer(Lower, BleCapability.Peripheral, Higher, BleCapability.Both));
        Assert.False(BleLinkArbiter.ShouldDialPeer(Higher, BleCapability.Peripheral, Lower, BleCapability.Both));
    }

    /// <summary>Two devices that can neither of them advertise have no link to arrange.</summary>
    [Fact]
    public void Two_scanners_never_dial_each_other()
    {
        Assert.False(BleLinkArbiter.ShouldDialPeer(Lower, BleCapability.Central, Higher, BleCapability.Central));
        Assert.False(BleLinkArbiter.ShouldDialPeer(Higher, BleCapability.Central, Lower, BleCapability.Central));
    }

    /// <summary>The answer does not drift between calls; nothing here is time or order dependent.</summary>
    [Fact]
    public void The_answer_is_stable()
    {
        bool first = BleLinkArbiter.ShouldDialPeer(Lower, BleCapability.Both, Higher);

        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(first, BleLinkArbiter.ShouldDialPeer(Lower, BleCapability.Both, Higher));
        }
    }
}
