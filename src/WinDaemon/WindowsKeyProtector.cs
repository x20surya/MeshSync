using System;
using System.Security.Cryptography;
using CoreLib.Diagnostics;
using CoreLib.Identity;

namespace WinDaemon
{
    /// <summary>
    /// Wraps the device key with DPAPI, bound to the signed-in user.
    ///
    /// <para><b>What it fixes.</b> <c>RestrictToOwner</c> deliberately did nothing on Windows,
    /// on the reasoning that the user profile ACL already keeps other users out. That is true
    /// and beside the point: it keeps out other <em>users</em>, not other <em>processes running
    /// as this user</em>, which is every program the person has ever installed. A plain PKCS#8
    /// key in a predictable path under LOCALAPPDATA is readable by any of them.</para>
    ///
    /// <para>DPAPI at <see cref="DataProtectionScope.CurrentUser"/> means the wrapped blob is
    /// only openable while signed in as that user on that machine. Copying <c>device.key</c> to
    /// another machine, or reading it from another account, now yields nothing usable.</para>
    ///
    /// <para><b>What it is not.</b> It is not hardware-backed, and it does not defend against
    /// code already running as this user at the moment it asks - that code can simply call
    /// Unprotect too. Raising that bar needs a TPM-bound key or a user prompt, and neither is
    /// worth it while the thing being protected is a clipboard. It would be worth it for a
    /// vault.</para>
    /// </summary>
    public sealed class WindowsKeyProtector : IKeyProtector
    {
        /// <summary>
        /// Mixed into the wrap so a blob taken from this app cannot be handed to some other
        /// DPAPI consumer running as the same user, or the reverse.
        /// </summary>
        private static readonly byte[] Entropy = "MeshSync/device.key/v1"u8.ToArray();

        public string Name => "Windows DPAPI";

        public byte[] Protect(byte[] plaintext) =>
            ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

        public byte[]? TryUnprotect(byte[] stored)
        {
            try
            {
                return ProtectedData.Unprotect(stored, Entropy, DataProtectionScope.CurrentUser);
            }
            catch (Exception ex)
            {
                // Expected when the file came from another machine or another account, which is
                // exactly the case this protects against - so it is a fact, not a fault.
                Log.Write("Identity", "DPAPI could not unwrap the stored identity.", ex);
                return null;
            }
        }
    }
}
