using System;
using System.Threading;

namespace CoreLib.Identity
{
    /// <summary>
    /// The interval during which a device will accept a peer it has never met.
    ///
    /// <para><b>Why it has to exist.</b> Pairing carries one key in one direction: the QR shows
    /// this device's public key and the other device scans it. That is enough for the scanner
    /// to authenticate us, and nothing at all for us to authenticate the scanner. Something has
    /// to tell this side that the stranger now knocking is the device the user just pointed a
    /// camera at.</para>
    ///
    /// <para><b>What it is.</b> Showing the pairing code is the signal. While that screen is up
    /// the user is standing in front of this device asking for something to be paired, so the
    /// first peer to introduce itself is recorded. When it is not up, an unknown key is
    /// refused. This is the same bargain Bluetooth pairing mode makes, and it rests on the same
    /// assumption: reaching the screen requires being at the machine.</para>
    ///
    /// <para><b>What it is not.</b> It is not a defence against someone who is already on the
    /// network at the moment the window is open and who can win the race to connect. The window
    /// is short and user-initiated to keep that opportunity small; closing it entirely needs the
    /// user to confirm the fingerprint on both devices, which is a UI change rather than a
    /// protocol one.</para>
    ///
    /// <para>Owned by a <see cref="PeerSecurity"/> rather than being static. It reads like
    /// process-wide state - there is one user in front of one machine - but making it global
    /// means two devices cannot be modelled independently, which is exactly what a test of
    /// "a stranger is refused" needs.</para>
    /// </summary>
    public sealed class PairingWindow
    {
        /// <summary>
        /// Long enough to scan a code, unlock a phone and let it connect. Short enough that
        /// leaving the pairing screen open by accident is not an open door for the afternoon.
        /// </summary>
        public static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(3);

        private long _openUntilTicks;

        /// <summary>Raised when the window opens or closes, so a UI can say which it is.</summary>
        public event Action? Changed;

        public bool IsOpen => DateTime.UtcNow.Ticks < Interlocked.Read(ref _openUntilTicks);

        /// <summary>How much longer the window stays open, or zero when it is shut.</summary>
        public TimeSpan Remaining
        {
            get
            {
                var until = new DateTime(Interlocked.Read(ref _openUntilTicks), DateTimeKind.Utc);
                var remaining = until - DateTime.UtcNow;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        public void Open(TimeSpan? duration = null)
        {
            bool was = IsOpen;
            Interlocked.Exchange(ref _openUntilTicks, DateTime.UtcNow.Add(duration ?? DefaultDuration).Ticks);

            if (!was)
            {
                Diagnostics.Log.Write("Pairing", $"Accepting new devices for {(duration ?? DefaultDuration).TotalMinutes:F0} minutes.");
                Changed?.Invoke();
            }
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _openUntilTicks, 0) == 0) return;

            Diagnostics.Log.Write("Pairing", "No longer accepting new devices.");
            Changed?.Invoke();
        }
    }
}
