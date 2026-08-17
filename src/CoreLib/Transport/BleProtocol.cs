using System;
using System.Linq;

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

        // ──────────────────────────── liveness

        /// <summary>
        /// Marks a two-byte control frame: [Control][kind]. Shorter than both a receipt (4)
        /// and the smallest data chunk (5), so the three can never be confused.
        ///
        /// A GATT link outlives the process that published the service. Restart the desktop
        /// app and the phone's connection survives at the OS level: its writes still arrive,
        /// so the computer receives clipboard items, but it subscribed to the previous
        /// service instance and that subscription is never re-announced. The computer is
        /// then deaf in one direction - it shows no device and cannot notify - while the
        /// phone still believes it is connected. Pinging proves the link end to end rather
        /// than trusting a subscription event that only ever fires once.
        /// </summary>
        public const byte ControlMarker = 0xC7;

        public const int ControlLength = 2;
        public const byte ControlPing = 0x01;
        public const byte ControlPong = 0x02;

        /// <summary>
        /// "Raise Wi-Fi, I have something this link cannot carry."
        ///
        /// Both tiers make the phone the client - it scans for the service and it opens the
        /// socket - so the computer has no way to dial the phone when it copies an image and
        /// only Bluetooth is up. That item used to be logged and dropped. With Bluetooth as
        /// the standing link that is every image copied on the computer, so the request rides
        /// the link already open and the phone brings the socket up in response.
        ///
        /// Two bytes like the others, so the length discrimination against a receipt (4) and
        /// the smallest data chunk (5) is unaffected.
        /// </summary>
        public const byte ControlWakeWiFi = 0x03;

        public static byte[] BuildControl(byte kind) => new[] { ControlMarker, kind };

        // ──────────────────────────── extended control

        /// <summary>
        /// Marks a control frame too long to fit in two bytes: <c>[0x00][kind][payload...]</c>.
        ///
        /// <para>The three original frame types are told apart by length alone, which works
        /// only while every one of them has a fixed size. An identity exchange does not - a
        /// public key is around 120 bytes, which lands squarely in the range reserved for data
        /// chunks.</para>
        ///
        /// <para>So this borrows the one value a data chunk can never carry. A chunk's first
        /// byte is its message id, and both sides now skip zero when allocating one, which
        /// makes a leading zero unambiguous however long the frame is. It has to be checked
        /// before the receipt and the reassembler, not after.</para>
        /// </summary>
        public const byte ExtendedMarker = 0x00;

        /// <summary>Sender's base64 public key, so the peer knows which key to use and whether to allow it.</summary>
        public const byte ExtendedHello = 0x01;

        /// <summary>
        /// Cap on the friendly name in a hello. Keeps the whole frame inside one chunk, and
        /// stops a hostile or simply strange device name filling the link.
        /// </summary>
        public const int MaxDeviceNameBytes = 64;

        public const int ExtendedHeaderSize = 2;

        public static byte[] BuildExtended(byte kind, ReadOnlySpan<byte> payload)
        {
            var frame = new byte[ExtendedHeaderSize + payload.Length];
            frame[0] = ExtendedMarker;
            frame[1] = kind;
            payload.CopyTo(frame.AsSpan(ExtendedHeaderSize));
            return frame;
        }

        public static bool TryParseExtended(ReadOnlySpan<byte> data, out byte kind, out byte[] payload)
        {
            if (data.Length >= ExtendedHeaderSize && data[0] == ExtendedMarker)
            {
                kind = data[1];
                payload = data.Slice(ExtendedHeaderSize).ToArray();
                return true;
            }

            kind = 0;
            payload = Array.Empty<byte>();
            return false;
        }

        /// <summary>
        /// Builds a hello payload: the sender's identity key, its friendly name, the mesh name
        /// and this link's ephemeral key, in that order.
        ///
        /// <para>Separated by newlines, which a base64 key can never contain, so the fields can
        /// be read positionally and a shorter payload still parses.</para>
        ///
        /// <para>The name matters more here than it looks. Wi-Fi carries it in its own hello, so
        /// a device paired over Wi-Fi has a name to show. Bluetooth carried identity but no
        /// name, which left a Bluetooth-only pair with nothing to call each other and a
        /// notification reading "your devices" for ever.</para>
        ///
        /// <para>The ephemeral key is what gives this tier forward secrecy, and it roughly
        /// doubles the size of the frame. A hello is written in one go rather than through
        /// <see cref="BleFragmenter"/>, because an extended control frame is marked by a leading
        /// zero and a fragmented chunk starts with its message id instead - so the two shapes
        /// cannot be mixed. At a negotiated MTU there is ample room; the senders check and log
        /// rather than letting an oversized hello vanish silently.
        /// </para>
        /// </summary>
        public static byte[] BuildHelloPayload(string publicKey, string? deviceName,
                                               string? meshName = null, string? ephemeralKey = null)
        {
            string name = Clean(deviceName);
            string mesh = Clean(meshName);
            string ephemeral = (ephemeralKey ?? "").Replace('\n', ' ').Trim();

            if (ephemeral.Length > 0) return System.Text.Encoding.UTF8.GetBytes($"{publicKey}\n{name}\n{mesh}\n{ephemeral}");
            if (name.Length == 0 && mesh.Length == 0) return System.Text.Encoding.UTF8.GetBytes(publicKey);
            if (mesh.Length == 0) return System.Text.Encoding.UTF8.GetBytes($"{publicKey}\n{name}");

            return System.Text.Encoding.UTF8.GetBytes($"{publicKey}\n{name}\n{mesh}");
        }

        /// <summary>
        /// Trims a name to something that fits and cannot break the line separator.
        ///
        /// Cut on a character boundary rather than a byte one, so a multi-byte name is never
        /// halved and delivered as mojibake.
        /// </summary>
        private static string Clean(string? value)
        {
            string name = (value ?? "").Replace('\n', ' ').Trim();
            if (name.Length == 0) return "";

            if (System.Text.Encoding.UTF8.GetByteCount(name) > MaxDeviceNameBytes)
            {
                name = new string(name.Take(MaxDeviceNameBytes / 4).ToArray()).Trim();
            }

            return name;
        }

        /// <summary>
        /// Splits a hello payload into its identity key, device name, mesh name and ephemeral
        /// key.
        ///
        /// Everything after the identity key is optional and read positionally, so a shorter
        /// payload still parses. A base64 key can contain no newline, which is what makes the
        /// separator unambiguous. An empty ephemeral key means no session can be agreed, which
        /// the caller treats as a refusal rather than as a peer to fall back for.
        /// </summary>
        public static bool TryParseHelloPayload(byte[] payload, out string publicKey,
                                                out string deviceName, out string meshName,
                                                out string ephemeralKey)
        {
            publicKey = "";
            deviceName = "";
            meshName = "";
            ephemeralKey = "";

            if (payload == null || payload.Length == 0) return false;

            string text;
            try { text = System.Text.Encoding.UTF8.GetString(payload); }
            catch { return false; }

            var parts = text.Split('\n');

            publicKey = parts[0].Trim();
            if (parts.Length > 1) deviceName = parts[1].Trim();
            if (parts.Length > 2) meshName = parts[2].Trim();
            if (parts.Length > 3) ephemeralKey = parts[3].Trim();

            return publicKey.Length > 0;
        }

        /// <summary>
        /// The next message id, never zero.
        ///
        /// Zero is reserved for extended control frames. The counter used to wrap straight
        /// through it after 255 messages, which would have made one clipboard item in every 256
        /// parse as an identity exchange.
        /// </summary>
        public static byte NextMessageId(ref byte counter)
        {
            do { counter = unchecked((byte)(counter + 1)); } while (counter == ExtendedMarker);
            return counter;
        }

        public static bool TryParseControl(ReadOnlySpan<byte> data, out byte kind)
        {
            if (data.Length == ControlLength && data[0] == ControlMarker)
            {
                kind = data[1];
                return true;
            }

            kind = 0;
            return false;
        }

        /// <summary>How often the phone proves the link is still whole.</summary>
        public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(8);

        /// <summary>Silence beyond this means the peer is gone, whatever the radio thinks.</summary>
        public static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(24);

        /// <summary>
        /// Usable bytes in a single write or notification: the MTU less the ATT header, then
        /// clamped to the spec's 512-octet attribute ceiling.
        /// </summary>
        public static int UsablePayload(int negotiatedMtu) =>
            Math.Clamp(negotiatedMtu - 3, BleFragmenter.MinimumMtuPayload, MaxAttributeValueBytes);
    }
}
