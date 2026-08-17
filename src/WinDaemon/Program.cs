using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CoreLib;
using CoreLib.Diagnostics;
using CoreLib.Identity;
using CoreLib.Transport;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;

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

        private static MeshLinks? _mesh;

        /// <summary>The Bluetooth service this machine publishes, for peers that connect to it.</summary>
        private static WindowsBleTransport? _bleTransport;

        /// <summary>
        /// The Bluetooth link this machine opens, for peers it should connect to instead.
        ///
        /// Which of the two applies to a given peer is decided by <see cref="BleRoleRules"/>
        /// rather than by platform. Without it, two laptops would both advertise and neither
        /// would ever go looking.
        /// </summary>
        private static WindowsBleCentral? _bleCentral;

        private static CancellationTokenSource? _dialCts;
        private static CancellationTokenSource? _bleCentralCts;
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

        public static string LogDirectory { get; private set; } = "";

        /// <summary>This device's identity and the devices it is paired with. Null before startup.</summary>
        public static PeerSecurity? Security => _security;

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
            // derived from and what decides whether a peer is let in at all.
            _security = PeerSecurity.LoadOrCreate(LogDirectory);

            // One link per paired device, and this machine both listens and dials. Nothing here
            // is a server: which side accepts a given link is settled per connection, so a
            // laptop can pair with a laptop as readily as with a phone.
            _mesh = new MeshLinks(_security) { LocalDeviceName = Environment.MachineName };

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

            // Both transports report into ConnectionState; the tray and the dashboard read
            // only that, so a Bluetooth-only link shows as connected like any other.
            _mesh.PeerConnected += peer =>
            {
                ConnectionState.SetWiFi(true, peer.Name);

                // Telling a peer where we are is what makes a lease change survivable; without
                // it the address in the QR code is the only one it will ever know.
                AnnounceAddressOverMesh(peer.Fingerprint);
            };

            _mesh.PeerDisconnected += _ => ConnectionState.SetWiFi(_mesh.IsConnectedToAny);
            ConnectionState.Changed += UpdateTrayState;

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
                bool connected = ConnectionState.IsConnected;
                var previous = _trayIcon.Icon;
                _trayIcon.Icon = TrayIcons.Create(connected);
                previous?.Dispose();

                // Named after the mesh, not after whichever device answered - the tray text is
                // the same sentence the window shows, and both are about the set rather than one
                // member of it.
                string mesh = _security?.Peers.MeshNameOrDefault ?? "your mesh";
                string via = ConnectionState.ActiveLink == LinkKind.Ble ? " over Bluetooth" : "";
                _trayIcon.Text = connected
                    ? $"Mesh Sync - {mesh} connected{via}"
                    : $"Mesh Sync - {mesh} unreachable";
            });
        }

        private static void ShowDashboardFromTray() =>
            _app?.Dispatcher.BeginInvoke(() => _window?.ShowDashboard());

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
            try
            {
                // No key derivation here any more. Keys are per peer and derived by agreement
                // against the public key each one presents, so there is nothing to prepare
                // until a peer actually turns up.
                if (TransportSettings.AllowsWiFi)
                {
                    await _mesh!.StartListeningAsync().ConfigureAwait(false);
                    StartDialLoop();
                }
                else
                {
                    Log.Write("Daemon", "Wi-Fi listener not started: the transport preference is Bluetooth only.");
                }
            }
            catch (Exception ex)
            {
                Log.Write("Daemon", "Network initialisation failed", ex);
            }

            // BLE runs alongside Wi-Fi rather than instead of it, so text still syncs when
            // there is no network at all. Failure here must not affect the TCP path.
            try
            {
                _bleTransport = new WindowsBleTransport
                {
                    LocalPublicKey = _security!.Identity.PublicKey,
                    LocalDeviceName = Environment.MachineName,
                    LocalMeshName = _security.Peers.MeshName,
                    OpenSession = OpenBleSession
                };

                WireBleTransport(_bleTransport);

                if (TransportSettings.AllowsBle)
                {
                    await _bleTransport.StartListeningAsync().ConfigureAwait(false);
                    StartBleCentralLoop();
                }
                else
                {
                    Log.Write("Daemon", "Bluetooth not advertised: the transport preference is Wi-Fi only.");
                }
            }
            catch (Exception ex)
            {
                Log.Write("Daemon", "BLE unavailable, continuing on Wi-Fi only", ex);
            }

            TransportSettings.Changed += ApplyTransportPreference;
        }

        /// <summary>
        /// Applies a preference change without a restart, so the control in the window means
        /// something the moment it is used.
        /// </summary>
        private static async void ApplyTransportPreference(TransportPreference preference)
        {
            try
            {
                if (TransportSettings.AllowsWiFi)
                {
                    await _mesh!.StartListeningAsync().ConfigureAwait(false);
                    StartDialLoop();
                }
                else
                {
                    _dialCts?.Cancel();
                    _mesh!.StopListening();
                }

                if (_bleTransport != null)
                {
                    if (TransportSettings.AllowsBle)
                    {
                        await _bleTransport.StartListeningAsync().ConfigureAwait(false);
                        StartBleCentralLoop();
                    }
                    else
                    {
                        _bleCentralCts?.Cancel();
                        RetireBleCentral();
                        await _bleTransport.DisconnectAsync().ConfigureAwait(false);
                    }
                }

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
        private static PeerSession? OpenBleSession(string peerPublicKey, string peerEphemeral,
                                                   EphemeralKeyPair localEphemeral)
        {
            var security = _security;
            if (security == null) return null;

            return security.Authorise(peerPublicKey)
                ? security.OpenSession(peerPublicKey, localEphemeral, peerEphemeral)
                : null;
        }

        private static void WireBleTransport(WindowsBleTransport ble)
        {
            ble.ClientConnected += (_, _) =>
            {
                Log.Write("Daemon", "Phone connected over BLE.");
                ConnectionState.SetBle(true);
            };

            ble.ConnectionClosed += (_, _) =>
            {
                Log.Write("Daemon", "BLE link closed.");
                ConnectionState.SetBle(false);
            };

            ble.PayloadReceived += (_, e) => HandleIncomingPayload(e.EncryptedPayload, "BLE", ble.Peer);

            // Now that the tier says who it is talking to, the address can be announced to the
            // right device rather than to whichever one happened to be the only one paired.
            ble.PeerIdentified += (_, e) =>
            {
                ConnectionState.SetBle(true);

                // Bluetooth carries the peer's name in its hello now, so a device paired only
                // over Bluetooth has something to be called in the tray and the dashboard.
                if (!string.IsNullOrWhiteSpace(e.DeviceName))
                {
                    _security?.Peers.NoteSeen(e.Fingerprint, null, e.DeviceName);
                    ConnectionState.SetBle(true, e.DeviceName);
                }

                // Announced here rather than on connect, because there is no key to seal it
                // with until the hello has crossed. The link Bluetooth is holding is also the
                // one that tells the phone where to dial when it needs Wi-Fi, which is the case
                // a lease change would otherwise break with no way back short of a rescan.
                AnnounceAddressOverBle(ble, ble.Peer);
            };
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
            if (_mesh?.IsConnectedToAny != true && _bleTransport?.IsConnected != true) return;

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
                int sent = _mesh != null ? await _mesh.BroadcastAsync(contentType, body).ConfigureAwait(false) : 0;

                sent += await SendOverBluetoothAsync(contentType, body).ConfigureAwait(false);

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
        /// Sends over Bluetooth, but only to a device Wi-Fi did not already reach.
        ///
        /// Without that check a phone holding both links would receive every copy twice, which
        /// is a real prospect now that both are held at once rather than one replacing the
        /// other. Returns how many devices it reached, so the caller can tell whether anything
        /// went anywhere at all.
        /// </summary>
        private static async Task<int> SendOverBluetoothAsync(byte contentType, byte[] body)
        {
            if (_security == null) return 0;

            // Either role may be the live one, so the send path asks which rather than assuming
            // this machine is the peripheral.
            bool viaCentral = _bleCentral?.IsConnected == true;
            ITransportConnection? ble = viaCentral ? _bleCentral : _bleTransport;
            if (ble?.IsConnected != true) return 0;

            // Sealed with the key this link agreed, so there is no longer any inferring of who
            // the peer is: a link with no session has not finished its handshake and is skipped.
            var session = viaCentral ? _bleCentral!.Peer : _bleTransport?.Peer;
            if (session == null) return 0;

            string fingerprint = session.Fingerprint;
            if (_mesh?.IsConnectedTo(fingerprint) == true) return 0;

            byte[]? encrypted = session.Encrypt(contentType, body);
            if (encrypted == null) return 0;

            if (encrypted.Length > BleProtocol.MaxPayloadBytes)
            {
                // Too big for Bluetooth, and this machine may not be able to dial the peer, so
                // the route is to ask it to raise Wi-Fi and come to us. Before Bluetooth became
                // the standing link this was logged and dropped, which was tolerable when
                // Bluetooth was rarely up and would now mean losing every image copied here.
                Log.Write("Daemon",
                    $"{encrypted.Length} bytes is over the Bluetooth ceiling; asking the peer to raise Wi-Fi.");

                bool asked = viaCentral
                    ? await _bleCentral!.RequestWiFiAsync().ConfigureAwait(false)
                    : await _bleTransport!.RequestWiFiAsync(fingerprint).ConfigureAwait(false);

                if (!asked) return 0;

                var deadline = DateTime.UtcNow + WiFiWakeTimeout;
                while (DateTime.UtcNow < deadline)
                {
                    if (_mesh?.IsConnectedTo(fingerprint) == true)
                    {
                        Log.Write("Daemon", "The phone raised Wi-Fi; sending over it.");
                        return await _mesh.SendToAsync(fingerprint, contentType, body).ConfigureAwait(false) ? 1 : 0;
                    }

                    await Task.Delay(200).ConfigureAwait(false);
                }

                Log.Write("Daemon",
                    $"The phone did not raise Wi-Fi within {WiFiWakeTimeout.TotalSeconds:F0}s; the item was dropped.");
                return 0;
            }

            try
            {
                // Addressed rather than broadcast when we are the peripheral: notifying the
                // characteristic reaches every subscriber, and each payload is sealed for one.
                if (viaCentral) await _bleCentral!.SendPayloadAsync(encrypted).ConfigureAwait(false);
                else await _bleTransport!.SendPayloadToAsync(fingerprint, encrypted).ConfigureAwait(false);

                return 1;
            }
            catch (Exception ex)
            {
                Log.Write("Daemon", "Send over Bluetooth failed", ex);
                return 0;
            }
        }

        private static void WirePayloadReceive()
        {
            // The mesh decrypts before it raises this, because the session a payload arrived on
            // is also the answer to which device sent it.
            _mesh!.PayloadReceived += (_, e) => Apply(e.Peer, e.ContentType, e.Body, "Wi-Fi");
        }

        /// <summary>
        /// Opens a payload that arrived over Bluetooth.
        ///
        /// <paramref name="session"/> is the link's own agreed key, so there is nothing to
        /// search and nothing to infer. This used to try every paired device's key in turn and
        /// fall back to "there is only one device it could be", because the key belonged to the
        /// peer rather than to the connection. It belongs to the connection now.
        /// </summary>
        private static void HandleIncomingPayload(byte[] encrypted, string via, PeerSession? session)
        {
            if (session == null)
            {
                Log.Write("Daemon", "Dropped a Bluetooth payload that arrived before a key was agreed.");
                return;
            }

            if (!session.TryDecrypt(encrypted, out var decrypted))
            {
                Log.Write("Daemon",
                    $"Dropped a payload from {DeviceIdentity.Shorten(session.Fingerprint)}: it does not authenticate under this link's key.");
                return;
            }

            Apply(decrypted.Peer, decrypted.ContentType, decrypted.Body, via);
        }

        private static void Apply(PeerRecord peer, byte contentType, byte[] body, string via)
        {
            string from = peer.Name ?? DeviceIdentity.Shorten(peer.Fingerprint);

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
        /// Tells a peer where this device is reachable, so a DHCP lease change cannot strand it.
        ///
        /// Sent on every link that comes up rather than only when the address changes: this
        /// side has no way to know what the peer last recorded, and the payload is a few dozen
        /// bytes on a link that has just been established anyway.
        /// </summary>
        private static void AnnounceAddressOverMesh(string fingerprint)
        {
            string? address = NetworkUtil.GetLocalLanAddress();
            if (string.IsNullOrEmpty(address) || _mesh == null) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await _mesh.SendToAsync(fingerprint, SyncContent.Address,
                        System.Text.Encoding.UTF8.GetBytes(address)).ConfigureAwait(false);
                    Log.Write("Daemon", $"Announced this computer at {address}.");
                }
                catch (Exception ex)
                {
                    // Informational. A failure here costs nothing until the address changes.
                    Log.Write("Daemon", "Could not announce this computer's address", ex);
                }
            });
        }

        private static void AnnounceAddressOverBle(ITransportConnection link, PeerSession? session)
        {
            if (session == null) return;

            string? address = NetworkUtil.GetLocalLanAddress();
            if (string.IsNullOrEmpty(address)) return;

            byte[]? payload = session.Encrypt(SyncContent.Address,
                System.Text.Encoding.UTF8.GetBytes(address));
            if (payload == null) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await link.SendPayloadAsync(payload).ConfigureAwait(false);
                    Log.Write("Daemon", $"Announced this computer at {address} over Bluetooth.");
                }
                catch (Exception ex)
                {
                    Log.Write("Daemon", "Could not announce this computer's address over Bluetooth", ex);
                }
            });
        }

        /// <summary>How often to try reaching paired devices that are not currently connected.</summary>
        private static readonly TimeSpan DialInterval = TimeSpan.FromSeconds(20);

        /// <summary>How often to look for a Bluetooth peer this machine should connect to.</summary>
        private static readonly TimeSpan BleScanInterval = TimeSpan.FromSeconds(30);

        /// <summary>
        /// True when this machine should be the one connecting over Bluetooth, for any peer.
        ///
        /// The peer's capability is assumed to be both roles, which is the optimistic reading:
        /// a peer that can only ever be a central will simply never be found by this scan and
        /// will find us instead, because the service stays advertised either way. Getting it
        /// wrong therefore costs nothing, whereas not scanning at all would leave two laptops
        /// waiting for each other.
        /// </summary>
        private static bool ShouldDialAnyPeerOverBluetooth()
        {
            var security = _security;
            if (security == null) return false;

            foreach (var peer in security.Peers.Peers)
            {
                var role = BleRoleRules.DecideFor(
                    security.Identity.Fingerprint, BleCapability.Both,
                    peer.Fingerprint, BleCapability.Both);

                if (role == BleRole.Central) return true;
            }

            return false;
        }

        /// <summary>
        /// Looks for a Bluetooth peer this machine should connect to, rather than wait for.
        ///
        /// Windows only ever advertised, which is why Bluetooth could join a phone to a
        /// computer and nothing else - two laptops would both publish a service and neither
        /// would go looking. Advertising continues alongside this: the two are not exclusive,
        /// and a peer that cannot advertise depends on us still being findable.
        /// </summary>
        private static void StartBleCentralLoop()
        {
            _bleCentralCts?.Cancel();
            _bleCentralCts = new CancellationTokenSource();
            var token = _bleCentralCts.Token;

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (TransportSettings.AllowsBle &&
                            _bleCentral?.IsConnected != true &&
                            _bleTransport?.IsConnected != true &&
                            ShouldDialAnyPeerOverBluetooth())
                        {
                            await TryBleCentralConnectAsync(token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Log.Write("Daemon", "A Bluetooth scan round failed", ex);
                    }

                    try { await Task.Delay(BleScanInterval, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            });
        }

        private static async Task TryBleCentralConnectAsync(CancellationToken token)
        {
            RetireBleCentral();

            var central = new WindowsBleCentral
            {
                LocalPublicKey = _security!.Identity.PublicKey,
                LocalDeviceName = Environment.MachineName,
                LocalMeshName = _security.Peers.MeshName,
                OpenSession = OpenBleSession
            };

            central.PayloadReceived += (_, e) => HandleIncomingPayload(e.EncryptedPayload, "BLE", central.Peer);
            central.ConnectionClosed += (_, _) =>
            {
                Log.Write("Daemon", "The outbound Bluetooth link closed.");
                ConnectionState.SetBle(_bleTransport?.IsConnected == true);
            };
            central.PeerIdentified += (_, e) =>
            {
                ConnectionState.SetBle(true, e.DeviceName);

                if (!string.IsNullOrWhiteSpace(e.DeviceName))
                    _security?.Peers.NoteSeen(e.Fingerprint, null, e.DeviceName);

                AnnounceAddressOverBle(central, central.Peer);
            };
            central.WiFiRequested += (_, _) =>
            {
                // Nothing to raise on this side - the listener is always up - so the useful
                // response is to go to the peer rather than wait for it.
                Log.Write("Daemon", "A peer asked for Wi-Fi; dialling it now.");
                _ = _mesh?.ConnectToAllAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            };

            _bleCentral = central;

            var address = await central.FindPeerAsync(TimeSpan.FromSeconds(12), token).ConfigureAwait(false);
            if (address == null)
            {
                RetireBleCentral();
                return;
            }

            try
            {
                await central.ConnectAsync(address.Value.ToString("X"), token).ConfigureAwait(false);
                Log.Write("Daemon", "Connected to a peer over Bluetooth as the central.");
                ConnectionState.SetBle(true);
            }
            catch (Exception ex)
            {
                Log.Write("Daemon", "Connecting over Bluetooth as the central failed", ex);
                RetireBleCentral();
            }
        }

        private static void RetireBleCentral()
        {
            var central = Interlocked.Exchange(ref _bleCentral, null);
            if (central == null) return;

            try { central.Dispose(); }
            catch (Exception ex) { Log.Write("Daemon", "Disposing the Bluetooth central failed", ex); }
        }

        /// <summary>
        /// Dials paired devices that are not connected.
        ///
        /// This side used to listen and nothing else, because the phone was hardcoded as the
        /// client. It cannot stay that way once any device can pair with any other: two
        /// laptops would both sit waiting for the other to call. So both ends dial, and the
        /// collision that produces is settled in <see cref="MeshLinks"/>.
        /// </summary>
        private static void StartDialLoop()
        {
            _dialCts?.Cancel();
            _dialCts = new CancellationTokenSource();
            var token = _dialCts.Token;

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (_mesh != null && TransportSettings.AllowsWiFi)
                        {
                            await _mesh.ConnectToAllAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Log.Write("Daemon", "A dialling round failed", ex);
                    }

                    try { await Task.Delay(DialInterval, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            });
        }

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
                try { _bleCentralCts?.Cancel(); } catch { }
                RetireBleCentral();

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
            try { _dialCts?.Cancel(); _dialCts?.Dispose(); } catch { }
            try { _mesh?.Dispose(); } catch { }
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
