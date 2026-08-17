using System.Security.Cryptography;
using CoreLib.Identity;

namespace CoreLib.Tests;

/// <summary>
/// The key agreement, which is the one piece where a subtle mistake produces no symptom until
/// two real devices refuse to talk to each other with nothing on the wire to say why.
/// </summary>
public class SessionKeysTests
{
    /// <summary>
    /// One complete two-sided agreement, the way a connection actually does it: both ends mint
    /// an ephemeral, announce it, and derive from their own private material plus what the
    /// other announced.
    /// </summary>
    private static (byte[] Local, byte[] Remote) Agree(DeviceIdentity a, DeviceIdentity b)
    {
        using var ephemeralA = EphemeralKeyPair.Create();
        using var ephemeralB = EphemeralKeyPair.Create();

        return (SessionKeys.Derive(a, b.PublicKey, ephemeralA, ephemeralB.PublicKey),
                SessionKeys.Derive(b, a.PublicKey, ephemeralB, ephemeralA.PublicKey));
    }

    /// <summary>
    /// The property the whole scheme rests on. Both devices have to arrive at the same key from
    /// opposite ends without exchanging it, or every payload fails to decrypt.
    /// </summary>
    [Fact]
    public void Two_devices_agree_the_same_key_from_opposite_sides()
    {
        using var laptop = DeviceIdentity.CreateEphemeral();
        using var phone = DeviceIdentity.CreateEphemeral();

        var (fromLaptop, fromPhone) = Agree(laptop, phone);

        Assert.Equal(CryptoEngine.KeySize, fromLaptop.Length);
        Assert.Equal(fromLaptop, fromPhone);
    }

    /// <summary>
    /// Guards the ordering. The fingerprints are sorted into the salt precisely so the caller's
    /// side does not change the answer - unsorted, the two ends derive different keys and
    /// nothing ever decrypts.
    /// </summary>
    [Fact]
    public void Agreement_does_not_depend_on_which_side_asks()
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            using var a = DeviceIdentity.CreateEphemeral();
            using var b = DeviceIdentity.CreateEphemeral();

            var (fromA, fromB) = Agree(a, b);
            Assert.Equal(fromA, fromB);
        }
    }

    /// <summary>
    /// Forward secrecy, stated as an assertion. The same two devices must never derive the same
    /// key twice - that is exactly what the static-static agreement this replaced did, and why
    /// one recovered private key would have opened every session it had ever had.
    /// </summary>
    [Fact]
    public void The_same_pair_agrees_a_different_key_on_every_connection()
    {
        using var laptop = DeviceIdentity.CreateEphemeral();
        using var phone = DeviceIdentity.CreateEphemeral();

        var (first, _) = Agree(laptop, phone);
        var (second, _) = Agree(laptop, phone);
        var (third, _) = Agree(laptop, phone);

        Assert.NotEqual(first, second);
        Assert.NotEqual(second, third);
        Assert.NotEqual(first, third);
    }

    /// <summary>
    /// The authentication half. An attacker can complete the ephemeral exchange with anyone -
    /// it is unauthenticated by construction - but without a private key this device has paired
    /// with they cannot complete the static one, so the two ends never agree.
    /// </summary>
    [Fact]
    public void An_injected_ephemeral_key_does_not_agree()
    {
        using var laptop = DeviceIdentity.CreateEphemeral();
        using var phone = DeviceIdentity.CreateEphemeral();
        using var attacker = DeviceIdentity.CreateEphemeral();

        using var laptopEphemeral = EphemeralKeyPair.Create();
        using var attackerEphemeral = EphemeralKeyPair.Create();

        // The laptop believes it is talking to the phone, and the attacker's ephemeral key
        // reached it instead.
        byte[] laptopKey = SessionKeys.Derive(laptop, phone.PublicKey, laptopEphemeral, attackerEphemeral.PublicKey);

        // The attacker holds their own identity, not the phone's, so their static secret with
        // the laptop is a different value.
        byte[] attackerKey = SessionKeys.Derive(attacker, laptop.PublicKey, attackerEphemeral, laptopEphemeral.PublicKey);

        Assert.NotEqual(laptopKey, attackerKey);

        byte[] sealed_ = CryptoEngine.EncryptTagged(0x00, "a password"u8, laptopKey);
        Assert.ThrowsAny<CryptographicException>(() => CryptoEngine.DecryptTagged(sealed_, attackerKey));
    }

    /// <summary>
    /// The reason keys are per pair rather than one for the mesh. With a shared key this
    /// assertion could not even be written.
    /// </summary>
    [Fact]
    public void Different_pairs_get_different_keys()
    {
        using var laptop = DeviceIdentity.CreateEphemeral();
        using var phone = DeviceIdentity.CreateEphemeral();
        using var tablet = DeviceIdentity.CreateEphemeral();

        var (laptopPhone, _) = Agree(laptop, phone);
        var (laptopTablet, _) = Agree(laptop, tablet);
        var (phoneTablet, _) = Agree(phone, tablet);

        Assert.NotEqual(laptopPhone, laptopTablet);
        Assert.NotEqual(laptopPhone, phoneTablet);
        Assert.NotEqual(laptopTablet, phoneTablet);
    }

    /// <summary>
    /// The previous test expressed in the terms that actually matter: a payload sealed for one
    /// peer is refused by another, rather than decrypting to rubbish.
    /// </summary>
    [Fact]
    public void A_third_device_cannot_read_traffic_between_the_other_two()
    {
        using var laptop = DeviceIdentity.CreateEphemeral();
        using var phone = DeviceIdentity.CreateEphemeral();
        using var tablet = DeviceIdentity.CreateEphemeral();

        var (between, _) = Agree(laptop, phone);
        var (eavesdropper, _) = Agree(laptop, tablet);

        byte[] sealed_ = CryptoEngine.EncryptTagged(0x00, "a password"u8, between);

        // ThrowsAny, because GCM rejects it as an authentication tag mismatch - a subclass of
        // CryptographicException, and the specific failure that matters: it did not merely
        // decrypt to rubbish, it refused.
        Assert.ThrowsAny<CryptographicException>(() => CryptoEngine.DecryptTagged(sealed_, eavesdropper));
    }

    /// <summary>An ephemeral key is announced as base64, and a mangled one must fail loudly.</summary>
    [Fact]
    public void Agreeing_against_rubbish_throws_rather_than_producing_a_key()
    {
        using var identity = DeviceIdentity.CreateEphemeral();
        using var peer = DeviceIdentity.CreateEphemeral();
        using var ephemeral = EphemeralKeyPair.Create();

        Assert.ThrowsAny<Exception>(() => SessionKeys.Derive(identity, "not a key", ephemeral, ephemeral.PublicKey));
        Assert.ThrowsAny<Exception>(() => SessionKeys.Derive(identity, peer.PublicKey, ephemeral, "not a key"));
        Assert.Throws<ArgumentException>(() => SessionKeys.Derive(identity, "", ephemeral, ephemeral.PublicKey));
        Assert.Throws<ArgumentException>(() => SessionKeys.Derive(identity, peer.PublicKey, ephemeral, ""));
    }

    /// <summary>Every ephemeral keypair must be new, or there is no forward secrecy to have.</summary>
    [Fact]
    public void Every_ephemeral_keypair_is_distinct()
    {
        var seen = new HashSet<string>();

        for (int i = 0; i < 16; i++)
        {
            using var ephemeral = EphemeralKeyPair.Create();
            Assert.True(seen.Add(ephemeral.PublicKey), "An ephemeral public key repeated.");
            Assert.True(DeviceIdentity.IsValidPublicKey(ephemeral.PublicKey));
        }
    }
}
