using System;
using Android.Security.Keystore;
using CoreLib.Diagnostics;
using CoreLib.Identity;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace AndroidClient.Platforms.Android
{
    /// <summary>
    /// Wraps the device key with an AES key held in the Android Keystore.
    ///
    /// <para><b>Why wrap rather than generate in the keystore.</b> Generating the identity key
    /// inside the keystore would be stronger - the private key would never exist in process
    /// memory at all - but it means doing ECDH through <c>javax.crypto.KeyAgreement</c> instead
    /// of .NET's <c>ECDiffieHellman</c>, which forks the key agreement between platforms. That
    /// agreement is the one piece where the two ends must compute byte-identical results, so
    /// forking it is exactly the wrong place to spend risk. Wrapping keeps one implementation
    /// and still means the bytes on disk are useless without a key that never leaves the TEE.
    /// </para>
    ///
    /// <para>The app directory is already private to the app, so this defends against the cases
    /// that get past that: a rooted device, an offline image of the filesystem, and a backup
    /// that captured the file anyway. The manifest sets <c>allowBackup="false"</c> for the last
    /// of those, and this covers it if the flag is ever missed.</para>
    /// </summary>
    public sealed class AndroidKeyProtector : IKeyProtector
    {
        private const string KeystoreName = "AndroidKeyStore";
        private const string KeyAlias = "MeshSyncDeviceKeyWrap";
        private const string Transformation = "AES/GCM/NoPadding";

        /// <summary>GCM's nonce, prefixed to the wrapped blob because it is needed to unwrap.</summary>
        private const int NonceBytes = 12;

        private const int TagBits = 128;

        public string Name => "the Android Keystore";

        public byte[] Protect(byte[] plaintext)
        {
            var cipher = Cipher.GetInstance(Transformation)
                ?? throw new InvalidOperationException("AES/GCM is unavailable on this device.");

            cipher.Init(CipherMode.EncryptMode, LoadOrCreateWrappingKey());

            byte[] nonce = cipher.GetIV() ?? throw new InvalidOperationException("The cipher produced no IV.");
            byte[] sealed_ = cipher.DoFinal(plaintext) ?? throw new InvalidOperationException("Wrapping produced nothing.");

            var blob = new byte[nonce.Length + sealed_.Length];
            nonce.CopyTo(blob, 0);
            sealed_.CopyTo(blob, nonce.Length);
            return blob;
        }

        public byte[]? TryUnprotect(byte[] stored)
        {
            try
            {
                if (stored.Length <= NonceBytes) return null;

                var nonce = new byte[NonceBytes];
                Buffer.BlockCopy(stored, 0, nonce, 0, NonceBytes);

                var body = new byte[stored.Length - NonceBytes];
                Buffer.BlockCopy(stored, NonceBytes, body, 0, body.Length);

                var cipher = Cipher.GetInstance(Transformation);
                if (cipher == null) return null;

                cipher.Init(CipherMode.DecryptMode, LoadWrappingKey(), new GCMParameterSpec(TagBits, nonce));
                return cipher.DoFinal(body);
            }
            catch (Exception ex)
            {
                // Expected if the keystore entry has gone - a factory reset, a restore onto
                // another device, or the user clearing app data. Costs a re-pair, which is
                // visible, and is precisely the case this is meant to make unrecoverable.
                Log.Write("Identity", "The Keystore could not unwrap the stored identity.", ex);
                return null;
            }
        }

        private static IKey LoadWrappingKey()
        {
            var keystore = KeyStore.GetInstance(KeystoreName)
                ?? throw new InvalidOperationException("The Android Keystore is unavailable.");

            keystore.Load(null);

            return keystore.GetKey(KeyAlias, null)
                ?? throw new InvalidOperationException("No wrapping key is stored.");
        }

        /// <summary>
        /// The AES key that wraps the identity, created once and then reused.
        ///
        /// <c>SetUserAuthenticationRequired(false)</c> deliberately: the links are held by a
        /// foreground service that has to reconnect while the phone is locked, and a key that
        /// needed an unlock would make Bluetooth standby impossible - which is the one thing
        /// this app is for.
        /// </summary>
        private static IKey LoadOrCreateWrappingKey()
        {
            var keystore = KeyStore.GetInstance(KeystoreName)
                ?? throw new InvalidOperationException("The Android Keystore is unavailable.");

            keystore.Load(null);

            var existing = keystore.GetKey(KeyAlias, null);
            if (existing != null) return existing;

            var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeystoreName)
                ?? throw new InvalidOperationException("AES key generation is unavailable.");

            var spec = new KeyGenParameterSpec.Builder(
                    KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)!
                .SetBlockModes(KeyProperties.BlockModeGcm)!
                .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
                .SetKeySize(256)!
                .SetUserAuthenticationRequired(false)!
                .Build();

            generator.Init(spec);
            return generator.GenerateKey()
                ?? throw new InvalidOperationException("The Keystore returned no key.");
        }
    }
}
