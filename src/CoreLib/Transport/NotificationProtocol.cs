using System;
using System.Buffers.Binary;

namespace CoreLib.Transport
{
    /// <summary>One notification, as it crossed the wire.</summary>
    public sealed class MirroredNotification
    {
        public MirroredNotification(string key, string package, string appName,
                                    string title, string text, DateTimeOffset postedUtc,
                                    bool canReply = false, string replyLabel = "")
        {
            Key = key;
            Package = package;
            AppName = appName;
            Title = title;
            Text = text;
            PostedUtc = postedUtc;
            CanReply = canReply;
            ReplyLabel = replyLabel;
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

        /// <summary>
        /// True when the device this came from can send a reply back into the app that posted it.
        ///
        /// <para>Set by the sender, never guessed by the receiver: only the phone knows whether
        /// the notification actually carried a reply action, and offering a reply box for one
        /// that did not is a message the user believes they sent.</para>
        /// </summary>
        public bool CanReply { get; }

        /// <summary>
        /// What the app calls its reply action - "Reply", "Antworten", "Mark as read" on some.
        ///
        /// Worth carrying rather than hardcoding "Reply": it is the app's own word for what the
        /// button does, and on a few of them the action is not a reply at all.
        /// </summary>
        public string ReplyLabel { get; }
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

        /// <summary>"Reply", "Antworten". A word, not a sentence.</summary>
        public const int MaxReplyLabelBytes = 64;

        /// <summary>
        /// A reply is typed by a person on a keyboard, so it can be longer than a notification
        /// preview - but it still has to cross Bluetooth in one piece.
        /// </summary>
        public const int MaxReplyBytes = 2048;

        public static byte[] Build(MirroredNotification notification)
        {
            byte[] key = Clamp(notification.Key, MaxKeyBytes);
            byte[] package = Clamp(notification.Package, MaxPackageBytes);
            byte[] appName = Clamp(notification.AppName, MaxPackageBytes);
            byte[] title = Clamp(notification.Title, MaxTitleBytes);
            byte[] text = Clamp(notification.Text, MaxTextBytes);

            byte[] replyLabel = Clamp(notification.ReplyLabel, MaxReplyLabelBytes);

            var body = new byte[8 + 2 + key.Length + 2 + package.Length + 2 + appName.Length
                                  + 2 + title.Length + 2 + text.Length
                                  + 1 + 2 + replyLabel.Length];

            BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(0, 8),
                notification.PostedUtc.ToUnixTimeMilliseconds());

            int at = 8;
            at = WriteField(body, at, key);
            at = WriteField(body, at, package);
            at = WriteField(body, at, appName);
            at = WriteField(body, at, title);
            at = WriteField(body, at, text);

            // Appended rather than inserted, and read back only if it is there, so a device on
            // the older build reads the five fields it knows and stops. Both directions keep
            // working across a mixed mesh, which matters because the phone and the desktop are
            // updated on different days by different means.
            body[at++] = notification.CanReply ? (byte)1 : (byte)0;
            WriteField(body, at, replyLabel);

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

            // Absent on anything built before replies existed, which is a notification that
            // cannot be replied to rather than a malformed one.
            bool canReply = false;
            string replyLabel = "";

            if (at < body.Length)
            {
                canReply = body[at++] != 0;
                if (!ReadField(body, ref at, MaxReplyLabelBytes, out replyLabel)) replyLabel = "";
            }

            DateTimeOffset postedUtc;
            try { postedUtc = DateTimeOffset.FromUnixTimeMilliseconds(posted); }
            catch { postedUtc = DateTimeOffset.UtcNow; }

            notification = new MirroredNotification(key, package, appName, title, text, postedUtc,
                                                    canReply, replyLabel);
            return true;
        }

        /// <summary>
        /// A reply on its way back: which notification, and what to send.
        ///
        /// <para>The key is the sending device's own opaque handle, returned verbatim exactly as
        /// a dismissal returns it. Only the device it came from knows what it means, and only
        /// that device can do anything with it.</para>
        /// </summary>
        public static byte[] BuildReply(string key, string text)
        {
            byte[] keyBytes = Clamp(key, MaxKeyBytes);
            byte[] textBytes = Clamp(text, MaxReplyBytes);

            var body = new byte[2 + keyBytes.Length + 2 + textBytes.Length];

            int at = WriteField(body, 0, keyBytes);
            WriteField(body, at, textBytes);

            return body;
        }

        public static bool TryParseReply(byte[] body, out string key, out string text)
        {
            key = "";
            text = "";
            if (body == null) return false;

            int at = 0;
            if (!ReadField(body, ref at, MaxKeyBytes, out key)) return false;
            if (!ReadField(body, ref at, MaxReplyBytes, out text)) return false;

            // An empty reply is not a reply. Sending one would post a blank message into
            // somebody's conversation, which is worse than doing nothing.
            return key.Length > 0 && text.Trim().Length > 0;
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
