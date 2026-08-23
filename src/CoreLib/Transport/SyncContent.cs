namespace CoreLib.Transport
{
    /// <summary>
    /// The one-byte tag inside every encrypted payload, saying what the bytes after it are.
    ///
    /// Both apps carried their own copies of these as private constants, which is exactly the
    /// kind of duplication that lets two sides of a protocol drift apart silently. They live
    /// here now so a new type cannot be added to one end alone.
    /// </summary>
    public static class SyncContent
    {
        public const byte Text = 0x00;

        public const byte Image = 0x01;

        /// <summary>
        /// "This is where I am reachable." UTF-8, the sender's current LAN address.
        ///
        /// <para>Replaces wiring up UDP discovery, which was built on both sides and consumed
        /// by neither. Pairing pinned the address baked into the QR code, so a DHCP lease
        /// change broke it until the code was rescanned. Now whichever link is up carries the
        /// new address the moment it changes.</para>
        ///
        /// <para><b>Why a content type rather than a control frame.</b> Bluetooth tells its
        /// frames apart by length alone - two bytes is control, four is a chunk receipt, five
        /// or more is data - so an address would collide with clipboard content. Riding the
        /// normal encrypted path avoids that entirely, and gets authentication for free: an
        /// address is exactly the sort of thing that must not be accepted from a stranger, or
        /// it becomes an invitation to redirect the next connection.</para>
        /// </summary>
        public const byte Address = 0x02;

        /// <summary>
        /// "I have a file for you": an id, a name, a size and a SHA-256.
        ///
        /// <para>A file cannot be one payload the way a clipboard image is. The frame ceiling is
        /// 32 MB, and even under it, holding a whole video in memory on both ends to encrypt and
        /// decrypt it is not something to do to a phone. So the transfer is an offer, a decision,
        /// and a stream of chunks written straight to disk as they land.</para>
        ///
        /// <para>The hash is in the offer rather than at the end so the receiver knows what it is
        /// checking for before it starts, and a truncated transfer is a failure rather than a
        /// file that looks fine and is not.</para>
        /// </summary>
        public const byte FileOffer = 0x03;

        /// <summary>Whether the receiver wants it. A refusal is a normal answer, not an error.</summary>
        public const byte FileAck = 0x04;

        /// <summary>One piece of a file: the id, the offset it belongs at, and the bytes.</summary>
        public const byte FileChunk = 0x05;

        /// <summary>
        /// "Make a noise so I can find you." One byte: non-zero to start, zero to stop.
        ///
        /// <para><b>Why a content type and not a control frame.</b> Two bytes down the Bluetooth
        /// control path would have been the obvious shape and is the wrong one: control frames
        /// ride outside the encrypted payload, so anything that knew the service UUID could make
        /// a phone shriek from across the street. Riding the normal path costs nothing and makes
        /// the request authenticated, exactly as an address is.</para>
        ///
        /// <para>It is small enough for Bluetooth, which is the whole point - the moment you
        /// most want to find a device is the moment it is not on any network.</para>
        /// </summary>
        public const byte Ring = 0x06;

        /// <summary>
        /// A notification from the sending device, mirrored so it can be read elsewhere.
        ///
        /// <para>A few hundred bytes, so Bluetooth carries it - which is the differentiator
        /// again: notifications keep mirroring when there is no network at all.</para>
        ///
        /// <para>This is the most private thing the app carries. It is opt-in, allowed per
        /// application rather than wholesale, and never written to the activity log or anywhere
        /// else. Clipboard traffic is ephemeral by rule; this is more so.</para>
        /// </summary>
        public const byte Notification = 0x07;

        /// <summary>
        /// "That notification is gone." Carries the key alone.
        ///
        /// Dismissal in both directions is what makes mirroring feel finished rather than like a
        /// second inbox to clear. Clearing it on the desktop clears it on the phone.
        /// </summary>
        public const byte NotificationDismiss = 0x08;

        /// <summary>
        /// "Show me what is in this folder." A shared-folder id and a path relative to it.
        ///
        /// <para><b>Why browsing is not just more file transfer.</b> Sending a file is the sender
        /// deciding to hand something over. Browsing is the other device deciding what it wants,
        /// which means the request names something the receiver has to go and find - and a
        /// request that names a path is a request that can name the wrong one. So a browse never
        /// carries an absolute path: it carries the id of a folder that was explicitly shared,
        /// and a relative path underneath it, resolved and then checked to still be inside that
        /// folder. See <see cref="SharedFolders"/>.</para>
        ///
        /// <para>Small enough for Bluetooth in both directions, so browsing works on the standing
        /// link; only the fetch that follows needs Wi-Fi.</para>
        /// </summary>
        public const byte BrowseRequest = 0x09;

        /// <summary>The answer to a browse: the folder's contents, or why there are none.</summary>
        public const byte BrowseReply = 0x0A;

        /// <summary>
        /// "Send me that one." Named the same way a browse is, and answered with an ordinary
        /// file offer - so the transfer, the hashing and the resume behaviour are the ones that
        /// were already built and tested rather than a second copy of them.
        /// </summary>
        public const byte FetchRequest = 0x0B;

        /// <summary>
        /// "Send this back as a reply to that notification." A key and the text to send.
        ///
        /// <para>The one thing mirroring could not do. Reading a message on the laptop and then
        /// picking up the phone to answer it is most of the reason a mirror gets switched off
        /// again - the second screen shows you what you have to go and deal with elsewhere.</para>
        ///
        /// <para><b>It is not a message the app sends; it is the notification's own reply action
        /// being pulled.</b> Android attaches a <c>RemoteInput</c> to the reply action of a
        /// messaging notification, and firing that action with the text filled in is exactly what
        /// happens when you reply from the shade. So the message goes out through WhatsApp, or
        /// Signal, or Messages, by the account already signed in on the phone. Nothing here has
        /// or needs any credential, and no app is automated from the outside - which is the line
        /// this project drew when it banned the accessibility service.</para>
        ///
        /// <para>Two short strings, so Bluetooth carries it. Answering a message with no network
        /// is the case that makes the feature worth having rather than a convenience.</para>
        /// </summary>
        public const byte NotificationReply = 0x0C;
    }
}
