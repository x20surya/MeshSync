using System.Text;
using CoreLib.Diagnostics;
using CoreLib.Identity;
using CoreLib.Transport;
using Tmds.DBus.Protocol;

namespace DesktopCore.Bluetooth;

/// <summary>
/// The central half of the Bluetooth tier on Linux: scan for the service, connect, and hold the
/// link open.
///
/// <para><b>Why this is the half that came first.</b> Being the central needs only D-Bus calls
/// <em>out</em> to BlueZ. Being the peripheral means exporting a GATT tree for BlueZ to call
/// back into, which is a different and larger shape. <c>BleRoleRules</c> is built for exactly
/// this: a device that cannot advertise is always the central, so a Linux box with only this
/// half is a legitimate member of the mesh rather than a broken one - it simply obliges the
/// phone to take the peripheral role, which Android can do.</para>
///
/// <para>All the framing, fragmentation, acknowledgement and identity logic is CoreLib's and
/// shared with Windows and Android. What is here is the GATT plumbing under it.</para>
/// </summary>
public sealed class LinuxBleCentral : ITransportConnection
{
    private readonly BlueZ _bluez;
    private readonly BleReassembler _reassembler = new(BleProtocol.MaxPayloadBytes);
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private string? _devicePath;
    private string? _inboxPath;
    private string? _outboxPath;
    private int _usablePayload = BleFragmenter.MinimumMtuPayload;
    private bool _linkUp;
    private bool _discovering;
    private string? _adapterPath;
    private DateTime _linkUpAtUtc = DateTime.MinValue;

    /// <summary>
    /// Devices that connected but never agreed a session, and when to stop ignoring them.
    ///
    /// <para>The service UUID is the same for every install of this app, so a scan finds every
    /// nearby Mesh Sync device including ones in somebody else's mesh. Those are refused, as they
    /// must be - but without remembering the refusal the scan finds them again four seconds later
    /// and the radio spends its life connecting to a stranger it will never be allowed to talk
    /// to.</para>
    /// </summary>
    private readonly Dictionary<string, DateTime> _rejected = new(StringComparer.Ordinal);

    private static readonly TimeSpan RejectionCooldown = TimeSpan.FromMinutes(5);

    /// <summary>How long a link may sit without the peer proving who it is.</summary>
    private static readonly TimeSpan IdentityGrace = TimeSpan.FromSeconds(12);
    private byte _messageCounter;
    private EphemeralKeyPair? _ephemeral;
    private PeerSession? _peer;
    private DateTime _lastHeard = DateTime.MinValue;
    private bool _disposed;

    public event EventHandler<PayloadReceivedEventArgs>? PayloadReceived;
    public event EventHandler? ConnectionClosed;
    public event EventHandler<PeerIdentifiedEventArgs>? PeerIdentified;

    /// <summary>The peer asked for Wi-Fi to be raised, because Bluetooth cannot carry what it has.</summary>
    public event EventHandler? WiFiRequested;

    public string? LocalPublicKey { get; set; }
    public string? LocalDeviceName { get; set; }
    public string? LocalMeshName { get; set; }

    /// <summary>Authorises the peer and agrees the key, in one step, exactly as TCP does.</summary>
    public Func<string, string, string, EphemeralKeyPair, PeerSession?>? OpenSession { get; set; }

    public PeerSession? Peer => Volatile.Read(ref _peer);
    public string? RemoteDeviceName { get; private set; }
    public string RemoteFingerprint { get; private set; } = string.Empty;

    public bool IsConnected => _peer != null && _inboxPath != null;

    private LinuxBleCentral(BlueZ bluez) => _bluez = bluez;

    public static async Task<LinuxBleCentral?> TryCreateAsync()
    {
        var bluez = await BlueZ.TryConnectAsync().ConfigureAwait(false);
        if (bluez == null) return null;

        var (present, _, _, detail) = await BlueZCapability.ProbeAsync(bluez).ConfigureAwait(false);
        if (!present)
        {
            Log.Write("BleCentral", $"Bluetooth is unusable: {detail}.");
            bluez.Dispose();
            return null;
        }

        return new LinuxBleCentral(bluez);
    }

    // ──────────────────────────────── scanning

    /// <summary>
    /// Scans until a device advertising the mesh service turns up, connects, and stays connected.
    ///
    /// Restarted rather than abandoned when a link drops: a Bluetooth link that comes and goes is
    /// the normal case, not the failure case, and HANDOFF records one dropping about twice a
    /// minute for reasons below this code.
    /// </summary>
    public async Task RunAsync(string adapterPath, CancellationToken cancellationToken)
    {
        _adapterPath = adapterPath;
        await _bluez.WatchPropertiesAsync(OnPropertyChanged).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // _linkUp, not IsConnected. The GATT link comes up first and the session is
                // agreed on it afterwards; testing for the session here tore down the very link
                // the peer's hello was about to arrive on, once every cycle, forever.
                if (!_linkUp) await FindAndConnectAsync(adapterPath, cancellationToken).ConfigureAwait(false);
                else await HeartbeatAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Write("BleCentral", "The scan loop failed", ex);
                await DropAsync().ConfigureAwait(false);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task FindAndConnectAsync(string adapterPath, CancellationToken cancellationToken)
    {
        // Filtered on our service UUID so the radio is not woken for every beacon in the room,
        // and on "le" so classic Bluetooth devices never appear at all.
        try
        {
            if (_discovering) { /* already scanning */ }
            else await _bluez.CallAsync(adapterPath, BlueZ.AdapterInterface, "SetDiscoveryFilter", "a{sv}", (ref MessageWriter writer) =>
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

            if (!_discovering)
            {
                await _bluez.CallAsync(adapterPath, BlueZ.AdapterInterface, "StartDiscovery").ConfigureAwait(false);
                _discovering = true;
                Log.Write("BleCentral", "Scanning for the mesh service.");
            }
        }
        catch (Exception ex)
        {
            // Another client may already have the adapter scanning, which is fine and is not
            // worth saying twice a minute.
            _discovering = true;
            Log.Write("BleCentral", $"Discovery could not be started: {ex.Message}");
        }

        var objects = await _bluez.GetObjectsAsync().ConfigureAwait(false);

        // Anything advertising the mesh service, but only if it is actually there.
        //
        // A phone rotates its LE address for privacy, and BlueZ keeps a device object for every
        // address it has ever seen - each one still carrying the service UUID it advertised at
        // the time. Taking the first match means dialling an address that stopped existing
        // minutes ago, which is why nine connect attempts in ten never resolved a GATT tree.
        //
        // RSSI is the discriminator: BlueZ publishes it only while a device is being seen in the
        // current discovery session, and drops it when the device goes away. A cached ghost has
        // none. Strongest signal wins, so the nearest live radio is preferred over a weak one.
        var candidate = objects
            .Where(o => o.Has(BlueZ.DeviceInterface) &&
                        o.Strings(BlueZ.DeviceInterface, "UUIDs")
                         .Any(u => string.Equals(u, BleProtocol.ServiceUuid.ToString("D"),
                                                 StringComparison.OrdinalIgnoreCase)))
            .Where(o => o.Bool(BlueZ.DeviceInterface, "Connected") ||
                        o.Property(BlueZ.DeviceInterface, "RSSI") != null)
            .Where(o => !InCooldown(o.Path))
            .OrderByDescending(o => o.Bool(BlueZ.DeviceInterface, "Connected"))
            .ThenByDescending(o => Rssi(o))
            .FirstOrDefault();

        if (candidate == null) return;

        Log.Write("BleCentral", $"Found {candidate.String(BlueZ.DeviceInterface, "Alias") ?? candidate.Path}; connecting.");

        if (!candidate.Bool(BlueZ.DeviceInterface, "Connected"))
        {
            await _bluez.CallAsync(candidate.Path, BlueZ.DeviceInterface, "Connect").ConfigureAwait(false);
        }

        _devicePath = candidate.Path;
        RemoteDeviceName = candidate.String(BlueZ.DeviceInterface, "Alias");

        await ResolveCharacteristicsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Finds the two characteristics under the connected device and subscribes to notifications.</summary>
    private async Task ResolveCharacteristicsAsync(CancellationToken cancellationToken)
    {
        if (_devicePath == null) return;

        // BlueZ publishes the GATT tree only once it has resolved services, which is not
        // immediate after Connect returns.
        for (int attempt = 0; attempt < 20 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            var objects = await _bluez.GetObjectsAsync().ConfigureAwait(false);

            var service = objects.FirstOrDefault(o =>
                o.Path.StartsWith(_devicePath, StringComparison.Ordinal) &&
                o.Has(BlueZ.ServiceInterface) &&
                string.Equals(o.String(BlueZ.ServiceInterface, "UUID"),
                              BleProtocol.ServiceUuid.ToString("D"), StringComparison.OrdinalIgnoreCase));

            if (service != null)
            {
                _inboxPath = FindCharacteristic(objects, service.Path, BleProtocol.InboxCharacteristicUuid);
                _outboxPath = FindCharacteristic(objects, service.Path, BleProtocol.OutboxCharacteristicUuid);

                if (_inboxPath != null && _outboxPath != null)
                {
                    await SubscribeAsync().ConfigureAwait(false);

                    // The GATT link is open; the session is not. The loop must leave it alone
                    // long enough for the peer to answer, but no longer - see HeartbeatAsync.
                    _linkUp = true;
                    _lastHeard = DateTime.UtcNow;
                    _linkUpAtUtc = DateTime.UtcNow;

                    await SendHelloAsync().ConfigureAwait(false);
                    return;
                }
            }

            try { await Task.Delay(500, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        Log.Write("BleCentral", "That device never published the mesh service; forgetting it.");

        // Removed from BlueZ outright, not just disconnected. Left in place it keeps its cached
        // UUIDs and is picked again on the next sweep, forever.
        string? stale = _devicePath;
        await DropAsync().ConfigureAwait(false);

        if (stale != null && _adapterPath != null)
        {
            try
            {
                await _bluez.CallAsync(_adapterPath, BlueZ.AdapterInterface, "RemoveDevice", "o",
                    (ref MessageWriter w) => w.WriteObjectPath(stale)).ConfigureAwait(false);
            }
            catch (Exception ex) { Log.Write("BleCentral", $"Could not forget it: {ex.GetType().Name}"); }
        }
    }

    /// <summary>
    /// Forgets every refusal.
    ///
    /// <para>Called when the set of paired devices changes. A device that was refused a moment ago
    /// because it had not been confirmed yet is exactly the device being confirmed now, and
    /// leaving it in cooldown means waiting out five minutes for a link that should come straight
    /// up. Clearing the lot is right: a device from another mesh will simply be refused again on
    /// the next sweep and cost one connection.</para>
    /// </summary>
    public void ForgetRejections()
    {
        if (_rejected.Count == 0) return;

        _rejected.Clear();
        Log.Write("BleCentral", "The paired devices changed; giving every device another try.");
    }

    private bool InCooldown(string path)
    {
        if (!_rejected.TryGetValue(path, out var until)) return false;
        if (DateTime.UtcNow < until) return true;

        _rejected.Remove(path);
        return false;
    }

    /// <summary>Signal strength, or the weakest possible value when the device is not being seen.</summary>
    private static int Rssi(BlueZObject device)
    {
        var value = device.Property(BlueZ.DeviceInterface, "RSSI");
        try { return value?.GetInt16() ?? short.MinValue; } catch { return short.MinValue; }
    }

    private static string? FindCharacteristic(List<BlueZObject> objects, string servicePath, Guid uuid) =>
        objects.FirstOrDefault(o =>
            o.Path.StartsWith(servicePath, StringComparison.Ordinal) &&
            o.Has(BlueZ.CharacteristicInterface) &&
            string.Equals(o.String(BlueZ.CharacteristicInterface, "UUID"),
                          uuid.ToString("D"), StringComparison.OrdinalIgnoreCase))?.Path;

    private async Task SubscribeAsync()
    {
        await _bluez.CallAsync(_outboxPath!, BlueZ.CharacteristicInterface, "StartNotify").ConfigureAwait(false);

        // BlueZ reports the negotiated MTU per characteristic once the link is up. Without it the
        // fragmenter would keep to the 23-byte floor and every payload would take five times as
        // many round trips as it needs.
        try
        {
            var mtu = await _bluez.GetPropertyAsync(_inboxPath!, BlueZ.CharacteristicInterface, "MTU")
                .ConfigureAwait(false);

            int negotiated = (int)mtu.GetUInt16();
            _usablePayload = Math.Max(BleFragmenter.MinimumMtuPayload, BleProtocol.UsablePayload(negotiated));
            Log.Write("BleCentral", $"Negotiated MTU {negotiated}; {_usablePayload} bytes per chunk.");
        }
        catch
        {
            _usablePayload = BleFragmenter.MinimumMtuPayload;
        }

        Log.Write("BleCentral", "Subscribed to the peer's outbox.");
    }

    // ──────────────────────────────── frames

    private void OnPropertyChanged(PropertyChange change)
    {
        try
        {
            if (change.Interface == BlueZ.CharacteristicInterface &&
                change.Path == _outboxPath &&
                change.Changed.TryGetValue("Value", out var value))
            {
                OnFrame(value.GetArray<byte>());
                return;
            }

            if (change.Interface == BlueZ.DeviceInterface && change.Path == _devicePath &&
                change.Changed.TryGetValue("Connected", out var connected) && !connected.GetBool())
            {
                Log.Write("BleCentral", "The peer disconnected.");
                _ = DropAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Write("BleCentral", "A Bluetooth property change could not be handled", ex);
        }
    }

    private void OnFrame(byte[] frame)
    {
        _lastHeard = DateTime.UtcNow;

        // Told apart by length alone, which is the whole Bluetooth framing rule: two bytes is
        // control, four is an acknowledgement, five or more is a data chunk, and a leading zero
        // marks the one extended frame there is.
        if (BleProtocol.TryParseControl(frame, out byte control))
        {
            if (control == BleProtocol.ControlPing) _ = SendRawAsync(BleProtocol.BuildControl(BleProtocol.ControlPong));
            else if (control == BleProtocol.ControlWakeWiFi && _peer != null) WiFiRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (BleProtocol.TryParseExtended(frame, out byte kind, out byte[] payload))
        {
            if (kind == BleProtocol.ExtendedHello) HandleHello(payload);
            return;
        }

        if (BleProtocol.TryParseAck(frame, out _, out _)) return;

        if (frame.Length >= BleFragmenter.HeaderSize)
        {
            byte[]? whole = _reassembler.Accept(frame);
            if (whole == null) return;

            PayloadReceived?.Invoke(this, new PayloadReceivedEventArgs
            {
                EncryptedPayload = whole,
                Fingerprint = RemoteFingerprint,
            });
        }
    }

    private void HandleHello(byte[] payload)
    {
        if (!BleProtocol.TryParseHelloPayload(payload, out string publicKey, out string deviceName,
                                              out string meshName, out string ephemeralKey))
        {
            Log.Write("BleCentral", "A hello arrived that could not be parsed.");
            return;
        }

        if (_ephemeral == null || OpenSession == null) return;

        var session = OpenSession(publicKey, deviceName, ephemeralKey, _ephemeral);
        if (session == null)
        {
            Log.Write("BleCentral",
                $"{deviceName} is not in this mesh. Ignoring it for {RejectionCooldown.TotalMinutes:F0} minutes.");

            if (_devicePath != null) _rejected[_devicePath] = DateTime.UtcNow + RejectionCooldown;

            _ = DropAsync();
            return;
        }

        Volatile.Write(ref _peer, session);
        RemoteDeviceName = string.IsNullOrWhiteSpace(deviceName) ? RemoteDeviceName : deviceName;
        RemoteFingerprint = session.Fingerprint;
        _lastHeard = DateTime.UtcNow;

        Log.Write("BleCentral", $"Bluetooth link up to \"{RemoteDeviceName}\" ({DeviceIdentity.Shorten(RemoteFingerprint)}).");

        PeerIdentified?.Invoke(this, new PeerIdentifiedEventArgs
        {
            DeviceName = RemoteDeviceName ?? "",
            PublicKey = publicKey,
            Fingerprint = RemoteFingerprint,
            MeshName = meshName,
        });
    }

    // ──────────────────────────────── sending

    private async Task SendHelloAsync()
    {
        if (LocalPublicKey == null) return;

        _ephemeral?.Dispose();
        _ephemeral = EphemeralKeyPair.Create();

        byte[] frame = BleProtocol.BuildExtended(BleProtocol.ExtendedHello,
            BleProtocol.BuildHelloPayload(LocalPublicKey, LocalDeviceName, LocalMeshName, _ephemeral.PublicKey));

        await SendRawAsync(frame).ConfigureAwait(false);
        Log.Write("BleCentral", "Announced this device over Bluetooth.");
    }

    private async Task HeartbeatAsync()
    {
        // A device that has not proved who it is inside the grace period is not one of ours.
        // Almost always it is a Mesh Sync device in somebody else's mesh: it advertises the same
        // service, so it is found, and it refuses the session, as it should. Dropping it and
        // remembering it is what stops the radio going back to it every four seconds.
        if (_peer == null && DateTime.UtcNow - _linkUpAtUtc > IdentityGrace)
        {
            string name = RemoteDeviceName ?? _devicePath ?? "a device";
            Log.Write("BleCentral", $"{name} never agreed a session; it belongs to another mesh. Ignoring it for {RejectionCooldown.TotalMinutes:F0} minutes.");

            if (_devicePath != null) _rejected[_devicePath] = DateTime.UtcNow + RejectionCooldown;

            await DropAsync().ConfigureAwait(false);
            return;
        }

        // The peer has to have answered at some point. A link that comes up and stays silent is
        // a peer that is not running the app, and holding it open forever helps nobody.
        if (DateTime.UtcNow - _lastHeard > BleProtocol.PeerTimeout)
        {
            Log.Write("BleCentral", "The peer stopped answering; dropping the link.");
            await DropAsync().ConfigureAwait(false);
            return;
        }

        await SendRawAsync(BleProtocol.BuildControl(BleProtocol.ControlPing)).ConfigureAwait(false);
    }

    /// <summary>Asks the peer to raise Wi-Fi, for something Bluetooth cannot carry.</summary>
    public async Task<bool> RequestWiFiAsync()
    {
        if (!IsConnected) return false;

        await SendRawAsync(BleProtocol.BuildControl(BleProtocol.ControlWakeWiFi)).ConfigureAwait(false);
        return true;
    }

    public async Task SendPayloadAsync(byte[] encryptedPayload, CancellationToken cancellationToken = default)
    {
        if (!IsConnected) throw new InvalidOperationException("No Bluetooth link.");

        byte messageId = BleProtocol.NextMessageId(ref _messageCounter);

        foreach (byte[] chunk in BleFragmenter.Fragment(encryptedPayload, _usablePayload, messageId))
        {
            await SendRawAsync(chunk).ConfigureAwait(false);
        }
    }

    /// <summary>One GATT write. Serialised, because two at once on one characteristic is a stall.</summary>
    private async Task SendRawAsync(byte[] frame)
    {
        string? inbox = _inboxPath;
        if (inbox == null) return;

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _bluez.CallAsync(inbox, BlueZ.CharacteristicInterface, "WriteValue", "aya{sv}", (ref MessageWriter writer) =>
            {
                writer.WriteArray(frame);
                var options = writer.WriteArrayStart(DBusType.DictEntry);
                writer.WriteArrayEnd(options);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Write("BleCentral", "A Bluetooth write failed", ex);
            await DropAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // ──────────────────────────────── teardown

    private async Task DropAsync()
    {
        bool wasConnected = IsConnected;

        _linkUp = false;
        _linkUpAtUtc = DateTime.MinValue;
        _inboxPath = null;
        _outboxPath = null;

        var session = Interlocked.Exchange(ref _peer, null);
        session?.Dispose();       // disposing the key is what makes the traffic unrecoverable

        RemoteFingerprint = string.Empty;

        if (_devicePath != null)
        {
            try { await _bluez.CallAsync(_devicePath, BlueZ.DeviceInterface, "Disconnect").ConfigureAwait(false); }
            catch { /* Already gone is the usual reason to be here. */ }
        }

        _devicePath = null;

        if (wasConnected) ConnectionClosed?.Invoke(this, EventArgs.Empty);
    }

    public Task ConnectAsync(string deviceId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;   // the scan loop owns connecting

    public Task DisconnectAsync() => DropAsync();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _ = DropAsync();
        _ephemeral?.Dispose();
        _writeGate.Dispose();
        _bluez.Dispose();

        PayloadReceived = null;
        ConnectionClosed = null;
        PeerIdentified = null;
        WiFiRequested = null;
    }
}
