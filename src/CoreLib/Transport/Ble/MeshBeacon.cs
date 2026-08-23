using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CoreLib.Transport.Ble
{
    /// <summary>What a beacon says about the device publishing it, beyond which mesh it is in.</summary>
    [Flags]
    public enum MeshBeaconFlags : byte
    {
        None = 0,

        /// <summary>Low four bits. Bumping this is how the layout can ever change.</summary>
        VersionMask = 0x0F,

        /// <summary>A human is at this device inviting something in.</summary>
        PairingOpen = 0x10,

        /// <summary>This device can also scan, so a peer that cannot advertise may wait for it.</summary>
        CanBeCentral = 0x20,
    }

    /// <summary>
    /// Six bytes in the advertisement that say which mesh a device belongs to.
    ///
    /// <para><b>The problem it solves.</b> Every install advertises the same service UUID, so a
    /// scan finds every Mesh Sync device in range and not only the ones in this mesh. "Is this one
    /// of mine" could not be answered until after a connect, an MTU exchange and a hello - by which
    /// point both devices had told each other their device name and mesh name. That is why every
    /// refusal cost seconds of radio and needed three cooldown maps to be survivable, and it is the
    /// open protocol decision in <c>HANDOFF.md</c>.</para>
    ///
    /// <para><b>It is a filter, not a credential.</b> This is the rule to hold on to. Authorisation
    /// stays exactly where it is: the peer registry, the per-connection key agreement, and a human
    /// comparing fingerprints. A forged or replayed beacon buys an attacker one wasted connect
    /// attempt - which is what <em>every</em> stranger costs today - and nothing else. The mesh key
    /// must never enter a session key derivation, and
    /// <c>The_mesh_key_never_reaches_a_session_key</c> asserts it.</para>
    ///
    /// <para><b>Why it rotates.</b> A fixed tag would make every Mesh Sync device trackable across
    /// venues for as long as the mesh existed. The epoch is fifteen minutes, matched to the LE
    /// private-address rotation window, so this adds no linkability the radio does not already
    /// have. Longer would; shorter would cost clock-skew tolerance for nothing.</para>
    ///
    /// <para><b>Why the tag is only four bytes.</b> That is what fits. The legacy advertisement is
    /// 31 bytes: three for flags, eighteen for the 128-bit service UUID - kept so every platform's
    /// existing scan filter goes on working unchanged - and ten for a manufacturer-data section,
    /// two of which are the company id. One in 4.3 billion accidental matches, and a match only
    /// earns a connect attempt that then has to survive the registry. Truncation is safe precisely
    /// because this is not a credential.</para>
    /// </summary>
    public static class MeshBeacon
    {
        /// <summary>
        /// The Bluetooth SIG's reserved identifier, used for internal and test work.
        ///
        /// A constant rather than a literal so it can be swapped for a registered one without a
        /// protocol change. Recorded in <c>SECURITY.md</c> beside the other honest limitations.
        /// </summary>
        public const ushort CompanyId = 0xFFFF;

        public const byte Version = 1;

        /// <summary>Flags, epoch, and four bytes of tag.</summary>
        public const int Length = 6;

        public const int TagLength = 4;

        /// <summary>Fifteen minutes, matching how often an LE private address rotates.</summary>
        public static readonly TimeSpan Epoch = TimeSpan.FromMinutes(15);

        /// <summary>
        /// How many epochs either side of ours a beacon may claim.
        ///
        /// Forty-five minutes of total tolerance. Devices on one LAN are normally within seconds of
        /// each other; one more than fifteen minutes out of true is a real failure and should be
        /// reported as one rather than silently half-working.
        /// </summary>
        public const int EpochTolerance = 1;

        /// <summary>Domain separation, so this HMAC can never collide with another use of the key.</summary>
        private static readonly byte[] Context = Encoding.ASCII.GetBytes("meshsync-beacon-v1");

        /// <summary>The 32-byte discovery key a whole mesh shares.</summary>
        public const int KeyLength = 32;

        public static uint EpochOf(DateTime utcNow) =>
            (uint)(new DateTimeOffset(utcNow, TimeSpan.Zero).ToUnixTimeSeconds() / (long)Epoch.TotalSeconds);

        /// <summary>Builds the six bytes to advertise. Empty when there is no key yet.</summary>
        public static byte[] Build(byte[]? meshKey, DateTime utcNow, MeshBeaconFlags flags = MeshBeaconFlags.None)
        {
            if (meshKey == null || meshKey.Length == 0) return Array.Empty<byte>();

            byte header = (byte)(((byte)flags & ~(byte)MeshBeaconFlags.VersionMask) | Version);
            uint epoch = EpochOf(utcNow);

            var beacon = new byte[Length];
            beacon[0] = header;
            beacon[1] = (byte)(epoch & 0xFF);
            Tag(meshKey, epoch, header).AsSpan(0, TagLength).CopyTo(beacon.AsSpan(2));

            return beacon;
        }

        /// <summary>
        /// True when this beacon was built from the same mesh key, within the epoch tolerance.
        ///
        /// <para>The flags are read from the beacon and mixed into the tag, so a flipped bit fails
        /// verification rather than being believed.</para>
        /// </summary>
        public static bool Verify(byte[]? meshKey, ReadOnlySpan<byte> beacon, DateTime utcNow,
                                  out MeshBeaconFlags flags)
        {
            flags = MeshBeaconFlags.None;

            if (meshKey == null || meshKey.Length == 0) return false;
            if (beacon.Length != Length) return false;

            byte header = beacon[0];
            if ((header & (byte)MeshBeaconFlags.VersionMask) != Version) return false;

            uint now = EpochOf(utcNow);

            for (int offset = -EpochTolerance; offset <= EpochTolerance; offset++)
            {
                uint candidate = unchecked((uint)((long)now + offset));
                if ((byte)(candidate & 0xFF) != beacon[1]) continue;

                var expected = Tag(meshKey, candidate, header);

                // Fixed-time, out of habit rather than necessity: this is a filter, and a timing
                // oracle on it reveals only which mesh a device is in to somebody standing in the
                // room with it.
                if (!CryptographicOperations.FixedTimeEquals(expected.AsSpan(0, TagLength), beacon.Slice(2))) continue;

                flags = (MeshBeaconFlags)(header & ~(byte)MeshBeaconFlags.VersionMask);
                return true;
            }

            return false;
        }

        /// <summary>
        /// The tag a device joining by pairing code can compute before it has a mesh key.
        ///
        /// <para>The inviting device - the one showing the code, the one whose pairing window is
        /// open - advertises this, and the joiner scans for exactly it. That is what lets two
        /// devices pair with no network at all, which is the one step of this project that has
        /// never honoured its own central claim.</para>
        /// </summary>
        public static byte[] PairingSecretFrom(string publicKey)
        {
            if (string.IsNullOrWhiteSpace(publicKey)) return Array.Empty<byte>();

            // Derived from the key already in the pairing payload rather than from a new secret,
            // so nothing has to be added to the QR code for this to work.
            return SHA256.HashData(Encoding.UTF8.GetBytes("meshsync-pairing-v1:" + publicKey.Trim()));
        }

        private static byte[] Tag(byte[] key, uint epoch, byte header)
        {
            Span<byte> input = stackalloc byte[Context.Length + 4 + 1];
            Context.CopyTo(input);
            BinaryPrimitives.WriteUInt32LittleEndian(input.Slice(Context.Length, 4), epoch);
            input[Context.Length + 4] = header;

            return HMACSHA256.HashData(key, input);
        }

        /// <summary>
        /// The complete manufacturer-data section, company id first.
        ///
        /// Ten bytes with the length prefix, which is exactly what is left of the legacy 31 once
        /// flags and the 128-bit service UUID have taken theirs.
        /// </summary>
        public static byte[] ManufacturerData(byte[] beacon)
        {
            if (beacon == null || beacon.Length == 0) return Array.Empty<byte>();

            var section = new byte[2 + beacon.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(section.AsSpan(0, 2), CompanyId);
            beacon.CopyTo(section.AsSpan(2));
            return section;
        }

        /// <summary>Pulls the beacon back out of a manufacturer-data section.</summary>
        public static bool TryReadManufacturerData(ReadOnlySpan<byte> section, out byte[] beacon)
        {
            beacon = Array.Empty<byte>();

            if (section.Length != 2 + Length) return false;
            if (BinaryPrimitives.ReadUInt16LittleEndian(section.Slice(0, 2)) != CompanyId) return false;

            beacon = section.Slice(2).ToArray();
            return true;
        }

        /// <summary>
        /// The whole advertisement, sized so a test can assert it fits.
        ///
        /// <para>3 flags + 18 service UUID + 10 manufacturer data = <b>31 bytes exactly</b>. There
        /// is no room for a local name and there must not be: a machine name in an advertisement is
        /// readable by anyone in the room, which is the leak this exists to close.</para>
        /// </summary>
        public static int AdvertisementBytes(bool withBeacon) => 3 + 18 + (withBeacon ? 2 + 2 + Length : 0);

        /// <summary>The legacy advertising limit. Extended advertising is not universal.</summary>
        public const int MaxAdvertisementBytes = 31;
    }
}
