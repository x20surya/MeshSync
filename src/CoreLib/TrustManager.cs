using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace CoreLib
{
    /// <summary>
    /// Manages Zero-Trust Device Pairing and Whitelisting.
    /// Devices must exchange their Public Keys (via QR Code or PIN) before they are allowed to join the mesh.
    /// </summary>
    public class TrustManager
    {
        private readonly ECDsa _localKey;
        private readonly HashSet<string> _trustedPublicKeys;

        public TrustManager()
        {
            // Generate a unique Elliptic Curve keypair for this device
            _localKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _trustedPublicKeys = new HashSet<string>();
        }

        /// <summary>
        /// Returns this device's Public Key as a Base64 string. 
        /// This is what gets converted into a QR Code or copied as a pairing PIN.
        /// </summary>
        public string GetMyPublicKeyPin()
        {
            byte[] publicKeyBytes = _localKey.ExportSubjectPublicKeyInfo();
            return Convert.ToBase64String(publicKeyBytes);
        }

        /// <summary>
        /// Adds a foreign device's Public Key to our trusted whitelist.
        /// Call this when the user scans a QR code or types a PIN.
        /// </summary>
        public void TrustDevice(string foreignPublicKeyPin)
        {
            _trustedPublicKeys.Add(foreignPublicKeyPin);
            Console.WriteLine($"[TrustManager] Device paired and whitelisted successfully!");
        }

        /// <summary>
        /// Checks if a given public key is in our trusted whitelist.
        /// </summary>
        public bool IsDeviceTrusted(string foreignPublicKeyPin)
        {
            return _trustedPublicKeys.Contains(foreignPublicKeyPin);
        }

        /// <summary>
        /// Signs a payload to prove it came from this exact device.
        /// </summary>
        public byte[] SignPayload(byte[] data)
        {
            return _localKey.SignData(data, HashAlgorithmName.SHA256);
        }

        /// <summary>
        /// Verifies that a payload was actually signed by the claimed trusted device.
        /// </summary>
        public bool VerifyPayloadSignature(byte[] data, byte[] signature, string foreignPublicKeyPin)
        {
            if (!IsDeviceTrusted(foreignPublicKeyPin))
                return false;

            try
            {
                byte[] pubKeyBytes = Convert.FromBase64String(foreignPublicKeyPin);
                using var foreignKey = ECDsa.Create();
                foreignKey.ImportSubjectPublicKeyInfo(pubKeyBytes, out _);

                return foreignKey.VerifyData(data, signature, HashAlgorithmName.SHA256);
            }
            catch
            {
                return false;
            }
        }
    }
}
