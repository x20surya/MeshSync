using System;
using System.Security.Cryptography;

namespace CoreLib.Identity
{
    /// <summary>
    /// A keypair minted for one connection and thrown away with it.
    ///
    /// <para>Its only job is to make the session key unrecoverable afterwards. The device
    /// keypair is long-lived by necessity - it is the identity - so a secret derived from it
    /// alone can always be recomputed by anyone who later obtains the private key. A key that
    /// exists only for the life of a socket cannot be.</para>
    /// </summary>
    public sealed class EphemeralKeyPair : IDisposable
    {
        private readonly ECDiffieHellman _key;
        private bool _disposed;

        /// <summary>Base64 SubjectPublicKeyInfo, announced in the hello.</summary>
        public string PublicKey { get; }

        private EphemeralKeyPair(ECDiffieHellman key)
        {
            _key = key;
            PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        }

        public static EphemeralKeyPair Create() =>
            new(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

        /// <summary>The raw ECDH secret with a peer's ephemeral key. Never used on its own.</summary>
        internal byte[] RawSecretWith(string peerEphemeralPublicKey)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            using var peer = ECDiffieHellman.Create();
            peer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(peerEphemeralPublicKey), out _);

            return _key.DeriveRawSecretAgreement(peer.PublicKey);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _key.Dispose();
        }
    }

    /// <summary>
    /// Agrees the key one connection is encrypted with.
    ///
    /// <para><b>What it replaces.</b> A static-static agreement: the same pair of devices always
    /// derived the same key, for ever. It authenticated correctly, but it had no forward
    /// secrecy - recovering one device's private key would decrypt every session that device
    /// had ever had with that peer, including traffic captured years earlier.</para>
    ///
    /// <para><b>The construction.</b> Two ECDH secrets are mixed, and each answers a different
    /// question:</para>
    /// <code>
    /// key = HKDF-SHA256(
    ///     ikm  = ECDH(ephemeral_local, ephemeral_peer)   // forward secrecy
    ///         || ECDH(static_local,    static_peer),     // authentication
    ///     salt = sorted(fingerprint_local, fingerprint_peer),
    ///     info = "MeshSync/session-key/v2")
    /// </code>
    ///
    /// <para>The ephemeral half is what nobody can recompute later, because both halves of it
    /// are discarded when the connection closes. The static half is what stops an attacker
    /// simply substituting their own ephemeral key: they can complete the first ECDH with
    /// anyone, but not the second without a private key this device has paired with, so the
    /// two ends derive different keys and AES-GCM refuses the payload.</para>
    ///
    /// <para>This is the shape of the Noise framework's <c>KK</c> handshake, deliberately, so it
    /// can be reviewed against a known-good pattern rather than assessed as a bespoke
    /// invention. It is not a full Noise implementation and does not claim to be.</para>
    ///
    /// <para>The fingerprints are sorted into the salt for the same reason they always were:
    /// unsorted, the two ends mix the same bytes in different orders, derive different keys,
    /// and every payload fails to decrypt with nothing on the wire to say why.</para>
    /// </summary>
    public static class SessionKeys
    {
        /// <summary>
        /// Bound into every derived key so a secret agreed for this app cannot be replayed into
        /// some other protocol that happens to use the same curve and the same keys. The v2
        /// marks the move from static-static to the mixed agreement above.
        /// </summary>
        private static readonly byte[] Context =
            System.Text.Encoding.UTF8.GetBytes("MeshSync/session-key/v2");

        /// <summary>
        /// Derives the key for one connection. Both ends call this with their own private
        /// material and the other's announced public keys, and arrive at the same value.
        /// </summary>
        public static byte[] Derive(DeviceIdentity identity,
                                    string peerStaticPublicKey,
                                    EphemeralKeyPair localEphemeral,
                                    string peerEphemeralPublicKey)
        {
            ArgumentNullException.ThrowIfNull(identity);
            ArgumentNullException.ThrowIfNull(localEphemeral);
            if (string.IsNullOrWhiteSpace(peerStaticPublicKey))
                throw new ArgumentException("A peer static public key is required.", nameof(peerStaticPublicKey));
            if (string.IsNullOrWhiteSpace(peerEphemeralPublicKey))
                throw new ArgumentException("A peer ephemeral public key is required.", nameof(peerEphemeralPublicKey));

            byte[] ephemeralSecret = localEphemeral.RawSecretWith(peerEphemeralPublicKey);
            byte[] staticSecret = identity.RawSecretWith(peerStaticPublicKey);

            // One buffer rather than two, so the whole input to the KDF can be wiped in a
            // single pass rather than leaving either half behind in a stray array.
            byte[] material = new byte[ephemeralSecret.Length + staticSecret.Length];

            try
            {
                Buffer.BlockCopy(ephemeralSecret, 0, material, 0, ephemeralSecret.Length);
                Buffer.BlockCopy(staticSecret, 0, material, ephemeralSecret.Length, staticSecret.Length);

                string peerFingerprint = DeviceIdentity.FingerprintOf(peerStaticPublicKey);
                var (first, second) = string.CompareOrdinal(identity.Fingerprint, peerFingerprint) <= 0
                    ? (identity.Fingerprint, peerFingerprint)
                    : (peerFingerprint, identity.Fingerprint);

                byte[] salt = System.Text.Encoding.UTF8.GetBytes(first + second);

                return HKDF.DeriveKey(HashAlgorithmName.SHA256, material, CryptoEngine.KeySize, salt, Context);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(material);
                CryptographicOperations.ZeroMemory(ephemeralSecret);
                CryptographicOperations.ZeroMemory(staticSecret);
            }
        }
    }
}
