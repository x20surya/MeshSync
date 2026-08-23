using System.Net;
using System.Text;
using CoreLib;
using CoreLib.Diagnostics;
using CoreLib.Identity;
using CoreLib.Transport;
using DesktopCore.Bluetooth;
using DesktopCore.Clipboard;
using DesktopCore.Platform;

namespace DesktopCore;

/// <summary>
/// The running device: an identity, the Wi-Fi links to every paired peer, and the clipboard.
///
/// <para><b>What this is not.</b> It is not the Linux client. It is the smallest thing that can
/// stand on the mesh as a real device, so that CoreLib meets a phone over a real radio from Linux
/// before a UI is built on top of it. HANDOFF.md records four defects that only hardware found,
/// none of which any test could have; this exists so the same class of defect in the Linux port
/// is found in a console app rather than behind a window.</para>
///
/// <para><b>Both tiers, arbitrated.</b> Wi-Fi carries everything and Bluetooth carries text when
/// there is no network. Which of the two <em>radio</em> halves carries a given peer is settled by
/// <c>BleLinkArbiter</c> over <c>BleRoleRules</c>, exactly as it is on Windows and Android: this
/// file used to start both halves unconditionally and ask nothing, so two devices in range each
/// dialled the other, both links stayed up, and the clipboard crossed twice.</para>
///
/// <para>A machine with no radio, or one whose adapter cannot advertise, is a supported device
/// rather than a broken one - it is simply always the central, and Wi-Fi carries the rest.</para>
/// </summary>
public sealed class Daemon : IDisposable
{
    private readonly Paths _paths;
    private readonly EchoSuppressor _echo = new();
    private readonly ClipboardWatcher _watcher;
    private readonly SemaphoreSlim _dialNow = new(0, 1);
    private bool _disposed;

    /// <summary>What this machine's radio can do, for the arbiter. Central only until proven otherwise.</summary>
    private BleCapability _bleCapability = BleCapability.Central;

    /// <summary>Held so it can be detached again; a lambda cannot be unsubscribed by reference.</summary>
    private Action? _onPeersChanged;

    /// <summary>This device's run, so a tier turned back on later can be started against it.</summary>
    private CancellationToken _lifetime = CancellationToken.None;

    /// <summary>How often paired devices that are not connected are dialled again.</summary>
    private static readonly TimeSpan DialInterval = TimeSpan.FromSeconds(15);

    /// <summary>Long enough for a phone on the same LAN, short enough not to stall the loop.</summary>
    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(6);

    /// <summary>The port this device listens on. Not always the default: two devices on one
    /// machine cannot share a listening port.</summary>
    public int Port { get; }

    public string DataDirectory => _paths.DataDirectory;

    /// <summary>The name announced to peers, and shown in their device lists.</summary>
    public string DeviceName => Mesh.LocalDeviceName;

    public PeerSecurity Security { get; }

    public MeshLinks Mesh { get; }

    public IClipboardBridge ClipboardBridge { get; }

    public SyncActivityLog Activity { get; } = new();

    /// <summary>
    /// Which link is carrying a peer, and whether anything is.
    ///
    /// <para>Shared with the Windows daemon rather than reimplemented. Every screen reads this
    /// and nothing reads a transport directly, which is what stops the sidebar saying "Bluetooth"
    /// while the device list says the same peer has not been seen for twenty minutes.</para>
    /// </summary>
    public LinkState Links { get; } = new();

    /// <summary>Which links this device is allowed to offer, remembered between runs.</summary>
    public TransportSettings Transports { get; }

    /// <summary>Sending and receiving whole files, streamed rather than held in memory.</summary>
    public FileTransferService Files { get; }

    /// <summary>Listing and fetching from a peer's shared folders, and answering theirs.</summary>
    public BrowseService Browse { get; }

    /// <summary>The alarm, for when this computer is the thing that has been lost.</summary>
    public Ringer Ringer { get; } = new();

    /// <summary>Mirrored phone notifications. Memory only, by rule.</summary>
    public MirroredNotifications Notifications { get; } = new();

    /// <summary>The desktop's own notification centre.</summary>
    public DesktopNotifier Notifier { get; } = new();

    /// <summary>
    /// Whether this device draws its own tray icon.
    ///
    /// <para>Off is for the person who has put the Plasma widget in the system tray and does not
    /// want the same mark twice. See <see cref="TraySettings"/>.</para>
    /// </summary>
    public bool TrayIconVisible
    {
        get => TraySettings.IsVisible(_paths.DataDirectory);
        set
        {
            if (value == TrayIconVisible) return;

            TraySettings.SetVisible(_paths.DataDirectory, value);
            TrayIconVisibleChanged?.Invoke(value);
        }
    }

    /// <summary>Raised when the tray icon is turned on or off, so it can appear or go.</summary>
    public event Action<bool>? TrayIconVisibleChanged;

    /// <summary>
    /// Whether a mirrored notification's sender and text go on the session bus.
    ///
    /// <para>Off unless the owner turns it on. See <see cref="TraySettings.ShowsContent"/> for
    /// why the default is the strict one.</para>
    /// </summary>
    public bool ShowNotificationContent
    {
        get => TraySettings.ShowsContent(_paths.DataDirectory);
        set
        {
            if (value == ShowNotificationContent) return;

            TraySettings.SetShowsContent(_paths.DataDirectory, value);
            NotificationContentChanged?.Invoke(value);
        }
    }

    public event Action<bool>? NotificationContentChanged;

    /// <summary>
    /// The Bluetooth tier, or null where there is no usable radio.
    ///
    /// <para>Central only for now: this device scans and connects out, which obliges the peer to
    /// advertise. <c>BleRoleRules</c> is built for that - a device that cannot advertise is always
    /// the central - so it is a supported arrangement rather than a missing half.</para>
    /// </summary>
    public LinuxBleCentral? Ble { get; private set; }

    /// <summary>The peripheral half, where the adapter can advertise. Null where it cannot.</summary>
    public LinuxBleServer? BleServer { get; private set; }

    private LinuxBlePeripheral? _peripheral;
    private BlueZ? _bleBus;
    private BlueZ? _peripheralBus;

    /// <summary>True when a Bluetooth link is up and has agreed a key, either way round.</summary>
    public bool IsBluetoothConnected => Ble?.IsConnected == true || BleServer?.IsConnected == true;

    /// <summary>
    /// True when this particular peer is reachable over Bluetooth, either way round.
    ///
    /// Exists so a device list can say which link a device is on rather than testing Wi-Fi and
    /// calling everything else disconnected.
    /// </summary>
    public bool IsBluetoothConnectedTo(string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint)) return false;

        return (Ble?.IsConnected == true &&
                string.Equals(Ble.RemoteFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            || (BleServer?.IsConnected == true &&
                string.Equals(BleServer.RemoteFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when this peer is reachable at all, over either tier.</summary>
    public bool IsConnectedTo(string fingerprint) =>
        Mesh.IsConnectedTo(fingerprint) || IsBluetoothConnectedTo(fingerprint);

    /// <summary>What this machine's radio can do, for the window to say so.</summary>
    public string BluetoothStatus { get; private set; } = "not started";

    /// <summary>What wraps the device key at rest, or null where nothing can.</summary>
    public IKeyProtector? KeyProtector { get; }

    /// <summary>How the identity is protected on disk, for the Settings page to say plainly.</summary>
    public string KeyProtectionStatus => KeyProtector == null
        ? "not wrapped - readable by anything running as you"
        : $"wrapped by {KeyProtector.Name}";

    private static IKeyProtector? ResolveProtector()
    {
        try
        {
            var pending = Task.Run(SecretServiceKeyProtector.TryCreateAsync);
            return pending.Wait(TimeSpan.FromSeconds(5)) ? pending.Result : null;
        }
        catch (Exception ex)
        {
            Log.Write("Identity", "Could not reach a keyring", ex);
            return null;
        }
    }

    /// <summary>Files that have arrived this session, newest first.</summary>
    public IReadOnlyList<ReceivedFile> ReceivedFiles { get { lock (_receivedGate) return _received.ToList(); } }

    private readonly object _receivedGate = new();
    private readonly List<ReceivedFile> _received = new();

    /// <summary>Devices waiting for a human to compare fingerprints, newest last.</summary>
    public IReadOnlyList<PendingPairing> Pending => Security.PendingPairings;

    /// <summary>
    /// True while a round of dialling is in flight.
    ///
    /// Exists so a UI can say "connecting" rather than "nothing connected" for the several
    /// seconds a dial takes. Those read identically to a user and mean opposite things.
    /// </summary>
    public bool IsDialling { get; private set; }

    public Daemon(Paths paths, int port = TcpTransportConnection.DefaultPort, string? deviceName = null)
    {
        _paths = paths;
        Port = port;

        Transports = new TransportSettings(new FileTransportPreferenceStore(paths.DataDirectory));
        Transports.Changed += _ => ApplyTransportPreference();

        // Resolved before the identity is loaded, because DeviceIdentity rewrites a plaintext
        // key wrapped the moment it is handed a protector - so an existing unwrapped key upgrades
        // itself on this run and costs no re-pair.
        //
        // Task.Run keeps it off whatever context is constructing us, and the timeout is because a
        // locked keyring can sit waiting on a prompt the user may never answer. Falling back to an
        // unwrapped key is worse than wrapping it and far better than failing to start.
        KeyProtector = ResolveProtector();

        Security = PeerSecurity.LoadOrCreate(paths.DataDirectory, KeyProtector);

        // Listens where it was told, but dials a bare address on the standard port. Those are
        // the same number for every real device; they differ only when two of these share a
        // machine, and then dialling its own port would just reach itself.
        Mesh = new MeshLinks(Security, port, peerPort: TcpTransportConnection.DefaultPort)
        {
            LocalDeviceName = deviceName ?? Environment.MachineName
        };
        Mesh.PayloadReceived += OnPayload;
        Mesh.PeerConnected += OnPeerConnected;
        Mesh.PeerDisconnected += OnPeerDisconnected;

        Security.PairingRequested += OnPairingRequested;

        Files = new FileTransferService(Path.Combine(paths.IncomingDirectory, "partial"),
            (fingerprint, contentType, body, token) => Mesh.SendToAsync(fingerprint, contentType, body, token));

        Files.FileReceived += OnFileReceived;
        Files.FileFailed += (name, reason) => Log.Write("Files", $"{name} did not arrive: {reason}.");

        Browse = new BrowseService
        {
            Send = (fingerprint, contentType, body) => Mesh.SendToAsync(fingerprint, contentType, body),
            SendFile = (fingerprint, path) => Files.SendAsync(fingerprint, path),
        };

        // Downloads is shared out of the box on both other platforms, so it is here too.
        string downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) Browse.Shared.Add(downloads, "Downloads");

        ClipboardBridge = ClipboardFactory.Detect();
        _watcher = new ClipboardWatcher(ClipboardBridge);
        _watcher.TextChanged += OnLocalClipboardChangedAsync;
    }

    /// <summary>
    /// The code a phone scans. Same shape the Windows daemon puts in its QR, because the phone
    /// parses one format and a second one would be a second thing to keep in step.
    /// </summary>
    public string PairingUri
    {
        get
        {
            string ip = NetworkUtil.GetLocalLanAddress() ?? "0.0.0.0";
            // The port rides along only when it is not the default, because a peer that reads
            // a bare address dials 45001 - which is right for every real device and wrong only
            // for a second one sharing this machine.
            if (Port != TcpTransportConnection.DefaultPort) ip = $"{ip}:{Port}";
            string mesh = Security.Peers.MeshName ?? "";

            return $"meshsync://pair?ip={Uri.EscapeDataString(ip)}" +
                   $"&key={Uri.EscapeDataString(Security.Identity.PublicKey)}" +
                   (mesh.Length > 0 ? $"&mesh={Uri.EscapeDataString(mesh)}" : "");
        }
    }

    // ──────────────────────────────── lifecycle

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.IncomingDirectory);
        _lifetime = cancellationToken;

        if (Transports.AllowsWiFi)
        {
            await Mesh.StartListeningAsync(cancellationToken).ConfigureAwait(false);
            Log.Write("Daemon", $"Listening on {Port}.");
        }
        else
        {
            Log.Write("Daemon", "Wi-Fi listener not started: the transport preference is Bluetooth only.");
        }

        // A device with nobody to talk to is a device that has just been installed, so the
        // window opens itself rather than making the first run start with a command.
        if (Security.Peers.IsEmpty) Security.Pairing.Open();

        await StartBluetoothAsync(cancellationToken).ConfigureAwait(false);

        _ = Task.Run(() => DialLoopAsync(cancellationToken), CancellationToken.None);
        _ = Task.Run(() => _watcher.RunAsync(cancellationToken), CancellationToken.None);
    }

    /// <summary>
    /// Brings up the Bluetooth tier, if this machine has a radio.
    ///
    /// Failure here is not fatal and deliberately not loud: a desktop with no Bluetooth is a
    /// perfectly ordinary desktop, and Wi-Fi carries everything Bluetooth would have.
    /// </summary>
    private async Task StartBluetoothAsync(CancellationToken cancellationToken)
    {
        if (!Transports.AllowsBle)
        {
            BluetoothStatus = "off - the transport preference is Wi-Fi only";
            Log.Write("Ble", "Bluetooth not started: the transport preference is Wi-Fi only.");
            return;
        }

        try
        {
            _bleBus = await BlueZ.TryConnectAsync().ConfigureAwait(false);
            if (_bleBus == null)
            {
                BluetoothStatus = "no Bluetooth on this machine";
                Log.Write("Ble", "No Bluetooth on this machine; Wi-Fi only.");
                return;
            }

            var (present, canAdvertise, adapterPath, detail) =
                await BlueZCapability.ProbeAsync(_bleBus).ConfigureAwait(false);

            if (!present || adapterPath == null)
            {
                BluetoothStatus = detail;
                Log.Write("Ble", $"Bluetooth unusable: {detail}.");
                return;
            }

            Log.Write("Ble", $"{detail}.");

            // Both halves run at once where the adapter allows it, exactly as the Windows daemon
            // does. Which one carries a given peer is settled per link by BleRoleRules, and a
            // device that can only scan simply never wins the peripheral half.
            if (canAdvertise)
            {
                // Its own connection. BlueZ closes the bus connection outright when it dislikes
                // an exported object tree, and that must not take the scanner down with it.
                _peripheralBus = await BlueZ.TryConnectAsync().ConfigureAwait(false);

                _peripheral = _peripheralBus == null ? null : await LinuxBlePeripheral.TryStartAsync(
                    _peripheralBus, adapterPath, Mesh.LocalDeviceName).ConfigureAwait(false);

                if (_peripheral != null)
                {
                    BleServer = new LinuxBleServer(_peripheral)
                    {
                        LocalPublicKey = Security.Identity.PublicKey,
                        LocalDeviceName = Mesh.LocalDeviceName,
                        LocalMeshName = Security.Peers.MeshName,
                        OpenSession = (peerKey, peerName, peerEphemeral, localEphemeral) =>
                            Security.Authorise(peerKey, peerName)
                                ? Security.OpenSession(peerKey, localEphemeral, peerEphemeral)
                                : null,
                    };

                    BleServer.PayloadReceived += (_, e) => OnRadioPayload(BleServer.Peer, e);
                    BleServer.PeerIdentified += OnRadioPeerIdentified;
                    BleServer.ConnectionClosed += (_, _) => Links.SetBle(IsBluetoothConnected);
                    BleServer.WiFiRequested += (_, _) => RaiseWiFiFor("the peer on the inbound link");
                }
            }

            // Honest rather than optimistic: an adapter that claims it can advertise but whose
            // GATT tree BlueZ then refused cannot advertise, and telling the arbiter otherwise
            // would have both devices agree an arrangement neither can carry out.
            _bleCapability = _peripheral != null ? BleCapability.Both : BleCapability.Central;

            BluetoothStatus = _peripheral != null ? "scanning and advertising" : "scanning";

            Ble = await LinuxBleCentral.TryCreateAsync().ConfigureAwait(false);
            if (Ble == null)
            {
                // Never silent. A scanner that failed to start looks exactly like one that
                // started and found nothing, and telling those apart from the log is the whole
                // difference between a five-minute diagnosis and an afternoon.
                BluetoothStatus = "the scanner could not be started";
                Log.Write("Ble", "The Bluetooth scanner could not be started; this device can only be connected to.");
                return;
            }

            Ble.LocalPublicKey = Security.Identity.PublicKey;
            Ble.LocalDeviceName = Mesh.LocalDeviceName;
            Ble.LocalMeshName = Security.Peers.MeshName;

            Ble.OpenSession = (peerKey, peerName, peerEphemeral, localEphemeral) =>
                Security.Authorise(peerKey, peerName)
                    ? Security.OpenSession(peerKey, localEphemeral, peerEphemeral)
                    : null;

            Ble.PayloadReceived += (_, e) => OnRadioPayload(Ble.Peer, e);
            Ble.PeerIdentified += OnRadioPeerIdentified;
            Ble.ConnectionClosed += (_, _) => Links.SetBle(IsBluetoothConnected);

            // A peer asking for Wi-Fi is asking because it has something Bluetooth cannot carry.
            // This used to answer with a log line saying Wi-Fi was already up here, which is an
            // assumption rather than a check: the listener being up is not a socket to that peer.
            Ble.WiFiRequested += (_, _) => RaiseWiFiFor("the peer on the outbound link");

            // The gate this end never had. BleRoleRules decides which device advertises and which
            // connects; without asking it, two devices in range each dial the other.
            Ble.ShouldDial = ShouldDialOverBluetooth;

            // A device refused for not being paired is the same device being paired seconds
            // later, and it must not sit out its cooldown after that.
            _onPeersChanged = () => Ble?.ForgetRejections();
            Security.Peers.Changed += _onPeersChanged;

            // The fault is observed rather than discarded. A bare Task.Run swallows whatever the
            // scan loop throws on its way up, so a scanner that died on its first D-Bus call is
            // indistinguishable from one quietly finding nothing - which is precisely how long
            // this took to see.
            _ = Task.Run(() => Ble.RunAsync(adapterPath, cancellationToken), CancellationToken.None)
                    .ContinueWith(t =>
                    {
                        if (t.Exception != null)
                            Log.Write("Ble", "The Bluetooth scan loop stopped", t.Exception.GetBaseException());
                    }, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            Log.Write("Ble", "Bluetooth could not be started", ex);
        }
    }

    /// <summary>
    /// Whether this device should be the one connecting out over Bluetooth, right now.
    ///
    /// <para>Three questions, and every one has to answer yes. Is the radio allowed at all; is
    /// either half already holding a link, in which case dialling can only produce a second one;
    /// and does <c>BleRoleRules</c> make this device the central for some device it is paired
    /// with. The Windows daemon has asked the same three all along.</para>
    /// </summary>
    private bool ShouldDialOverBluetooth()
    {
        if (!Transports.AllowsBle) return false;
        if (Ble?.IsConnected == true || BleServer?.IsConnected == true) return false;

        var peers = Security.Peers.Peers.Select(peer => peer.Fingerprint).ToList();

        // The pairing window is part of the decision, not a detail of it. With nothing paired
        // there is no peer to arbitrate a role with, so the rule answers no - and this machine
        // cannot advertise, so it would then be neither scanning nor advertising and could not be
        // paired over Bluetooth at all. Observed: the only peer was forgotten, the phone still
        // trusted this laptop, knocked, and was never heard.
        bool dial = BleLinkArbiter.ShouldDialAnyPeer(
            Security.Identity.Fingerprint, _bleCapability, peers, Security.Pairing.IsOpen);

        // Said once per change of mind, not once per round. A device that has decided not to scan
        // looks exactly like a device whose Bluetooth is broken, and the whole reason this gate
        // was missing for so long is that nothing anywhere said which of the two was happening.
        //
        // The two silent cases are kept apart deliberately. "Not scanning because nothing is
        // paired" and "not scanning because the peer is the one that dials" produce identical
        // silence and are very different problems, and saying the second when the first is true
        // sends a reader looking for an arbitration bug that is not there.
        if (dial != _loggedDialDecision)
        {
            _loggedDialDecision = dial;

            Log.Write("Ble",
                dial ? $"This device takes the central half ({_bleCapability}); scanning."
                : peers.Count == 0 ? "Nothing is paired and the pairing window is shut, so there is nothing to scan for."
                : $"The peer opens the link for every paired device ({_bleCapability}); waiting to be connected to rather than scanning.");
        }

        return dial;
    }

    private bool? _loggedDialDecision;

    /// <summary>
    /// Drops whichever Bluetooth link the role rule says should not exist.
    ///
    /// <para>Only ever does anything when both halves are holding the same peer at once. The dial
    /// gate makes that rare but cannot make it impossible: two devices can dial each other inside
    /// the same moment, before either has a link to notice. Both ends compute the same answer
    /// from fingerprints they have already exchanged, so exactly one link is dropped rather than
    /// both or neither. Android repairs the same race the same way.</para>
    /// </summary>
    private void ResolveBleCollision(string peerFingerprint)
    {
        if (string.IsNullOrEmpty(peerFingerprint)) return;
        if (Ble?.IsConnected != true || BleServer?.IsConnected != true) return;

        if (!string.Equals(Ble.RemoteFingerprint, peerFingerprint, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(BleServer.RemoteFingerprint, peerFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return;   // two links, but to two different peers, which is exactly what a mesh is
        }

        var keep = BleLinkArbiter.KeepFor(Security.Identity.Fingerprint, _bleCapability, peerFingerprint);

        if (keep == BleRole.Peripheral)
        {
            Log.Write("Ble", "Two Bluetooth links to one peer; keeping the one it opened.");
            _ = Ble.DisconnectAsync();
        }
        else
        {
            Log.Write("Ble", "Two Bluetooth links to one peer; keeping the one this device opened.");
            BleServer.Disconnect();
        }
    }

    /// <summary>
    /// A peer has something Bluetooth cannot carry, so go to it.
    ///
    /// <para>The listener here is always up, which is what this used to answer with - but a
    /// listener is not a socket to the device that asked. It may have just changed address, or
    /// never had one recorded here at all. Dialling is the useful answer and it is the one
    /// Windows gives.</para>
    /// </summary>
    private void RaiseWiFiFor(string who)
    {
        if (!Transports.AllowsWiFi)
        {
            Log.Write("Ble", $"{who} asked for Wi-Fi, but this device is set to Bluetooth only.");
            return;
        }

        Log.Write("Ble", $"{who} asked for Wi-Fi; dialling now.");
        NudgeDial();
    }

    /// <summary>
    /// Tells a peer where this device is reachable, over the radio link rather than the socket.
    ///
    /// <para>Sealed with the key this link agreed, and sent once the hello has crossed because
    /// before that there is no key to seal it with. This is the only address announcement a
    /// device paired over Bluetooth alone will ever receive: without it, it knows this machine
    /// only as a radio and has no route to a socket, so an image copied here has nowhere to go
    /// and a lease change strands it with no way back short of a rescan.</para>
    /// </summary>
    private void AnnounceAddressOverRadio(PeerSession? session, Func<byte[], Task> send)
    {
        if (session == null) return;

        string? address = NetworkUtil.GetLocalLanAddress();
        if (string.IsNullOrEmpty(address)) return;

        if (Port != TcpTransportConnection.DefaultPort) address = $"{address}:{Port}";

        byte[]? payload = session.Encrypt(SyncContent.Address, Encoding.UTF8.GetBytes(address));
        if (payload == null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await send(payload).ConfigureAwait(false);
                Log.Write("Ble", $"Announced this device at {address} over Bluetooth.");
            }
            catch (Exception ex)
            {
                // Informational. A failure costs nothing until the address changes.
                Log.Write("Ble", "Could not announce this device's address over Bluetooth", ex);
            }
        });
    }

    /// <summary>Starts or stops a tier when the preference changes, without a restart.</summary>
    private void ApplyTransportPreference()
    {
        try
        {
            if (Transports.AllowsWiFi)
            {
                _ = Mesh.StartListeningAsync(_lifetime);
                NudgeDial();
            }
            else
            {
                Mesh.StopListening();
                Mesh.DisconnectAll();
            }

            if (Transports.AllowsBle)
            {
                if (Ble == null && BleServer == null) _ = StartBluetoothAsync(_lifetime);
            }
            else
            {
                _ = Ble?.DisconnectAsync();
                BleServer?.Disconnect();
                BluetoothStatus = "off - the transport preference is Wi-Fi only";
            }

            Links.SetWiFi(Mesh.IsConnectedToAny);
            Links.SetBle(IsBluetoothConnected);
        }
        catch (Exception ex)
        {
            Log.Write("Daemon", "Could not apply the transport preference", ex);
        }
    }

    /// <summary>
    /// A peer proved who it is over the radio.
    ///
    /// Bluetooth carries no hello of the kind TCP has, so the sender is identified by which key
    /// authenticates the payload - which is the same test the socket path applies anyway.
    /// </summary>
    private void OnRadioPeerIdentified(object? sender, PeerIdentifiedEventArgs e)
    {
        Security.Peers.NoteSeen(e.Fingerprint, null, e.DeviceName);
        Security.Peers.AdoptMeshName(e.MeshName);
        Links.SetBle(true, e.DeviceName);
        Log.Write("Ble", $"{e.DeviceName} is in range over Bluetooth.");

        if (ReferenceEquals(sender, Ble) && Ble is { } central)
            AnnounceAddressOverRadio(central.Peer, payload => central.SendPayloadAsync(payload));
        else if (ReferenceEquals(sender, BleServer) && BleServer is { } server)
            AnnounceAddressOverRadio(server.Peer, payload => server.SendPayloadAsync(payload));

        ResolveBleCollision(e.Fingerprint);
    }

    private void OnRadioPayload(PeerSession? session, PayloadReceivedEventArgs e)
    {
        if (session == null) return;

        if (!session.TryDecrypt(e.EncryptedPayload, out var decrypted))
        {
            Log.Write("Ble", "Dropped a payload that does not authenticate under this link's key.");
            return;
        }

        OnPayload(this, new MeshPayloadEventArgs
        {
            Peer = decrypted.Peer,
            ContentType = decrypted.ContentType,
            Body = decrypted.Body,
            Via = "Bluetooth",
        });
    }

    /// <summary>Dials paired devices that are not connected, and can be nudged to go early.</summary>
    private async Task DialLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // The inbound radio link has no loop of its own, so its liveness check runs
                // here. It was written to be called from this loop and never was, which left a
                // peripheral link whose central had walked away showing as connected forever.
                BleServer?.CheckHeartbeat();

                if (!Security.Peers.IsEmpty && Transports.AllowsWiFi)
                {
                    IsDialling = true;
                    try
                    {
                        await Mesh.ConnectToAllAsync(DialTimeout, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        IsDialling = false;
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Write("Daemon", "The dial loop failed", ex);
            }

            try
            {
                // Waits out the interval unless something asks for a dial sooner - which
                // confirming a pairing does, so a freshly confirmed device connects now rather
                // than after whatever is left of the interval.
                await _dialNow.WaitAsync(DialInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Asks the dial loop to run now rather than at the next interval.</summary>
    public void NudgeDial()
    {
        try { _dialNow.Release(); }
        catch (SemaphoreFullException) { /* One pending nudge is as good as several. */ }
    }

    // ──────────────────────────────── sending

    /// <summary>
    /// Sends text to every connected device.
    ///
    /// Echo suppression is deliberately not consulted: this is what an explicit
    /// <c>send</c> means, and refusing to send the same string twice would make the command
    /// useless for exactly the back-to-back testing it exists for.
    /// </summary>
    public async Task<int> SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        byte[] body = Encoding.UTF8.GetBytes(text);
        int sent = await BroadcastAsync(SyncContent.Text, body, cancellationToken).ConfigureAwait(false);

        if (sent > 0) Activity.Record(SyncDirection.Sent, SyncItemKind.Text, body.Length, text);
        return sent;
    }

    /// <summary>
    /// Sends over every tier that is up, once per peer.
    ///
    /// <para>Wi-Fi first because it carries anything; Bluetooth as well when a peer is only
    /// reachable that way, which is the whole point of holding the radio link open.</para>
    ///
    /// <para><b>Once per peer, not once per link.</b> The two radio halves can hold two different
    /// peers, and then sending over both is exactly right. They can equally hold the <em>same</em>
    /// peer, one link in each direction, and then sending over both delivers the clipboard twice.
    /// Deduplicating on the fingerprint covers both cases; picking one link, as the Windows
    /// daemon does, would only ever have covered the second.</para>
    /// </summary>
    private async Task<int> BroadcastAsync(byte contentType, byte[] body, CancellationToken cancellationToken)
    {
        int sent = await Mesh.BroadcastAsync(contentType, body, cancellationToken).ConfigureAwait(false);

        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        sent += await SendOverRadioAsync(Ble?.Peer, Ble?.RemoteFingerprint, reached,
            payload => Ble!.SendPayloadAsync(payload, cancellationToken), contentType, body).ConfigureAwait(false);

        sent += await SendOverRadioAsync(BleServer?.Peer, BleServer?.RemoteFingerprint, reached,
            payload => BleServer!.SendPayloadAsync(payload), contentType, body).ConfigureAwait(false);

        return sent;
    }

    /// <summary>
    /// Sends over one radio link, unless that peer has already been reached this round.
    ///
    /// Skipping a peer reachable more than one way is not an optimisation: sending twice would
    /// deliver the clipboard twice, and the echo suppressor is on the sending side rather than
    /// the receiving one.
    /// </summary>
    private async Task<int> SendOverRadioAsync(PeerSession? session, string? fingerprint,
                                               HashSet<string> reached,
                                               Func<byte[], Task> send, byte contentType, byte[] body)
    {
        if (session == null || string.IsNullOrEmpty(fingerprint)) return 0;
        if (Mesh.IsConnectedTo(fingerprint)) return 0;
        if (!reached.Add(fingerprint)) return 0;

        try
        {
            byte[]? payload = session.Encrypt(contentType, body);
            if (payload == null) return 0;

            await send(payload).ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex)
        {
            Log.Write("Ble", "Sending over Bluetooth failed", ex);
            return 0;
        }
    }

    /// <summary>
    /// Joins a mesh from a pairing code, which is the other half of showing one.
    ///
    /// <para>The Android client does exactly this after a scan, and it has to exist here too or
    /// laptop-to-laptop is impossible: two devices that can only be joined and never join have
    /// no way to reach each other. The phone is simply the usual scanner, not the only one.</para>
    ///
    /// <para>The key is validated before anything is stored. A code that will not parse is
    /// refused here rather than producing a link that connects and then fails every
    /// decryption.</para>
    /// </summary>
    public (bool Ok, string Message) Join(string pairingUri)
    {
        if (!Uri.TryCreate(pairingUri.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "meshsync", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "That is not a meshsync:// pairing code.");
        }

        var query = ParseQuery(uri.Query);

        if (!query.TryGetValue("key", out string? key) || !DeviceIdentity.IsValidPublicKey(key))
        {
            return (false, "That code carries no usable public key.");
        }

        query.TryGetValue("ip", out string? address);
        query.TryGetValue("mesh", out string? mesh);

        // Adopted only if this device has no name of its own, so joining names an unnamed mesh
        // and re-pairing later cannot silently rename it underneath the user.
        Security.Peers.AdoptMeshName(mesh);

        if (!Security.Peers.Trust(key!, name: null, address: address))
        {
            return (false, "That pairing key is not valid.");
        }

        // The other end refuses the first attempt and asks a human to compare fingerprints, so
        // the dial loop is nudged rather than waited out.
        NudgeDial();

        string fingerprint = DeviceIdentity.FingerprintOf(key!);
        return (true, $"Trusting {DeviceIdentity.Shorten(fingerprint)}" +
                      (address != null ? $" at {address}." : ".") +
                      " Now confirm this device on the other screen.");
    }

    /// <summary>
    /// Reads a URI query into a dictionary. Hand-rolled rather than pulling in
    /// <c>HttpUtility</c> for three keys that this code also generates.
    /// </summary>
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;

            result[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }

        return result;
    }

    /// <summary>
    /// Sends a file to one device. Wi-Fi only, streamed in chunks, and hashed on both ends so a
    /// truncated transfer is a failure rather than a file that looks complete.
    /// </summary>
    public async Task<FileSendResult> SendFileAsync(string fingerprint, string path,
                                                    CancellationToken cancellationToken = default)
    {
        await EnsureWiFiToAsync(fingerprint, cancellationToken).ConfigureAwait(false);

        return await Files.SendAsync(fingerprint, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// How long to wait for a peer to raise Wi-Fi after being asked. The Windows daemon's
    /// timeout, and for its reason: the request is not a guarantee, so it is bounded.
    /// </summary>
    private static readonly TimeSpan WiFiWakeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Makes sure there is a socket to this peer, raising Wi-Fi over the radio if there is not.
    ///
    /// <para>A file needs the socket - Bluetooth will not carry one - so a peer reachable only
    /// over the radio used to be simply absent from the list of things a file could be sent to,
    /// with nothing said about why. Asking it to raise Wi-Fi and waiting is what the Windows
    /// daemon does with an image for exactly the same reason.</para>
    /// </summary>
    public async Task<bool> EnsureWiFiToAsync(string fingerprint, CancellationToken cancellationToken = default)
    {
        if (Mesh.IsConnectedTo(fingerprint)) return true;
        if (!Transports.AllowsWiFi) return false;

        // Both directions at once: this device goes to the peer, and the peer is asked to come
        // to this device. Either may be the one that can actually open the socket.
        NudgeDial();

        if (Ble?.IsConnected == true &&
            string.Equals(Ble.RemoteFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            await Ble.RequestWiFiAsync().ConfigureAwait(false);
        }
        else if (BleServer?.IsConnected == true &&
                 string.Equals(BleServer.RemoteFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            BleServer.RequestWiFi();
        }

        DateTime deadline = DateTime.UtcNow + WiFiWakeTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (Mesh.IsConnectedTo(fingerprint)) return true;

            try { await Task.Delay(200, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }

        Log.Write("Daemon",
            $"{DeviceIdentity.Shorten(fingerprint)} did not come up on Wi-Fi within {WiFiWakeTimeout.TotalSeconds:F0}s.");
        return false;
    }

    /// <summary>
    /// Dismisses a mirrored notification here and on the phone it came from.
    ///
    /// Both ways is what makes mirroring feel finished rather than like a second inbox to clear.
    /// </summary>
    public async Task DismissNotificationAsync(string namespacedKey)
    {
        var (fingerprint, key) = MirroredNotifications.Split(namespacedKey);

        Notifications.RemoveByKey(namespacedKey);
        await Notifier.CloseAsync(namespacedKey).ConfigureAwait(false);

        if (fingerprint.Length == 0) return;

        await Mesh.SendToAsync(fingerprint, SyncContent.NotificationDismiss,
            NotificationProtocol.BuildDismiss(key)).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends whatever is on this machine's clipboard right now.
    ///
    /// <para>The watcher already sends a copy the moment it happens, so this is not how the
    /// clipboard normally travels. It exists for the two cases the watcher cannot serve: a
    /// session where the clipboard cannot be watched in the background and is polled or not read
    /// at all, and a deliberate resend of something copied before a device came back.</para>
    ///
    /// <para>The echo suppressor is not consulted. A person asking for this a second time means
    /// it, and refusing on the grounds that it was already sent is the app arguing with them.</para>
    /// </summary>
    public async Task<(bool Ok, string Message)> SendClipboardAsync(CancellationToken cancellationToken = default)
    {
        if (!ClipboardBridge.IsAvailable) return (false, "There is no clipboard on this session.");

        string? text;
        try
        {
            text = await ClipboardBridge.GetTextAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Write("Clipboard", "Could not read the clipboard to send it", ex);
            return (false, "The clipboard could not be read.");
        }

        if (string.IsNullOrEmpty(text)) return (false, "The clipboard is empty.");

        int sent = await SendTextAsync(text, cancellationToken).ConfigureAwait(false);

        return sent > 0
            ? (true, $"Sent to {sent} device(s).")
            : (false, "Nothing is reachable, so nothing was sent.");
    }

    /// <summary>
    /// Answers a mirrored notification, in the app that posted it, on the device it came from.
    ///
    /// <para><b>What actually happens.</b> Nothing here talks to WhatsApp or to Messages. The
    /// phone pulls the reply action the notification already carried - the same action the
    /// notification shade offers - with this text filled into its <c>RemoteInput</c>. The message
    /// goes out through the app, from the account signed in there. That is the whole mechanism,
    /// and it is why this needs no credential and automates no app from the outside.</para>
    ///
    /// <para><b>Over either tier.</b> Two short strings fit a Bluetooth frame, and answering a
    /// message when there is no network is the case this feature is for. So it is not the Wi-Fi
    /// only path a dismissal takes.</para>
    ///
    /// <para>The reply is not recorded. It is a message the user wrote, which makes it at least
    /// as private as the notification that prompted it.</para>
    /// </summary>
    public async Task<(bool Ok, string Message)> ReplyToNotificationAsync(
        string namespacedKey, string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return (false, "There is nothing to send.");

        var (fingerprint, key) = MirroredNotifications.Split(namespacedKey);
        if (fingerprint.Length == 0 || key.Length == 0) return (false, "That notification is not one of ours.");

        var entry = Notifications.Snapshot()
            .FirstOrDefault(e => string.Equals(e.Key, namespacedKey, StringComparison.Ordinal));

        if (entry == null) return (false, "That notification has already gone.");
        if (!entry.CanReply) return (false, $"{entry.AppName} did not offer a reply to that one.");

        byte[] body = NotificationProtocol.BuildReply(key, text);

        if (!await SendToPeerAsync(fingerprint, SyncContent.NotificationReply, body, cancellationToken)
                .ConfigureAwait(false))
        {
            return (false, $"{entry.From} is not reachable right now.");
        }

        // The app name and the device, never the text. A reply is a message.
        Log.Write("Daemon", $"Replied to a {entry.AppName} notification on {entry.From}.");
        return (true, $"Replied on {entry.From}.");
    }

    /// <summary>
    /// Sends one payload to one peer over whichever tier is holding it.
    ///
    /// <para>Wi-Fi first because it carries anything, then whichever radio half has that peer.
    /// <c>Mesh.SendToAsync</c> alone would silently do nothing for a device that is only on
    /// Bluetooth, which is exactly the device a reply is most wanted for.</para>
    /// </summary>
    private async Task<bool> SendToPeerAsync(string fingerprint, byte contentType, byte[] body,
                                             CancellationToken cancellationToken = default)
    {
        if (Mesh.IsConnectedTo(fingerprint) &&
            await Mesh.SendToAsync(fingerprint, contentType, body, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (await SendOneOverRadioAsync(Ble?.Peer, Ble?.RemoteFingerprint, fingerprint, contentType, body,
                payload => Ble!.SendPayloadAsync(payload, cancellationToken)).ConfigureAwait(false))
        {
            return true;
        }

        return await SendOneOverRadioAsync(BleServer?.Peer, BleServer?.RemoteFingerprint, fingerprint,
            contentType, body, payload => BleServer!.SendPayloadAsync(payload)).ConfigureAwait(false);
    }

    private static async Task<bool> SendOneOverRadioAsync(PeerSession? session, string? linkFingerprint,
                                                          string wanted, byte contentType, byte[] body,
                                                          Func<byte[], Task> send)
    {
        if (session == null || string.IsNullOrEmpty(linkFingerprint)) return false;
        if (!string.Equals(linkFingerprint, wanted, StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            byte[]? payload = session.Encrypt(contentType, body);
            if (payload == null) return false;

            await send(payload).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Ble", "Sending over Bluetooth failed", ex);
            return false;
        }
    }

    /// <summary>Clears every mirrored notification, telling each phone as it goes.</summary>
    public async Task DismissAllNotificationsAsync()
    {
        foreach (var entry in Notifications.Snapshot())
        {
            await DismissNotificationAsync(entry.Key).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A file arrived whole and matched its hash. It is moved out of the working directory into
    /// the user's Downloads, which is where they will go looking for it.
    /// </summary>
    private void OnFileReceived(ReceivedFile file)
    {
        string finalPath = file.Path;

        try
        {
            string downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloads);

            // Never overwrite: a second file of the same name is a second file.
            string name = Path.GetFileName(file.Name);
            string candidate = Path.Combine(downloads, name);
            for (int i = 1; File.Exists(candidate); i++)
            {
                candidate = Path.Combine(downloads,
                    $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
            }

            File.Move(file.Path, candidate);
            finalPath = candidate;
        }
        catch (Exception ex)
        {
            Log.Write("Files", $"Could not move {file.Name} into Downloads; it is at {file.Path}", ex);
        }

        lock (_receivedGate) _received.Insert(0, file);

        Activity.Record(SyncDirection.Received, SyncItemKind.File, file.Size, file.Name, finalPath);
        Log.Write("Files", $"Received \"{file.Name}\", {file.Size} bytes, saved to {finalPath}.");

        _ = Notifier.ShowAsync($"file|{file.Name}", "File received",
            $"{file.Name} from {file.PeerFingerprint[..Math.Min(9, file.PeerFingerprint.Length)]}");
    }

    /// <summary>Asks one device to make a noise, or to stop.</summary>
    public Task<bool> RingAsync(string fingerprint, bool on, CancellationToken cancellationToken = default) =>
        Mesh.SendToAsync(fingerprint, SyncContent.Ring, [on ? (byte)1 : (byte)0], cancellationToken);

    private async Task OnLocalClipboardChangedAsync(string text)
    {
        byte[] body = Encoding.UTF8.GetBytes(text);

        // The one decision point for whether local clipboard content goes out. It catches both
        // content this device just applied after receiving it, and the repeat notifications a
        // single copy produces.
        if (!_echo.ShouldSend(body)) return;

        int sent = await BroadcastAsync(SyncContent.Text, body, CancellationToken.None).ConfigureAwait(false);
        if (sent == 0) return;

        Activity.Record(SyncDirection.Sent, SyncItemKind.Text, body.Length, text);
        Log.Write("Daemon", $"Sent {body.Length} bytes of clipboard text to {sent} device(s).");
    }

    // ──────────────────────────────── receiving

    private void OnPayload(object? sender, MeshPayloadEventArgs e)
    {
        string from = e.Peer.Name ?? DeviceIdentity.Shorten(e.Peer.Fingerprint);

        // The file and browse services own their content types outright, and say so by
        // returning true. Doing this before the switch keeps the type numbers in one place.
        if (Files.Handle(e.Peer.Fingerprint, e.ContentType, e.Body)) return;
        if (Browse.Handle(e.Peer.Fingerprint, e.ContentType, e.Body)) return;

        switch (e.ContentType)
        {
            case SyncContent.Text:
                _ = ApplyTextAsync(e.Body, from, e.Via);
                break;

            case SyncContent.Image:
                SaveImage(e.Body, from, e.Via);
                break;

            case SyncContent.Address:
                NoteAnnouncedAddress(e.Peer.Fingerprint, e.Body, from);
                break;

            case SyncContent.Ring:
                // Authenticated by having arrived at all: it opened under this connection's key.
                if (e.Body.Length > 0 && e.Body[0] != 0)
                {
                    Ringer.Start(from);
                    _ = Notifier.ShowAsync("meshsync-ring", "Mesh Sync",
                        $"{from} is looking for this computer.", urgent: true);
                }
                else
                {
                    Ringer.Stop();
                    _ = Notifier.CloseAsync("meshsync-ring");
                }
                break;

            case SyncContent.Notification:
                // Never written down - not to the activity log, not to disk, and not into a log
                // line carrying the contents. That it arrived is the most that may be recorded.
                if (NotificationProtocol.TryParse(e.Body, out var mirrored) && mirrored != null)
                {
                    Notifications.Add(e.Peer.Fingerprint, from, mirrored);

                    _ = Notifier.ShowAsync($"{e.Peer.Fingerprint}|{mirrored.Key}",
                        string.IsNullOrWhiteSpace(mirrored.AppName) ? from : $"{mirrored.AppName} on {from}",
                        string.IsNullOrWhiteSpace(mirrored.Title) ? mirrored.Text
                                                                  : $"{mirrored.Title}\n{mirrored.Text}");

                    Log.Write("Daemon", $"Mirrored a notification from {from}.");
                }
                break;

            case SyncContent.NotificationDismiss:
                if (NotificationProtocol.TryParseDismiss(e.Body, out string dismissedKey))
                {
                    // Cleared here too, so dismissing on the phone clears the desktop banner.
                    Notifications.Remove(e.Peer.Fingerprint, dismissedKey);
                    _ = Notifier.CloseAsync($"{e.Peer.Fingerprint}|{dismissedKey}");
                }
                Log.Write("Daemon", $"{from} dismissed a mirrored notification.");
                break;

            default:
                // Named rather than swallowed. A content type this build does not handle is a
                // peer that is ahead of it, and that is worth knowing during a port.
                Log.Write("Daemon", $"Ignored content type 0x{e.ContentType:X2} from {from} ({e.Body.Length} bytes).");
                break;
        }
    }

    private async Task ApplyTextAsync(byte[] body, string from, string via)
    {
        string text = Encoding.UTF8.GetString(body);

        // Recorded before it is applied. Setting the clipboard raises a change notification
        // that comes straight back through the watcher, and this is what stops it being sent
        // out again as though the user had copied it.
        _echo.NoteInbound(body);

        bool applied = await ClipboardBridge.SetTextAsync(text, CancellationToken.None).ConfigureAwait(false);

        Activity.Record(SyncDirection.Received, SyncItemKind.Text, body.Length, text);
        Log.Write("Daemon", applied
            ? $"Received text from {from} over {via}, {body.Length} bytes, on the clipboard."
            : $"Received text from {from} over {via}, {body.Length} bytes; no clipboard to put it on.");

        if (!applied) Console.WriteLine($"  text from {from}: {Preview(text)}");
    }

    private void SaveImage(byte[] body, string from, string via)
    {
        // No UI to show it in, so it lands somewhere findable instead. The clipboard bridges
        // here are text only; images are a later step and this keeps the payload rather than
        // dropping it.
        try
        {
            string name = $"clip_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg";
            string path = Path.Combine(_paths.IncomingDirectory, name);
            File.WriteAllBytes(path, body);

            Activity.Record(SyncDirection.Received, SyncItemKind.Image, body.Length, location: path);
            Log.Write("Daemon", $"Received an image from {from} over {via}, {body.Length} bytes, saved as {name}.");
        }
        catch (Exception ex)
        {
            Log.Write("Daemon", $"Could not save an image from {from}", ex);
        }
    }

    private void NoteAnnouncedAddress(string fingerprint, byte[] body, string from)
    {
        string address = Encoding.UTF8.GetString(body).Trim();

        // Parsed rather than believed, even though it arrived inside an authenticated payload
        // from a paired device. An address is exactly the sort of thing that must not be taken
        // on trust, because it decides where the next connection goes.
        //
        // Both forms are accepted because MeshLinks already dials either: a bare IP, which is
        // what every device on the default port announces, and host:port, which is what a
        // second device sharing a machine has to announce to be reachable at all.
        if (!IPAddress.TryParse(address, out _) && !IPEndPoint.TryParse(address, out _))
        {
            Log.Write("Daemon", $"Ignoring an implausible address announced by {from}.");
            return;
        }

        Security.Peers.NoteSeen(fingerprint, address);
        Log.Write("Daemon", $"{from} is reachable at {address}.");
    }

    // ──────────────────────────────── peers

    private void OnPeerConnected(PeerRecord peer)
    {
        string name = peer.Name ?? DeviceIdentity.Shorten(peer.Fingerprint);
        Log.Write("Daemon", $"{name} connected.");

        Links.SetWiFi(true, name);
        AnnounceAddress(peer.Fingerprint);
    }

    private void OnPeerDisconnected(string fingerprint)
    {
        Log.Write("Daemon", $"{DeviceIdentity.Shorten(fingerprint)} disconnected.");

        // The aggregate, not this one peer: another may still be up, and the UI asks whether
        // anything is reachable rather than whether this device is.
        Links.SetWiFi(Mesh.IsConnectedToAny);
    }

    /// <summary>
    /// Tells a peer where this device is reachable, so a DHCP lease change cannot strand it.
    /// Sent on every link that comes up, because this side cannot know what the peer last
    /// recorded and the payload is a few dozen bytes on a link that is already open.
    /// </summary>
    private void AnnounceAddress(string fingerprint)
    {
        string? address = NetworkUtil.GetLocalLanAddress();
        if (string.IsNullOrEmpty(address)) return;

        // A bare address is what every real device announces, and what the Windows and Android
        // ends expect - they parse it as an IP and drop anything else. The port is appended only
        // when this device is not on the default, which happens when two of them share a
        // machine, and in that case the peer is another one of these.
        if (Port != TcpTransportConnection.DefaultPort) address = $"{address}:{Port}";

        _ = Task.Run(async () =>
        {
            try
            {
                await Mesh.SendToAsync(fingerprint, SyncContent.Address,
                    Encoding.UTF8.GetBytes(address)).ConfigureAwait(false);
                Log.Write("Daemon", $"Announced this device at {address}.");
            }
            catch (Exception ex)
            {
                // Informational. A failure costs nothing until the address changes.
                Log.Write("Daemon", "Could not announce this device's address", ex);
            }
        });
    }

    private void OnPairingRequested(PendingPairing pending)
    {
        // Printed rather than logged alone: the whole point is that a human is standing here to
        // compare it against what the other device is showing.
        Console.WriteLine();
        Console.WriteLine($"  {pending.Name ?? "A device"} wants to join.");
        Console.WriteLine($"  Fingerprint  {pending.ShortFingerprint}");
        Console.WriteLine($"  Check it matches the other screen, then: confirm {pending.ShortFingerprint.Split('-')[0]}");
        Console.WriteLine();
    }

    /// <summary>
    /// Confirms a device by any unambiguous prefix of its fingerprint, so the whole thing does
    /// not have to be typed off another screen.
    /// </summary>
    public (bool Ok, string Message) Confirm(string prefix)
    {
        var matches = Pending
            .Where(p => Matches(p.Fingerprint, prefix))
            .ToList();

        if (matches.Count == 0) return (false, "No device waiting with that fingerprint.");
        if (matches.Count > 1) return (false, $"{matches.Count} devices match that prefix; type more of it.");

        if (!Security.ConfirmPairing(matches[0].Fingerprint))
        {
            return (false, "Could not confirm it. The pairing window may have closed - run `pair` again.");
        }

        // The peer is refused once by design and reconnects on its next retry, so the loop is
        // nudged rather than waited out.
        NudgeDial();
        return (true, $"Paired with {matches[0].ShortFingerprint}.");
    }

    public (bool Ok, string Message) Reject(string prefix)
    {
        var matches = Pending.Where(p => Matches(p.Fingerprint, prefix)).ToList();

        if (matches.Count == 0) return (false, "No device waiting with that fingerprint.");
        if (matches.Count > 1) return (false, $"{matches.Count} devices match that prefix; type more of it.");

        return Security.RejectPairing(matches[0].Fingerprint)
            ? (true, $"Turned away {matches[0].ShortFingerprint}.")
            : (false, "It is no longer waiting.");
    }

    /// <summary>Finds one paired device by any unambiguous prefix of its fingerprint or name.</summary>
    public PeerRecord? FindPeer(string prefix)
    {
        var byFingerprint = Security.Peers.Peers.Where(p => Matches(p.Fingerprint, prefix)).ToList();
        if (byFingerprint.Count == 1) return byFingerprint[0];
        if (byFingerprint.Count > 1) return null;

        var byName = Security.Peers.Peers
            .Where(p => p.Name != null && p.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return byName.Count == 1 ? byName[0] : null;
    }

    /// <summary>Compares against the fingerprint with and without its grouping dashes.</summary>
    private static bool Matches(string fingerprint, string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return false;

        string bare = fingerprint.Replace("-", "");
        string wanted = prefix.Replace("-", "");

        return bare.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)
            || DeviceIdentity.Shorten(fingerprint).Replace("-", "")
                   .StartsWith(wanted, StringComparison.OrdinalIgnoreCase);
    }

    private static string Preview(string text)
    {
        string oneLine = text.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= 60 ? oneLine : oneLine[..57] + "...";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _watcher.TextChanged -= OnLocalClipboardChangedAsync;
        _watcher.Dispose();

        Mesh.PayloadReceived -= OnPayload;
        Mesh.PeerConnected -= OnPeerConnected;
        Mesh.PeerDisconnected -= OnPeerDisconnected;
        Mesh.Dispose();

        Security.PairingRequested -= OnPairingRequested;
        if (_onPeersChanged != null) Security.Peers.Changed -= _onPeersChanged;
        Security.Dispose();

        Files.Dispose();
        Ringer.Dispose();
        Ble?.Dispose();
        BleServer?.Dispose();
        _peripheral?.Dispose();
        _peripheralBus?.Dispose();
        _bleBus?.Dispose();
        _dialNow.Dispose();
    }
}
