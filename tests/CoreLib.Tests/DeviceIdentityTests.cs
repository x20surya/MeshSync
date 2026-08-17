using System.Security.Cryptography;
using CoreLib.Identity;

namespace CoreLib.Tests;

public class DeviceIdentityTests
{
    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "meshsync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// The property the whole scheme rests on. Both devices have to arrive at the same key
    /// from opposite ends without exchanging it, or every payload fails to decrypt.
    /// </summary>
    [Fact]
    public void Two_devices_derive_the_same_key_from_opposite_sides()
    {
        using var laptop = DeviceIdentity.CreateEphemeral();
        using var phone = DeviceIdentity.CreateEphemeral();

        byte[] fromLaptop = laptop.DeriveSharedKey(phone.PublicKey);
        byte[] fromPhone = phone.DeriveSharedKey(laptop.PublicKey);

        Assert.Equal(CryptoEngine.KeySize, fromLaptop.Length);
        Assert.Equal(fromLaptop, fromPhone);
    }

    /// <summary>
    /// Guards the ordering. The fingerprints are sorted before being mixed in precisely so
    /// that the caller's side does not change the answer - unsorted, the two ends would
    /// derive different keys and nothing would ever decrypt.
    /// </summary>
    [Fact]
    public void Derivation_does_not_depend_on_which_side_asks()
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            using var a = DeviceIdentity.CreateEphemeral();
            using var b = DeviceIdentity.CreateEphemeral();

            Assert.Equal(a.DeriveSharedKey(b.PublicKey), b.DeriveSharedKey(a.PublicKey));
        }
    }

    /// <summary>
    /// The reason keys are per peer rather than one for the mesh. With a shared key this
    /// assertion could not even be written.
    /// </summary>
    [Fact]
    public void Different_pairs_get_different_keys()
    {
        using var laptop = DeviceIdentity.CreateEphemeral();
        using var phone = DeviceIdentity.CreateEphemeral();
        using var tablet = DeviceIdentity.CreateEphemeral();

        byte[] laptopPhone = laptop.DeriveSharedKey(phone.PublicKey);
        byte[] laptopTablet = laptop.DeriveSharedKey(tablet.PublicKey);
        byte[] phoneTablet = phone.DeriveSharedKey(tablet.PublicKey);

        Assert.NotEqual(laptopPhone, laptopTablet);
        Assert.NotEqual(laptopPhone, phoneTablet);
        Assert.NotEqual(laptopTablet, phoneTablet);
    }

    /// <summary>
    /// A payload sealed for one peer must be unreadable by another, which is the whole point
    /// of the previous test expressed in the terms that actually matter.
    /// </summary>
    [Fact]
    public void A_third_device_cannot_read_traffic_between_the_other_two()
    {
        using var laptop = DeviceIdentity.CreateEphemeral();
        using var phone = DeviceIdentity.CreateEphemeral();
        using var tablet = DeviceIdentity.CreateEphemeral();

        byte[] between = laptop.DeriveSharedKey(phone.PublicKey);
        byte[] eavesdropper = tablet.DeriveSharedKey(laptop.PublicKey);

        byte[] sealed_ = CryptoEngine.EncryptTagged(0x00, "a password"u8, between);

        // ThrowsAny, because GCM rejects it as an authentication tag mismatch - a subclass of
        // CryptographicException, and the specific failure that matters: it did not merely
        // decrypt to rubbish, it refused.
        Assert.ThrowsAny<CryptographicException>(() => CryptoEngine.DecryptTagged(sealed_, eavesdropper));
    }

    /// <summary>
    /// The identity has to survive a restart, or roles could not be settled by comparing
    /// fingerprints and every restart would cost a re-pair. This is the bug the old
    /// TrustManager had: a fresh keypair on every construction.
    /// </summary>
    [Fact]
    public void Identity_survives_a_restart()
    {
        string directory = TempDirectory();

        string publicKey;
        string fingerprint;

        using (var first = DeviceIdentity.LoadOrCreate(directory))
        {
            publicKey = first.PublicKey;
            fingerprint = first.Fingerprint;
        }

        using var second = DeviceIdentity.LoadOrCreate(directory);

        Assert.Equal(publicKey, second.PublicKey);
        Assert.Equal(fingerprint, second.Fingerprint);
    }

    /// <summary>Two installs must never collide, however close together they are created.</summary>
    [Fact]
    public void Separate_installs_get_separate_identities()
    {
        using var a = DeviceIdentity.LoadOrCreate(TempDirectory());
        using var b = DeviceIdentity.LoadOrCreate(TempDirectory());

        Assert.NotEqual(a.Fingerprint, b.Fingerprint);
    }

    [Fact]
    public void Fingerprint_is_a_stable_hash_of_the_public_key()
    {
        using var identity = DeviceIdentity.CreateEphemeral();

        Assert.Equal(identity.Fingerprint, DeviceIdentity.FingerprintOf(identity.PublicKey));
        Assert.Equal(64, identity.Fingerprint.Length); // SHA-256 as hex
        Assert.Equal(identity.Fingerprint, identity.Fingerprint.ToLowerInvariant());
    }

    /// <summary>A mistyped or truncated pairing code must fail at entry, not as a link that never works.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 at all!")]
    [InlineData("aGVsbG8gd29ybGQ=")] // valid base64, not a key
    public void Rubbish_is_not_a_public_key(string? candidate)
    {
        Assert.False(DeviceIdentity.IsValidPublicKey(candidate));
    }

    [Fact]
    public void A_real_public_key_validates()
    {
        using var identity = DeviceIdentity.CreateEphemeral();
        Assert.True(DeviceIdentity.IsValidPublicKey(identity.PublicKey));
    }

    [Fact]
    public void Deriving_against_rubbish_throws_rather_than_producing_a_key()
    {
        using var identity = DeviceIdentity.CreateEphemeral();

        Assert.ThrowsAny<Exception>(() => identity.DeriveSharedKey("not a key"));
        Assert.Throws<ArgumentException>(() => identity.DeriveSharedKey(""));
    }

    /// <summary>The short form is for a human to compare, so it has to be readable and stable.</summary>
    [Fact]
    public void Short_fingerprint_is_grouped_and_derived_from_the_full_one()
    {
        using var identity = DeviceIdentity.CreateEphemeral();

        string shortForm = identity.ShortFingerprint;

        Assert.Equal(19, shortForm.Length); // four groups of four, three separators
        Assert.Equal(3, shortForm.Count(c => c == '-'));
        Assert.Equal(shortForm, DeviceIdentity.Shorten(identity.Fingerprint));
        Assert.Equal(identity.Fingerprint[..4].ToUpperInvariant(), shortForm[..4]);
    }
}
