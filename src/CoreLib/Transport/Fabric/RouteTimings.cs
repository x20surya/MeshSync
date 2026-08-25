using System;

namespace CoreLib.Transport.Fabric
{
    /// <summary>
    /// Every interval the fabric runs on, in one record.
    ///
    /// <para>The values are the ones already in the field, collected from
    /// <c>docs/reference/timings.md</c> rather than chosen afresh: the twelve-second identity grace
    /// the Linux scanner settled on, the one-to-sixty-second backoff the Android loop settled on,
    /// and the two ceilings that make a screen-off device retry slowly. They are a record so a test
    /// can shrink them to milliseconds without a <c>Task.Delay</c> anywhere in the suite.</para>
    /// </summary>
    public sealed record RouteTimings
    {
        public static readonly RouteTimings Default = new();

        /// <summary>
        /// How long a connected route has to agree a session before it is closed.
        ///
        /// <para>This is the single value that closes the defect where a stranger's device held the
        /// standing link: it applies to every route kind on every platform, because the state
        /// machine is shared rather than reimplemented three times.</para>
        /// </summary>
        public TimeSpan HandshakeGrace { get; init; } = TimeSpan.FromSeconds(12);

        public TimeSpan MinBackoff { get; init; } = TimeSpan.FromSeconds(1);

        public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);

        /// <summary>Backoff ceiling while somebody is at the device. Reconnect promptly.</summary>
        public TimeSpan ActiveCeiling { get; init; } = TimeSpan.FromSeconds(8);

        /// <summary>
        /// Backoff ceiling with the screen off.
        ///
        /// Scanning is the expensive half of the radio tier, and retrying every few seconds all
        /// night - which is what a single brisk ceiling does with the other device switched off -
        /// is exactly the drain that holding a cheap link was supposed to avoid.
        /// </summary>
        public TimeSpan IdleCeiling { get; init; } = TimeSpan.FromSeconds(60);

        /// <summary>How long a device that refused a session is left alone.</summary>
        public TimeSpan RefusalCooldown { get; init; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// How often a scan round runs while some peer is still owed a link.
        ///
        /// <para>Windows and Linux already agree on 30 seconds. It was 4 and ungated once, which is
        /// most of why an established link felt rough rather than merely duplicated: an active scan
        /// contends with every live link for the same antenna.</para>
        /// </summary>
        public TimeSpan ScanInterval { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// How long one discovery window stays open.
        ///
        /// A window rather than a subscription, and stopped in a <c>finally</c> between rounds
        /// rather than started once and left running for the life of the process.
        /// </summary>
        public TimeSpan ScanWindow { get; init; } = TimeSpan.FromSeconds(12);

        /// <summary>
        /// The longest a whole scan round may take before it is abandoned and retried.
        ///
        /// <para><b>Why a round needs a bound and not only the window.</b> The window is what the
        /// scan aims for; this is what happens when it never comes back at all. A radio call that
        /// goes unanswered leaves the round awaiting for ever - no error, no exception, no log
        /// line - and Bluetooth is simply gone for the life of the process while every other part
        /// of the app carries on. Observed on Linux: the last scan line in the log was three hours
        /// old and the adapter was still discovering, because the round's own cleanup never ran
        /// either.</para>
        ///
        /// <para>Comfortably more than <see cref="ScanWindow"/>, because overrunning the window is
        /// normal and being gone is not.</para>
        /// </summary>
        public TimeSpan ScanRoundBudget { get; init; } = TimeSpan.FromSeconds(45);

        /// <summary>How often the radio reconsiders which peers hold its central links.</summary>
        public TimeSpan RotationInterval { get; init; } = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Concurrent outbound radio links.
        ///
        /// <para>Four covers a phone, a laptop and a desktop with headroom, sits inside every
        /// platform ceiling - a GATT central holds around seven on Android - and keeps the airtime
        /// per link high enough that a standing link is worth holding. Peers past it rotate in.</para>
        /// </summary>
        public int MaxBleCentralLinks { get; init; } = 4;

        /// <summary>How often the supervisor reconciles when nothing has signalled it.</summary>
        public TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromSeconds(15);

        /// <summary>
        /// The shortest gap between two attempts to open the same kind of route to one peer.
        ///
        /// <para><b>A hard ceiling on dial rate, and the only one that cannot be argued away.</b>
        /// The backoff after a failure is cleared by the next success, which is correct for a
        /// backoff and useless as a rate limit: in a glare loop every cycle establishes something
        /// briefly, so the backoff resets and the next attempt goes out immediately.</para>
        ///
        /// <para>Two devices on a desk opened 285 sockets to each other in under three minutes,
        /// each one settling correctly and none of them lasting. The settlement was not the
        /// problem - the rate was. This is not cleared by anything.</para>
        /// </summary>
        public TimeSpan MinDialInterval { get; init; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// How long the end that would lose a socket collision waits before dialling anyway.
        ///
        /// <para><b>Both devices dial, and that is deliberate</b> - either end may be the only one
        /// that can open the socket. But both ends already agree which link survives a collision:
        /// the one dialled by the lower fingerprint. So the higher one has nothing to gain by
        /// racing, and everything to lose - its link is dropped, which it sees as an ordinary loss
        /// and retries.</para>
        ///
        /// <para>The old dial loop ran on a fifteen-second timer and glare cost one wasted socket
        /// per round. A supervisor that reconciles the moment anything changes has no such
        /// accidental rate limit: a phone and a laptop on one desk produced a collision every two
        /// seconds indefinitely, each one re-establishing the link and clearing the very backoff
        /// meant to damp it.</para>
        ///
        /// <para>So the higher fingerprint gives the lower one first refusal, and dials only if
        /// nothing has arrived by the time this elapses. A peer that is asleep or unreachable
        /// still gets dialled, just a beat later.</para>
        /// </summary>
        public TimeSpan DialGrace { get; init; } = TimeSpan.FromSeconds(6);

        /// <summary>A reconcile pass that has not finished in this long means the loop is wedged.</summary>
        public TimeSpan SupervisorWatchdog { get; init; } = TimeSpan.FromSeconds(60);
    }
}
