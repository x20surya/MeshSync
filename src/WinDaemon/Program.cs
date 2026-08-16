using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WinDaemon
{
    static class Program
    {
        public static bool IsInjecting = false;
        private static CoreLib.Transport.TcpTransportConnection? _tcpTransport;
        private static CoreLib.TrustManager? _trustManager;
        private static string _myPubKey = "";

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool isStartup = args.Length > 0 && args[0] == "--startup";

            // Enable startup by default if it's the first time running
            SetStartup(true);

            using var listener = new ClipboardListenerWindow();
            _tcpTransport = new CoreLib.Transport.TcpTransportConnection();
            
            // Setup network in background
            _ = Task.Run(() => SetupNetworkAsync(listener));

            // Wait for network to initialize so we have IP and PubKey
            Thread.Sleep(500); 

            string localIp = "Unknown";
            try
            {
                foreach (var ip in System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        localIp = ip.ToString();
                        break;
                    }
                }
            }
            catch { }

            // Start application and show UI only if it wasn't launched by Windows startup
            Application.Run(new MeshSyncApplicationContext(localIp, _myPubKey, !isStartup, _tcpTransport!));
        }

        public static void ToggleStartup()
        {
            try
            {
                string runKey = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(runKey, true))
                {
                    if (key != null)
                    {
                        if (key.GetValue("MeshSyncDaemon") == null)
                        {
                            key.SetValue("MeshSyncDaemon", $"\"{Application.ExecutablePath}\" --startup");
                            MessageBox.Show("Mesh Sync will now run automatically on startup.", "Startup Enabled");
                        }
                        else
                        {
                            key.DeleteValue("MeshSyncDaemon", false);
                            MessageBox.Show("Mesh Sync will no longer run on startup.", "Startup Disabled");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to toggle startup: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public static void SetStartup(bool enable)
        {
            try
            {
                string runKey = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(runKey, true))
                {
                    // Only write if it's not already there so we don't spam the registry
                    if (enable && key?.GetValue("MeshSyncDaemon") == null)
                    {
                        key?.SetValue("MeshSyncDaemon", $"\"{Application.ExecutablePath}\" --startup");
                    }
                    else if (!enable)
                    {
                        key?.DeleteValue("MeshSyncDaemon", false);
                    }
                }
            }
            catch { }
        }

        static async Task SetupNetworkAsync(ClipboardListenerWindow listener)
        {
            _trustManager = new CoreLib.TrustManager();
            _tcpTransport = new CoreLib.Transport.TcpTransportConnection();
            var discovery = new CoreLib.Transport.TcpDiscoveryService();

            byte[] aesKey = CoreLib.CryptoEngine.DeriveKey("MasterPassword123", System.Text.Encoding.UTF8.GetBytes("Salt"));
            _myPubKey = _trustManager.GetMyPublicKeyPin();

            // Always run Windows as Host (Server) for simplicity
            _ = _tcpTransport.StartListeningAsync();
            await discovery.StartAdvertisingAsync(System.Text.Encoding.UTF8.GetBytes(_myPubKey));

            _tcpTransport.PayloadReceived += (s, e) =>
            {
                try
                {
                    byte[] decrypted = CoreLib.CryptoEngine.Decrypt(e.EncryptedPayload, aesKey);
                    if (decrypted.Length == 0) return;
                    
                    byte type = decrypted[0];
                    byte[] data = new byte[decrypted.Length - 1];
                    Buffer.BlockCopy(decrypted, 1, data, 0, data.Length);
                    
                    IsInjecting = true;
                    Thread thread = new Thread(() =>
                    {
                        try
                        {
                            if (type == 0x00)
                            {
                                Clipboard.SetText(System.Text.Encoding.UTF8.GetString(data));
                            }
                            else if (type == 0x01)
                            {
                                using var ms = new System.IO.MemoryStream(data);
                                using var img = System.Drawing.Image.FromStream(ms);
                                Clipboard.SetImage(img);
                            }
                        }
                        catch { }
                    });
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.Start();
                    thread.Join();
                    
                    Task.Delay(500).ContinueWith(_ => IsInjecting = false);
                }
                catch { }
            };

            listener.ClipboardTextChanged += async (text) =>
            {
                if (IsInjecting) return;
                if (_tcpTransport.IsConnected)
                {
                    try
                    {
                        byte[] textBytes = System.Text.Encoding.UTF8.GetBytes(text);
                        byte[] payload = new byte[textBytes.Length + 1];
                        payload[0] = 0x00; // Type 0 = Text
                        Buffer.BlockCopy(textBytes, 0, payload, 1, textBytes.Length);
                        
                        byte[] encrypted = CoreLib.CryptoEngine.Encrypt(payload, aesKey);
                        await _tcpTransport.SendPayloadAsync(encrypted);
                    }
                    catch { }
                }
            };

            listener.ClipboardImageChanged += async (imgBytes) =>
            {
                if (IsInjecting) return;
                if (_tcpTransport.IsConnected)
                {
                    try
                    {
                        byte[] payload = new byte[imgBytes.Length + 1];
                        payload[0] = 0x01; // Type 1 = Image
                        Buffer.BlockCopy(imgBytes, 0, payload, 1, imgBytes.Length);
                        
                        byte[] encrypted = CoreLib.CryptoEngine.Encrypt(payload, aesKey);
                        await _tcpTransport.SendPayloadAsync(encrypted);
                    }
                    catch { }
                }
            };
        }
    }

    class MeshSyncApplicationContext : ApplicationContext
    {
        private MainForm _mainForm;

        public MeshSyncApplicationContext(string ip, string pubKey, bool showUi, CoreLib.Transport.TcpTransportConnection tcpTransport)
        {
            _mainForm = new MainForm(ip, pubKey, tcpTransport);
            
            if (showUi)
            {
                _mainForm.ShowDashboard();
            }
        }
    }

    class ClipboardListenerWindow : NativeWindow, IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        private const int WM_CLIPBOARDUPDATE = 0x031D;
        public event Action<string>? ClipboardTextChanged;
        public event Action<byte[]>? ClipboardImageChanged;

        public ClipboardListenerWindow()
        {
            CreateHandle(new CreateParams
            {
                Caption = "ClipboardListener",
                Parent = new IntPtr(-3)
            });

            AddClipboardFormatListener(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE)
            {
                try
                {
                    System.Threading.Thread.Sleep(50);
                    if (Clipboard.ContainsImage())
                    {
                        using var img = Clipboard.GetImage();
                        if (img != null)
                        {
                            using var ms = new System.IO.MemoryStream();
                            img.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                            ClipboardImageChanged?.Invoke(ms.ToArray());
                        }
                    }
                    else if (Clipboard.ContainsText())
                    {
                        ClipboardTextChanged?.Invoke(Clipboard.GetText());
                    }
                }
                catch { }
            }

            base.WndProc(ref m);
        }

        public void Dispose()
        {
            RemoveClipboardFormatListener(Handle);
            DestroyHandle();
        }
    }
}
