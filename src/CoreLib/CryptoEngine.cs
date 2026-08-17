using System;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace CoreLib
{
    public static class CryptoEngine
    {
        public const int KeySize = 32;   // 256 bits for AES-256
        public const int NonceSize = 12; // 96 bits for GCM
        public const int TagSize = 16;   // 128 bits for GCM

        /// <summary>Overhead added to a plaintext by <see cref="Encrypt"/>.</summary>
        public const int Overhead = NonceSize + TagSize;

        /// <summary>
        /// Overhead added by <see cref="EncryptTagged"/> - the nonce and tag plus the content
        /// type byte. Exposed so a caller can size a payload before it knows which key it will
        /// be encrypted with, which matters now that the key depends on the peer.
        /// </summary>
        public const int TaggedOverheadBytes = Overhead + 1;

        /// <summary>
        /// Derives a 256-bit encryption key from a master password using Argon2id.
        /// This is deliberately expensive (~64 MB, ~200 ms) - never call it on a UI thread.
        /// </summary>
        public static byte[] DeriveKey(string masterPassword, byte[] salt)
        {
            if (masterPassword == null) throw new ArgumentNullException(nameof(masterPassword));
            if (salt == null) throw new ArgumentNullException(nameof(salt));

            using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(masterPassword));
            argon2.Salt = salt;
            argon2.DegreeOfParallelism = 4; // 4 threads
            argon2.Iterations = 3;          // 3 iterations (OWASP recommendation)
            argon2.MemorySize = 65536;      // 64 MB of RAM

            return argon2.GetBytes(KeySize);
        }

        /// <summary>
        /// Encrypts plaintext using AES-256-GCM.
        /// Returns a single byte array containing [Nonce (12) | Tag (16) | Ciphertext].
        /// </summary>
        public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, byte[] key)
        {
            ValidateKey(key);

            // Written straight into the output buffer: the previous implementation allocated
            // separate nonce, tag and ciphertext arrays and then copied all three, which cost
            // three extra full-size copies of every screenshot.
            byte[] payload = new byte[Overhead + plaintext.Length];
            var nonce = payload.AsSpan(0, NonceSize);
            var tag = payload.AsSpan(NonceSize, TagSize);
            var ciphertext = payload.AsSpan(Overhead, plaintext.Length);

            RandomNumberGenerator.Fill(nonce);

            using var aesGcm = new AesGcm(key, TagSize);
            aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

            return payload;
        }

        /// <summary>
        /// Decrypts a payload formatted as [Nonce (12) | Tag (16) | Ciphertext] using AES-256-GCM.
        /// Throws <see cref="CryptographicException"/> if the payload was tampered with
        /// or was produced with a different key.
        /// </summary>
        public static byte[] Decrypt(ReadOnlySpan<byte> payload, byte[] key)
        {
            ValidateKey(key);
            if (payload.Length < Overhead)
                throw new ArgumentException("Payload is too short to contain a valid ciphertext.", nameof(payload));

            byte[] plaintext = new byte[payload.Length - Overhead];

            using var aesGcm = new AesGcm(key, TagSize);
            aesGcm.Decrypt(
                payload.Slice(0, NonceSize),
                payload.Slice(Overhead),
                payload.Slice(NonceSize, TagSize),
                plaintext);

            return plaintext;
        }

        /// <summary>
        /// Encrypts <paramref name="body"/> prefixed with a one-byte content type, in a single pass.
        /// Callers previously built the tagged plaintext into a throwaway array first, which
        /// duplicated the entire image in memory before encryption.
        /// </summary>
        public static byte[] EncryptTagged(byte contentType, ReadOnlySpan<byte> body, byte[] key)
        {
            ValidateKey(key);

            byte[] payload = new byte[Overhead + 1 + body.Length];
            var nonce = payload.AsSpan(0, NonceSize);
            var tag = payload.AsSpan(NonceSize, TagSize);
            var ciphertext = payload.AsSpan(Overhead, 1 + body.Length);

            RandomNumberGenerator.Fill(nonce);

            // Build the tagged plaintext in a rented-free stack/heap buffer sized once.
            byte[] tagged = new byte[1 + body.Length];
            tagged[0] = contentType;
            body.CopyTo(tagged.AsSpan(1));

            try
            {
                using var aesGcm = new AesGcm(key, TagSize);
                aesGcm.Encrypt(nonce, tagged, ciphertext, tag);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(tagged);
            }

            return payload;
        }

        /// <summary>Decrypts a payload produced by <see cref="EncryptTagged"/>.</summary>
        public static (byte ContentType, byte[] Body) DecryptTagged(ReadOnlySpan<byte> payload, byte[] key)
        {
            byte[] plaintext = Decrypt(payload, key);
            if (plaintext.Length == 0)
                throw new CryptographicException("Decrypted payload was empty.");

            byte contentType = plaintext[0];
            byte[] body = new byte[plaintext.Length - 1];
            Buffer.BlockCopy(plaintext, 1, body, 0, body.Length);
            CryptographicOperations.ZeroMemory(plaintext);

            return (contentType, body);
        }

        private static void ValidateKey(byte[] key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (key.Length != KeySize) throw new ArgumentException($"Key must be {KeySize} bytes.", nameof(key));
        }
    }
}
