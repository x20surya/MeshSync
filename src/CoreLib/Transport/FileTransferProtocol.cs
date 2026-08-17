using System;
using System.Buffers.Binary;

namespace CoreLib.Transport
{
    /// <summary>What a peer has offered to send.</summary>
    public sealed class FileOffer
    {
        public FileOffer(uint transferId, string name, long size, byte[] sha256)
        {
            TransferId = transferId;
            Name = name;
            Size = size;
            Sha256 = sha256;
        }

        public uint TransferId { get; }

        /// <summary>
        /// The sender's name for it, already stripped of anything path-like.
        ///
        /// Sanitised on the way out <em>and</em> on the way in. A name is the one field in a
        /// transfer that decides where bytes land, and a paired device with a bug should not be
        /// able to write outside the folder any more than a hostile one should.
        /// </summary>
        public string Name { get; }

        public long Size { get; }

        /// <summary>SHA-256 of the whole file, known before the first byte arrives.</summary>
        public byte[] Sha256 { get; }
    }

    /// <summary>
    /// The three frames a file transfer is made of.
    ///
    /// <para>Deliberately separate content types rather than a sub-protocol inside one, so they
    /// ride the same encrypted, authenticated path as everything else and inherit its
    /// properties for free. A file offer is exactly the sort of thing that must not be
    /// accepted from a stranger.</para>
    ///
    /// <para>Wi-Fi only. Over Bluetooth the offer goes out and the sender asks the peer to raise
    /// Wi-Fi with the wake frame that already exists - at roughly 6.7 KB/s a photograph would
    /// take a quarter of an hour, so carrying chunks there would be a promise the tier cannot
    /// keep.</para>
    /// </summary>
    public static class FileTransferProtocol
    {
        /// <summary>
        /// Bytes per chunk. Well under the 32 MB frame ceiling so the header, the crypto
        /// overhead and the length prefix all have room, and small enough that neither end
        /// holds much of the file at once.
        /// </summary>
        public const int ChunkBytes = 1024 * 1024;

        /// <summary>Long enough for any real filename, short enough to bound the frame.</summary>
        public const int MaxNameBytes = 255;

        /// <summary>
        /// Ceiling on a single transfer. Generous for photographs and videos, and a bound rather
        /// than an invitation: a peer claiming a petabyte should be refused at the offer rather
        /// than after it has filled the disk.
        /// </summary>
        public const long MaxFileBytes = 4L * 1024 * 1024 * 1024;

        private const int Sha256Bytes = 32;

        // ──────────────────────────────── offer

        public static byte[] BuildOffer(uint transferId, string name, long size, byte[] sha256)
        {
            if (sha256 == null || sha256.Length != Sha256Bytes)
                throw new ArgumentException($"A {Sha256Bytes} byte hash is required.", nameof(sha256));

            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(SafeName(name));
            if (nameBytes.Length > MaxNameBytes) nameBytes = nameBytes.AsSpan(0, MaxNameBytes).ToArray();

            var body = new byte[4 + 2 + nameBytes.Length + 8 + Sha256Bytes];

            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), transferId);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4, 2), (ushort)nameBytes.Length);
            nameBytes.CopyTo(body, 6);
            BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(6 + nameBytes.Length, 8), size);
            sha256.CopyTo(body, 6 + nameBytes.Length + 8);

            return body;
        }

        public static bool TryParseOffer(byte[] body, out FileOffer? offer)
        {
            offer = null;
            if (body == null || body.Length < 6) return false;

            uint transferId = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0, 4));
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4, 2));

            if (nameLength > MaxNameBytes) return false;
            if (body.Length < 6 + nameLength + 8 + Sha256Bytes) return false;

            string name;
            try { name = System.Text.Encoding.UTF8.GetString(body, 6, nameLength); }
            catch { return false; }

            long size = BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(6 + nameLength, 8));
            if (size < 0 || size > MaxFileBytes) return false;

            var hash = new byte[Sha256Bytes];
            Buffer.BlockCopy(body, 6 + nameLength + 8, hash, 0, Sha256Bytes);

            // Sanitised again on arrival. The sender already did it, which protects against a
            // careless name; doing it here protects against a sender that did not.
            offer = new FileOffer(transferId, SafeName(name), size, hash);
            return true;
        }

        // ──────────────────────────────── acknowledgement

        public static byte[] BuildAck(uint transferId, bool accepted)
        {
            var body = new byte[5];
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), transferId);
            body[4] = accepted ? (byte)1 : (byte)0;
            return body;
        }

        public static bool TryParseAck(byte[] body, out uint transferId, out bool accepted)
        {
            transferId = 0;
            accepted = false;
            if (body == null || body.Length < 5) return false;

            transferId = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0, 4));
            accepted = body[4] != 0;
            return true;
        }

        // ──────────────────────────────── chunk

        public static byte[] BuildChunk(uint transferId, long offset, ReadOnlySpan<byte> data)
        {
            var body = new byte[12 + data.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), transferId);
            BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(4, 8), offset);
            data.CopyTo(body.AsSpan(12));
            return body;
        }

        public static bool TryParseChunk(byte[] body, out uint transferId, out long offset, out ArraySegment<byte> data)
        {
            transferId = 0;
            offset = 0;
            data = default;

            if (body == null || body.Length < 12) return false;

            transferId = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0, 4));
            offset = BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(4, 8));

            if (offset < 0) return false;

            data = new ArraySegment<byte>(body, 12, body.Length - 12);
            return true;
        }

        // ──────────────────────────────── names

        /// <summary>
        /// Reduces a name to something that can only ever be a file inside the folder it is
        /// meant for.
        ///
        /// <para>Strips any directory part, refuses the specials, and replaces every character
        /// that means something to a path. A transfer's name is the one field that decides where
        /// bytes land, so it is treated the way an address is: parsed rather than trusted, even
        /// though it arrives inside an authenticated payload from a paired device.</para>
        /// </summary>
        public static string SafeName(string? name)
        {
            string candidate = (name ?? "").Trim();
            if (candidate.Length == 0) return "received-file";

            // Both separators, whatever the platform, so a Windows name cannot escape on Linux
            // or the reverse.
            int cut = candidate.LastIndexOfAny(new[] { '/', '\\' });
            if (cut >= 0) candidate = candidate.Substring(cut + 1);

            var cleaned = new System.Text.StringBuilder(candidate.Length);
            foreach (char c in candidate)
            {
                cleaned.Append(c switch
                {
                    ':' or '*' or '?' or '"' or '<' or '>' or '|' or '\0' => '_',
                    _ when char.IsControl(c) => '_',
                    _ => c
                });
            }

            string result = cleaned.ToString().Trim().TrimEnd('.', ' ');

            // "." and ".." survive everything above and are not filenames.
            if (result.Length == 0 || result == "." || result == "..") return "received-file";

            return result.Length > MaxNameBytes ? result.Substring(0, MaxNameBytes) : result;
        }
    }
}
