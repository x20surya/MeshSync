using System;
using System.Threading;

namespace CoreLib.Transport.Fabric
{
    /// <summary>Which medium a route to a peer runs over.</summary>
    public enum RouteKind
    {
        /// <summary>A TCP socket. Carries anything, including images and files.</summary>
        WiFi,

        /// <summary>A GATT link this device opened. It scanned and connected out.</summary>
        BleCentral,

        /// <summary>A GATT link a peer opened to this device's advertised service.</summary>
        BlePeripheral
    }

    /// <summary>
    /// Where one route is in its life.
    ///
    /// <para><b>Why this is an enum rather than three booleans.</b> Every head answered "is this
    /// usable" differently - <c>_ready</c> on the Android central, <c>_hasSubscriber</c> on the
    /// Windows peripheral, a session check on the socket - and two of those three answered yes for
    /// a peer that had been refused. There is one definition now, and it is
    /// <see cref="Established"/>.</para>
    ///
    /// <para><b>The load-bearing property.</b> There is no transition into
    /// <see cref="Established"/> that does not pass through <see cref="Handshaking"/>, and
    /// <see cref="Handshaking"/> has a deadline. A peer that connects, answers pings and never
    /// agrees a session therefore cannot reach a state anything will park on - which is the whole
    /// of the defect where a stranger's device held the standing link.</para>
    /// </summary>
    public enum RouteState
    {
        /// <summary>Nothing attempted, and nothing wanted.</summary>
        Idle,

        /// <summary>Policy wants this route to exist. Nothing has been opened yet.</summary>
        Wanted,

        /// <summary>Looking for the peer: a scan window, or an address to dial.</summary>
        Discovering,

        /// <summary>A transport-level connect is in flight.</summary>
        Connecting,

        /// <summary>
        /// Connected, with no session agreed yet. <b>This state has a deadline.</b>
        /// </summary>
        Handshaking,

        /// <summary>A session is agreed and the link has proven it carries traffic.</summary>
        Established,

        /// <summary>Closing, with in-flight sends allowed to finish.</summary>
        Draining,

        /// <summary>Failed. Will not be retried before <see cref="IPeerRoute.RetryAtUtc"/>.</summary>
        Backoff
    }

    /// <summary>One route, named by the peer it reaches and the medium it runs over.</summary>
    public readonly record struct RouteKey(string Fingerprint, RouteKind Kind)
    {
        public override string ToString() =>
            $"{Identity.DeviceIdentity.Shorten(Fingerprint)}/{Kind}";
    }

    /// <summary>
    /// Where time comes from.
    ///
    /// <para>Backoff, the handshake deadline, the rotation interval and the beacon epoch are all
    /// clock-driven, and none of them could be tested while the clock was
    /// <see cref="DateTime.UtcNow"/> at forty call sites. A test drives
    /// <see cref="TestClockAdvance"/> instead of sleeping, so a suite that covers a five-minute
    /// cooldown still runs in milliseconds.</para>
    /// </summary>
    public interface ILinkClock
    {
        DateTime UtcNow { get; }
    }

    /// <summary>The real clock. One instance is enough; it holds nothing.</summary>
    public sealed class SystemClock : ILinkClock
    {
        public static readonly SystemClock Instance = new();

        public DateTime UtcNow => DateTime.UtcNow;
    }

}
