using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CoreLib;
using CoreLib.Diagnostics;
using CoreLib.Identity;
using CoreLib.Transport;
using CoreLib.Transport.Ble;
using CoreLib.Transport.Fabric;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;
// WinForms has a LinkState of its own and this project's implicit usings pull it in, so the one
// that matters here is named outright rather than left to the compiler to guess between.
using LinkState = CoreLib.Transport.LinkState;

namespace WinDaemon
{
    static class Program
    {
        private const byte ContentText = SyncContent.Text;
        private const byte ContentImage = SyncContent.Image;

        private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "MeshSyncDaemon";
        private const string SettingsKeyPath = @"SOFTWARE\MeshSync";
        private const string SingleInstanceMutex = @"Global\MeshSyncDaemon.SingleInstance";

        /// <summary>
        /// Every way this machine can reach every device it is paired with.
        ///
        /// <para>Replaces a <c>MeshLinks</c> holding the sockets and two nullable radio fields
        /// beside it. One route table, so "is this peer reachable, and over what" has one answer
        /// - which is also what lets the device list stop guessing by name.</para>
        /// </summary>
        private static MeshFabric? _fabric;
        private static WiFiRouteProvider? _wifi;
        private static LinkSupervisor? _supervisor;
        private static MeshDiscovery? _discovery;
        private static WindowsBleRadio? _radio;
        private static BleRadioScheduler? _scheduler;
        private static WindowsBleServerRoute? _inbound;

        /// <summary>Peers with a send in flight that needs a socket, and peers that asked for one.</summary>
        private static readonly HashSet<string> _wifiHolds = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> _wifiWake = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _demandGate = new();

        /// <summary>
        /// What this machine's radio can do, taken from whether the GATT server actually published.
        ///
        /// <para>Claiming both halves on the strength of having an adapter makes the arbiter
        /// answer "you advertise" to a machine that then does not, and it neither advertises nor
        /// scans - a deadlock rather than a degraded state.</para>
        /// </summary>
        private static BleCapability _bleCapability = BleCapability.Central;

        /// <summary>
        /// Everything about this machine that decides which routes it wants.
        ///
        /// <para>A desktop is a device somebody is at, so Wi-Fi is wanted for every peer nothing
        /// else is carrying presence for - which is also what this machine did before, only now
        /// it is stated once rather than implied by a loop that always dialled.</para>
        /// </summary>
        private static LocalConditions CurrentConditions()
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
                LocalFingerprint = _security?.Identity.Fingerprint ?? "",
                ScreenOn = true,
                HasUsableNetwork = Transports.AllowsWiFi,
                Transport = Transports.Current,
                LocalCapability = _bleCapability,
                PeerCapabilities = _security?.Peers.Capabilities ?? new Dictionary<string, BleCapability>(),
                WiFiHolds = holds,
                WiFiWakeUntilUtc = wake,
                PairingOpen = _security?.Pairing.IsOpen ?? false,
            };
        }

        /// <summary>Publishes or withdraws the service, and refreshes the beacon as its epoch turns.</summary>
        private static async Task ApplyAdvertisingAsync(bool wanted)
        {
            var scheduler = _scheduler;
            var discovery = _discovery;
            if (scheduler == null || discovery == null) return;

            try
            {
                await scheduler.SetAdvertisingAsync(wanted, discovery.CurrentAdvertisement(_bleCapability))
                    .ConfigureAwait(false);
            }
            catch (Exception ex) { Log.Write("Ble", "Applying the advertisement failed", ex); }
        }

        /// <summary>
        /// A peer has something Bluetooth cannot carry, so go to it.
        ///
        /// <para>Per peer now. The window used to be a single flag for the whole machine.</para>
        /// </summary>
        private static void RaiseWiFiFor(string fingerprint)
        {
            if (!Transports.AllowsWiFi || string.IsNullOrWhiteSpace(fingerprint)) return;

            lock (_demandGate) _wifiWake[fingerprint] = DateTime.UtcNow.Add(WiFiWakeWindow);

            Log.Write("Daemon", $"{DeviceIdentity.Shorten(fingerprint)} asked for Wi-Fi; dialling it now.");
            _supervisor?.Signal();
        }

        /// <summary>How long a request from a peer keeps Wi-Fi up for that peer.</summary>
        private static readonly TimeSpan WiFiWakeWindow = TimeSpan.FromSeconds(60);

        /// <summary>Gives a new outbound radio link its identity, its key agreement and its handlers.</summary>
        private static void PrepareLink(WindowsBleCentral link)
        {
            link.LocalPublicKey = _security!.Identity.PublicKey;
            link.LocalDeviceName = Environment.MachineName;
            link.LocalMeshName = _security.Peers.MeshName;
            link.LocalCapability = _bleCapability;
            link.OpenSession = OpenBleSession;

            link.Identified += (l, e) =>
            {
                OnRadioIdentified(e);
                AnnounceAddressOverRadio(payload => l.SendAsync(SyncContent.Address, payload));
            };

            link.WiFiRequested += l => RaiseWiFiFor(l.PeerFingerprint);
        }

        /// <summary>A peer proved who it is over the radio.</summary>
        private static void OnRadioIdentified(PeerIdentifiedEventArgs e)
        {
            _security?.Peers.NoteSeen(e.Fingerprint, null, e.DeviceName, e.Capability);
            _security?.Peers.AdoptMeshName(e.MeshName);

            Log.Write("Daemon", $"{e.DeviceName} is in range over Bluetooth.");
            _supervisor?.Signal();
        }

        /// <summary>Tells a peer where this machine is reachable, over the radio rather than a socket.</summary>
        private static void AnnounceAddressOverRadio(Func<byte[], Task<bool>> send)
        {
            string? address = NetworkUtil.GetLocalLanAddress();
            if (address == null) return;

            byte[] payload = System.Text.Encoding.UTF8.GetBytes(address);

            _ = Task.Run(async () =>
            {
                try
                {
                    if (await send(payload).ConfigureAwait(false))
                        Log.Write("Daemon", $"Announced this computer at {address} over Bluetooth.");
                }
                catch (Exception ex) { Log.Write("Daemon", "Could not announce the address over Bluetooth", ex); }
            });
        }

        /// <summary>Keeps the shared link state in step with the fabric, for every screen that reads it.</summary>
        private static void OnFabricChanged()
        {
            var fabric = _fabric;
            if (fabric == null) return;

            var wifi = fabric.Links.FirstOrDefault(l => l.RouteOf(RouteKind.WiFi)?.State == RouteState.Established);
            var radio = fabric.Links.FirstOrDefault(l => l.LiveRoutes.Any(r => r.Kind != RouteKind.WiFi));

            Links.SetWiFi(wifi != null, wifi?.Peer.Name);
            Links.SetBle(radio != null, radio?.Peer.Name);
        }

        /// <summary>The Bluetooth service this machine publishes, for peers that connect to it.</summary>
        private static WindowsBleTransport? _bleTransport;

        /// <summary>
        /// The Bluetooth link this machine opens, for peers it should connect to instead.
        ///
        /// Which of the two applies to a given peer is decided by <see cref="BleRoleRules"/>
        /// rather than by platform. Without it, two laptops would both advertise and neither
        /// would ever go looking.
        /// </summary>

        private static CancellationTokenSource? _linkCts;
        private static ClipboardWorker? _clipboard;
        private static ClipboardListenerWindow? _listener;
        private static Forms.NotifyIcon? _trayIcon;
        private static MainWindow? _window;
        private static Wpf.Application? _app;
        private static Mutex? _instanceMutex;

        private static readonly SyncActivityLog _activity = new(capacity: 12);
        private static readonly EchoSuppressor _echo = new(TimeSpan.FromSeconds(10));

        private static PeerSecurity? _security;
        private static bool _trayHintShown;

        /// <summary>
        /// Which link is carrying a peer, and whether anything is.
        ///
        /// The type is CoreLib's and the Linux head holds one of its own. It used to be a static
        /// class here, which is safe in a process that is one device and is not safe anywhere
        /// else - so the rule moved into shared code and only the instance stayed behind.
        /// </summary>
        public static LinkState Links { get; } = new();

        /// <summary>Which links this computer is allowed to offer, kept in the registry.</summary>
        public static TransportSettings Transports { get; } = new(new RegistryTransportPreferenceStore());

        /// <summary>
        /// File transfer, both directions.
        ///
        /// Wi-Fi only: at roughly 6.7 KB/s a photograph would take a quarter of an hour over
        /// Bluetooth, so an offer that finds only Bluetooth up asks the peer to raise Wi-Fi with
        /// the wake frame that already exists rather than promising something the tier cannot do.
        /// </summary>
        private static FileTransferService? _files;

        private static readonly BrowseService _browse = new();

        /// <summary>Browsing this machine's shared folders, and other devices' in return.</summary>
        public static BrowseService Browse => _browse;

        public static FileTransferService? Files => _files;

        public static string LogDirectory { get; private set; } = "";

        /// <summary>This device's identity and the devices it is paired with. Null before startup.</summary>
        public static PeerSecurity? Security => _security;

        /// <summary>The route table, for the device list to ask per peer rather than per app.</summary>
        public static MeshFabric? Fabric => _fabric;

        /// <summary>True when this peer has a socket, as opposed to any link at all.</summary>
        public static bool IsWiFiConnectedTo(string fingerprint) =>
            _fabric?.LinkTo(fingerprint)?.RouteOf(RouteKind.WiFi)?.State == RouteState.Established;

        /// <summary>True when this peer is reachable over the radio, either way round.</summary>
        public static bool IsBluetoothConnectedTo(string fingerprint) =>
            _fabric?.LinkTo(fingerprint)?.LiveRoutes.Any(r => r.Kind != RouteKind.WiFi) == true;

        public static bool IsConnectedTo(string fingerprint) => _fabric?.IsConnectedTo(fingerprint) == true;

        /// <summary>Everything about reachability, for a diagnostics view or a log line.</summary>
        public static MeshHealth? Health => _fabric == null || _supervisor == null ? null : MeshHealth.Of(
            _fabric, SystemClock.Instance, _supervisor.LastPassUtc, _supervisor.Passes, _supervisor.Restarts,
            _radio?.Status ?? "no adapter", _scheduler?.LiveCentralLinks ?? 0,
            _scheduler == null ? 0 : _fabric.Timings.MaxBleCentralLinks,
            _scheduler?.IsAdvertising ?? false, _scheduler?.LastRound ?? default,
            RoutePolicy.Plan(_security!.Peers.Peers, CurrentConditions(), DateTime.UtcNow).Routes);

        [STAThread]
        static void Main(string[] args)
        {
            // A second copy would fight for port 45001 and put a duplicate icon in the tray.
            _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutex, out bool isFirst);
            if (!isFirst)
            {
                _instanceMutex.Dispose();
                return;
            }

            ConfigureLogging();
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log.Write("Fatal", $"Unhandled exception: {e.ExceptionObject}");
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log.Write("Fatal", "Unobserved task exception", e.Exception);
                e.SetObserved();
            };

            bool isStartup = args.Length > 0 && args[0] == "--startup";
            EnableStartupOnFirstRunOnly();

            _clipboard = new ClipboardWorker();
            _listener = new ClipboardListenerWindow();

            // Loaded before anything can connect: the identity is what every session key is
            // derived from and what decides whether a peer is let in at all. Wrapped by DPAPI
            // so it is not a plain key file sitting in a predictable path under LOCALAPPDATA.
            _security = PeerSecurity.LoadOrCreate(LogDirectory, new WindowsKeyProtector());

            // One link per paired device, and this machine both listens and dials. Nothing here
            // is a server: which side accepts a given link is settled per connection, so a
            // laptop can pair with a laptop as readily as with a phone.
            _fabric = new MeshFabric(_security, () => _bleCapability);
            _wifi = new WiFiRouteProvider(_security)
            {
                LocalDeviceName = Environment.MachineName,
                LocalCapability = () => _bleCapability,
            };
            _fabric.AddProvider(_wifi);

            _discovery = new MeshDiscovery(_security);

            _supervisor = new LinkSupervisor(_fabric, CurrentConditions)
            {
                WantedCentralPeersChanged = peers => _scheduler?.SetWanted(peers),
                AdvertisingWanted = wanted => _ = ApplyAdvertisingAsync(wanted),
            };

            // A device refused a moment ago for not being paired is the same device being
            // confirmed now, and it must not sit out a cooldown after that.
            _security.Peers.Changed += () =>
            {
                _scheduler?.Cooldowns.Clear();

                // Minted here as well as at startup: a fresh install has nothing paired when it
                // starts, and there is no point advertising a beacon for a mesh of one.
                if (_discovery?.MintIfDue() != null) _ = ApplyAdvertisingAsync(_scheduler?.IsAdvertising ?? false);

                _supervisor?.Signal();
            };

            string pairingCode = _security.Identity.PublicKey;
            string localIp = NetworkUtil.GetLocalLanAddress() ?? "Unavailable";

            Log.Write("Daemon",
                $"Starting. LAN address {localIp}, identity {_security.Identity.ShortFingerprint}, " +
                $"{_security.Peers.Count} paired device(s), startup launch: {isStartup}");

            // Named on first run so there is something to call the set of devices from the
            // outset. The user can change it; a device that joins later adopts whatever this
            // one is called, because the pairing code carries it.
            if (string.IsNullOrWhiteSpace(_security.Peers.MeshName))
            {
                _security.Peers.MeshName = DefaultMeshName();
            }

            // With nothing paired there is no way for a first device to be let in, so the
            // window opens itself on a fresh install. It lapses on its own.
            if (_security.Peers.IsEmpty) _security.Pairing.Open();

            WireClipboardCapture();
            WirePayloadReceive();
            WireFileTransfer();

            // Dismissing here dismisses there, which is what makes mirroring feel finished
            // rather than like a second inbox to empty separately.
            MirroredNotifications.DismissOnPeer = (fingerprint, key) =>
                _fabric?.SendToAsync(fingerprint, SyncContent.NotificationDismiss,
                                   NotificationProtocol.BuildDismiss(key)) ?? Task.CompletedTask;

            _ = Task.Run(InitialiseNetworkAsync);

            // A GATT service registration outlives a process that dies without releasing it,
            // and the phone then keeps discovering the orphan instead of the next instance:
            // it connects, subscribes, reports success, and nothing crosses in either
            // direction. Releasing it on every exit path we control keeps that to genuine
            // crashes and Task Manager kills, which user code cannot intercept.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => ReleaseBluetooth();

            _app = new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
            _app.SessionEnding += (_, _) =>
            {
                Log.Write("Daemon", "Windows is signing out or shutting down.");
                ReleaseBluetooth();
            };
            _app.Resources.MergedDictionaries.Add(new Wpf.ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/WinDaemon;component/Themes/MeshTheme.xaml", UriKind.Absolute)
            });
            ThemeManager.Apply(_app);

            _window = new MainWindow(localIp, pairingCode, _activity);
            _window.ExitRequested += ExitApp;

            CreateTrayIcon();

            // Both transports report into LinkState; the tray and the dashboard read
            // only that, so a Bluetooth-only link shows as connected like any other.
            _fabric.Changed += OnFabricChanged;

            _fabric.PeerConnected += (link, route) =>
            {
                OnFabricChanged();

                // Only over the socket. On a radio link the address is announced by the link
                // itself once its hello has crossed, because before that there is no key to seal
                // it with.
                if (route.Kind == RouteKind.WiFi) AnnounceAddressOverMesh(link.Fingerprint);

                // The mesh key rides the links that already exist, which is what makes the
                // upgrade cost no re-pair.
                OfferMeshKey(link.Fingerprint);
            };

            _fabric.PeerDisconnected += (link, kind, reason) =>
            {
                string fingerprint = link.Fingerprint;
                Log.Write("Daemon", $"{DeviceIdentity.Shorten(fingerprint)} lost its {kind} route: {reason}.");
                OnFabricChanged();

                // A device that has gone is no longer showing what it was showing, and a stale
                // mirror is worse than none - it invites you to act on something already dealt
                // with, and dismissing it would reach nobody.
                MirroredNotifications.ClearFrom(fingerprint);
            };
            Links.Changed += UpdateTrayState;

            if (!isStartup) _window.ShowDashboard();

            _app.Run();
            Shutdown();
        }

        // ────────────────────────────── tray

        /// <summary>
        /// "Surya's Mesh", from the signed-in user. A guess, and a better starting point than
        /// an empty box - most people will never change it, and the ones who do would have had
        /// to type something anyway.
        /// </summary>
        private static string DefaultMeshName()
        {
            string user = Environment.UserName;
            if (string.IsNullOrWhiteSpace(user)) return "My Mesh";

            user = char.ToUpperInvariant(user[0]) + user.Substring(1);
            return user.EndsWith("s", StringComparison.OrdinalIgnoreCase) ? $"{user}' Mesh" : $"{user}'s Mesh";
        }

        private static void CreateTrayIcon()
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open Mesh Sync", null, (_, _) => ShowDashboardFromTray());
            menu.Items.Add("Send a file…", null, (_, _) => PromptForFileToSend());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Quit", null, (_, _) => ExitApp());

            _trayIcon = new Forms.NotifyIcon
            {
                Icon = TrayIcons.Create(connected: false),
                ContextMenuStrip = menu,
                Visible = true,
                Text = "Mesh Sync - waiting for a device"
            };
            _trayIcon.DoubleClick += (_, _) => ShowDashboardFromTray();
        }

        private static void UpdateTrayState()
        {
            if (_trayIcon == null || _app == null) return;

            _app.Dispatcher.BeginInvoke(() =>
            {
                bool connected = Links.IsConnected;
                var previous = _trayIcon.Icon;
                _trayIcon.Icon = TrayIcons.Create(connected);
                previous?.Dispose();

                // Named after the mesh, not after whichever device answered - the tray text is
                // the same sentence the window shows, and both are about the set rather than one
                // member of it.
                string mesh = _security?.Peers.MeshNameOrDefault ?? "your mesh";
                string via = Links.ActiveLink == LinkKind.Ble ? " over Bluetooth" : "";
                _trayIcon.Text = connected
                    ? $"Mesh Sync - {mesh} connected{via}"
                    : $"Mesh Sync - {mesh} unreachable";
            });
        }

        private static void ShowDashboardFromTray() =>
            _app?.Dispatcher.BeginInvoke(() => _window?.ShowDashboard());

        /// <summary>
        /// Asks a device to make a noise so it can be found.
        ///
        /// Goes over whichever tier is up. One byte fits comfortably in a Bluetooth frame, which
        /// is the point: the moment you most want to find a device is the moment it is not on
        /// any network.
        /// </summary>
        public static async Task<bool> RingAsync(string fingerprint, bool on)
        {
            var fabric = _fabric;
            if (fabric == null) return false;

            // One call, whichever tier is up. The peer link picks the route - Wi-Fi first because
            // it carries anything, the radio when that is what exists - so this no longer has to
            // know which half of the tier is holding the device it is trying to find.
            return await fabric.SendToAsync(fingerprint, SyncContent.Ring, [on ? (byte)1 : (byte)0])
                .ConfigureAwait(false);
        }

        /// <summary>Asks one peer, over whichever radio half is carrying it, to raise Wi-Fi.</summary>
        private static async Task AskPeerForWiFiAsync(string fingerprint)
        {
            var link = _fabric?.LinkTo(fingerprint);
            if (link == null) return;

            foreach (var route in link.LiveRoutes)
            {
                switch (route)
                {
                    case WindowsBleCentral central:
                        await central.RequestWiFiAsync().ConfigureAwait(false);
                        return;

                    case WindowsBleServerRoute inbound:
                        await inbound.RequestWiFiAsync().ConfigureAwait(false);
                        return;
                }
            }
        }

        /// <summary>Asks for a file and sends it to every connected device.</summary>
        public static void PromptForFileToSend() =>
            _app?.Dispatcher.BeginInvoke(() =>
            {
                var picker = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Send a file to your mesh",
                    CheckFileExists = true,
                    Multiselect = false
                };

                if (picker.ShowDialog() != true) return;
                _ = SendFileAsync(picker.FileName);
            });

        /// <summary>
        /// Sends a file to every connected device, raising Wi-Fi first when only Bluetooth is up.
        ///
        /// Fanned out rather than sent once: there is a key per connection, so a file goes to
        /// each device as its own stream. That is genuinely N times the bytes, and it is the
        /// cost of a paired device being unable to read another pair's traffic.
        /// </summary>
        public static async Task SendFileAsync(string path)
        {
            var files = _files;
            var fabric = _fabric;
            if (files == null || fabric == null) return;

            // Per peer, and that is the change. A file needs a socket, so every peer that has
            // only the radio is asked to raise one - for itself. This used to ask whichever half
            // of the tier happened to be live and then wait on the aggregate count, so one peer
            // coming up satisfied the wait for all of them.
            var needing = fabric.NeedingWiFiFor(int.MaxValue)
                .Select(l => l.Fingerprint)
                .Where(f => !IsWiFiConnectedTo(f))
                .ToList();

            foreach (string fingerprint in needing)
            {
                Log.Write("Daemon",
                    $"Only Bluetooth reaches {DeviceIdentity.Shorten(fingerprint)}; asking it to raise Wi-Fi before sending a file.");
                await AskPeerForWiFiAsync(fingerprint).ConfigureAwait(false);
            }

            if (needing.Count > 0)
            {
                _supervisor?.Signal();

                var deadline = DateTime.UtcNow + WiFiWakeTimeout;
                while (DateTime.UtcNow < deadline && needing.Any(f => !IsWiFiConnectedTo(f)))
                {
                    await Task.Delay(200).ConfigureAwait(false);
                }
            }

            var targets = fabric.Links
                .Where(l => IsWiFiConnectedTo(l.Fingerprint))
                .Select(l => l.Fingerprint)
                .ToList();

            if (targets.Count == 0)
            {
                Log.Write("Daemon", $"Nothing was reachable over Wi-Fi, so \"{Path.GetFileName(path)}\" was not sent.");
                return;
            }

            foreach (string fingerprint in targets)
            {
                var result = await files.SendAsync(fingerprint, path).ConfigureAwait(false);

                if (result == FileSendResult.Sent)
                {
                    try
                    {
                        _activity.Record(SyncDirection.Sent, SyncItemKind.File,
                                         new FileInfo(path).Length, Path.GetFileName(path));
                    }
                    catch { }
                }
                else
                {
                    Log.Write("Daemon",
                        $"\"{Path.GetFileName(path)}\" to {DeviceIdentity.Shorten(fingerprint)}: {result}.");
                }
            }
        }

        /// <summary>Shown once, the first time the window is dismissed, so it is a hint and not nagging.</summary>
        public static void NotifyHiddenToTray()
        {
            if (_trayHintShown || _trayIcon == null) return;
            _trayHintShown = true;
            _trayIcon.ShowBalloonTip(2500, "Still running",
                "Mesh Sync keeps syncing in the background. Double-click the tray icon to reopen.",
                Forms.ToolTipIcon.Info);
        }

        // ────────────────────────────── network

        private static async Task InitialiseNetworkAsync()
        {
            _linkCts = new CancellationTokenSource();
            var token = _linkCts.Token;

            try
            {
                // No key derivation here any more. Keys are per peer and derived by agreement
                // against the public key each one presents, so there is nothing to prepare until
                // a peer actually turns up.
                _wifi!.IsAvailable = Transports.AllowsWiFi;

                if (Transports.AllowsWiFi) await _wifi.StartListeningAsync(token).ConfigureAwait(false);
                else Log.Write("Daemon", "Wi-Fi listener not started: the transport preference is Bluetooth only.");
            }
            catch (Exception ex)
            {
                Log.Write("Daemon", "Network initialisation failed", ex);
            }

            // The radio runs alongside Wi-Fi rather than instead of it, so text still syncs when
            // there is no network at all. Failure here must not affect the socket path.
            try
            {
                _bleTransport = new WindowsBleTransport
                {
                    LocalPublicKey = _security!.Identity.PublicKey,
                    LocalDeviceName = Environment.MachineName,
                    LocalMeshName = _security.Peers.MeshName,
                    OpenSession = OpenBleSession
                };

                _radio = new WindowsBleRadio { Prepare = PrepareLink };

                _scheduler = new BleRadioScheduler(_radio)
                {
                    // Before there is a mesh key this accepts everything, which is how every
                    // build before this one behaved. A beacon that verifies is a fast path; a
                    // beacon that is somebody else's is the one case worth refusing outright.
                    BeaconFilter = _discovery!.Accepts,
                    BeaconRank = _discovery.RankOf,
                };

                _fabric!.AddProvider(_scheduler.CentralRoutes);
                _fabric.AddProvider(_scheduler.InboundRoutes);

                if (Transports.AllowsBle)
                {
                    await _bleTransport.StartListeningAsync().ConfigureAwait(false);

                    // Honest rather than optimistic: the capability the arbiter sees comes from
                    // the server having actually published, not from the adapter existing.
                    _bleCapability = BleCapability.Both;
                    _radio.Capability = _bleCapability;

                    WireBleTransport(_bleTransport);
                }
                else
                {
                    _radio.IsAvailable = false;
                    Log.Write("Daemon", "Bluetooth not advertised: the transport preference is Wi-Fi only.");
                }

                _ = Task.Run(() => _scheduler.RunAsync(token), CancellationToken.None)
                        .ContinueWith(t =>
                        {
                            if (t.Exception != null)
                                Log.Write("Daemon", "The Bluetooth scheduler stopped", t.Exception.GetBaseException());
                        }, TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception ex)
            {
                Log.Write("Daemon", "BLE unavailable, continuing on Wi-Fi only", ex);
            }

            // Minted on the first run that has peers and no key, then offered over the links that
            // already exist - which is what makes the upgrade cost no re-pair.
            _discovery?.MintIfDue();

            _ = Task.Run(() => _supervisor!.RunAsync(token), CancellationToken.None);
            _ = Task.Run(() => InboundHeartbeatAsync(token), CancellationToken.None);

            Transports.Changed += ApplyTransportPreference;
        }

        /// <summary>
        /// Runs the inbound half's liveness check, which has no loop of its own.
        ///
        /// It was written to be called from a loop and never was, which left a peripheral link
        /// whose central had walked away showing as connected forever.
        /// </summary>
        private static async Task InboundHeartbeatAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try { _inbound?.CheckHeartbeat(); }
                catch (Exception ex) { Log.Write("Daemon", "The inbound liveness check failed", ex); }

                try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        /// <summary>
        /// Applies a preference change without a restart, so the control in the window means
        /// something the moment it is used.
        ///
        /// <para>Closing a tier is now a matter of telling the provider it is unavailable and
        /// letting the next reconcile pass close what policy no longer wants. That is one code
        /// path instead of two, and it cannot leave a route open that nothing owns.</para>
        /// </summary>
        private static async void ApplyTransportPreference(TransportPreference preference)
        {
            try
            {
                if (_wifi != null)
                {
                    _wifi.IsAvailable = Transports.AllowsWiFi;

                    if (Transports.AllowsWiFi) await _wifi.StartListeningAsync().ConfigureAwait(false);
                    else _wifi.StopListening();
                }

                if (_bleTransport != null && _radio != null)
                {
                    if (Transports.AllowsBle)
                    {
                        await _bleTransport.StartListeningAsync().ConfigureAwait(false);
                        _radio.IsAvailable = true;
                        _bleCapability = BleCapability.Both;
                        _radio.Capability = _bleCapability;
                    }
                    else
                    {
                        _radio.IsAvailable = false;
                        await _bleTransport.DisconnectAsync().ConfigureAwait(false);
                    }
                }

                _supervisor?.Signal();
                Log.Write("Daemon", $"Applied transport preference {preference}.");
            }
            catch (Exception ex)
            {
                Log.Write("Daemon", "Could not apply the transport preference", ex);
            }
        }

        /// <summary>
        /// BLE shares the clipboard pipeline with TCP. Payloads are identical on both, so
        /// crypto, echo suppression and the activity log need no transport-specific paths.
        /// </summary>
        /// <summary>
        /// Authorises a Bluetooth peer and agrees this link's key in one step.
        ///
        /// Both halves of the tier use it, and both get the same answer: a device this computer
        /// has not paired with never reaches the point of having a session to encrypt with.
        /// </summary>
        private static PeerSession? OpenBleSession(string peerPublicKey, string peerName,
                                                   string peerEphemeral, EphemeralKeyPair localEphemeral)
        {
            var security = _security;
            if (security == null) return null;

            // The name goes in so a device waiting to be confirmed can say what it calls itself.
            // Without it the prompt reads "It did not say what it is called" about a device that
            // did say - which makes the one screen where the user is being asked to trust
            // something less informative than it has to be.
            return security.Authorise(peerPublicKey, peerName)
                ? security.OpenSession(peerPublicKey, localEphemeral, peerEphemeral)
                : null;
        }

        /// <summary>
        /// Turns the GATT server's link into an ordinary route in the fabric.
        ///
        /// <para>What this replaces is a set of handlers that wrote straight into
        /// <c>LinkState</c>: the server reported connected the moment a central subscribed,
        /// whatever it turned out to be, and the central scan loop was gated on that - so a
        /// device from another mesh subscribing here stopped this machine looking for its own
        /// peers.</para>
        /// </summary>
        private static void WireBleTransport(WindowsBleTransport ble)
        {
            ble.LocalCapability = _bleCapability;

            ble.ClientConnected += (_, _) =>
            {
                // A route, not a connection. It sits in Handshaking until the hello crosses, and
                // is closed on the shared deadline if it never does.
                var route = new WindowsBleServerRoute(ble);

                route.Identified += (r, e) =>
                {
                    OnRadioIdentified(e);
                    AnnounceAddressOverRadio(payload => r.SendAsync(SyncContent.Address, payload));
                };

                _inbound = route;
                Log.Write("Daemon", "A peer connected to this computer over Bluetooth.");
                _radio?.PublishInbound(route);
            };

            ble.WiFiRequested += (_, _) => RaiseWiFiFor(ble.RemoteFingerprint);
        }

        private static void WireClipboardCapture()
        {
            _listener!.ClipboardUpdated += () =>
            {
                _clipboard!.CaptureAsync(capture =>
                {
                    try
                    {
                        switch (capture.Kind)
                        {
                            case ClipboardKind.Text:
                                SendCapture(ContentText, System.Text.Encoding.UTF8.GetBytes(capture.TextValue!), capture.TextValue);
                                break;
                            case ClipboardKind.Image:
                                SendCapture(ContentImage, capture.ImageValue!, null);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Write("Daemon", "Clipboard capture handling failed", ex);
                    }
                });
            };
        }

        /// <summary>
        /// How long to wait for the phone to raise Wi-Fi after being asked.
        ///
        /// Covers the notification reaching it, the decision, and the socket coming back the
        /// other way. Bounded because the request is not a guarantee: the phone may be out of
        /// Wi-Fi range or have it switched off, in which case nothing will ever arrive.
        /// </summary>
        private static readonly TimeSpan WiFiWakeTimeout = TimeSpan.FromSeconds(15);

        private static void SendCapture(byte contentType, byte[] body, string? textContent)
        {
            if (_security == null) return;

            // Nothing at all is reachable, so there is no point encrypting anything.
            if (_fabric?.IsConnectedToAny != true) return;

            // One gate for both "this is our own injection bouncing back" and "this is a
            // repeat WM_CLIPBOARDUPDATE for a copy we just sent". Checking them separately
            // let an echo return early and skip the duplicate check, so the next
            // notification for that same content passed both and was transmitted.
            var kind = contentType == ContentImage ? SyncItemKind.Image : SyncItemKind.Text;
            if (!_echo.ShouldSend(body, kind))
            {
                Log.Write("Daemon", "Suppressed duplicate or echoed clipboard content.");
                return;
            }

            _ = Task.Run(async () =>
            {
                // Fanned out rather than sent: there is one key per pair, so a copy goes to
                // every connected device as its own ciphertext. Nobody relays, so there is no
                // routing to get wrong and no loop to prevent.
                // Once per peer, not once per link, and it is structural now: a peer reachable
                // over both tiers has one PeerLink, which picks the route. Sending over every
                // link separately delivered the clipboard twice to a peer holding two, and the
                // echo suppressor is on the sending side so the receiver had no defence.
                int sent = await _fabric!.BroadcastAsync(contentType, body).ConfigureAwait(false);

                // Whatever the radio could not carry - an image, at 6.7 KB/s - asks its peer to
                // raise Wi-Fi and follows up over that.
                sent += await SendByRaisingWiFiAsync(contentType, body).ConfigureAwait(false);

                if (sent == 0)
                {
                    Log.Write("Daemon", "Nothing was reachable, so the item was dropped.");
                    return;
                }

                _activity.Record(SyncDirection.Sent,
                    contentType == ContentText ? SyncItemKind.Text : SyncItemKind.Image,
                    body.Length, textContent);

                Log.Write("Daemon",
                    $"Sent {(contentType == ContentText ? "text" : "image")} to {sent} device(s), {body.Length} bytes.");
            });
        }

        /// <summary>
        /// Reaches peers whose only link cannot carry this payload, by asking them for Wi-Fi.
        ///
        /// <para>An image is tens to hundreds of kilobytes and the radio does about 6.7 KB/s, so
        /// a peer holding only a radio link needs a socket. This machine may not be able to dial
        /// it - it may never have had an address for it - so the useful move is to ask it to come
        /// here, over the link that is already open.</para>
        ///
        /// <para><b>Per peer.</b> This used to ask whichever half of the tier happened to be live
        /// and then wait on whether <em>anything</em> had Wi-Fi, so one peer coming up satisfied
        /// the wait for every other.</para>
        /// </summary>
        private static async Task<int> SendByRaisingWiFiAsync(byte contentType, byte[] body)
        {
            var fabric = _fabric;
            if (fabric == null || !Transports.AllowsWiFi) return 0;

            var needing = fabric.NeedingWiFiFor(body.Length).Select(l => l.Fingerprint).ToList();
            if (needing.Count == 0) return 0;

            foreach (string fingerprint in needing)
            {
                Log.Write("Daemon",
                    $"{body.Length} bytes is over the Bluetooth ceiling for {DeviceIdentity.Shorten(fingerprint)}; asking it to raise Wi-Fi.");

                lock (_demandGate) _wifiWake[fingerprint] = DateTime.UtcNow.Add(WiFiWakeWindow);
                await AskPeerForWiFiAsync(fingerprint).ConfigureAwait(false);
            }

            _supervisor?.Signal();

            var deadline = DateTime.UtcNow + WiFiWakeTimeout;
            while (DateTime.UtcNow < deadline && needing.Any(f => !IsWiFiConnectedTo(f)))
            {
                await Task.Delay(200).ConfigureAwait(false);
            }

            int sent = 0;

            foreach (string fingerprint in needing)
            {
                if (!IsWiFiConnectedTo(fingerprint))
                {
                    Log.Write("Daemon",
                        $"{DeviceIdentity.Shorten(fingerprint)} did not raise Wi-Fi within {WiFiWakeTimeout.TotalSeconds:F0}s; the item was dropped for it.");
                    continue;
                }

                if (await fabric.SendToAsync(fingerprint, contentType, body).ConfigureAwait(false)) sent++;
            }

            return sent;
        }

        /// <summary>Offers this machine's mesh discovery key to one peer, over the ordinary path.</summary>
        private static void OfferMeshKey(string fingerprint)
        {
            var key = _security?.Peers.MeshKey;
            if (key == null || _fabric == null) return;

            _ = Task.Run(async () =>
            {
                try { await _fabric.SendToAsync(fingerprint, SyncContent.MeshKeyOffer, key).ConfigureAwait(false); }
                catch (Exception ex) { Log.Write("Daemon", "Offering the mesh key failed", ex); }
            });
        }

        private static void WireFileTransfer()
        {
            _files = new FileTransferService(
                Path.Combine(LogDirectory, "incoming"),
                (fingerprint, contentType, body, token) =>
                    _fabric?.SendToAsync(fingerprint, contentType, body, token) ?? Task.FromResult(false));

            _files.FileReceived += file =>
            {
                try
                {
                    string destination = UniquePath(DownloadsFolder(), file.Name);
                    File.Move(file.Path, destination);

                    _activity.Record(SyncDirection.Received, SyncItemKind.File, file.Size, file.Name);
                    Log.Write("Daemon", $"Saved \"{file.Name}\" to {destination}.");
                }
                catch (Exception ex)
                {
                    Log.Write("Daemon", $"Could not save \"{file.Name}\"", ex);
                    try { File.Delete(file.Path); } catch { }
                }
            };

            _files.FileFailed += (name, reason) =>
                Log.Write("Daemon", $"\"{name}\" did not arrive: {reason}.");

            WireBrowsing();
        }

        /// <summary>
        /// Gives the browse service a way to talk, and a first shared folder.
        ///
        /// <para>Downloads is shared out of the box because it is where this app already puts
        /// everything it receives, so the alternative is a feature that does nothing until
        /// configured - which is the mistake notification mirroring made and had to be undone.
        /// Anything else is the user's to add, and this one is theirs to remove.</para>
        /// </summary>
        private static void WireBrowsing()
        {
            _browse.Send = (fingerprint, contentType, body) =>
                _fabric?.SendToAsync(fingerprint, contentType, body) ?? Task.FromResult(false);

            _browse.SendFile = async (fingerprint, path) =>
            {
                var files = _files;
                if (files == null) return;

                await files.SendAsync(fingerprint, path).ConfigureAwait(false);
            };

            _browse.Shared.Add(DownloadsFolder(), "Downloads");
        }

        private static string DownloadsFolder()
        {
            try
            {
                // UserProfile plus Downloads rather than a known folder id, because .NET exposes
                // no SpecialFolder for Downloads and the shell API is not worth a P/Invoke here.
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

                if (Directory.Exists(path)) return path;
            }
            catch { }

            string fallback = Path.Combine(LogDirectory, "received");
            Directory.CreateDirectory(fallback);
            return fallback;
        }

        /// <summary>A path that is not already taken, so nothing is quietly overwritten.</summary>
        private static string UniquePath(string folder, string name)
        {
            string candidate = Path.Combine(folder, name);
            if (!File.Exists(candidate)) return candidate;

            string stem = Path.GetFileNameWithoutExtension(name);
            string extension = Path.GetExtension(name);

            for (int attempt = 2; attempt < 1000; attempt++)
            {
                candidate = Path.Combine(folder, $"{stem} ({attempt}){extension}");
                if (!File.Exists(candidate)) return candidate;
            }

            return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){extension}");
        }

        private static void WirePayloadReceive()
        {
            // The route decrypts before it raises this, because the session a payload arrived on
            // is also the answer to which device sent it. One handler for both tiers now: the
            // radio used to have its own, which is how the two ended up dispatching differently.
            _fabric!.PayloadReceived += (_, payload) =>
                Apply(payload.Peer, payload.ContentType, payload.Body,
                      payload.Via == RouteKind.WiFi ? "Wi-Fi" : "Bluetooth");
        }

        private static void Apply(PeerRecord peer, byte contentType, byte[] body, string via)
        {
            string from = peer.Name ?? DeviceIdentity.Shorten(peer.Fingerprint);

            // File frames go straight through: they are not clipboard content, so noting them
            // as an inbound copy would poison the echo suppressor with bytes nobody copied.
            if (_files?.Handle(peer.Fingerprint, contentType, body) == true) return;

            // Browsing frames are the same case: a listing is not something anybody copied.
            if (_browse.Handle(peer.Fingerprint, contentType, body)) return;

            _echo.NoteInbound(body, contentType == ContentImage ? SyncItemKind.Image : SyncItemKind.Text);

            switch (contentType)
            {
                case ContentText:
                    string text = System.Text.Encoding.UTF8.GetString(body);
                    _clipboard!.SetText(text);
                    _activity.Record(SyncDirection.Received, SyncItemKind.Text, body.Length, text);
                    Log.Write("Daemon", $"Received text from {from} over {via}, {body.Length} bytes.");
                    break;
                case ContentImage:
                    _clipboard!.SetImage(body);
                    _activity.Record(SyncDirection.Received, SyncItemKind.Image, body.Length);
                    Log.Write("Daemon", $"Received image from {from} over {via}, {body.Length} bytes.");
                    break;
                case SyncContent.Address:
                    NoteAnnouncedAddress(peer.Fingerprint, body, from);
                    break;

                case SyncContent.MeshKeyOffer:
                    // Lowest key wins, so two halves of a mesh that minted separately converge in
                    // one exchange - and a device that adopts a new one re-advertises within an
                    // epoch. It rides the ordinary authenticated path, so only a paired device can
                    // ever offer one.
                    if (_discovery?.Adopt(body) == true)
                    {
                        Log.Write("Daemon", $"Adopted the mesh discovery key {peer.Name} offered.");
                        _ = ApplyAdvertisingAsync(_scheduler?.IsAdvertising ?? false);

                        foreach (string other in _fabric?.ConnectedPeers ?? Array.Empty<string>())
                        {
                            if (!string.Equals(other, peer.Fingerprint, StringComparison.OrdinalIgnoreCase))
                                OfferMeshKey(other);
                        }
                    }
                    break;
                case SyncContent.Notification:
                    if (NotificationProtocol.TryParse(body, out var mirrored) && mirrored != null)
                    {
                        MirroredNotifications.Add(peer.Fingerprint, from, mirrored);
                    }
                    break;
                case SyncContent.NotificationDismiss:
                    if (NotificationProtocol.TryParseDismiss(body, out string dismissedKey))
                    {
                        MirroredNotifications.Remove(dismissedKey, tellThePeer: false);
                    }
                    break;
                case SyncContent.Ring:
                    // Authenticated by having arrived at all: it opened under this connection's
                    // key, so it came from a device this one is paired with.
                    if (body.Length > 0 && body[0] != 0)
                    {
                        Ringer.Start(from);
                        // Brought to the front, because the banner with the Stop on it is no use
                        // behind whatever the person was doing while their laptop started
                        // shrieking at them.
                        ShowDashboardFromTray();
                    }
                    else
                    {
                        Ringer.Stop();
                    }
                    break;
                default:
                    Log.Write("Daemon", $"Ignoring unknown content type {contentType} from {from}.");
                    break;
            }
        }

        /// <summary>
        /// Records where a peer says it is reachable.
        ///
        /// Parsed rather than trusted: an address is the one field that decides where the next
        /// connection goes, so anything that is not literally an IP address is discarded. It
        /// arrives inside an authenticated payload, so it can only have come from a paired
        /// device, but a paired device with a bug should not be able to poison the registry
        /// either.
        /// </summary>
        private static void NoteAnnouncedAddress(string fingerprint, byte[] body, string from)
        {
            string address = System.Text.Encoding.UTF8.GetString(body).Trim();

            if (!System.Net.IPAddress.TryParse(address, out _))
            {
                Log.Write("Daemon", $"Ignoring an implausible address announced by {from}.");
                return;
            }

            _security?.Peers.NoteSeen(fingerprint, address);
            Log.Write("Daemon", $"{from} is reachable at {address}.");
        }

        /// <summary>
        /// Tells a peer where this computer is reachable, so a DHCP lease change cannot strand it.
        ///
        /// Sent on every socket that comes up, because this side cannot know what the peer last
        /// recorded and the payload is a few dozen bytes on a link that is already open.
        /// </summary>
        private static void AnnounceAddressOverMesh(string fingerprint)
        {
            string? address = NetworkUtil.GetLocalLanAddress();
            if (string.IsNullOrEmpty(address) || _fabric == null) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await _fabric.SendToAsync(fingerprint, SyncContent.Address,
                        System.Text.Encoding.UTF8.GetBytes(address)).ConfigureAwait(false);

                    Log.Write("Daemon", $"Announced this computer at {address}.");
                }
                catch (Exception ex)
                {
                    // Informational. A failure costs nothing until the address changes.
                    Log.Write("Daemon", "Could not announce this computer's address", ex);
                }
            });
        }

        /// <summary>
        /// Asks the supervisor to reconcile now rather than at the next interval.
        ///
        /// Confirming a device by hand should reach it now rather than up to a whole interval
        /// later, which reads as the confirmation not having worked.
        /// </summary>
        public static void SignalDial() => _supervisor?.Signal();

        // ────────────────────────────── lifetime

        private static void ExitApp()
        {
            if (_trayIcon != null) _trayIcon.Visible = false;
            _app?.Dispatcher.BeginInvoke(() => _app.Shutdown());
        }

        private static int _bluetoothReleased;

        /// <summary>
        /// Stops advertising and tears down the GATT service. Safe to call more than once,
        /// because several exit paths race to run it.
        /// </summary>
        private static void ReleaseBluetooth()
        {
            if (Interlocked.Exchange(ref _bluetoothReleased, 1) != 0) return;

            try
            {
                try { _linkCts?.Cancel(); } catch { }
                if (_scheduler != null) _scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();

                _bleTransport?.Dispose();
                Log.Write("Daemon", "Released the Bluetooth service.");
            }
            catch (Exception ex)
            {
                Log.Write("Daemon", "Releasing the Bluetooth service failed", ex);
            }
        }

        private static void Shutdown()
        {
            Log.Write("Daemon", "Shutting down.");
            ReleaseBluetooth();

            try { _window?.Teardown(); } catch { }
            try
            {
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Icon?.Dispose();
                    _trayIcon.Dispose();
                }
            }
            catch { }
            try { _listener?.Dispose(); } catch { }
            try { _linkCts?.Cancel(); _linkCts?.Dispose(); } catch { }
            try { _files?.Dispose(); } catch { }
            try { _supervisor?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            try { _inbound?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            try { _fabric?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            try { _clipboard?.Dispose(); } catch { }

            // Zeroes every cached per-peer key and disposes the private key with it.
            try { Interlocked.Exchange(ref _security, null)?.Dispose(); } catch { }

            try { _instanceMutex?.ReleaseMutex(); } catch { }
            _instanceMutex?.Dispose();
        }

        private static void ConfigureLogging()
        {
            try
            {
                LogDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeshSync");
                Directory.CreateDirectory(LogDirectory);
                string path = Path.Combine(LogDirectory, "daemon.log");

                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > 1024 * 1024)
                    {
                        string previous = Path.Combine(LogDirectory, "daemon.previous.log");
                        File.Delete(previous);
                        File.Move(path, previous);
                    }
                }
                catch { }

                var gate = new object();
                Log.Sink = line =>
                {
                    lock (gate)
                    {
                        try { File.AppendAllText(path, line + Environment.NewLine); } catch { }
                    }
                };

                Log.Write("Daemon", $"Log file: {path}");
            }
            catch
            {
                // Logging must never prevent startup.
            }
        }

        // ────────────────────────────── startup registration

        public static bool IsStartupEnabled()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(RunValueName) != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Returns the state actually in effect, so the UI can correct itself on failure.</summary>
        public static bool SetStartupEnabled(bool enable)
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                    ?? throw new InvalidOperationException("Could not open the Run key.");

                if (enable) key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\" --startup");
                else key.DeleteValue(RunValueName, throwOnMissingValue: false);

                Log.Write("Daemon", $"Run on startup {(enable ? "enabled" : "disabled")}.");
                return enable;
            }
            catch (Exception ex)
            {
                Log.Write("Daemon", "Changing the startup setting failed", ex);
                return IsStartupEnabled();
            }
        }

        private static void EnableStartupOnFirstRunOnly()
        {
            try
            {
                using RegistryKey settings = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true)
                    ?? throw new InvalidOperationException("Could not open the settings key.");

                if (settings.GetValue("StartupInitialised") != null) return;

                SetStartupEnabled(true);
                settings.SetValue("StartupInitialised", 1, RegistryValueKind.DWord);
                Log.Write("Daemon", "Enabled run-on-startup (first run).");
            }
            catch (Exception ex)
            {
                Log.Write("Daemon", "Could not configure startup", ex);
            }
        }
    }

    /// <summary>
    /// Message-only window that receives WM_CLIPBOARDUPDATE. It does no work beyond
    /// raising an event: anything slower would stall the whole message pump.
    /// </summary>
    sealed class ClipboardListenerWindow : Forms.NativeWindow, IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        private const int WM_CLIPBOARDUPDATE = 0x031D;
        private const int HWND_MESSAGE = -3;

        private bool _disposed;

        public event Action? ClipboardUpdated;

        public ClipboardListenerWindow()
        {
            CreateHandle(new Forms.CreateParams
            {
                Caption = "MeshSyncClipboardListener",
                Parent = new IntPtr(HWND_MESSAGE)
            });

            if (!AddClipboardFormatListener(Handle))
            {
                Log.Write("Clipboard", $"AddClipboardFormatListener failed, error {Marshal.GetLastWin32Error()}.");
            }
        }

        protected override void WndProc(ref Forms.Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE)
            {
                try { ClipboardUpdated?.Invoke(); }
                catch (Exception ex) { Log.Write("Clipboard", "Update handler threw", ex); }
            }

            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (Handle != IntPtr.Zero) RemoveClipboardFormatListener(Handle);
            }
            catch { }

            ClipboardUpdated = null;
            DestroyHandle();
        }
    }
}
