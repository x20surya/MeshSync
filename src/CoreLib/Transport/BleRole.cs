using System;

namespace CoreLib.Transport
{
    /// <summary>Which half of a GATT link a device can take.</summary>
    [Flags]
    public enum BleCapability
    {
        None = 0,

        /// <summary>Can scan and connect out. Every device with Bluetooth LE can do this.</summary>
        Central = 1,

        /// <summary>
        /// Can advertise and serve a GATT service.
        /// Not universal on Android: it is a hardware capability, and
        /// <c>getBluetoothLeAdvertiser()</c> returns null on devices that lack it.
        /// </summary>
        Peripheral = 2,

        Both = Central | Peripheral
    }

    /// <summary>The role this device takes on a link with one particular peer.</summary>
    public enum BleRole
    {
        /// <summary>No link is possible: nobody present can advertise, or this device has no radio.</summary>
        None,

        /// <summary>This device advertises and serves. The peer connects to it.</summary>
        Peripheral,

        /// <summary>This device scans and connects out.</summary>
        Central
    }

    /// <summary>
    /// Decides which device advertises and which connects.
    ///
    /// <para>GATT roles are not symmetric the way a TCP socket's are: one side advertises a
    /// service and the other scans for it, and they are different pieces of code. The computer
    /// was the peripheral and the phone the central, fixed at compile time, which is why
    /// phone-to-phone over Bluetooth was impossible.</para>
    ///
    /// <para><b>Capability first, fingerprint second.</b> The obvious rule - lower fingerprint
    /// advertises - is wrong on Android, where advertising is a hardware capability rather than
    /// a given. A phone that cannot advertise must always be the central whatever its
    /// fingerprint says, or the pair would agree on an arrangement neither can carry out. Only
    /// when both devices can do either does the fingerprint decide, and then it decides
    /// identically on both sides with no negotiation round trip.</para>
    /// </summary>
    public static class BleRoleRules
    {
        /// <summary>
        /// The role this device should take, given what each end is capable of.
        ///
        /// Both devices call this with the arguments swapped and arrive at complementary
        /// answers, which is the property that makes a round trip unnecessary.
        /// </summary>
        public static BleRole DecideFor(string localFingerprint, BleCapability local,
                                        string peerFingerprint, BleCapability peer)
        {
            if (local == BleCapability.None || peer == BleCapability.None) return BleRole.None;

            bool localCanAdvertise = local.HasFlag(BleCapability.Peripheral);
            bool peerCanAdvertise = peer.HasFlag(BleCapability.Peripheral);
            bool localCanScan = local.HasFlag(BleCapability.Central);
            bool peerCanScan = peer.HasFlag(BleCapability.Central);

            // Someone has to advertise and someone has to scan. Two devices that can only
            // advertise will never find each other, and two that can only scan have nothing
            // to find.
            bool weAdvertiseTheyScan = localCanAdvertise && peerCanScan;
            bool theyAdvertiseWeScan = peerCanAdvertise && localCanScan;

            if (!weAdvertiseTheyScan && !theyAdvertiseWeScan) return BleRole.None;

            // Only one arrangement is possible, so capability decides and the fingerprint does
            // not get a say. This is the case the obvious rule gets wrong.
            if (weAdvertiseTheyScan && !theyAdvertiseWeScan) return BleRole.Peripheral;
            if (theyAdvertiseWeScan && !weAdvertiseTheyScan) return BleRole.Central;

            // Both arrangements work, so it comes down to a value both sides already hold.
            // Lower fingerprint advertises - arbitrary, but identical on both ends.
            return string.CompareOrdinal(localFingerprint, peerFingerprint) < 0
                ? BleRole.Peripheral
                : BleRole.Central;
        }

        /// <summary>The role the peer takes, given ours. Only for logging and assertions.</summary>
        public static BleRole Opposite(BleRole role) => role switch
        {
            BleRole.Peripheral => BleRole.Central,
            BleRole.Central => BleRole.Peripheral,
            _ => BleRole.None
        };
    }
}
