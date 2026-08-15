using System;
using System.Threading.Tasks;
using CoreLib;
using CoreLib.Transport;

#if ANDROID
using Android.App;
using Android.Content;
#endif

namespace AndroidClient
{
    public static class SyncManager
    {
        public static TcpTransportConnection? Transport { get; private set; }
        public static TrustManager? Trust { get; private set; }
        private static byte[] _aesKey = CryptoEngine.DeriveKey("MasterPassword123", System.Text.Encoding.UTF8.GetBytes("Salt"));
        public static bool IsInjectingClipboard = false;

        public static event Action<string>? OnConnectionStatusChanged;
        public static event Action<string>? OnClipboardReceived;

        public static async Task ConnectAsync(string hostIp, string hostPubKey)
        {
#if ANDROID
            var prefs = Android.App.Application.Context.GetSharedPreferences("SyncPrefs", FileCreationMode.Private);
            prefs?.Edit()?.PutString("HostIp", hostIp)?.PutString("HostPubKey", hostPubKey)?.Apply();
#endif

            Trust = new TrustManager();
            Trust.TrustDevice(hostPubKey);

            if (Transport != null)
            {
                await Transport.DisconnectAsync();
            }

            Transport = new TcpTransportConnection();

            Transport.PayloadReceived += async (s, args) =>
            {
                try
                {
                    byte[] decrypted = CryptoEngine.Decrypt(args.EncryptedPayload, _aesKey);
                    string receivedText = System.Text.Encoding.UTF8.GetString(decrypted);

                    IsInjectingClipboard = true;
                    OnClipboardReceived?.Invoke(receivedText);
                    
#if ANDROID
                    var clipboard = (ClipboardManager?)Android.App.Application.Context.GetSystemService(Context.ClipboardService);
                    if (clipboard != null)
                    {
                        clipboard.PrimaryClip = ClipData.NewPlainText("Mesh", receivedText);
                    }
#endif
                    
                    // Small delay to prevent infinite loop
                    await Task.Delay(500);
                    IsInjectingClipboard = false;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Decrypt failed: {ex.Message}");
                }
            };

            Transport.ConnectionClosed += async (s, args) =>
            {
                OnConnectionStatusChanged?.Invoke("Disconnected. Retrying...");
                await Task.Delay(3000);
                _ = AutoConnectAsync(); // Try to automatically reconnect!
            };

            OnConnectionStatusChanged?.Invoke($"Connecting to {hostIp}...");
            try
            {
                await Transport.ConnectAsync(hostIp);
                OnConnectionStatusChanged?.Invoke("Connected!");
            }
            catch (Exception ex)
            {
                OnConnectionStatusChanged?.Invoke($"Connection Failed: {ex.Message}");
            }
        }

        public static async Task AutoConnectAsync()
        {
            string hostIp = "";
            string hostPubKey = "";

#if ANDROID
            var prefs = Android.App.Application.Context.GetSharedPreferences("SyncPrefs", FileCreationMode.Private);
            hostIp = prefs?.GetString("HostIp", "") ?? "";
            hostPubKey = prefs?.GetString("HostPubKey", "") ?? "";
#endif
            
            if (!string.IsNullOrEmpty(hostIp) && !string.IsNullOrEmpty(hostPubKey))
            {
                await ConnectAsync(hostIp, hostPubKey);
            }
        }

        public static async Task SendClipboardAsync(string text)
        {
            if (Transport != null && Transport.IsConnected && !IsInjectingClipboard)
            {
                byte[] payload = CryptoEngine.Encrypt(System.Text.Encoding.UTF8.GetBytes(text), _aesKey);
                await Transport.SendPayloadAsync(payload);
            }
        }

        public static async Task DisconnectAsync()
        {
#if ANDROID
            var prefs = Android.App.Application.Context.GetSharedPreferences("SyncPrefs", FileCreationMode.Private);
            prefs?.Edit()?.Remove("HostIp")?.Apply(); // Prevent auto-reconnect
#endif
            if (Transport != null)
            {
                await Transport.DisconnectAsync();
            }
        }
    }
}
