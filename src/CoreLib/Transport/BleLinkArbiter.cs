using System.Collections.Generic;
using System.Linq;

namespace CoreLib.Transport
{
    /// <summary>
    /// Applies <see cref="BleRoleRules"/> to the two questions a daemon actually asks.
    ///
    /// <para><b>Why this exists.</b> <c>BleRoleRules</c> answers "which role should this device
    /// take with that peer". Every platform then has to turn that into "should I be scanning at
    /// all" and "I have ended up with two links to one peer, which one dies" - and each one
    /// answered those separately. Windows prevented the second case upfront, Android repaired it
    /// after the fact, and the Linux head did neither, so two devices in range each dialled the
    /// other and both links stayed up. The decisions belong together, in one place, so that a
    /// platform is wiring rather than reimplementing.</para>
    ///
    /// <para>Both ends compute these from values they have already exchanged, so they converge
    /// with no round trip and neither device has to be in charge.</para>
    /// </summary>
    public static class BleLinkArbiter
    {
        /// <summary>
        /// True when this device should be the one connecting out, for at least one paired peer.
        ///
        /// <para>The peer's capability is assumed to be both roles, which is the optimistic
        /// reading: a peer that can only ever be a central will simply never be found by this
        /// scan and will find us instead, because the service stays advertised either way.
        /// Getting it wrong therefore costs nothing, whereas not scanning at all would leave two
        /// laptops waiting for each other.</para>
        /// </summary>
        public static bool ShouldDialAnyPeer(string localFingerprint, BleCapability local,
                                             IEnumerable<string> peerFingerprints,
                                             bool pairingOpen = false)
        {
            if (peerFingerprints == null) return false;

            bool anyPeers = false;

            foreach (string peer in peerFingerprints)
            {
                anyPeers = true;
                if (ShouldDialPeer(localFingerprint, local, peer)) return true;
            }

            // Nothing paired, and a human is standing there inviting something in: scan.
            //
            // There is no peer to arbitrate a role with, so the rule above has nothing to decide
            // and answers no. On an adapter that cannot advertise, that leaves the device neither
            // scanning nor advertising - the exact deadlock this class exists to prevent, reached
            // from the other direction. Observed on a laptop whose only peer had just been
            // forgotten: the phone still trusted it, knocked, and was never heard.
            //
            // Gated on the pairing window rather than merely on having no peers, because a device
            // that is not being paired has no reason to hold the radio open for ever. The window
            // is three minutes and closes itself.
            return !anyPeers && pairingOpen;
        }

        /// <summary>True when this device takes the central half of a link with that peer.</summary>
        public static bool ShouldDialPeer(string localFingerprint, BleCapability local,
                                          string peerFingerprint,
                                          BleCapability peer = BleCapability.Both) =>
            BleRoleRules.DecideFor(localFingerprint, local, peerFingerprint, peer) == BleRole.Central;

        /// <summary>
        /// Which of two live links to one peer survives, when both ends have opened one.
        ///
        /// <para>Returns the role this device keeps: <see cref="BleRole.Central"/> to keep the
        /// link this device opened and drop the one the peer opened, <see cref="BleRole.Peripheral"/>
        /// for the reverse. The peer computes the complement from the same two fingerprints, so
        /// exactly one link is dropped rather than both or neither.</para>
        /// </summary>
        public static BleRole KeepFor(string localFingerprint, BleCapability local,
                                      string peerFingerprint,
                                      BleCapability peer = BleCapability.Both) =>
            BleRoleRules.DecideFor(localFingerprint, local, peerFingerprint, peer);
    }
}
