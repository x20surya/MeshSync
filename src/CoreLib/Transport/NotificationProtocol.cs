using System;
using System.Buffers.Binary;

namespace CoreLib.Transport
{
    /// <summary>One notification, as it crossed the wire.</summary>
    public sealed class MirroredNotification
    {
        public MirroredNotification(string key, string package, string appName,
                                    string title, string text, DateTimeOffset postedUtc)
        {
            Key = key;
            Package = package;
            AppName = appName;
            Title = title;
            Text = text;
            PostedUtc = postedUtc;
        }

        /// <summary>
        /// The sending device's own handle for it. Opaque here, and sent back verbatim to
        /// dismiss - only the device it came from knows what it means.
        /// </summary>
        public string Key { get; }

        /// <summary>Which application posted it, for the allowlist to be about something stable.</summary>
        public string Package { get; }

        /// <summary>What that application is called, because a package name is not a name.</summary>
        public string AppName { get; }

        public string Title { get; }

        public string Text { get; }

        public DateTimeOffset PostedUtc { get; }
    }

    /// <summary>
    /// Framing for mirrored notifications.
    ///
    /// <para>Five length-prefixed strings and a timestamp. Small enough that Bluetooth carries
    /// it without help, which is the whole reason this feature is on the list: notifications
    /// keep arriving when there is no network at all.</para>
    ///
    /// <para>Every field is capped. A notification is written by whatever app posted it, so its
    /// length is not this project's to assume - and an uncapped one on the Bluetooth tier would
    /// occupy the link for as long as the sender felt like.</para>
    /// </summary>
    public static class NotificationProtocol
    {
        /// <summary>Enough for a real title; anything longer is not being read on a second screen.</summary>
        public const int MaxTitleBytes = 256;

        /// <summary>Enough for a message worth mirroring. A wall of text is not one.</summary>
        public const int MaxTextBytes = 1024;

        public const int MaxKeyBytes = 256;

        public const int MaxPackageBytes = 256;

        public static byte[] Build(MirroredNotification notification)
        {
            byte[] key = Clamp(notification.Key, MaxKeyBytes);
            byte[] package = Clamp(notification.Package, MaxPackageBytes);
            byte[] appName = Clamp(notification.AppName, MaxPackageBytes);
            byte[] title = Clamp(notification.Title, MaxTitleBytes);
            byte[] text = Clamp(notification.Text, MaxTextBytes);

            var body = new byte[8 + 2 + key.Length + 2 + package.Length + 2 + appName.Length
                                  + 2 + title.Length + 2 + text.Length];

            BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(0, 8),
                notification.PostedUtc.ToUnixTimeMilliseconds());

            int at = 8;
            at = WriteField(body, at, key);
            at = WriteField(body, at, package);
            at = WriteField(body, at, appName);
            at = WriteField(body, at, title);
            WriteField(body, at, text);

            return body;
        }

        public static bool TryParse(byte[] body, out MirroredNotification? notification)
        {
            notification = null;
            if (body == null || body.Length < 8) return false;

            long posted = BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(0, 8));

            int at = 8;
            if (!ReadField(body, ref at, MaxKeyBytes, out string key)) return false;
            if (!ReadField(body, ref at, MaxPackageBytes, out string package)) return false;
            if (!ReadField(body, ref at, MaxPackageBytes, out string appName)) return false;
            if (!ReadField(body, ref at, MaxTitleBytes, out string title)) return false;
            if (!ReadField(body, ref at, MaxTextBytes, out string text)) return false;

            if (key.Length == 0) return false;

            DateTimeOffset postedUtc;
            try { postedUtc = DateTimeOffset.FromUnixTimeMilliseconds(posted); }
            catch { postedUtc = DateTimeOffset.UtcNow; }

            notification = new MirroredNotification(key, package, appName, title, text, postedUtc);
            return true;
        }

        public static byte[] BuildDismiss(string key) => Clamp(key, MaxKeyBytes);

        public static bool TryParseDismiss(byte[] body, out string key)
        {
            key = "";
            if (body == null || body.Length == 0 || body.Length > MaxKeyBytes) return false;

            try { key = System.Text.Encoding.UTF8.GetString(body); }
            catch { return false; }

            return key.Length > 0;
        }

        private static int WriteField(byte[] body, int at, byte[] value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(at, 2), (ushort)value.Length);
            value.CopyTo(body, at + 2);
            return at + 2 + value.Length;
        }

        private static bool ReadField(byte[] body, ref int at, int max, out string value)
        {
            value = "";
            if (body.Length < at + 2) return false;

            int length = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at, 2));
            if (length > max) return false;
            if (body.Length < at + 2 + length) return false;

            if (length > 0)
            {
                try { value = System.Text.Encoding.UTF8.GetString(body, at + 2, length); }
                catch { return false; }
            }

            at += 2 + length;
            return true;
        }

        /// <summary>
        /// Trims to a byte budget on a character boundary, so a multi-byte name is never halved
        /// and delivered as mojibake - the same care the Bluetooth hello takes with a device name.
        /// </summary>
        private static byte[] Clamp(string? value, int maxBytes)
        {
            string text = (value ?? "").Trim();
            if (text.Length == 0) return Array.Empty<byte>();

            var encoding = System.Text.Encoding.UTF8;
            if (encoding.GetByteCount(text) <= maxBytes) return encoding.GetBytes(text);

            // Worst case four bytes per character, so this can only ever be short enough.
            int characters = Math.Min(text.Length, maxBytes / 4);
            while (characters > 0 && encoding.GetByteCount(text.AsSpan(0, characters)) > maxBytes) characters--;

            return encoding.GetBytes(text.AsSpan(0, characters).ToString());
        }
    }
}
