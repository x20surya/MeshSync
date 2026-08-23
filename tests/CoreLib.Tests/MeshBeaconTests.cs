using System.Security.Cryptography;
using CoreLib.Identity;
using CoreLib.Transport.Ble;

namespace CoreLib.Tests;

/// <summary>
/// Six bytes in the advertisement that say which mesh a device belongs to.
///
/// <para>The rule these cases exist to hold: <b>the beacon decides who to try, never who is let
/// in.</b> A forged or replayed one buys an attacker one wasted connect attempt, which is what
/// every stranger costs today. The last case in this file asserts the part that would actually
/// matter if it were ever broken.</para>
/// </summary>
public class MeshBeaconTests
{
    private static readonly DateTime Now = new(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

    private static byte[] Key(byte fill = 0x11) => Enumerable.Repeat(fill, MeshBeacon.KeyLength).ToArray();

    [Fact]
    public void A_beacon_verifies_against_the_key_that_built_it()
    {
        var key = Key();
        var beacon = MeshBeacon.Build(key, Now);

        Assert.Equal(MeshBeacon.Length, beacon.Length);
        Assert.True(MeshBeacon.Verify(key, beacon, Now, out var flags));
        Assert.Equal(MeshBeaconFlags.None, flags);
    }

    /// <summary>The whole point: somebody else's mesh is told apart before anything connects.</summary>
    [Fact]
    public void Another_meshs_beacon_does_not_verify()
    {
        var beacon = MeshBeacon.Build(Key(0x11), Now);

        Assert.False(MeshBeacon.Verify(Key(0x22), beacon, Now, out _));
    }

    [Fact]
    public void A_device_with_no_key_publishes_nothing_and_accepts_nothing()
    {
        Assert.Empty(MeshBeacon.Build(null, Now));
        Assert.Empty(MeshBeacon.Build(Array.Empty<byte>(), Now));
        Assert.False(MeshBeacon.Verify(null, MeshBeacon.Build(Key(), Now), Now, out _));
    }

    // ── rotation ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A fixed tag would make every Mesh Sync device trackable across venues for as long as the
    /// mesh existed. Fifteen minutes matches how often an LE private address rotates, so this adds
    /// no linkability the radio does not already have.
    /// </summary>
    [Fact]
    public void The_tag_changes_between_epochs()
    {
        var key = Key();

        var first = MeshBeacon.Build(key, Now);
        var later = MeshBeacon.Build(key, Now + MeshBeacon.Epoch);

        Assert.NotEqual(first, later);
    }

    [Fact]
    public void A_clock_one_epoch_out_still_verifies_and_two_does_not()
    {
        var key = Key();
        var beacon = MeshBeacon.Build(key, Now);

        Assert.True(MeshBeacon.Verify(key, beacon, Now + MeshBeacon.Epoch, out _));
        Assert.True(MeshBeacon.Verify(key, beacon, Now - MeshBeacon.Epoch, out _));

        Assert.False(MeshBeacon.Verify(key, beacon, Now + MeshBeacon.Epoch * 2, out _));
        Assert.False(MeshBeacon.Verify(key, beacon, Now - MeshBeacon.Epoch * 2, out _));
    }

    /// <summary>An epoch byte is eight bits, so it wraps every 64 hours. It must still verify then.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(511)]
    public void The_epoch_byte_wrapping_does_not_break_verification(int epochs)
    {
        var key = Key();
        var moment = Now + MeshBeacon.Epoch * epochs;
        var beacon = MeshBeacon.Build(key, moment);

        Assert.True(MeshBeacon.Verify(key, beacon, moment, out _));
    }

    // ── flags are authenticated ──────────────────────────────────────────────

    [Fact]
    public void Flags_survive_the_round_trip()
    {
        var key = Key();
        var beacon = MeshBeacon.Build(key, Now, MeshBeaconFlags.PairingOpen | MeshBeaconFlags.CanBeCentral);

        Assert.True(MeshBeacon.Verify(key, beacon, Now, out var flags));
        Assert.True(flags.HasFlag(MeshBeaconFlags.PairingOpen));
        Assert.True(flags.HasFlag(MeshBeaconFlags.CanBeCentral));
    }

    /// <summary>The flags are mixed into the tag, so a flipped bit fails rather than being believed.</summary>
    [Fact]
    public void A_flipped_flag_bit_invalidates_the_beacon()
    {
        var key = Key();
        var beacon = MeshBeacon.Build(key, Now, MeshBeaconFlags.PairingOpen);

        beacon[0] ^= (byte)MeshBeaconFlags.CanBeCentral;

        Assert.False(MeshBeacon.Verify(key, beacon, Now, out _));
    }

    [Fact]
    public void A_beacon_from_a_future_layout_is_ignored_rather_than_misread()
    {
        var key = Key();
        var beacon = MeshBeacon.Build(key, Now);

        beacon[0] = (byte)((beacon[0] & ~(byte)MeshBeaconFlags.VersionMask) | 0x0F);

        Assert.False(MeshBeacon.Verify(key, beacon, Now, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(7)]
    public void A_beacon_of_the_wrong_length_is_refused(int length)
    {
        Assert.False(MeshBeacon.Verify(Key(), new byte[length], Now, out _));
    }

    // ── the advertisement has to fit ─────────────────────────────────────────

    /// <summary>
    /// 3 flags + 18 service UUID + 10 manufacturer data = 31 bytes exactly, which is the legacy
    /// limit. Any future field breaks discovery on the strictest stack, silently, so the budget is
    /// a test rather than a comment.
    /// </summary>
    [Fact]
    public void The_advertisement_fits_in_the_legacy_limit()
    {
        Assert.Equal(31, MeshBeacon.AdvertisementBytes(withBeacon: true));
        Assert.True(MeshBeacon.AdvertisementBytes(withBeacon: true) <= MeshBeacon.MaxAdvertisementBytes);
    }

    [Fact]
    public void Manufacturer_data_round_trips_under_the_company_id()
    {
        var beacon = MeshBeacon.Build(Key(), Now);
        var section = MeshBeacon.ManufacturerData(beacon);

        Assert.Equal(2 + MeshBeacon.Length, section.Length);
        Assert.True(MeshBeacon.TryReadManufacturerData(section, out var read));
        Assert.Equal(beacon, read);
    }

    [Fact]
    public void Another_vendors_manufacturer_data_is_not_read_as_a_beacon()
    {
        var section = new byte[2 + MeshBeacon.Length];
        section[0] = 0x4C;   // somebody else's company id
        section[1] = 0x00;

        Assert.False(MeshBeacon.TryReadManufacturerData(section, out _));
    }

    // ── pairing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A joining device has no mesh key, so the inviter advertises a tag derived from the pairing
    /// code instead. That is what lets two devices pair with no network at all.
    /// </summary>
    [Fact]
    public void A_joiner_finds_the_inviter_from_the_scanned_code_alone()
    {
        var inviter = DeviceIdentity.CreateEphemeral();

        var secret = MeshBeacon.PairingSecretFrom(inviter.PublicKey);
        var advertised = MeshBeacon.Build(secret, Now, MeshBeaconFlags.PairingOpen);

        // The joiner computes the same secret from the key it just scanned.
        var scanned = MeshBeacon.PairingSecretFrom(inviter.PublicKey);

        Assert.True(MeshBeacon.Verify(scanned, advertised, Now, out var flags));
        Assert.True(flags.HasFlag(MeshBeaconFlags.PairingOpen));

        var someoneElse = DeviceIdentity.CreateEphemeral();
        Assert.False(MeshBeacon.Verify(MeshBeacon.PairingSecretFrom(someoneElse.PublicKey), advertised, Now, out _));
    }

    // ── the rule ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The mesh key is a filter, not a credential.</b>
    ///
    /// <para>A reviewer's first instinct on seeing a mesh-wide shared secret is that it breaks the
    /// rule about never reintroducing a key that is not agreed per connection. It does not, and
    /// this is the assertion that keeps it that way: the key never reaches a session, and a device
    /// holding it is no closer to reading anything.</para>
    /// </summary>
    [Fact]
    public void The_mesh_key_never_reaches_a_session_key()
    {
        var key = Key();

        var a = PeerSecurity.CreateEphemeral();
        var b = PeerSecurity.CreateEphemeral();
        a.Peers.Trust(b.Identity.PublicKey, "b");
        b.Peers.Trust(a.Identity.PublicKey, "a");

        a.Peers.AdoptMeshKey(key);
        b.Peers.AdoptMeshKey(key);

        using var aEphemeral = EphemeralKeyPair.Create();
        using var bEphemeral = EphemeralKeyPair.Create();

        using var fromA = a.OpenSession(b.Identity.PublicKey, aEphemeral, bEphemeral.PublicKey);
        Assert.NotNull(fromA);

        var sealed1 = fromA!.Encrypt(0x00, "secret"u8.ToArray());
        Assert.NotNull(sealed1);

        // Somebody who has the mesh key and nothing else. Being in the mesh's beacon is not being
        // in the mesh.
        var eavesdropper = PeerSecurity.CreateEphemeral();
        eavesdropper.Peers.AdoptMeshKey(key);

        using var theirEphemeral = EphemeralKeyPair.Create();
        var theirSession = eavesdropper.OpenSession(b.Identity.PublicKey, theirEphemeral, bEphemeral.PublicKey);

        // They are not paired with b, so there is nothing to open it with at all.
        Assert.Null(theirSession);

        // And the raw key opens nothing either.
        Assert.ThrowsAny<CryptographicException>(() => CoreLib.CryptoEngine.DecryptTagged(sealed1!, key));
    }
}
