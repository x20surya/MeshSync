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

        public static async Task<bool> ConnectAsync(string hostIp, string hostPubKey)
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
                    if (decrypted.Length == 0) return;
                    
                    byte type = decrypted[0];
                    byte[] data = new byte[decrypted.Length - 1];
                    Buffer.BlockCopy(decrypted, 1, data, 0, data.Length);

                    IsInjectingClipboard = true;

                    if (type == 0x00) // Text
                    {
                        string receivedText = System.Text.Encoding.UTF8.GetString(data);
                        OnClipboardReceived?.Invoke(receivedText);
                        
#if ANDROID
                        var clipboard = (ClipboardManager?)Android.App.Application.Context.GetSystemService(Context.ClipboardService);
                        if (clipboard != null)
                        {
                            clipboard.PrimaryClip = ClipData.NewPlainText("Mesh", receivedText);
                        }
#endif
                    }
                    else if (type == 0x01) // Image
                    {
                        OnClipboardReceived?.Invoke("[Image Received]");
#if ANDROID
                        var context = Android.App.Application.Context;
                        var cacheDir = new Java.IO.File(context.CacheDir, "images");
                        if (!cacheDir.Exists()) cacheDir.Mkdirs();
                        
                        var imageFile = new Java.IO.File(cacheDir, "clipboard.jpg");
                        if (imageFile.Exists()) imageFile.Delete();
                        
                        System.IO.File.WriteAllBytes(imageFile.AbsolutePath, data);

                        var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(context, "com.companyname.androidclient.fileprovider", imageFile);
                        var clipboard = (ClipboardManager?)context.GetSystemService(Context.ClipboardService);
                        if (clipboard != null)
                        {
                            var clip = ClipData.NewUri(context.ContentResolver, "Mesh Sync Image", uri);
                            clipboard.PrimaryClip = clip;
                        }
#endif
                    }
                    
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
                if (_shouldStopConnecting) return;

                OnConnectionStatusChanged?.Invoke("Disconnected. Retrying...");
                _ = AutoConnectAsync(false); // Try to automatically reconnect
            };

            OnConnectionStatusChanged?.Invoke($"Connecting to {hostIp}...");
            try
            {
                await Transport.ConnectAsync(hostIp);
                OnConnectionStatusChanged?.Invoke("Connected!");
                return true;
            }
            catch (Exception ex)
            {
                OnConnectionStatusChanged?.Invoke($"Connection Failed: {ex.Message}");
                return false;
            }
        }

        private static bool _isAutoConnecting = false;
        private static bool _shouldStopConnecting = false;

        public static async Task AutoConnectAsync(bool isUserInitiated = false)
        {
            if (!isUserInitiated && _shouldStopConnecting) return;

            if (_isAutoConnecting) return;
            _isAutoConnecting = true;
            _shouldStopConnecting = false;

            string hostIp = "";
            string hostPubKey = "";

#if ANDROID
            var prefs = Android.App.Application.Context.GetSharedPreferences("SyncPrefs", FileCreationMode.Private);
            hostIp = prefs?.GetString("HostIp", "") ?? "";
            hostPubKey = prefs?.GetString("HostPubKey", "") ?? "";
#endif
            
            if (!string.IsNullOrEmpty(hostIp) && !string.IsNullOrEmpty(hostPubKey))
            {
                bool connected = false;
                while (!connected && !_shouldStopConnecting)
                {
                    connected = await ConnectAsync(hostIp, hostPubKey);
                    if (!connected && !_shouldStopConnecting)
                    {
                        await Task.Delay(3000); // Wait 3 seconds before retrying
                    }
                }
            }

            _isAutoConnecting = false;
        }

        public static async Task SendClipboardAsync(string text)
        {
            if (Transport != null && Transport.IsConnected && !IsInjectingClipboard)
            {
                byte[] textBytes = System.Text.Encoding.UTF8.GetBytes(text);
                byte[] payload = new byte[textBytes.Length + 1];
                payload[0] = 0x00;
                Buffer.BlockCopy(textBytes, 0, payload, 1, textBytes.Length);

                byte[] encrypted = CryptoEngine.Encrypt(payload, _aesKey);
                await Transport.SendPayloadAsync(encrypted);
            }
        }

        public static async Task SendClipboardImageAsync(byte[] imageBytes)
        {
            if (Transport != null && Transport.IsConnected && !IsInjectingClipboard)
            {
                byte[] payload = new byte[imageBytes.Length + 1];
                payload[0] = 0x01;
                Buffer.BlockCopy(imageBytes, 0, payload, 1, imageBytes.Length);

                byte[] encrypted = CryptoEngine.Encrypt(payload, _aesKey);
                await Transport.SendPayloadAsync(encrypted);
            }
        }

        public static async Task DisconnectAsync()
        {
            _shouldStopConnecting = true;
            // Do NOT remove HostIp here, so we can auto-reconnect when the service starts again!
            if (Transport != null)
            {
                await Transport.DisconnectAsync();
                Transport = null;
            }
        }
    }
}
