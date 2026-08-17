using System;
using System.IO;
using System.Security.Cryptography;

namespace CoreLib.Identity
{
    /// <summary>
    /// This device's long-lived cryptographic identity, and the source of every session key.
    ///
    /// <para>What it replaces: both sides used to derive from a literal
    /// <c>DeriveKey("MasterPassword123", "Salt")</c>, so every install of the app shared one
    /// key and the listener accepted anything that could reach it. On a public repository that
    /// meant anyone who had ever run the code could read or inject clipboard traffic. There is
    /// now a keypair per device and a distinct key per pair of devices.</para>
    ///
    /// <para>Why a keypair rather than a shared secret: the identity is also what decides
    /// roles. Neither device is a server by nature, so when both dial each other at once the
    /// collision is settled by comparing fingerprints - which needs an identity that is stable
    /// across restarts. The <c>TrustManager</c> this replaces minted a fresh keypair on every
    /// construction, so nothing could be keyed off it - and it has been deleted rather than
    /// left alongside, because a second, broken notion of trust is worse than none.</para>
    ///
    /// <para><b>Storage.</b> The private key is written to an application-private file and
    /// protected by nothing but the filesystem. On Android that directory is genuinely private
    /// to the app; on Windows it is readable by the signed-in user, which means it is as
    /// exposed as anything else that user can read. It is not hardware-backed and does not
    /// survive being copied to another machine as anything other than a clone of this
    /// identity.</para>
    /// </summary>
    public sealed class DeviceIdentity : IDisposable
    {
        private const string PrivateKeyFileName = "device.key";

        private readonly ECDiffieHellman _key;
        private bool _disposed;

        /// <summary>This device's public key, as base64 SubjectPublicKeyInfo. Goes in the QR code.</summary>
        public string PublicKey { get; }

        /// <summary>
        /// SHA-256 of the public key, lowercase hex. Stable, unique, and the value that
        /// settles which device takes which role on a link.
        /// </summary>
        public string Fingerprint { get; }

        /// <summary>The first four bytes of the fingerprint, grouped for a human to compare.</summary>
        public string ShortFingerprint => Shorten(Fingerprint);

        private DeviceIdentity(ECDiffieHellman key)
        {
            _key = key;
            PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            Fingerprint = FingerprintOf(PublicKey);
        }

        /// <summary>
        /// Loads this device's identity, creating and persisting one on first run.
        ///
        /// A corrupt or unreadable key file is replaced rather than thrown, because a device
        /// that cannot load its identity can do nothing at all - and a fresh identity, which
        /// costs a re-pair, is a better outcome than an app that will not start. The re-pair
        /// is visible; a silent failure to sync would not be.
        /// </summary>
        public static DeviceIdentity LoadOrCreate(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A directory is required.", nameof(directory));

            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, PrivateKeyFileName);

            if (File.Exists(path))
            {
                try
                {
                    var key = ECDiffieHellman.Create();
                    key.ImportPkcs8PrivateKey(File.ReadAllBytes(path), out _);
                    return new DeviceIdentity(key);
                }
                catch (Exception ex)
                {
                    Diagnostics.Log.Write("Identity", "The stored identity could not be read; generating a new one. Devices will need re-pairing.", ex);
                }
            }

            var created = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

            try
            {
                File.WriteAllBytes(path, created.ExportPkcs8PrivateKey());
                RestrictToOwner(path);
            }
            catch (Exception ex)
            {
                // Worth continuing: syncing works for this run, it just will not survive a
                // restart. Failing outright would be a worse outcome than a logged warning.
                Diagnostics.Log.Write("Identity", "Could not persist the device identity; it will not survive a restart.", ex);
            }

            var identity = new DeviceIdentity(created);
            Diagnostics.Log.Write("Identity", $"Generated a new device identity, fingerprint {identity.ShortFingerprint}.");
            return identity;
        }

        /// <summary>Creates an identity that is never written to disk. For tests.</summary>
        public static DeviceIdentity CreateEphemeral() =>
            new(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

        /// <summary>
        /// The raw ECDH secret between this device's long-lived key and a peer's.
        ///
        /// <para>Deliberately not a usable key on its own, and deliberately not public.
        /// <see cref="SessionKeys"/> mixes it with an ephemeral secret before anything is
        /// encrypted, because on its own it is exactly the static-static agreement that had no
        /// forward secrecy: the same pair of devices would derive the same key for ever, so
        /// recovering one private key would open every session that pair had ever had.</para>
        ///
        /// <para>What it still provides is authentication. Only a device whose public key this
        /// one holds can compute the matching value, which is what stops an attacker
        /// substituting an ephemeral key of their own.</para>
        /// </summary>
        internal byte[] RawSecretWith(string peerPublicKey)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(peerPublicKey)) throw new ArgumentException("A peer public key is required.", nameof(peerPublicKey));

            using var peer = ECDiffieHellman.Create();
            peer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(peerPublicKey), out _);

            return _key.DeriveRawSecretAgreement(peer.PublicKey);
        }

        /// <summary>SHA-256 of a base64 public key, lowercase hex. Throws if it is not a key.</summary>
        public static string FingerprintOf(string publicKey)
        {
            if (string.IsNullOrWhiteSpace(publicKey)) throw new ArgumentException("A public key is required.", nameof(publicKey));

            byte[] hash = SHA256.HashData(Convert.FromBase64String(publicKey));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>True when the key parses. Used to reject a mistyped or truncated pairing code.</summary>
        public static bool IsValidPublicKey(string? publicKey)
        {
            if (string.IsNullOrWhiteSpace(publicKey)) return false;

            try
            {
                using var peer = ECDiffieHellman.Create();
                peer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Four bytes of a fingerprint, grouped, for a human to read out or compare.</summary>
        public static string Shorten(string fingerprint)
        {
            if (string.IsNullOrEmpty(fingerprint)) return "";

            string head = fingerprint.Length <= 16 ? fingerprint : fingerprint.Substring(0, 16);
            var parts = new System.Collections.Generic.List<string>();
            for (int i = 0; i < head.Length; i += 4) parts.Add(head.Substring(i, Math.Min(4, head.Length - i)));
            return string.Join("-", parts).ToUpperInvariant();
        }

        /// <summary>
        /// Best-effort tightening of the key file's permissions.
        ///
        /// Android already gives every app a private directory, so this is really for Windows,
        /// where the file would otherwise inherit whatever the parent allows. It is advisory:
        /// a failure here is logged rather than fatal, because the alternative is refusing to
        /// run on a filesystem that does not support ACLs.
        /// </summary>
        private static void RestrictToOwner(string path)
        {
            try
            {
                if (OperatingSystem.IsWindows()) return; // Inherits the user-private profile ACL.

                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Write("Identity", "Could not tighten permissions on the key file", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _key.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DeviceIdentity));
        }
    }
}
