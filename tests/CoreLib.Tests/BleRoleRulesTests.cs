using CoreLib.Identity;
using CoreLib.Transport;

namespace CoreLib.Tests;

/// <summary>
/// Who advertises and who scans. The property that matters is that both devices work it out
/// separately and agree, because there is no round trip in which to discover they have not.
/// </summary>
public class BleRoleRulesTests
{
    private const string Lower = "0000000000000000000000000000000000000000000000000000000000000001";
    private const string Higher = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    private static (BleRole Ours, BleRole Theirs) Both(string a, BleCapability aCap, string b, BleCapability bCap) =>
        (BleRoleRules.DecideFor(a, aCap, b, bCap), BleRoleRules.DecideFor(b, bCap, a, aCap));

    /// <summary>
    /// The whole point. Two devices deciding independently must not both advertise, and must
    /// not both sit scanning for something nobody is broadcasting.
    /// </summary>
    [Theory]
    [InlineData(BleCapability.Both, BleCapability.Both)]
    [InlineData(BleCapability.Both, BleCapability.Central)]
    [InlineData(BleCapability.Central, BleCapability.Both)]
    [InlineData(BleCapability.Peripheral, BleCapability.Central)]
    [InlineData(BleCapability.Central, BleCapability.Peripheral)]
    public void The_two_sides_always_agree_on_complementary_roles(BleCapability a, BleCapability b)
    {
        var (ours, theirs) = Both(Lower, a, Higher, b);

        Assert.NotEqual(BleRole.None, ours);
        Assert.Equal(BleRoleRules.Opposite(ours), theirs);
    }

    /// <summary>
    /// The case the obvious "lower fingerprint advertises" rule gets wrong. A phone that
    /// cannot advertise must be the central however its fingerprint sorts, or the pair agrees
    /// on an arrangement neither can carry out.
    /// </summary>
    [Fact]
    public void A_device_that_cannot_advertise_is_always_the_central()
    {
        // Lower fingerprint, so the naive rule would make it advertise.
        var (ours, theirs) = Both(Lower, BleCapability.Central, Higher, BleCapability.Both);

        Assert.Equal(BleRole.Central, ours);
        Assert.Equal(BleRole.Peripheral, theirs);
    }

    [Fact]
    public void When_both_can_do_either_the_lower_fingerprint_advertises()
    {
        var (ours, theirs) = Both(Lower, BleCapability.Both, Higher, BleCapability.Both);

        Assert.Equal(BleRole.Peripheral, ours);
        Assert.Equal(BleRole.Central, theirs);
    }

    /// <summary>Two devices that can only scan have nothing to find.</summary>
    [Fact]
    public void Two_scan_only_devices_cannot_link()
    {
        var (ours, theirs) = Both(Lower, BleCapability.Central, Higher, BleCapability.Central);

        Assert.Equal(BleRole.None, ours);
        Assert.Equal(BleRole.None, theirs);
    }

    /// <summary>And two that can only advertise will never find each other.</summary>
    [Fact]
    public void Two_advertise_only_devices_cannot_link()
    {
        var (ours, theirs) = Both(Lower, BleCapability.Peripheral, Higher, BleCapability.Peripheral);

        Assert.Equal(BleRole.None, ours);
        Assert.Equal(BleRole.None, theirs);
    }

    [Fact]
    public void A_device_with_no_radio_gets_no_role()
    {
        Assert.Equal(BleRole.None, BleRoleRules.DecideFor(Lower, BleCapability.None, Higher, BleCapability.Both));
        Assert.Equal(BleRole.None, BleRoleRules.DecideFor(Lower, BleCapability.Both, Higher, BleCapability.None));
    }

    /// <summary>
    /// Runs the rule over real fingerprints rather than hand-picked extremes, so an ordering
    /// mistake cannot hide behind convenient test data.
    /// </summary>
    [Fact]
    public void Real_identities_agree_in_both_directions()
    {
        for (int i = 0; i < 40; i++)
        {
            using var a = DeviceIdentity.CreateEphemeral();
            using var b = DeviceIdentity.CreateEphemeral();

            var (ours, theirs) = Both(a.Fingerprint, BleCapability.Both, b.Fingerprint, BleCapability.Both);

            Assert.NotEqual(BleRole.None, ours);
            Assert.Equal(BleRoleRules.Opposite(ours), theirs);
        }
    }

    /// <summary>The decision must not drift between calls, or a reconnect could flip the roles.</summary>
    [Fact]
    public void The_decision_is_stable()
    {
        using var a = DeviceIdentity.CreateEphemeral();
        using var b = DeviceIdentity.CreateEphemeral();

        var first = BleRoleRules.DecideFor(a.Fingerprint, BleCapability.Both, b.Fingerprint, BleCapability.Both);

        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(first, BleRoleRules.DecideFor(a.Fingerprint, BleCapability.Both, b.Fingerprint, BleCapability.Both));
        }
    }
}
