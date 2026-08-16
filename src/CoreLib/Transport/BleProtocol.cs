using System;

namespace CoreLib.Transport
{
    /// <summary>
    /// Constants shared by both BLE transports, so the two platforms cannot drift apart on
    /// UUIDs or sizing.
    ///
    /// Roles mirror the TCP transport to keep one mental model: the computer is the GATT
    /// server (peripheral) and the phone is the GATT client (central). Two characteristics
    /// give a full duplex link, because a single one cannot carry traffic in both
    /// directions - a GATT client writes, and a GATT server notifies.
    /// </summary>
    public static class BleProtocol
    {
        /// <summary>The Mesh Sync service. Advertised by the computer, scanned for by the phone.</summary>
        public static readonly Guid ServiceUuid = Guid.Parse("7f3e2a10-9c41-4b8e-a2d7-5e1f0b6c8d90");

        /// <summary>Phone to computer. The client writes chunks here.</summary>
        public static readonly Guid InboxCharacteristicUuid = Guid.Parse("7f3e2a11-9c41-4b8e-a2d7-5e1f0b6c8d90");

        /// <summary>Computer to phone. The server notifies chunks on this.</summary>
        public static readonly Guid OutboxCharacteristicUuid = Guid.Parse("7f3e2a12-9c41-4b8e-a2d7-5e1f0b6c8d90");

        /// <summary>MTU to ask for. 517 is the ceiling; 512 usable after the 3-byte ATT header.</summary>
        public const int PreferredMtu = 517;

        /// <summary>
        /// BLE is the small-payload tier. Images go over Wi-Fi, because at roughly 1 to 2 KB
        /// per second of usable throughput a screenshot would take minutes.
        /// </summary>
        public const int MaxPayloadBytes = 64 * 1024;

        /// <summary>
        /// Chunks are written back to back with no application-level flow control, so a
        /// small gap keeps a burst from overrunning the peer's receive queue.
        /// </summary>
        public const int InterChunkDelayMs = 12;

        /// <summary>Usable bytes in a write once the 3-byte ATT header is deducted.</summary>
        public static int UsablePayload(int negotiatedMtu) =>
            Math.Max(BleFragmenter.MinimumMtuPayload, negotiatedMtu - 3);
    }
}
