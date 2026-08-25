using CoreLib.Transport;
using CoreLib.Transport.Ble;
using CoreLib.Transport.Fabric;

namespace CoreLib.Tests.Fakes;

/// <summary>
/// A radio with scriptable advertisements and no hardware.
///
/// <para>The highest-value piece of the harness. Every finding in <c>HANDOFF.md</c> under
/// "Bluetooth" - a device that answers pings and never identifies itself, a ghost object with no
/// RSSI, a phone that rotates its address mid-cooldown, a foreign mesh sitting closer than your
/// own - becomes a scripted scenario here instead of an afternoon with two devices and a log.</para>
///
/// <para>No head has ever had a test. This is the seam that changes that.</para>
/// </summary>
public sealed class FakeBleRadio : IBleRadio
{
    private readonly FakeClock _clock;
    private readonly List<BleCandidate> _inRange = new();

    public FakeBleRadio(FakeClock clock, BleCapability capability = BleCapability.Both)
    {
        _clock = clock;
        Capability = capability;
    }

    public BleCapability Capability { get; set; }
    public bool IsAvailable { get; set; } = true;
    public string Status => IsAvailable ? "scanning" : "off";

    /// <summary>Every scan window this radio was asked to run.</summary>
    public List<TimeSpan> ScanWindows { get; } = new();

    /// <summary>Every candidate it was asked to connect to, in order.</summary>
    public List<BleCandidate> ConnectAttempts { get; } = new();

    public List<BleAdvertisement> Published { get; } = new();
    public int StopAdvertisingCalls { get; private set; }
    public bool Advertising { get; private set; }

    /// <summary>Candidates whose connect should fail outright rather than produce a route.</summary>
    public HashSet<string> RefuseConnect { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Candidates whose route is already established by the time <see cref="ConnectAsync"/>
    /// returns, mapped to the fingerprint it identified as.
    ///
    /// <para>Not a contrivance: a peripheral sends its hello the instant a central subscribes, and
    /// a real connect does not return until it has subscribed. The fast path is the normal one on
    /// a responsive peer.</para>
    /// </summary>
    public Dictionary<string, string> LiveOnArrival { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Routes handed out by <see cref="ConnectAsync"/>, keyed by the address dialled.</summary>
    public Dictionary<string, FakeRoute> Opened { get; } = new(StringComparer.OrdinalIgnoreCase);

    public event Action<IPeerRoute>? InboundRoute;

    // ── scripting ────────────────────────────────────────────────────────────

    /// <summary>Puts a device in range. Absent RSSI marks a ghost the scanner should ignore.</summary>
    public FakeBleRadio Place(string address, int rssi = -50, string? name = null,
                              byte[]? beacon = null, bool present = true)
    {
        _inRange.Add(new BleCandidate
        {
            Address = address,
            Name = name,
            Rssi = rssi,
            Beacon = beacon,
            IsPresent = present,
        });

        return this;
    }

    public void ClearRange() => _inRange.Clear();

    /// <summary>A peer connects to this device's advertised service.</summary>
    public FakeRoute Arrive(string fingerprint = "")
    {
        var route = new FakeRoute(RouteKind.BlePeripheral, _clock, fingerprint, outbound: false);
        InboundRoute?.Invoke(route);
        return route;
    }

    // ── the interface ────────────────────────────────────────────────────────

    public Task StartAdvertisingAsync(BleAdvertisement advertisement, CancellationToken cancellationToken = default)
    {
        Published.Add(advertisement);
        Advertising = true;
        return Task.CompletedTask;
    }

    public Task StopAdvertisingAsync()
    {
        StopAdvertisingCalls++;
        Advertising = false;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Set to make a scan never come back on its own, the way an unanswered platform call does.
    ///
    /// <para>It still honours the token, because that is exactly what the real radio does: the
    /// round is cancelled, its cleanup runs, and the antenna is handed back.</para>
    /// </summary>
    public bool WedgeScan { get; set; }

    /// <summary>Whether a wedged scan was let go rather than left hanging.</summary>
    public int ScansCancelled { get; private set; }

    public async Task<IReadOnlyList<BleCandidate>> ScanAsync(TimeSpan window, CancellationToken cancellationToken = default)
    {
        ScanWindows.Add(window);

        if (WedgeScan)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ScansCancelled++;
                throw;
            }
        }

        return _inRange.ToList();
    }

    /// <summary>Addresses this fake should claim it already holds a link to.</summary>
    public HashSet<string> Linked { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasLinkTo(string address) => Linked.Contains(address);

    public Task<IPeerRoute?> ConnectAsync(BleCandidate candidate, CancellationToken cancellationToken = default)
    {
        ConnectAttempts.Add(candidate);

        if (RefuseConnect.Contains(candidate.Address)) return Task.FromResult<IPeerRoute?>(null);

        var route = new FakeRoute(RouteKind.BleCentral, _clock).Connect();

        if (LiveOnArrival.TryGetValue(candidate.Address, out var fingerprint))
        {
            route.Identify(fingerprint).Establish();
        }

        Opened[candidate.Address] = route;
        return Task.FromResult<IPeerRoute?>(route);
    }

    public ValueTask DisposeAsync()
    {
        InboundRoute = null;
        return ValueTask.CompletedTask;
    }
}
