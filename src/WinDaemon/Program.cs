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
            WireFileTransfer();

            // Dismissing here dismisses there, which is what makes mirroring feel finished
            // rather than like a second inbox to empty separately.
            MirroredNotifications.DismissOnPeer = (fingerprint, key) =>
                _mesh?.SendToAsync(fingerprint, SyncContent.NotificationDismiss,
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
            _mesh.PeerConnected += peer =>
            {
                Links.SetWiFi(true, peer.Name);

                // Telling a peer where we are is what makes a lease change survivable; without
                // it the address in the QR code is the only one it will ever know.
                AnnounceAddressOverMesh(peer.Fingerprint);
            };

            _mesh.PeerDisconnected += fingerprint =>
            {
                Links.SetWiFi(_mesh.IsConnectedToAny);

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
            var mesh = _mesh;
            byte[] body = { on ? (byte)1 : (byte)0 };

            if (mesh?.IsConnectedTo(fingerprint) == true &&
                await mesh.SendToAsync(fingerprint, SyncContent.Ring, body).ConfigureAwait(false))
            {
                return true;
            }

            // No Wi-Fi to that device, so try Bluetooth - which is the case this feature is for.
            var session = _bleCentral?.IsConnected == true ? _bleCentral.Peer : _bleTransport?.Peer;
            if (session == null || !string.Equals(session.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                return false;

            byte[]? sealed_ = session.Encrypt(SyncContent.Ring, body);
            if (sealed_ == null) return false;

            try
            {
                if (_bleCentral?.IsConnected == true) await _bleCentral.SendPayloadAsync(sealed_).ConfigureAwait(false);
                else await _bleTransport!.SendPayloadToAsync(fingerprint, sealed_).ConfigureAwait(false);

                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Daemon", "Could not ask the device to ring", ex);
                return false;
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
            var mesh = _mesh;
            if (files == null || mesh == null) return;

            var targets = mesh.ConnectedPeers;

            if (targets.Count == 0)
            {
                // Only Bluetooth is up, which cannot carry a file at 6.7 KB/s. Asking the peer
                // to raise Wi-Fi is the same move an image already makes.
                if (_bleTransport?.IsConnected == true || _bleCentral?.IsConnected == true)
                {
                    Log.Write("Daemon", "Only Bluetooth is up; asking the peer to raise Wi-Fi before sending a file.");

                    if (_bleCentral?.IsConnected == true) await _bleCentral.RequestWiFiAsync().ConfigureAwait(false);
                    else if (_bleTransport != null) await _bleTransport.RequestWiFiAsync(_bleTransport.Peer?.Fingerprint).ConfigureAwait(false);

                    var deadline = DateTime.UtcNow + WiFiWakeTimeout;
                    while (DateTime.UtcNow < deadline && mesh.ConnectedCount == 0)
                    {
                        await Task.Delay(200).ConfigureAwait(false);
                    }

                    targets = mesh.ConnectedPeers;
                }
            }

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
            try
            {
                // No key derivation here any more. Keys are per peer and derived by agreement
                // against the public key each one presents, so there is nothing to prepare
                // until a peer actually turns up.
                if (Transports.AllowsWiFi)
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

                if (Transports.AllowsBle)
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

            Transports.Changed += ApplyTransportPreference;
        }

        /// <summary>
        /// Applies a preference change without a restart, so the control in the window means
        /// something the moment it is used.
        /// </summary>
        private static async void ApplyTransportPreference(TransportPreference preference)
        {
            try
            {
                if (Transports.AllowsWiFi)
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
                    if (Transports.AllowsBle)
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

        private static void WireBleTransport(WindowsBleTransport ble)
        {
            ble.ClientConnected += (_, _) =>
            {
                Log.Write("Daemon", "Phone connected over BLE.");
                Links.SetBle(true);
            };

            ble.ConnectionClosed += (_, _) =>
            {
                Log.Write("Daemon", "BLE link closed.");
                Links.SetBle(false);
            };

            ble.PayloadReceived += (_, e) => HandleIncomingPayload(e.EncryptedPayload, "BLE", ble.Peer);

            // Now that the tier says who it is talking to, the address can be announced to the
            // right device rather than to whichever one happened to be the only one paired.
            ble.PeerIdentified += (_, e) =>
            {
                Links.SetBle(true);

                // Bluetooth carries the peer's name in its hello now, so a device paired only
                // over Bluetooth has something to be called in the tray and the dashboard.
                if (!string.IsNullOrWhiteSpace(e.DeviceName))
                {
                    _security?.Peers.NoteSeen(e.Fingerprint, null, e.DeviceName);
                    Links.SetBle(true, e.DeviceName);
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

        /// <summary>
        /// Sets up file transfer and decides where a finished file lands.
        ///
        /// CoreLib deliberately does not know that Downloads exists, so the move happens here.
        /// A name that is already taken gets a numeric suffix rather than overwriting - a
        /// transfer must never quietly replace something the user already had.
        /// </summary>
        private static void WireFileTransfer()
        {
            _files = new FileTransferService(
                Path.Combine(LogDirectory, "incoming"),
                (fingerprint, contentType, body, token) =>
                    _mesh?.SendToAsync(fingerprint, contentType, body, token) ?? Task.FromResult(false));

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
                _mesh?.SendToAsync(fingerprint, contentType, body) ?? Task.FromResult(false);

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

        /// <summary>
        /// Wakes the dial loop early. Confirming a device by hand should reach it now rather
        /// than up to twenty seconds later, which reads as the confirmation not having worked.
        /// </summary>
        private static readonly SemaphoreSlim _dialSignal = new(0);

        public static void SignalDial()
        {
            try { _dialSignal.Release(); } catch (SemaphoreFullException) { }
        }

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
                        if (Transports.AllowsBle &&
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
                Links.SetBle(_bleTransport?.IsConnected == true);
            };
            central.PeerIdentified += (_, e) =>
            {
                Links.SetBle(true, e.DeviceName);

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
                Links.SetBle(true);
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
                        if (_mesh != null && Transports.AllowsWiFi)
                        {
                            await _mesh.ConnectToAllAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Log.Write("Daemon", "A dialling round failed", ex);
                    }

                    // Waits on the signal rather than the clock, so a device confirmed by hand
                    // is dialled at once instead of on the next scheduled round.
                    try { await _dialSignal.WaitAsync(DialInterval, token).ConfigureAwait(false); }
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
            try { _files?.Dispose(); } catch { }
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
