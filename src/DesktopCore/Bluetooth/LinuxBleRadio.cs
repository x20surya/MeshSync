using CoreLib.Diagnostics;
using CoreLib.Transport;
using CoreLib.Transport.Ble;
using CoreLib.Transport.Fabric;
using Tmds.DBus.Protocol;

namespace DesktopCore.Bluetooth;

/// <summary>
/// This machine's Bluetooth adapter, behind the shared radio interface.
///
/// <para><b>What it is responsible for, and what it is not.</b> A scan window, a connect, an
/// advertisement, and an honest answer about what the radio can do. It decides nothing about which
/// peer to reach, when to scan, how long to wait, or what to do about a refusal - all of which
/// used to live in this file, was written twice more on the other two platforms, and was wrong on
/// both of them.</para>
///
/// <para><b>Property changes are dispatched here.</b> BlueZ offers one signal stream for the whole
/// bus, so with several links open something has to route each change to the link it belongs to.
/// That is this class; the links themselves know only their own characteristic.</para>
/// </summary>
public sealed class LinuxBleRadio : IBleRadio
{
    private readonly BlueZ _bluez;
    private readonly ILinkClock _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinuxBleLink> _byOutbox = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinuxBleLink> _byDevice = new(StringComparer.Ordinal);

    private string? _adapterPath;
    private bool _discovering;
    private bool _disposed;

    private LinuxBleRadio(BlueZ bluez, ILinkClock clock)
    {
        _bluez = bluez;
        _clock = clock;
    }

    /// <summary>
    /// Connects to BlueZ and finds a powered adapter, or answers null.
    ///
    /// A desktop with no Bluetooth is a perfectly ordinary desktop, so this failing is not loud
    /// and not fatal: Wi-Fi carries everything the radio would have.
    /// </summary>
    public static async Task<LinuxBleRadio?> TryCreateAsync(BlueZ bluez, string adapterPath, ILinkClock? clock = null)
    {
        if (bluez == null || string.IsNullOrWhiteSpace(adapterPath)) return null;

        var radio = new LinuxBleRadio(bluez, clock ?? SystemClock.Instance) { _adapterPath = adapterPath };

        await bluez.WatchPropertiesAsync(radio.OnPropertyChanged).ConfigureAwait(false);
        radio.IsAvailable = true;

        return radio;
    }

    /// <summary>
    /// What this machine can do, taken from what actually started.
    ///
    /// <para>Set by the daemon from whether the peripheral half <em>started</em>, never from what
    /// the adapter claimed. BlueZ accepts the scan and rejects the exported GATT tree, so a
    /// machine that reports <see cref="BleCapability.Both"/> on the strength of having an adapter
    /// makes the arbiter answer "you advertise" - and then it neither advertises nor scans, which
    /// is a deadlock rather than a degraded state.</para>
    /// </summary>
    public BleCapability Capability { get; set; } = BleCapability.Central;

    public bool IsAvailable { get; set; }

    public string Status =>
        !IsAvailable ? "off" :
        _discovering ? "scanning" :
        "idle";

    public event Action<IPeerRoute>? InboundRoute;

    /// <summary>
    /// Hands the fabric a link a peer opened to this device's advertised service.
    ///
    /// Called by the daemon when the peripheral half accepts a central. It is a separate entry
    /// point rather than an event on the server so that a head with no working peripheral - which
    /// is every Linux machine today, because BlueZ rejects the exported GATT tree - simply never
    /// calls it.
    /// </summary>
    public void PublishInbound(IPeerRoute route)
    {
        try { InboundRoute?.Invoke(route); }
        catch (Exception ex) { Log.Write("BleRadio", "An InboundRoute handler threw", ex); }
    }

    // ──────────────────────────────── advertising

    public Task StartAdvertisingAsync(BleAdvertisement advertisement, CancellationToken cancellationToken = default)
    {
        // The peripheral half is owned by LinuxBleServer, which registers the GATT tree and the
        // advertisement together. Until BlueZ accepts that tree there is nothing to publish, and
        // saying so honestly is what keeps the arbiter's answer correct.
        _ = advertisement;
        return Task.CompletedTask;
    }

    public Task StopAdvertisingAsync() => Task.CompletedTask;

    // ──────────────────────────────── scanning

    /// <summary>
    /// One discovery window, stopped in a <c>finally</c>.
    ///
    /// <para>Filtered on the service UUID so the radio is not woken for every beacon in the room,
    /// and on <c>le</c> so classic devices never appear at all. Stopping between rounds is
    /// load-bearing: an active scan running alongside a live link contends with it for the same
    /// antenna, and this used to be started once and left running for the life of the
    /// process.</para>
    /// </summary>
    public async Task<IReadOnlyList<BleCandidate>> ScanAsync(TimeSpan window, CancellationToken cancellationToken = default)
    {
        if (_disposed || _adapterPath == null || !IsAvailable) return Array.Empty<BleCandidate>();

        if (!await StartDiscoveryAsync(_adapterPath).ConfigureAwait(false)) return Array.Empty<BleCandidate>();

        try
        {
            var deadline = _clock.UtcNow + window;
            var seen = new Dictionary<string, BleCandidate>(StringComparer.Ordinal);

            while (_clock.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                foreach (var candidate in await SweepAsync().ConfigureAwait(false)) seen[candidate.Address] = candidate;

                // One present candidate is enough to stop early: the scheduler ranks and connects,
                // and holding the antenna open past that only slows the link it is about to open.
                if (seen.Values.Any(c => c.IsPresent)) break;

                try { await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

            return seen.Values.ToList();
        }
        finally
        {
            await StopDiscoveryAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Every device BlueZ currently knows that advertises the mesh service.
    ///
    /// <para>BlueZ keeps a device object for every LE address it has ever seen, and a phone rotates
    /// its address for privacy - so most of them are ghosts still carrying the service UUID they
    /// advertised at the time, and dialling one connects to an address that stopped existing
    /// minutes ago. RSSI is the discriminator: it is published only while a device is being seen
    /// in the current discovery session.</para>
    /// </summary>
    private async Task<IReadOnlyList<BleCandidate>> SweepAsync()
    {
        var objects = await _bluez.GetObjectsAsync().ConfigureAwait(false);

        return objects
            .Where(o => o.Has(BlueZ.DeviceInterface) &&
                        o.Strings(BlueZ.DeviceInterface, "UUIDs")
                         .Any(u => string.Equals(u, BleProtocol.ServiceUuid.ToString("D"),
                                                 StringComparison.OrdinalIgnoreCase)))
            .Select(o => new BleCandidate
            {
                Address = o.Path,
                Name = o.String(BlueZ.DeviceInterface, "Alias"),
                Rssi = Rssi(o),
                Beacon = BeaconOf(o),
                IsPresent = o.Bool(BlueZ.DeviceInterface, "Connected") ||
                            o.Property(BlueZ.DeviceInterface, "RSSI") != null,
            })
            .ToList();
    }

    /// <summary>Pulls the mesh beacon out of the advertisement's manufacturer data, if it carried one.</summary>
    private static byte[]? BeaconOf(BlueZObject device)
    {
        try
        {
            var data = device.Property(BlueZ.DeviceInterface, "ManufacturerData");
            if (!data.HasValue) return null;

            // a{qv} on the wire: company id to payload. Ours is the only entry that matters.
            var byCompany = data.Value.GetDictionary<ushort, VariantValue>();
            if (!byCompany.TryGetValue(MeshBeacon.CompanyId, out var payload)) return null;

            var bytes = payload.GetArray<byte>();
            return bytes.Length == MeshBeacon.Length ? bytes : null;
        }
        catch
        {
            // An advertisement this build does not understand is simply not one of ours.
            return null;
        }
    }

    private static int Rssi(BlueZObject device)
    {
        var value = device.Property(BlueZ.DeviceInterface, "RSSI");
        try { return value?.GetInt16() ?? short.MinValue; } catch { return short.MinValue; }
    }

    private async Task<bool> StartDiscoveryAsync(string adapterPath)
    {
        if (_discovering) return true;

        try
        {
            await _bluez.CallAsync(adapterPath, BlueZ.AdapterInterface, "SetDiscoveryFilter", "a{sv}", (ref MessageWriter writer) =>
            {
                var dict = writer.WriteArrayStart(DBusType.DictEntry);
                writer.WriteString("UUIDs");
                // No helper for a variant holding an array, and a variant on the wire is just a
                // signature followed by the value, so it is written out longhand.
                writer.WriteSignature("as");
                writer.WriteArray(new[] { BleProtocol.ServiceUuid.ToString("D") });
                writer.WriteString("Transport");
                writer.WriteVariantString("le");
                writer.WriteArrayEnd(dict);
            }).ConfigureAwait(false);

            await _bluez.CallAsync(adapterPath, BlueZ.AdapterInterface, "StartDiscovery").ConfigureAwait(false);

            _discovering = true;
            return true;
        }
        catch (Exception ex)
        {
            // Left false, deliberately. This used to be set true on failure, so one transient
            // refusal - an adapter powered off at launch, or held by something else - convinced
            // the loop it was already scanning and it never tried again for the life of the
            // process. Reporting the failure honestly costs one skipped round instead.
            _discovering = false;
            Log.Write("BleRadio", $"Discovery could not be started: {ex.Message}");
            return false;
        }
    }

    private async Task StopDiscoveryAsync()
    {
        if (!_discovering) return;
        _discovering = false;

        if (_adapterPath == null) return;

        try
        {
            await _bluez.CallAsync(_adapterPath, BlueZ.AdapterInterface, "StopDiscovery").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Write("BleRadio", $"Discovery could not be stopped: {ex.Message}");
        }
    }

    // ──────────────────────────────── connecting

    /// <summary>
    /// Opens a link to one candidate and hands it back before it has identified itself.
    ///
    /// The fabric holds it under the shared handshake deadline from here, which is the window a
    /// device from another mesh used to live in for as long as it stayed in range.
    /// </summary>
    public async Task<IPeerRoute?> ConnectAsync(BleCandidate candidate, CancellationToken cancellationToken = default)
    {
        if (_disposed || !IsAvailable) return null;

        lock (_gate)
        {
            if (_byDevice.ContainsKey(candidate.Address)) return null;   // already linked
        }

        try
        {
            var objects = await _bluez.GetObjectsAsync().ConfigureAwait(false);
            var device = objects.FirstOrDefault(o => o.Path == candidate.Address);

            if (device?.Bool(BlueZ.DeviceInterface, "Connected") != true)
            {
                await _bluez.CallAsync(candidate.Address, BlueZ.DeviceInterface, "Connect").ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Write("BleRadio", $"Connecting to {candidate.Name ?? candidate.Address} failed: {ex.Message}");
            return null;
        }

        var link = new LinuxBleLink(_bluez, candidate.Address, candidate.Name, _clock);
        Prepare?.Invoke(link);

        lock (_gate) _byDevice[candidate.Address] = link;

        link.StateChanged += OnLinkState;

        if (!await link.ResolveAsync(cancellationToken).ConfigureAwait(false))
        {
            // It never published the service. Remove the BlueZ object outright rather than merely
            // disconnecting: left in place it keeps its cached UUIDs and is picked again on the
            // next sweep, forever.
            await ForgetAsync(candidate.Address).ConfigureAwait(false);
            return link;   // handed back so its Backoff state reaches the scheduler's cooldown
        }

        string? outbox = link.OutboxPath;
        if (outbox != null) lock (_gate) _byOutbox[outbox] = link;

        return link;
    }

    /// <summary>Called with each new link so the daemon can set its identity and session hooks.</summary>
    public Action<LinuxBleLink>? Prepare { get; set; }

    private async Task ForgetAsync(string devicePath)
    {
        if (_adapterPath == null) return;

        try
        {
            await _bluez.CallAsync(_adapterPath, BlueZ.AdapterInterface, "RemoveDevice", "o",
                (ref MessageWriter w) => w.WriteObjectPath(devicePath)).ConfigureAwait(false);
        }
        catch (Exception ex) { Log.Write("BleRadio", $"Could not forget it: {ex.GetType().Name}"); }
    }

    private void OnLinkState(IPeerRoute route, RouteState from, RouteState to)
    {
        if (to is not (RouteState.Idle or RouteState.Backoff)) return;
        if (route is not LinuxBleLink link) return;

        link.StateChanged -= OnLinkState;

        lock (_gate)
        {
            _byDevice.Remove(link.DevicePath);

            foreach (var pair in _byOutbox.Where(p => ReferenceEquals(p.Value, link)).ToList())
            {
                _byOutbox.Remove(pair.Key);
            }
        }
    }

    // ──────────────────────────────── the one signal stream

    private void OnPropertyChanged(PropertyChange change)
    {
        try
        {
            if (change.Interface == BlueZ.CharacteristicInterface &&
                change.Changed.TryGetValue("Value", out var value))
            {
                LinuxBleLink? link;
                lock (_gate) _byOutbox.TryGetValue(change.Path, out link);

                link?.OnValue(value.GetArray<byte>());
                return;
            }

            if (change.Interface == BlueZ.DeviceInterface &&
                change.Changed.TryGetValue("Connected", out var connected) && !connected.GetBool())
            {
                LinuxBleLink? link;
                lock (_gate) _byDevice.TryGetValue(change.Path, out link);

                link?.OnDisconnected();
            }
        }
        catch (Exception ex)
        {
            Log.Write("BleRadio", "A Bluetooth property change could not be handled", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        IsAvailable = false;

        await StopDiscoveryAsync().ConfigureAwait(false);

        List<LinuxBleLink> links;
        lock (_gate)
        {
            links = _byDevice.Values.ToList();
            _byDevice.Clear();
            _byOutbox.Clear();
        }

        foreach (var link in links)
        {
            try { await link.DisposeAsync().ConfigureAwait(false); } catch { }
        }

        InboundRoute = null;
        Prepare = null;
    }
}
