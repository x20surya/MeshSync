using CoreLib.Transport.Fabric;

namespace CoreLib.Tests.Fakes;

/// <summary>
/// A clock a test moves by hand.
///
/// Every interval in the fabric - the handshake grace, the backoff, the rotation window - is
/// clock-driven, and a suite that covered a five-minute cooldown by sleeping would take five
/// minutes. Nothing here sleeps.
/// </summary>
public sealed class FakeClock : ILinkClock
{
    private DateTime _now;

    public FakeClock(DateTime? start = null) =>
        _now = start ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public DateTime UtcNow => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
