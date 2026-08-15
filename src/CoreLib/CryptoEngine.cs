using System;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace CoreLib
{
    public class CryptoEngine
    {
        private const int KeySize = 32; // 256 bits for AES-256
        private const int NonceSize = 12; // 96 bits for GCM
        private const int TagSize = 16; // 128 bits for GCM

        /// <summary>
        /// Derives a 256-bit encryption key from a master password using Argon2id.
        /// </summary>
        public static byte[] DeriveKey(string masterPassword, byte[] salt)
        {
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
        public static byte[] Encrypt(byte[] plaintext, byte[] key)
        {
            if (key.Length != KeySize) throw new ArgumentException($"Key must be {KeySize} bytes.");

            // Generate a random 12-byte Nonce (IV)
            byte[] nonce = new byte[NonceSize];
            RandomNumberGenerator.Fill(nonce);

            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[TagSize];

            using (var aesGcm = new AesGcm(key))
            {
                // Encrypt payload
                aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);
            }

            // Pack the payload: Nonce + Tag + Ciphertext
            byte[] payload = new byte[NonceSize + TagSize + ciphertext.Length];
            Buffer.BlockCopy(nonce, 0, payload, 0, NonceSize);
            Buffer.BlockCopy(tag, 0, payload, NonceSize, TagSize);
            Buffer.BlockCopy(ciphertext, 0, payload, NonceSize + TagSize, ciphertext.Length);

            return payload;
        }

        /// <summary>
        /// Decrypts a payload formatted as [Nonce (12) | Tag (16) | Ciphertext] using AES-256-GCM.
        /// </summary>
        public static byte[] Decrypt(byte[] payload, byte[] key)
        {
            if (key.Length != KeySize) throw new ArgumentException($"Key must be {KeySize} bytes.");
            if (payload.Length < NonceSize + TagSize) throw new ArgumentException("Payload is too short to contain a valid ciphertext.");

            // Extract the Nonce, Tag, and Ciphertext from the payload
            byte[] nonce = new byte[NonceSize];
            byte[] tag = new byte[TagSize];
            byte[] ciphertext = new byte[payload.Length - NonceSize - TagSize];

            Buffer.BlockCopy(payload, 0, nonce, 0, NonceSize);
            Buffer.BlockCopy(payload, NonceSize, tag, 0, TagSize);
            Buffer.BlockCopy(payload, NonceSize + TagSize, ciphertext, 0, ciphertext.Length);

            byte[] plaintext = new byte[ciphertext.Length];

            using (var aesGcm = new AesGcm(key))
            {
                // Decrypt payload. If the tag is invalid, this throws a CryptographicException.
                aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
            }

            return plaintext;
        }
    }
}
