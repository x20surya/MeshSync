using CoreLib.Diagnostics;
using CoreLib.Identity;
using CoreLib.Transport;

namespace DesktopCore.Bluetooth;

/// <summary>
/// The peripheral side of a Bluetooth link, once a central has connected to it.
///
/// <para><see cref="LinuxBlePeripheral"/> owns the GATT plumbing - advertising, the exported
/// object tree, and moving bytes. This owns what those bytes mean: the identity exchange, the
/// session key, fragmentation and the heartbeat. They are split because the first half is
/// entirely about D-Bus and the second is entirely about the protocol, and mixing them made the
/// Windows equivalent hard to follow.</para>
/// </summary>
public sealed class LinuxBleServer : IDisposable
{
    private readonly LinuxBlePeripheral _peripheral;
    private readonly BleReassembler _reassembler = new(BleProtocol.MaxPayloadBytes);

    private EphemeralKeyPair? _ephemeral;
    private PeerSession? _peer;
    private byte _messageCounter;
    private DateTime _lastHeard = DateTime.MinValue;
    private bool _disposed;

    public event EventHandler<PayloadReceivedEventArgs>? PayloadReceived;
    public event EventHandler<PeerIdentifiedEventArgs>? PeerIdentified;
    public event EventHandler? WiFiRequested;

    /// <summary>
    /// The inbound link went away.
    ///
    /// <para>The central half has always had this; this half did not, so nothing could tell the
    /// rest of the daemon that the peer it was showing as connected over Bluetooth had gone.</para>
    /// </summary>
    public event EventHandler? ConnectionClosed;

    public string? LocalPublicKey { get; set; }
    public string? LocalDeviceName { get; set; }
    public string? LocalMeshName { get; set; }

    public Func<string, string, string, EphemeralKeyPair, PeerSession?>? OpenSession { get; set; }

    public PeerSession? Peer => Volatile.Read(ref _peer);
    public string RemoteFingerprint { get; private set; } = string.Empty;
    public string? RemoteDeviceName { get; private set; }

    public bool IsConnected => _peer != null && _peripheral.IsSubscribed;

    public LinuxBleServer(LinuxBlePeripheral peripheral)
    {
        _peripheral = peripheral;
        _peripheral.FrameReceived += OnFrame;
        _peripheral.SubscriptionChanged += OnSubscriptionChanged;
    }

    /// <summary>
    /// A central subscribing is the closest thing this side has to a connection.
    ///
    /// The hello goes out immediately: the peripheral cannot know when the central is ready
    /// beyond this, and a hello that crosses the peer's own is harmless because both ends mint
    /// their ephemeral key independently.
    /// </summary>
    private void OnSubscriptionChanged(bool subscribed)
    {
        if (!subscribed) { Drop(); return; }

        _lastHeard = DateTime.UtcNow;
        _ = SendHelloAsync();
    }

    private async Task SendHelloAsync()
    {
        if (LocalPublicKey == null) return;

        _ephemeral?.Dispose();
        _ephemeral = EphemeralKeyPair.Create();

        byte[] frame = BleProtocol.BuildExtended(BleProtocol.ExtendedHello,
            BleProtocol.BuildHelloPayload(LocalPublicKey, LocalDeviceName, LocalMeshName, _ephemeral.PublicKey));

        _peripheral.Notify(frame);
        Log.Write("BleServer", "Announced this device to the connected central.");
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void OnFrame(byte[] frame)
    {
        _lastHeard = DateTime.UtcNow;

        try
        {
            if (BleProtocol.TryParseControl(frame, out byte control))
            {
                if (control == BleProtocol.ControlPing) _peripheral.Notify(BleProtocol.BuildControl(BleProtocol.ControlPong));
                else if (control == BleProtocol.ControlWakeWiFi && _peer != null) WiFiRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (BleProtocol.TryParseExtended(frame, out byte kind, out byte[] payload))
            {
                if (kind == BleProtocol.ExtendedHello) HandleHello(payload);
                return;
            }

            if (BleProtocol.TryParseAck(frame, out _, out _)) return;

            if (frame.Length < BleFragmenter.HeaderSize) return;

            byte[]? whole = _reassembler.Accept(frame);
            if (whole == null) return;

            PayloadReceived?.Invoke(this, new PayloadReceivedEventArgs
            {
                EncryptedPayload = whole,
                Fingerprint = RemoteFingerprint,
            });
        }
        catch (Exception ex)
        {
            Log.Write("BleServer", "A frame could not be handled", ex);
        }
    }

    private void HandleHello(byte[] payload)
    {
        if (!BleProtocol.TryParseHelloPayload(payload, out string publicKey, out string deviceName,
                                              out string meshName, out string ephemeralKey))
        {
            return;
        }

        if (_ephemeral == null || OpenSession == null) return;

        var session = OpenSession(publicKey, deviceName, ephemeralKey, _ephemeral);
        if (session == null)
        {
            Log.Write("BleServer",
                $"Refusing {DeviceIdentity.Shorten(DeviceIdentity.FingerprintOf(publicKey))}: no session could be agreed.");
            Drop();
            return;
        }

        // A second hello on one link would otherwise leak the first key. There is no legitimate
        // reason for one, so the replacement is logged rather than silent, and the key it
        // replaces is disposed - which is what makes the traffic under it unrecoverable. The TCP
        // transport and the Windows radio have both done this all along.
        var previous = Interlocked.Exchange(ref _peer, session);
        if (previous != null)
        {
            Log.Write("BleServer", "A second hello arrived on one link; the earlier session key was discarded.");
            previous.Dispose();
        }
        RemoteDeviceName = string.IsNullOrWhiteSpace(deviceName) ? RemoteDeviceName : deviceName;
        RemoteFingerprint = session.Fingerprint;

        Log.Write("BleServer", $"Peer identified as \"{RemoteDeviceName}\" ({DeviceIdentity.Shorten(RemoteFingerprint)}).");

        PeerIdentified?.Invoke(this, new PeerIdentifiedEventArgs
        {
            DeviceName = RemoteDeviceName ?? "",
            PublicKey = publicKey,
            Fingerprint = RemoteFingerprint,
            MeshName = meshName,
        });
    }

    public Task SendPayloadAsync(byte[] encryptedPayload)
    {
        if (!IsConnected) return Task.CompletedTask;

        byte messageId = BleProtocol.NextMessageId(ref _messageCounter);

        foreach (byte[] chunk in BleFragmenter.Fragment(encryptedPayload, BleFragmenter.MinimumMtuPayload, messageId))
        {
            if (!_peripheral.Notify(chunk)) break;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Asks the central to raise Wi-Fi, for something Bluetooth cannot carry.
    ///
    /// The mirror of the central's own request, so a large item can cross whichever way round
    /// the two devices happen to have arranged themselves.
    /// </summary>
    public bool RequestWiFi() =>
        IsConnected && _peripheral.Notify(BleProtocol.BuildControl(BleProtocol.ControlWakeWiFi));

    /// <summary>Drops the link if the central has gone quiet. Called from the daemon's loop.</summary>
    public void CheckHeartbeat()
    {
        if (_peer == null) return;

        if (DateTime.UtcNow - _lastHeard > BleProtocol.PeerTimeout)
        {
            Log.Write("BleServer", "The central stopped answering; dropping the link.");
            Drop();
        }
    }

    /// <summary>Drops the inbound link, for the arbiter and for a transport preference change.</summary>
    public void Disconnect() => Drop();

    private void Drop()
    {
        var session = Interlocked.Exchange(ref _peer, null);
        session?.Dispose();       // disposing the key is what makes the traffic unrecoverable

        RemoteFingerprint = string.Empty;
        RemoteDeviceName = null;

        if (session != null) ConnectionClosed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _peripheral.FrameReceived -= OnFrame;
        _peripheral.SubscriptionChanged -= OnSubscriptionChanged;

        Drop();
        _ephemeral?.Dispose();

        PayloadReceived = null;
        PeerIdentified = null;
        WiFiRequested = null;
        ConnectionClosed = null;
    }
}
