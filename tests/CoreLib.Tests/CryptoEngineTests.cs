using System.Security.Cryptography;
using CoreLib;

namespace CoreLib.Tests;

public class CryptoEngineTests
{
    private static byte[] TestKey()
    {
        var key = new byte[CryptoEngine.KeySize];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    [Fact]
    public void Encrypt_then_decrypt_round_trips()
    {
        var key = TestKey();
        var plaintext = System.Text.Encoding.UTF8.GetBytes("correct horse battery staple");

        var payload = CryptoEngine.Encrypt(plaintext, key);
        var recovered = CryptoEngine.Decrypt(payload, key);

        Assert.Equal(plaintext, recovered);
    }

    [Fact]
    public void Encrypt_produces_a_distinct_nonce_each_call()
    {
        var key = TestKey();
        var plaintext = new byte[64];

        var first = CryptoEngine.Encrypt(plaintext, key);
        var second = CryptoEngine.Encrypt(plaintext, key);

        // Reusing a nonce under the same key would be a catastrophic GCM failure.
        Assert.NotEqual(
            first.AsSpan(0, CryptoEngine.NonceSize).ToArray(),
            second.AsSpan(0, CryptoEngine.NonceSize).ToArray());
    }

    [Fact]
    public void Decrypt_rejects_a_tampered_payload()
    {
        var key = TestKey();
        var payload = CryptoEngine.Encrypt(new byte[128], key);

        payload[^1] ^= 0xFF;

        // ThrowsAny: the runtime raises AuthenticationTagMismatchException, a subclass.
        Assert.ThrowsAny<CryptographicException>(() => CryptoEngine.Decrypt(payload, key));
    }

    [Fact]
    public void Decrypt_rejects_the_wrong_key()
    {
        var payload = CryptoEngine.Encrypt(new byte[32], TestKey());

        Assert.ThrowsAny<CryptographicException>(() => CryptoEngine.Decrypt(payload, TestKey()));
    }

    [Fact]
    public void Decrypt_rejects_a_payload_shorter_than_the_header()
    {
        var key = TestKey();

        Assert.Throws<ArgumentException>(() => CryptoEngine.Decrypt(new byte[CryptoEngine.Overhead - 1], key));
    }

    [Fact]
    public void EncryptTagged_round_trips_the_content_type_and_body()
    {
        var key = TestKey();
        var body = new byte[4096];
        RandomNumberGenerator.Fill(body);

        var payload = CryptoEngine.EncryptTagged(0x01, body, key);
        var (contentType, recovered) = CryptoEngine.DecryptTagged(payload, key);

        Assert.Equal(0x01, contentType);
        Assert.Equal(body, recovered);
    }

    [Fact]
    public void EncryptTagged_of_an_empty_body_still_carries_its_type()
    {
        var key = TestKey();

        var payload = CryptoEngine.EncryptTagged(0x00, ReadOnlySpan<byte>.Empty, key);
        var (contentType, recovered) = CryptoEngine.DecryptTagged(payload, key);

        Assert.Equal(0x00, contentType);
        Assert.Empty(recovered);
    }

    [Fact]
    public void DeriveKey_is_deterministic_for_the_same_password_and_salt()
    {
        var salt = System.Text.Encoding.UTF8.GetBytes("a-sixteen-byte!!");

        var a = CryptoEngine.DeriveKey("hunter2", salt);
        var b = CryptoEngine.DeriveKey("hunter2", salt);

        Assert.Equal(a, b);
        Assert.Equal(CryptoEngine.KeySize, a.Length);
    }

    [Fact]
    public void DeriveKey_separates_different_salts()
    {
        var a = CryptoEngine.DeriveKey("hunter2", System.Text.Encoding.UTF8.GetBytes("salt-one-1234567"));
        var b = CryptoEngine.DeriveKey("hunter2", System.Text.Encoding.UTF8.GetBytes("salt-two-1234567"));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Encrypt_rejects_a_key_of_the_wrong_length()
    {
        Assert.Throws<ArgumentException>(() => CryptoEngine.Encrypt(new byte[16], new byte[16]));
    }
}
