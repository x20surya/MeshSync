using System;

namespace CoreLib.Identity
{
    /// <summary>
    /// Wraps the private key before it touches the disk, using whatever the platform offers.
    ///
    /// <para><b>What it replaces.</b> Nothing. <c>device.key</c> was a plain PKCS#8 blob
    /// protected by filesystem permissions alone - and on Windows not even that, because
    /// <c>RestrictToOwner</c> returned early there on the reasoning that the user profile ACL
    /// was enough. It is enough to keep out other users; it is not enough to keep out anything
    /// running <em>as</em> that user, which is every process the person has ever launched.</para>
    ///
    /// <para>The implementations live in the apps rather than here, because CoreLib is built
    /// once for every platform and must not reference DPAPI or the Android Keystore. Leaving it
    /// unset stores the key exactly as before, which is what the tests want and what a platform
    /// with nothing to offer falls back to.</para>
    /// </summary>
    public interface IKeyProtector
    {
        /// <summary>What is doing the protecting, for the log line on first use.</summary>
        string Name { get; }

        /// <summary>Wraps a private key for storage. Throws if the platform refuses.</summary>
        byte[] Protect(byte[] plaintext);

        /// <summary>
        /// Unwraps a stored key, or returns null if it cannot - a key wrapped on another
        /// machine, under another user, or by a keystore entry that has since been cleared.
        /// Null costs a re-pair, which is visible; throwing would stop the app starting at all.
        /// </summary>
        byte[]? TryUnprotect(byte[] stored);
    }
}
