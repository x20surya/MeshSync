using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CoreLib;
using CoreLib.Diagnostics;
using CoreLib.Transport;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;

namespace WinDaemon
{
    static class Program
    {
        private const byte ContentText = 0x00;
        private const byte ContentImage = 0x01;

        private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "MeshSyncDaemon";
        private const string SettingsKeyPath = @"SOFTWARE\MeshSync";
        private const string SingleInstanceMutex = @"Global\MeshSyncDaemon.SingleInstance";

        private static TcpTransportConnection? _transport;
        private static WindowsBleTransport? _bleTransport;
        private static ClipboardWorker? _clipboard;
        private static ClipboardListenerWindow? _listener;
        private static Forms.NotifyIcon? _trayIcon;
        private static MainWindow? _window;
        private static Wpf.Application? _app;
        private static Mutex? _instanceMutex;

        private static readonly SyncActivityLog _activity = new(capacity: 12);
        private static readonly EchoSuppressor _echo = new(TimeSpan.FromSeconds(10));

        private static byte[]? _aesKey;
        private static bool _trayHintShown;

        public static string LogDirectory { get; private set; } = "";

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
            _transport = new TcpTransportConnection { LocalDeviceName = Environment.MachineName };

            var trustManager = new TrustManager();
            string pairingCode = trustManager.GetMyPublicKeyPin();
            string localIp = NetworkUtil.GetLocalLanAddress() ?? "Unavailable";

            Log.Write("Daemon", $"Starting. LAN address {localIp}, startup launch: {isStartup}");

            WireClipboardCapture();
            WirePayloadReceive();

            _ = Task.Run(InitialiseNetworkAsync);

            _app = new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
            _app.Resources.MergedDictionaries.Add(new Wpf.ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/WinDaemon;component/Themes/MeshTheme.xaml", UriKind.Absolute)
            });
            ThemeManager.Apply(_app);

            _window = new MainWindow(localIp, pairingCode, _transport, _activity);
            _window.ExitRequested += ExitApp;

            CreateTrayIcon();

            // Both transports report into ConnectionState; the tray and the dashboard read
            // only that, so a Bluetooth-only link shows as connected like any other.
            _transport.ClientConnected += (_, _) => ConnectionState.SetWiFi(true, _transport.RemoteDeviceName);
            _transport.ConnectionClosed += (_, _) => ConnectionState.SetWiFi(false);
            _transport.PeerIdentified += (_, e) => ConnectionState.SetWiFi(true, e.DeviceName);
            ConnectionState.Changed += UpdateTrayState;

            if (!isStartup) _window.ShowDashboard();

            _app.Run();
            Shutdown();
        }

        // ────────────────────────────── tray

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

                string peer = ConnectionState.PeerName ?? "your phone";
                string via = ConnectionState.ActiveLink == LinkKind.Ble ? " over Bluetooth" : "";
                _trayIcon.Text = connected
                    ? $"Mesh Sync - connected to {peer}{via}"
                    : "Mesh Sync - waiting for a device";
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
                _aesKey = CryptoEngine.DeriveKey("MasterPassword123", System.Text.Encoding.UTF8.GetBytes("Salt"));
                Log.Write("Daemon", "Key derivation complete.");

                if (TransportSettings.AllowsWiFi)
                {
                    await _transport!.StartListeningAsync().ConfigureAwait(false);
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
                _bleTransport = new WindowsBleTransport();
                WireBleTransport(_bleTransport);

                if (TransportSettings.AllowsBle)
                {
                    await _bleTransport.StartListeningAsync().ConfigureAwait(false);
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
                if (TransportSettings.AllowsWiFi) await _transport!.StartListeningAsync().ConfigureAwait(false);
                else await _transport!.DisconnectAsync().ConfigureAwait(false);

                if (_bleTransport != null)
                {
                    if (TransportSettings.AllowsBle) await _bleTransport.StartListeningAsync().ConfigureAwait(false);
                    else await _bleTransport.DisconnectAsync().ConfigureAwait(false);
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

            ble.PayloadReceived += (_, e) => HandleIncomingPayload(e.EncryptedPayload, "BLE");
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

        private static void SendCapture(byte contentType, byte[] body, string? textContent)
        {
            var key = _aesKey;
            if (key == null) return;

            // Wi-Fi first: it carries anything. BLE is the fallback for when there is no
            // network at all, and only for text, since at BLE throughput an image would
            // take minutes.
            ITransportConnection? transport = null;
            string via = "Wi-Fi";

            if (_transport?.IsConnected == true)
            {
                transport = _transport;
            }
            else if (_bleTransport?.IsConnected == true && contentType == ContentText)
            {
                transport = _bleTransport;
                via = "BLE";
            }
            else if (_bleTransport?.IsConnected == true)
            {
                Log.Write("Daemon", "Skipping image: only BLE is connected and it is text-only.");
                return;
            }

            if (transport == null) return;

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

            byte[] encrypted;
            try
            {
                encrypted = CryptoEngine.EncryptTagged(contentType, body, key);
            }
            catch (Exception ex)
            {
                Log.Write("Daemon", "Encryption failed", ex);
                return;
            }

            int limit = via == "BLE" ? BleProtocol.MaxPayloadBytes : TcpTransportConnection.MaxPayloadBytes;
            if (encrypted.Length > limit)
            {
                Log.Write("Daemon", $"Refusing to send {encrypted.Length} byte payload over {via} (limit {limit}).");
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await transport.SendPayloadAsync(encrypted).ConfigureAwait(false);
                    _activity.Record(SyncDirection.Sent,
                        contentType == ContentText ? SyncItemKind.Text : SyncItemKind.Image,
                        body.Length, textContent);
                    Log.Write("Daemon", $"Sent {(contentType == ContentText ? "text" : "image")} payload over {via}, {encrypted.Length} bytes.");
                }
                catch (Exception ex)
                {
                    Log.Write("Daemon", $"Send over {via} failed", ex);
                }
            });
        }

        private static void WirePayloadReceive()
        {
            _transport!.PayloadReceived += (_, e) => HandleIncomingPayload(e.EncryptedPayload, "Wi-Fi");
        }

        private static void HandleIncomingPayload(byte[] encrypted, string via)
        {
            var key = _aesKey;
            if (key == null)
            {
                Log.Write("Daemon", "Dropped payload: key derivation has not finished yet.");
                return;
            }

            byte contentType;
            byte[] body;
            try
            {
                (contentType, body) = CryptoEngine.DecryptTagged(encrypted, key);
            }
            catch (Exception ex)
            {
                Log.Write("Daemon", "Decryption failed - different key or corrupt frame", ex);
                return;
            }

            _echo.NoteInbound(body, contentType == ContentImage ? SyncItemKind.Image : SyncItemKind.Text);

            switch (contentType)
            {
                case ContentText:
                    string text = System.Text.Encoding.UTF8.GetString(body);
                    _clipboard!.SetText(text);
                    _activity.Record(SyncDirection.Received, SyncItemKind.Text, body.Length, text);
                    Log.Write("Daemon", $"Received text payload over {via}, {body.Length} bytes.");
                    break;
                case ContentImage:
                    _clipboard!.SetImage(body);
                    _activity.Record(SyncDirection.Received, SyncItemKind.Image, body.Length);
                    Log.Write("Daemon", $"Received image payload over {via}, {body.Length} bytes.");
                    break;
                default:
                    Log.Write("Daemon", $"Ignoring unknown content type {contentType}.");
                    break;
            }
        }

        // ────────────────────────────── lifetime

        private static void ExitApp()
        {
            if (_trayIcon != null) _trayIcon.Visible = false;
            _app?.Dispatcher.BeginInvoke(() => _app.Shutdown());
        }

        private static void Shutdown()
        {
            Log.Write("Daemon", "Shutting down.");

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
            try { _transport?.Dispose(); } catch { }
            try { _bleTransport?.Dispose(); } catch { }
            try { _clipboard?.Dispose(); } catch { }

            var key = Interlocked.Exchange(ref _aesKey, null);
            if (key != null) CryptographicOperations.ZeroMemory(key);

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
