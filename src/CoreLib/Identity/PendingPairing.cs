using System;

namespace CoreLib.Identity
{
    /// <summary>
    /// A device that has knocked while the pairing window was open, and is waiting for a human
    /// to say it is the right one.
    ///
    /// <para><b>Why the wait exists.</b> Pairing carries one key in one direction: the QR shows
    /// this device's public key and the other device scans it. That lets the scanner
    /// authenticate us and gives us nothing to authenticate the scanner with. The open window
    /// used to be the whole answer - if the code was on screen, the first stranger to connect
    /// was trusted - and <c>HANDOFF.md</c> was honest that this loses to anyone already on the
    /// network who wins the race to connect.</para>
    ///
    /// <para>Comparing a fingerprint closes that race, because winning it no longer helps: the
    /// attacker's fingerprint is not the one on the other device's screen. It is the same
    /// numeric-comparison step Bluetooth pairing and Signal both use, and it costs the user one
    /// glance.</para>
    /// </summary>
    public sealed class PendingPairing
    {
        public PendingPairing(string publicKey, string? name, string? address)
        {
            PublicKey = publicKey;
            Fingerprint = DeviceIdentity.FingerprintOf(publicKey);
            Name = name;
            Address = address;
            SeenUtc = DateTimeOffset.UtcNow;
        }

        public string PublicKey { get; }

        public string Fingerprint { get; }

        /// <summary>The four groups a human actually compares against the other device's screen.</summary>
        public string ShortFingerprint => DeviceIdentity.Shorten(Fingerprint);

        /// <summary>
        /// What the device called itself. Display only, and deliberately not trusted: a
        /// stranger picks its own name, so the fingerprint is the part that decides.
        /// </summary>
        public string? Name { get; }

        public string? Address { get; }

        public DateTimeOffset SeenUtc { get; }
    }
}
