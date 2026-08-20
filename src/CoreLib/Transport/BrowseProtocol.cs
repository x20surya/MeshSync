using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace CoreLib.Transport
{
    /// <summary>One row in a listing: a name, whether it opens, how big it is, and when it changed.</summary>
    public sealed class BrowseEntry
    {
        public BrowseEntry(string name, bool isDirectory, long sizeBytes, DateTime modifiedUtc, string id = "")
        {
            Name = name;
            IsDirectory = isDirectory;
            SizeBytes = sizeBytes;
            ModifiedUtc = modifiedUtc;
            Id = id;
        }

        public string Name { get; }

        /// <summary>
        /// Set only on the entries of a root listing, where each row is a shared folder rather
        /// than something inside one.
        ///
        /// <para>A folder is addressed by id and everything under it by a path relative to that
        /// id, so the first listing a device asks for has to hand back the ids - otherwise there
        /// is no way to descend into anything. Empty for every ordinary row.</para>
        /// </summary>
        public string Id { get; }
        public bool IsDirectory { get; }
        public long SizeBytes { get; }
        public DateTime ModifiedUtc { get; }

        /// <summary>Sizes as a person reads them. Directories have nothing worth saying.</summary>
        public string SizeLabel => IsDirectory
            ? ""
            : SizeBytes switch
            {
                < 1024 => $"{SizeBytes} B",
                < 1024 * 1024 => $"{SizeBytes / 1024.0:0.#} KB",
                < 1024L * 1024 * 1024 => $"{SizeBytes / (1024.0 * 1024):0.#} MB",
                _ => $"{SizeBytes / (1024.0 * 1024 * 1024):0.#} GB"
            };
    }

    /// <summary>Why a listing came back with nothing in it.</summary>
    public enum BrowseStatus : byte
    {
        Ok = 0,
        NoSuchFolder = 1,
        NotAllowed = 2,
        NotFound = 3
    }

    /// <summary>
    /// Framing for browsing another device's shared folders.
    ///
    /// <para>Shaped like <see cref="NotificationProtocol"/>: length-prefixed strings, every field
    /// capped, nothing that requires the reader to trust the writer about how much to read. A
    /// listing is written by whatever is on the other device's disk, so its size is not this
    /// project's to assume.</para>
    ///
    /// <para><b>Requests carry an id, never a path.</b> The reason is in
    /// <see cref="SharedFolders"/>, and it is the whole security story of this feature.</para>
    ///
    /// <para>A reply is capped at a few hundred entries. A folder with more in it than that
    /// exists, and sending all of it over Bluetooth at 6.7 KB/s to populate a list nobody will
    /// scroll to the end of is not a service to anyone; the cap is reported so the other end can
    /// say the listing was shortened rather than quietly showing part of a folder.</para>
    /// </summary>
    public static class BrowseProtocol
    {
        public const int MaxEntries = 500;

        private const int MaxIdBytes = 64;
        private const int MaxPathBytes = 1024;
        private const int MaxNameBytes = 512;

        // ------------------------------------------------------------------ request

        /// <summary>[idLen u16][id][pathLen u16][relative path]</summary>
        public static byte[] BuildRequest(string folderId, string relativePath)
        {
            byte[] id = Clamp(folderId, MaxIdBytes);
            byte[] path = Clamp(relativePath, MaxPathBytes);

            var buffer = new byte[2 + id.Length + 2 + path.Length];
            int at = 0;

            at = WriteBlock(buffer, at, id);
            WriteBlock(buffer, at, path);

            return buffer;
        }

        public static bool TryParseRequest(byte[] body, out string folderId, out string relativePath)
        {
            folderId = "";
            relativePath = "";

            int at = 0;
            if (!ReadBlock(body, ref at, MaxIdBytes, out folderId)) return false;
            if (!ReadBlock(body, ref at, MaxPathBytes, out relativePath)) return false;

            return true;
        }

        // ------------------------------------------------------------------ reply

        /// <summary>
        /// [status u8][idLen u16][id][pathLen u16][path][truncated u8][count u16]
        /// then per entry: [nameLen u16][name][idLen u16][id][flags u8][size i64][modified i64]
        /// </summary>
        public static byte[] BuildReply(string folderId, string relativePath, BrowseStatus status,
                                        IReadOnlyList<BrowseEntry> entries)
        {
            byte[] id = Clamp(folderId, MaxIdBytes);
            byte[] path = Clamp(relativePath, MaxPathBytes);

            bool truncated = entries.Count > MaxEntries;
            int count = truncated ? MaxEntries : entries.Count;

            var names = new byte[count][];
            int total = 1 + 2 + id.Length + 2 + path.Length + 1 + 2;

            var ids = new byte[count][];

            for (int i = 0; i < count; i++)
            {
                names[i] = Clamp(entries[i].Name, MaxNameBytes);
                ids[i] = Clamp(entries[i].Id, MaxIdBytes);
                total += 2 + names[i].Length + 2 + ids[i].Length + 1 + 8 + 8;
            }

            var buffer = new byte[total];
            int at = 0;

            buffer[at++] = (byte)status;
            at = WriteBlock(buffer, at, id);
            at = WriteBlock(buffer, at, path);
            buffer[at++] = truncated ? (byte)1 : (byte)0;

            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(at), (ushort)count);
            at += 2;

            for (int i = 0; i < count; i++)
            {
                at = WriteBlock(buffer, at, names[i]);
                at = WriteBlock(buffer, at, ids[i]);
                buffer[at++] = entries[i].IsDirectory ? (byte)1 : (byte)0;

                BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(at), entries[i].SizeBytes);
                at += 8;

                BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(at),
                    new DateTimeOffset(DateTime.SpecifyKind(entries[i].ModifiedUtc, DateTimeKind.Utc))
                        .ToUnixTimeMilliseconds());
                at += 8;
            }

            return buffer;
        }

        public static bool TryParseReply(byte[] body, out BrowseReply reply)
        {
            reply = new BrowseReply("", "", BrowseStatus.NotFound, Array.Empty<BrowseEntry>(), false);

            if (body.Length < 1) return false;

            int at = 0;
            var status = (BrowseStatus)body[at++];

            if (!ReadBlock(body, ref at, MaxIdBytes, out string id)) return false;
            if (!ReadBlock(body, ref at, MaxPathBytes, out string path)) return false;

            if (at >= body.Length) return false;
            bool truncated = body[at++] != 0;

            if (at + 2 > body.Length) return false;
            int count = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(at));
            at += 2;

            if (count > MaxEntries) return false;

            var entries = new List<BrowseEntry>(count);

            for (int i = 0; i < count; i++)
            {
                if (!ReadBlock(body, ref at, MaxNameBytes, out string name)) return false;
                if (!ReadBlock(body, ref at, MaxIdBytes, out string entryId)) return false;

                if (at + 1 + 8 + 8 > body.Length) return false;

                bool isDirectory = body[at++] != 0;

                long size = BinaryPrimitives.ReadInt64BigEndian(body.AsSpan(at));
                at += 8;

                long modified = BinaryPrimitives.ReadInt64BigEndian(body.AsSpan(at));
                at += 8;

                // A name is chosen on the other device and is not this one's to trust as a path
                // component. Anything that could climb out of a folder is dropped rather than
                // shown, so it can never be echoed back as part of a fetch.
                if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name == ".." || name == ".")
                {
                    continue;
                }

                DateTime when;
                try { when = DateTimeOffset.FromUnixTimeMilliseconds(modified).UtcDateTime; }
                catch { when = DateTime.UnixEpoch; }

                entries.Add(new BrowseEntry(name, isDirectory, size < 0 ? 0 : size, when, entryId));
            }

            reply = new BrowseReply(id, path, status, entries, truncated);
            return true;
        }

        // ------------------------------------------------------------------ helpers

        private static int WriteBlock(byte[] buffer, int at, byte[] value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(at), (ushort)value.Length);
            at += 2;

            value.CopyTo(buffer, at);
            return at + value.Length;
        }

        private static bool ReadBlock(byte[] body, ref int at, int cap, out string value)
        {
            value = "";

            if (at + 2 > body.Length) return false;

            int length = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(at));
            at += 2;

            if (length > cap || at + length > body.Length) return false;

            value = Encoding.UTF8.GetString(body, at, length);
            at += length;

            return true;
        }

        /// <summary>Cut on a character boundary, so a clipped name is never mangled UTF-8.</summary>
        private static byte[] Clamp(string? value, int maxBytes)
        {
            if (string.IsNullOrEmpty(value)) return Array.Empty<byte>();

            byte[] encoded = Encoding.UTF8.GetBytes(value);
            if (encoded.Length <= maxBytes) return encoded;

            var encoder = Encoding.UTF8.GetEncoder();
            var destination = new byte[maxBytes];

            encoder.Convert(value.AsSpan(), destination.AsSpan(), flush: true,
                            out _, out int written, out _);

            return destination.AsSpan(0, written).ToArray();
        }
    }

    /// <summary>A listing as it arrived, with the request it answers so a late reply can be matched.</summary>
    public sealed class BrowseReply
    {
        public BrowseReply(string folderId, string relativePath, BrowseStatus status,
                           IReadOnlyList<BrowseEntry> entries, bool truncated)
        {
            FolderId = folderId;
            RelativePath = relativePath;
            Status = status;
            Entries = entries;
            Truncated = truncated;
        }

        public string FolderId { get; }
        public string RelativePath { get; }
        public BrowseStatus Status { get; }
        public IReadOnlyList<BrowseEntry> Entries { get; }

        /// <summary>True when the folder held more than the protocol will carry.</summary>
        public bool Truncated { get; }
    }
}
