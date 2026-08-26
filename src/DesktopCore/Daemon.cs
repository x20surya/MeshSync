using System.Net;
using System.Text;
using CoreLib;
using CoreLib.Diagnostics;
using CoreLib.Identity;
using CoreLib.Transport;
using CoreLib.Transport.Ble;
using CoreLib.Transport.Fabric;
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
    private readonly WiFiRouteProvider _wifi;
    private readonly LinkSupervisor _supervisor;
    private readonly MeshDiscovery _discovery;
    private bool _disposed;

    /// <summary>The radio, once one has been found. Null on a machine with no Bluetooth.</summary>
    private LinuxBleRadio? _radio;

    /// <summary>Scans, connects and rotates on one adapter. Null until the radio starts.</summary>
    private BleRadioScheduler? _scheduler;

    /// <summary>Peers with a send in flight that needs a socket, and peers that asked for one.</summary>
    private readonly HashSet<string> _wifiHolds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _wifiWake = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _demandGate = new();

    /// <summary>Set by the screen watcher on a desktop that has one. Assume present until told.</summary>
    private volatile bool _screenOn = true;

    /// <summary>What this machine's radio can do, for the arbiter. Central only until proven otherwise.</summary>
    private BleCapability _bleCapability = BleCapability.Central;

    /// <summary>Held so it can be detached again; a lambda cannot be unsubscribed by reference.</summary>
    private Action? _onPeersChanged;

    /// <summary>This device's run, so a tier turned back on later can be started against it.</summary>
    private CancellationToken _lifetime = CancellationToken.None;


    /// <summary>The port this device listens on. Not always the default: two devices on one
    /// machine cannot share a listening port.</summary>
    public int Port { get; }

    public string DataDirectory => _paths.DataDirectory;

    /// <summary>The name announced to peers, and shown in their device lists.</summary>
    public string DeviceName => _wifi.LocalDeviceName;

    public PeerSecurity Security { get; }

    /// <summary>
    /// Every way this device can reach every device it is paired with.
    ///
    /// <para>Replaces a <c>MeshLinks</c> holding the sockets and two nullable radio fields beside
    /// it. One route table now, so "is this peer reachable, and over what" has one answer instead
    /// of three that disagreed.</para>
    /// </summary>
    public MeshFabric Fabric { get; }

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
    /// <summary>
    /// The inbound radio half, when the adapter can advertise.
    ///
    /// <para>Null on every Linux machine today: BlueZ rejects the exported GATT tree, so
    /// <c>LinuxBlePeripheral</c> registers, fails and stands aside. That is a supported
    /// arrangement rather than a missing half, because arbitration is capability first.</para>
    /// </summary>
    private LinuxBleServerRoute? _inbound;

    /// <summary>The peripheral half, where the adapter can advertise. Null where it cannot.</summary>
    public LinuxBleServer? BleServer { get; private set; }

    /// <summary>Everything about reachability, for a status command or a dashboard panel.</summary>
    public MeshHealth Health => MeshHealth.Of(
        Fabric, SystemClock.Instance, _supervisor.LastPassUtc, _supervisor.Passes, _supervisor.Restarts,
        _radio?.Status ?? "no adapter", _scheduler?.LiveCentralLinks ?? 0,
        _scheduler == null ? 0 : Fabric.Timings.MaxBleCentralLinks,
        _scheduler?.IsAdvertising ?? false, _scheduler?.LastRound ?? default,
        RoutePolicy.Plan(Security.Peers.Peers, CurrentConditions(), DateTime.UtcNow).Routes);

    private LinuxBlePeripheral? _peripheral;
    private BlueZ? _bleBus;
    private BlueZ? _peripheralBus;

    /// <summary>True when a Bluetooth link is up and has agreed a key, either way round.</summary>
    public bool IsBluetoothConnected =>
        Fabric.Links.Any(l => l.LiveRoutes.Any(r => r.Kind != RouteKind.WiFi));

    /// <summary>
    /// True when this particular peer is reachable over Bluetooth, either way round.
    ///
    /// Exists so a device list can say which link a device is on rather than testing Wi-Fi and
    /// calling everything else disconnected.
    /// </summary>
    public bool IsBluetoothConnectedTo(string fingerprint)
    {
        var link = Fabric.LinkTo(fingerprint);
        return link != null && link.LiveRoutes.Any(r => r.Kind != RouteKind.WiFi);
    }

    /// <summary>The name a connected peer announced, if it has. For a device list.</summary>
    public string? NameOf(string fingerprint) => Fabric.LinkTo(fingerprint)?.Peer.Name;

    /// <summary>True when this peer has a socket, as opposed to any link at all.</summary>
    public bool IsWiFiConnectedTo(string fingerprint) =>
        Fabric.LinkTo(fingerprint)?.RouteOf(RouteKind.WiFi)?.State == RouteState.Established;

    public bool IsConnectedTo(string fingerprint) => Fabric.IsConnectedTo(fingerprint);

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
    /// <summary>
    /// True while any route is on its way up.
    ///
    /// <para>Used to be a flag the dial loop raised around one call, so it was true for the whole
    /// round whatever was happening in it. Reading the routes is both more honest and per peer.</para>
    /// </summary>
    public bool IsDialling => Fabric.Links.Any(l => l.AllRoutes.Any(
        r => r.State is RouteState.Connecting or RouteState.Discovering or RouteState.Handshaking));

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
        Fabric = new MeshFabric(Security, () => _bleCapability);
        Fabric.PayloadReceived += OnPayload;
        Fabric.PeerConnected += OnPeerConnected;
        Fabric.PeerDisconnected += OnPeerDisconnected;
        Fabric.Changed += OnFabricChanged;

        _wifi = new WiFiRouteProvider(Security, port, peerPort: TcpTransportConnection.DefaultPort)
        {
            LocalDeviceName = deviceName ?? Environment.MachineName,
            LocalCapability = () => _bleCapability,
        };
        Fabric.AddProvider(_wifi);

        _supervisor = new LinkSupervisor(Fabric, CurrentConditions)
        {
            WantedCentralPeersChanged = peers => _scheduler?.SetWanted(peers),
            AdvertisingWanted = wanted => _ = ApplyAdvertisingAsync(wanted),
            ProbingWanted = probing => _scheduler?.SetProbing(probing),
        };

        _discovery = new MeshDiscovery(Security);

        // A device refused a moment ago for not being paired is the same device being confirmed
        // now, and it must not sit out a cooldown after that.
        _onPeersChanged = () =>
        {
            _scheduler?.Cooldowns.Clear();

            // Minted here as well as at startup, because a fresh install has nothing paired when
            // it starts and there is no point advertising a beacon for a mesh of one. The first
            // pairing is the moment this device becomes a mesh.
            if (_discovery.MintIfDue() != null) _ = ApplyAdvertisingAsync(_scheduler?.IsAdvertising ?? false);

            _supervisor.Signal();
        };
        Security.Peers.Changed += _onPeersChanged;

        Security.PairingRequested += OnPairingRequested;

        Files = new FileTransferService(Path.Combine(paths.IncomingDirectory, "partial"),
            (fingerprint, contentType, body, token) => Fabric.SendToAsync(fingerprint, contentType, body, token));

        Files.FileReceived += OnFileReceived;
        Files.FileFailed += (name, reason) => Log.Write("Files", $"{name} did not arrive: {reason}.");

        Browse = new BrowseService
        {
            Send = (fingerprint, contentType, body) => Fabric.SendToAsync(fingerprint, contentType, body),
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

            return PairingCode.Build(Security.Identity.PublicKey, ip, Security.Peers.MeshName);
        }
    }

    // ──────────────────────────────── lifecycle

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.IncomingDirectory);
        _lifetime = cancellationToken;

        _wifi.IsAvailable = Transports.AllowsWiFi;

        if (Transports.AllowsWiFi)
        {
            await _wifi.StartListeningAsync(cancellationToken).ConfigureAwait(false);
            Log.Write("Daemon", $"Listening on {Port}.");
        }
        else
        {
            Log.Write("Daemon", "Wi-Fi listener not started: the transport preference is Bluetooth only.");
        }

        // A device with nobody to talk to is a device that has just been installed, so the
        // window opens itself rather than making the first run start with a command.
        if (Security.Peers.IsEmpty) Security.Pairing.Open();

        // Minted on the first run that has peers and no key, then offered over the links that
        // already exist - which is what makes the upgrade cost no re-pair.
        _discovery.MintIfDue();

        await StartBluetoothAsync(cancellationToken).ConfigureAwait(false);

        _ = Task.Run(() => _supervisor.RunAsync(cancellationToken), CancellationToken.None);
        _ = Task.Run(() => _watcher.RunAsync(cancellationToken), CancellationToken.None);

        // The inbound radio half has no loop of its own, so its liveness check runs here. It was
        // written to be called from the dial loop and never was, which left a peripheral link
        // whose central had walked away showing as connected forever.
        _ = Task.Run(() => InboundHeartbeatAsync(cancellationToken), CancellationToken.None);
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

            if (canAdvertise) await StartPeripheralAsync(adapterPath).ConfigureAwait(false);

            // Honest rather than optimistic: an adapter that claims it can advertise but whose
            // GATT tree BlueZ then refused cannot advertise, and telling the arbiter otherwise
            // would have both devices agree an arrangement neither can carry out.
            _bleCapability = _peripheral != null ? BleCapability.Both : BleCapability.Central;
            BluetoothStatus = _peripheral != null ? "scanning and advertising" : "scanning";

            _radio = await LinuxBleRadio.TryCreateAsync(_bleBus, adapterPath).ConfigureAwait(false);
            if (_radio == null)
            {
                // Never silent. A scanner that failed to start looks exactly like one that started
                // and found nothing, and telling those apart from the log is the whole difference
                // between a five-minute diagnosis and an afternoon.
                BluetoothStatus = "the scanner could not be started";
                Log.Write("Ble", "The Bluetooth scanner could not be started; this device can only be connected to.");
                return;
            }

            _radio.Capability = _bleCapability;
            _radio.Prepare = PrepareLink;

            _scheduler = new BleRadioScheduler(_radio)
            {
                // Before there is a mesh key this accepts everything, which is how every build
                // before this one behaved. Once there is one, a device from another mesh costs a
                // comparison instead of a connect, an MTU exchange and a hello.
                BeaconFilter = _discovery.Accepts,
                BeaconRank = _discovery.RankOf,
            };

            Fabric.AddProvider(_scheduler.CentralRoutes);
            Fabric.AddProvider(_scheduler.InboundRoutes);

            // The fault is observed rather than discarded. A bare Task.Run swallows whatever the
            // loop throws on its way up, so a scanner that died on its first D-Bus call is
            // indistinguishable from one quietly finding nothing.
            _ = Task.Run(() => _scheduler.RunAsync(cancellationToken), CancellationToken.None)
                    .ContinueWith(t =>
                    {
                        if (t.Exception != null)
                            Log.Write("Ble", "The Bluetooth scheduler stopped", t.Exception.GetBaseException());
                    }, TaskContinuationOptions.OnlyOnFaulted);

            _supervisor.Signal();
        }
        catch (Exception ex)
        {
            Log.Write("Ble", "Bluetooth could not be started", ex);
        }
    }

    /// <summary>
    /// Publishes the GATT service, where BlueZ will accept the tree.
    ///
    /// <para>Its own bus connection, because BlueZ closes the connection outright when it dislikes
    /// an exported object tree, and that must not take the scanner down with it. It does dislike
    /// this one today - <c>a{oa{sa{sv}}}</c> needs every dict entry aligned to eight bytes - so
    /// this stands aside and the capability reported to the arbiter says so.</para>
    /// </summary>
    private async Task StartPeripheralAsync(string adapterPath)
    {
        _peripheralBus = await BlueZ.TryConnectAsync().ConfigureAwait(false);

        _peripheral = _peripheralBus == null ? null : await LinuxBlePeripheral.TryStartAsync(
            _peripheralBus, adapterPath, DeviceName).ConfigureAwait(false);

        if (_peripheral == null) return;

        BleServer = new LinuxBleServer(_peripheral)
        {
            LocalPublicKey = Security.Identity.PublicKey,
            LocalDeviceName = DeviceName,
            LocalMeshName = Security.Peers.MeshName,
            OpenSession = (peerKey, peerName, peerEphemeral, localEphemeral) =>
                Security.Authorise(peerKey, peerName)
                    ? Security.OpenSession(peerKey, localEphemeral, peerEphemeral)
                    : null,
        };

        var route = new LinuxBleServerRoute(BleServer);
        route.Identified += (_, e) =>
        {
            OnRadioIdentified(e);
            AnnounceAddressOverRadio(route.Session, payload => route.SendAsync(SyncContent.Address, payload));
        };

        BleServer.WiFiRequested += (_, _) => RaiseWiFiFor(route.PeerFingerprint);
        _inbound = route;

        _radio?.PublishInbound(route);
    }

    /// <summary>Gives a new outbound link its identity, its key agreement and its handlers.</summary>
    private void PrepareLink(LinuxBleLink link)
    {
        link.LocalPublicKey = Security.Identity.PublicKey;
        link.LocalDeviceName = DeviceName;
        link.LocalMeshName = Security.Peers.MeshName;
        link.LocalCapability = _bleCapability;

        link.OpenSession = (peerKey, peerName, peerEphemeral, localEphemeral) =>
            Security.Authorise(peerKey, peerName)
                ? Security.OpenSession(peerKey, localEphemeral, peerEphemeral)
                : null;

        link.Identified += (l, e) =>
        {
            OnRadioIdentified(e);
            AnnounceAddressOverRadio(l.Session, payload => l.SendAsync(SyncContent.Address, payload));
        };

        // A peer asking for Wi-Fi is asking because it has something Bluetooth cannot carry. This
        // used to answer with a log line saying Wi-Fi was already up here, which is an assumption
        // rather than a check: the listener being up is not a socket to that peer.
        link.WiFiRequested += l => RaiseWiFiFor(l.PeerFingerprint);
    }

    /// <summary>
    /// A peer proved who it is over the radio.
    ///
    /// Bluetooth carries no hello of the kind TCP has, so the sender is identified by which key
    /// authenticates the payload - the same test the socket path applies anyway.
    /// </summary>
    private void OnRadioIdentified(PeerIdentifiedEventArgs e)
    {
        Security.Peers.NoteSeen(e.Fingerprint, null, e.DeviceName, e.Capability);
        Security.Peers.AdoptMeshName(e.MeshName);

        Log.Write("Ble", $"{e.DeviceName} is in range over Bluetooth.");
        _supervisor.Signal();
    }

    /// <summary>
    /// Runs the inbound half's liveness check, which has no loop of its own.
    /// </summary>
    private async Task InboundHeartbeatAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { _inbound?.CheckHeartbeat(); }
            catch (Exception ex) { Log.Write("Ble", "The inbound liveness check failed", ex); }

            try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Publishes or withdraws the service, and refreshes the beacon as its epoch turns.</summary>
    private async Task ApplyAdvertisingAsync(bool wanted)
    {
        var scheduler = _scheduler;
        if (scheduler == null) return;

        try
        {
            await scheduler.SetAdvertisingAsync(wanted, _discovery.CurrentAdvertisement(_bleCapability))
                .ConfigureAwait(false);
        }
        catch (Exception ex) { Log.Write("Ble", "Applying the advertisement failed", ex); }
    }

    /// <summary>
    /// Everything about this device that decides which routes it wants.
    ///
    /// <para>Gathered here and handed to <c>RoutePolicy</c> whole, so the rule that consumes it is
    /// a function of its arguments. Every one of these used to be a condition inside a loop, and
    /// none of them could be asserted on without a radio in the room.</para>
    /// </summary>
    private LocalConditions CurrentConditions()
    {
        Dictionary<string, DateTime> wake;
        HashSet<string> holds;

        lock (_demandGate)
        {
            wake = new Dictionary<string, DateTime>(_wifiWake, StringComparer.OrdinalIgnoreCase);
            holds = new HashSet<string>(_wifiHolds, StringComparer.OrdinalIgnoreCase);
        }

        return new LocalConditions
        {
            LocalFingerprint = Security.Identity.Fingerprint,

            // A desktop is a device somebody is at. There is no screen-off equivalent here yet,
            // so Wi-Fi is wanted for every peer that nothing else is carrying presence for.
            ScreenOn = _screenOn,
            HasUsableNetwork = Transports.AllowsWiFi && NetworkUtil.GetLocalLanAddress() != null,
            Transport = Transports.Current,
            LocalCapability = _bleCapability,
            PeerCapabilities = Security.Peers.Capabilities,
            WiFiHolds = holds,
            WiFiWakeUntilUtc = wake,
            PairingOpen = Security.Pairing.IsOpen,
        };
    }

    /// <summary>
    /// A peer has something Bluetooth cannot carry, so go to it.
    ///
    /// <para>The listener here is always up, which is what this used to answer with - but a
    /// listener is not a socket to the device that asked. It may have just changed address, or
    /// never had one recorded here at all. Dialling is the useful answer.</para>
    ///
    /// <para><b>Per peer now.</b> The window used to be a single flag for the whole device, so one
    /// peer asking held Wi-Fi up for every peer, and one peer no longer needing it dropped the
    /// socket to all of them.</para>
    /// </summary>
    private void RaiseWiFiFor(string fingerprint)
    {
        if (!Transports.AllowsWiFi)
        {
            Log.Write("Ble", "A peer asked for Wi-Fi, but this device is set to Bluetooth only.");
            return;
        }

        if (string.IsNullOrWhiteSpace(fingerprint)) return;

        lock (_demandGate) _wifiWake[fingerprint] = DateTime.UtcNow.Add(WiFiWakeWindow);

        Log.Write("Ble", $"{DeviceIdentity.Shorten(fingerprint)} asked for Wi-Fi; dialling it now.");
        _supervisor.Signal();
    }

    /// <summary>
    /// How long a request from a peer keeps Wi-Fi up for that peer. Lapses on its own, so nothing
    /// has to remember to cancel it.
    /// </summary>
    private static readonly TimeSpan WiFiWakeWindow = TimeSpan.FromSeconds(60);

    /// <summary>Asks the supervisor to reconcile now rather than at the next interval.</summary>
    public void NudgeDial() => _supervisor.Signal();

    /// <summary>
    /// Tells a peer where this device is reachable, over the radio rather than the socket.
    ///
    /// <para>Sealed with the key that link agreed, and sent once the hello has crossed because
    /// before that there is no key to seal it with. This is the only address announcement a device
    /// paired over Bluetooth alone will ever receive: without it, it knows this machine only as a
    /// radio and has no route to a socket, so an image copied here has nowhere to go and a lease
    /// change strands it with no way back short of a rescan.</para>
    /// </summary>
    private void AnnounceAddressOverRadio(PeerSession? session, Func<byte[], Task<bool>> send)
    {
        if (session == null) return;

        string? address = NetworkUtil.GetLocalLanAddress();
        if (address == null) return;

        byte[] payload = Encoding.UTF8.GetBytes(Port == TcpTransportConnection.DefaultPort
            ? address
            : $"{address}:{Port}");

        _ = Task.Run(async () =>
        {
            try
            {
                if (await send(payload).ConfigureAwait(false))
                    Log.Write("Ble", $"Announced this device at {address} over Bluetooth.");
            }
            catch (Exception ex)
            {
                // Informational. A failure costs nothing until the address changes.
                Log.Write("Ble", "Could not announce this device's address over Bluetooth", ex);
            }
        });
    }

    /// <summary>
    /// Starts or stops a tier when the preference changes, without a restart.
    ///
    /// <para>Closing a tier is now a matter of telling the provider it is unavailable and letting
    /// the next reconcile pass close what policy no longer wants. That is one code path instead of
    /// two, and it cannot leave a route open that nothing owns.</para>
    /// </summary>
    private void ApplyTransportPreference()
    {
        try
        {
            _wifi.IsAvailable = Transports.AllowsWiFi;

            if (Transports.AllowsWiFi) _ = _wifi.StartListeningAsync(_lifetime);
            else _wifi.StopListening();

            if (Transports.AllowsBle)
            {
                if (_radio == null) _ = StartBluetoothAsync(_lifetime);
            }
            else
            {
                if (_radio != null) _radio.IsAvailable = false;
                BluetoothStatus = "off - the transport preference is Wi-Fi only";
            }

            // The pass does the rest: routes policy no longer wants are closed, per peer, and the
            // radio is told what it is still owed.
            _supervisor.Signal();
        }
        catch (Exception ex)
        {
            Log.Write("Daemon", "Could not apply the transport preference", ex);
        }
    }

    /// <summary>Keeps the shared link state in step with the fabric, for every screen that reads it.</summary>
    private void OnFabricChanged()
    {
        Links.SetWiFi(Fabric.Links.Any(l => l.RouteOf(RouteKind.WiFi)?.State == RouteState.Established));
        Links.SetBle(IsBluetoothConnected);
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
    /// Sends to every connected device, once per peer.
    ///
    /// <para><b>Once per peer, not once per link, and it is now structural.</b> A peer reachable
    /// over both tiers has one <c>PeerLink</c>, which picks the route - Wi-Fi first because it
    /// carries anything, the radio when that is what exists. Sending over every link separately
    /// delivered the clipboard twice to a peer holding two, and the echo suppressor is on the
    /// sending side so the receiver had no defence.</para>
    /// </summary>
    private Task<int> BroadcastAsync(byte contentType, byte[] body, CancellationToken cancellationToken) =>
        Fabric.BroadcastAsync(contentType, body, cancellationToken);

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
        if (!PairingCode.TryParse(pairingUri, out var parsed, out string error))
        {
            return (false, error);
        }

        string key = parsed!.PublicKey;
        string? address = parsed.Address;

        // Adopted only if this device has no name of its own, so joining names an unnamed mesh
        // and re-pairing later cannot silently rename it underneath the user.
        Security.Peers.AdoptMeshName(parsed.MeshName);

        if (!Security.Peers.Trust(key, name: null, address: address))
        {
            return (false, "That pairing key is not valid.");
        }

        // The device whose code was just scanned, so the radio can look for that one and
        // nothing else. Its beacon is derived from the same key, so a second pairing screen
        // open in the same room is told apart rather than connected to.
        //
        // This is what lets two devices pair with no network at all: the QR carries a key,
        // the inviter advertises a tag derived from it, and the joiner scans for exactly that.
        _discovery.InvitedPublicKey = key;

        // The other end refuses the first attempt and asks a human to compare fingerprints, so
        // the supervisor is nudged rather than waited out.
        NudgeDial();

        string fingerprint = DeviceIdentity.FingerprintOf(key);
        return (true, $"Trusting {DeviceIdentity.Shorten(fingerprint)}" +
                      (address != null ? $" at {address}." : ".") +
                      " Now confirm this device on the other screen.");
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
        if (IsWiFiConnectedTo(fingerprint)) return true;
        if (!Transports.AllowsWiFi) return false;

        // Held for this peer alone, so raising Wi-Fi for a file does not keep sockets open to
        // every other device in the mesh.
        lock (_demandGate) _wifiHolds.Add(fingerprint);

        try
        {
            // Both directions at once: this device goes to the peer, and the peer is asked to come
            // to this device. Either may be the one that can actually open the socket.
            _supervisor.Signal();
            await AskPeerForWiFiAsync(fingerprint).ConfigureAwait(false);

            DateTime deadline = DateTime.UtcNow + WiFiWakeTimeout;

            while (DateTime.UtcNow < deadline)
            {
                if (IsWiFiConnectedTo(fingerprint)) return true;

                try { await Task.Delay(200, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return false; }
            }

            Log.Write("Daemon",
                $"{DeviceIdentity.Shorten(fingerprint)} did not come up on Wi-Fi within {WiFiWakeTimeout.TotalSeconds:F0}s.");
            return false;
        }
        finally
        {
            lock (_demandGate) _wifiHolds.Remove(fingerprint);
            _supervisor.Signal();
        }
    }

    /// <summary>Asks one peer, over whichever radio half is carrying it, to raise Wi-Fi.</summary>
    private async Task AskPeerForWiFiAsync(string fingerprint)
    {
        var link = Fabric.LinkTo(fingerprint);
        if (link == null) return;

        foreach (var route in link.LiveRoutes)
        {
            switch (route)
            {
                case LinuxBleLink central:
                    await central.RequestWiFiAsync().ConfigureAwait(false);
                    return;

                case LinuxBleServerRoute inbound:
                    inbound.RequestWiFi();
                    return;
            }
        }
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

        await Fabric.SendToAsync(fingerprint, SyncContent.NotificationDismiss,
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
    private Task<bool> SendToPeerAsync(string fingerprint, byte contentType, byte[] body,
                                       CancellationToken cancellationToken = default) =>
        Fabric.SendToAsync(fingerprint, contentType, body, cancellationToken);

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

    private readonly Lock _ringRequests = new();
    private readonly HashSet<string> _ringing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when this device has asked a peer to start or stop ringing.</summary>
    public event Action<string>? RingRequestChanged;

    /// <summary>Whether this device has asked that one to ring and not yet asked it to stop.</summary>
    public bool HasAskedToRing(string fingerprint)
    {
        lock (_ringRequests) return _ringing.Contains(fingerprint);
    }

    /// <summary>
    /// Asks one device to make a noise, or to stop.
    ///
    /// <para>Through the fabric rather than the socket table, which is the whole point of the
    /// feature: the moment you most want to find a device is the moment it is not on any network,
    /// and this used to reach only peers that had one.</para>
    ///
    /// <para><b>Why the request is remembered.</b> Whether a phone is actually making a noise is
    /// the phone's business and it does not report back, so the honest answer any UI can give is
    /// whether it was <em>asked</em>. Kept here rather than in whatever drew the button, because
    /// the Plasma widget held it in a list delegate - which is reused as the list re-sorts, and
    /// is rebuilt when the popup is - so a row offered to stop a ring it never started, and lost
    /// the offer for one it did.</para>
    /// </summary>
    public async Task<bool> RingAsync(string fingerprint, bool on, CancellationToken cancellationToken = default)
    {
        bool sent = await Fabric.SendToAsync(fingerprint, SyncContent.Ring,
            [on ? (byte)1 : (byte)0], cancellationToken).ConfigureAwait(false);

        // Only a request that arrived is remembered. One that did not leaves the button saying
        // "ring", which is the truth.
        if (!sent) return false;

        lock (_ringRequests)
        {
            if (on) _ringing.Add(fingerprint);
            else _ringing.Remove(fingerprint);
        }

        RingRequestChanged?.Invoke(fingerprint);
        return true;
    }

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

    /// <summary>
    /// Offers this device's mesh discovery key to one peer.
    ///
    /// <para>Over the ordinary authenticated path, so only a paired device can ever send or
    /// receive one - which is the whole security requirement, because the key decides which
    /// advertisements are worth connecting to and nothing else.</para>
    /// </summary>
    private void OfferMeshKey(string fingerprint)
    {
        var key = Security.Peers.MeshKey;
        if (key == null) return;

        _ = Task.Run(async () =>
        {
            try { await Fabric.SendToAsync(fingerprint, SyncContent.MeshKeyOffer, key).ConfigureAwait(false); }
            catch (Exception ex) { Log.Write("Ble", "Offering the mesh key failed", ex); }
        });
    }

    /// <summary>The tier a payload arrived on, as the activity log and the log lines say it.</summary>
    private static string Via(RouteKind kind) => kind == RouteKind.WiFi ? "Wi-Fi" : "Bluetooth";

    private void OnPayload(PeerLink link, RoutePayload payload)
    {
        var e = payload;
        string from = e.Peer.Name ?? DeviceIdentity.Shorten(e.Peer.Fingerprint);

        // The file and browse services own their content types outright, and say so by
        // returning true. Doing this before the switch keeps the type numbers in one place.
        if (Files.Handle(e.Peer.Fingerprint, e.ContentType, e.Body)) return;
        if (Browse.Handle(e.Peer.Fingerprint, e.ContentType, e.Body)) return;

        switch (e.ContentType)
        {
            case SyncContent.Text:
                _ = ApplyTextAsync(e.Body, from, Via(e.Via));
                break;

            case SyncContent.Image:
                SaveImage(e.Body, from, Via(e.Via));
                break;

            case SyncContent.Address:
                NoteAnnouncedAddress(e.Peer.Fingerprint, e.Body, from);
                break;

            case SyncContent.MeshKeyOffer:
                // Lowest key wins, so two halves of a mesh that minted separately converge in one
                // exchange - and a device that adopts a new one re-advertises within an epoch.
                if (_discovery.Adopt(e.Body))
                {
                    Log.Write("Ble", $"Adopted the mesh discovery key {from} offered.");
                    _ = ApplyAdvertisingAsync(_scheduler?.IsAdvertising ?? false);

                    // Everyone else this device can reach has to hear about it too, or a mesh of
                    // three converges only as far as the two that happened to meet first.
                    foreach (var other in Fabric.ConnectedPeers)
                    {
                        if (!string.Equals(other, e.Peer.Fingerprint, StringComparison.OrdinalIgnoreCase))
                            OfferMeshKey(other);
                    }
                }
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

    private void OnPeerConnected(PeerLink link, IPeerRoute route)
    {
        var peer = link.Peer;
        string name = peer.Name ?? DeviceIdentity.Shorten(peer.Fingerprint);
        Log.Write("Daemon", $"{name} connected over {route.Kind}.");

        OnFabricChanged();

        // Only over the socket. Announcing an address on a radio link is done by the link itself
        // once its hello has crossed, because before that there is no key to seal it with.
        if (route.Kind == RouteKind.WiFi) AnnounceAddress(peer.Fingerprint);

        // The mesh key rides the links that already exist, which is what makes the upgrade cost
        // no re-pair. Offered on every connect: it is 32 bytes, and a peer that already holds a
        // lower one simply keeps it.
        OfferMeshKey(peer.Fingerprint);
    }

    private void OnPeerDisconnected(PeerLink link, RouteKind kind, string reason)
    {
        Log.Write("Daemon", $"{DeviceIdentity.Shorten(link.Fingerprint)} lost its {kind} route: {reason}.");

        // The aggregate, not this one peer: another may still be up, and a screen reading
        // LinkState asks whether anything is reachable rather than whether this device is.
        OnFabricChanged();
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
                await Fabric.SendToAsync(fingerprint, SyncContent.Address,
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

        Fabric.PayloadReceived -= OnPayload;
        Fabric.PeerConnected -= OnPeerConnected;
        Fabric.PeerDisconnected -= OnPeerDisconnected;
        Fabric.Changed -= OnFabricChanged;

        _supervisor.DisposeAsync().AsTask().GetAwaiter().GetResult();

        if (_scheduler != null) _scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (_inbound != null) _inbound.DisposeAsync().AsTask().GetAwaiter().GetResult();

        Fabric.DisposeAsync().AsTask().GetAwaiter().GetResult();

        Security.PairingRequested -= OnPairingRequested;
        if (_onPeersChanged != null) Security.Peers.Changed -= _onPeersChanged;
        Security.Dispose();

        Files.Dispose();
        Ringer.Dispose();
        BleServer?.Dispose();
        _peripheral?.Dispose();
        _peripheralBus?.Dispose();
        _bleBus?.Dispose();
    }
}
