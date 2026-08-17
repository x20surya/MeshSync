using System.Security.Cryptography;
using CoreLib.Identity;

namespace CoreLib.Tests;

/// <summary>
/// The stored key file, and the upgrade path onto a wrapped one.
///
/// The real protectors are platform code - DPAPI on Windows, the Keystore on Android - so what
/// is tested here is the part that is shared and the part that can go wrong quietly: whether an
/// existing plaintext key survives the upgrade, and whether a key that cannot be unwrapped is
/// refused rather than half-loaded.
/// </summary>
public class KeyProtectionTests
{
    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "meshsync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Stands in for a platform protector. Reversible, and deliberately not encryption.</summary>
    private sealed class ReversingProtector : IKeyProtector
    {
        public string Name => "a test protector";

        public byte[] Protect(byte[] plaintext) => plaintext.Reverse().ToArray();

        public byte[]? TryUnprotect(byte[] stored) => stored.Reverse().ToArray();
    }

    /// <summary>Stands in for a key file that came from another machine or another user.</summary>
    private sealed class RefusingProtector : IKeyProtector
    {
        public string Name => "a protector that refuses";

        public byte[] Protect(byte[] plaintext) => plaintext.Reverse().ToArray();

        public byte[]? TryUnprotect(byte[] stored) => null;
    }

    [Fact]
    public void A_wrapped_identity_survives_a_restart()
    {
        string directory = TempDirectory();
        var protector = new ReversingProtector();

        string fingerprint;
        using (var first = DeviceIdentity.LoadOrCreate(directory, protector))
        {
            fingerprint = first.Fingerprint;
        }

        using var second = DeviceIdentity.LoadOrCreate(directory, protector);
        Assert.Equal(fingerprint, second.Fingerprint);
    }

    /// <summary>The point of the exercise: the raw PKCS#8 must not be sitting on disk.</summary>
    [Fact]
    public void A_wrapped_key_file_is_not_a_readable_private_key()
    {
        string directory = TempDirectory();
        using var identity = DeviceIdentity.LoadOrCreate(directory, new ReversingProtector());

        byte[] onDisk = File.ReadAllBytes(Path.Combine(directory, "device.key"));

        Assert.StartsWith("MSK1", System.Text.Encoding.ASCII.GetString(onDisk, 0, 4));
        Assert.ThrowsAny<CryptographicException>(() =>
        {
            using var key = ECDiffieHellman.Create();
            key.ImportPkcs8PrivateKey(onDisk, out _);
        });
    }

    /// <summary>
    /// The upgrade path. A key written by a build with no protector must be adopted as-is and
    /// rewritten wrapped - the identity is unchanged, so nothing re-pairs.
    /// </summary>
    [Fact]
    public void An_existing_plaintext_key_is_migrated_without_re_pairing()
    {
        string directory = TempDirectory();

        string fingerprint;
        using (var legacy = DeviceIdentity.LoadOrCreate(directory))
        {
            fingerprint = legacy.Fingerprint;
        }

        // Written by the old build: raw PKCS#8, no marker.
        byte[] before = File.ReadAllBytes(Path.Combine(directory, "device.key"));
        Assert.NotEqual("MSK1", System.Text.Encoding.ASCII.GetString(before, 0, 4));

        using (var upgraded = DeviceIdentity.LoadOrCreate(directory, new ReversingProtector()))
        {
            Assert.Equal(fingerprint, upgraded.Fingerprint);
        }

        byte[] after = File.ReadAllBytes(Path.Combine(directory, "device.key"));
        Assert.StartsWith("MSK1", System.Text.Encoding.ASCII.GetString(after, 0, 4));

        // And it still loads on the next run, from the wrapped form.
        using var later = DeviceIdentity.LoadOrCreate(directory, new ReversingProtector());
        Assert.Equal(fingerprint, later.Fingerprint);
    }

    /// <summary>
    /// A key that cannot be unwrapped - copied from another machine, or a keystore entry that
    /// has gone - costs a re-pair. That is the intended outcome, and it must be a clean new
    /// identity rather than a half-loaded one.
    /// </summary>
    [Fact]
    public void A_key_that_cannot_be_unwrapped_yields_a_fresh_identity()
    {
        string directory = TempDirectory();

        string original;
        using (var first = DeviceIdentity.LoadOrCreate(directory, new ReversingProtector()))
        {
            original = first.Fingerprint;
        }

        using var second = DeviceIdentity.LoadOrCreate(directory, new RefusingProtector());

        Assert.NotEqual(original, second.Fingerprint);
        Assert.True(DeviceIdentity.IsValidPublicKey(second.PublicKey));
    }

    /// <summary>
    /// A build with no protector must not quietly discard a wrapped key it cannot read. The
    /// key is still there and a later run will very likely open it, so replacing it would throw
    /// away a working identity for no reason.
    /// </summary>
    [Fact]
    public void A_wrapped_key_is_not_replaced_by_a_build_that_cannot_unwrap_it()
    {
        string directory = TempDirectory();
        using (var _ = DeviceIdentity.LoadOrCreate(directory, new ReversingProtector())) { }

        byte[] before = File.ReadAllBytes(Path.Combine(directory, "device.key"));

        using (var _ = DeviceIdentity.LoadOrCreate(directory)) { }

        Assert.Equal(before, File.ReadAllBytes(Path.Combine(directory, "device.key")));
    }
}
