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
        /// A GATT attribute value is capped at 512 octets by the Bluetooth spec, whatever the
        /// negotiated MTU says. Windows reports MaxNotificationSize as MTU minus the 3-byte
        /// ATT header, which comes out at 514 on a 517 MTU and is two bytes optimistic -
        /// anything over 512 is dropped with no error on either side. Measured exactly: a
        /// 512-byte chunk arrives, a 513-byte chunk never does.
        /// </summary>
        public const int MaxAttributeValueBytes = 512;

        /// <summary>
        /// BLE is the small-payload tier. Images go over Wi-Fi, because at roughly 1 to 2 KB
        /// per second of usable throughput a screenshot would take minutes.
        /// </summary>
        public const int MaxPayloadBytes = 64 * 1024;

        /// <summary>
        /// Marks a receipt written back by the phone: [Ack][messageId][seq lo][seq hi].
        ///
        /// The server needs to know a chunk landed before sending the next, because Windows
        /// keeps only one outstanding notification per characteristic and a second one
        /// overwrites the first in flight - a 128-chunk message arrived as its last chunk
        /// alone. Indications are acknowledged at the ATT layer and would solve this in
        /// principle, but on this stack they went unconfirmed and Windows tore the link down
        /// with GATT status 19. Acknowledging in our own protocol works on both platforms
        /// and is not at the mercy of either stack's quirks.
        ///
        /// Four bytes, and a data chunk is never shorter than the five byte header, so the
        /// two can never be confused.
        /// </summary>
        public const byte AckMarker = 0xAC;

        public const int AckLength = 4;

        public static byte[] BuildAck(byte messageId, int sequence) => new[]
        {
            AckMarker,
            messageId,
            (byte)(sequence & 0xFF),
            (byte)((sequence >> 8) & 0xFF)
        };

        public static bool TryParseAck(ReadOnlySpan<byte> data, out byte messageId, out int sequence)
        {
            if (data.Length == AckLength && data[0] == AckMarker)
            {
                messageId = data[1];
                sequence = data[2] | (data[3] << 8);
                return true;
            }

            messageId = 0;
            sequence = 0;
            return false;
        }

        /// <summary>How long the server waits for a chunk receipt before giving up.</summary>
        public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Usable bytes in a single write or notification: the MTU less the ATT header, then
        /// clamped to the spec's 512-octet attribute ceiling.
        /// </summary>
        public static int UsablePayload(int negotiatedMtu) =>
            Math.Clamp(negotiatedMtu - 3, BleFragmenter.MinimumMtuPayload, MaxAttributeValueBytes);
    }
}
