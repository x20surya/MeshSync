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
    }
}
