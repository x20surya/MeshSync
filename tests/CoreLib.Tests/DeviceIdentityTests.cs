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
