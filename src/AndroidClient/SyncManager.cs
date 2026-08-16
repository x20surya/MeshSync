using System;
using System.Threading;
using System.Threading.Tasks;
using CoreLib;
using CoreLib.Diagnostics;
using CoreLib.Transport;

#if ANDROID
using Android.App;
using Android.Content;
#endif

namespace AndroidClient
{
    /// <summary>
    /// Owns the single connection to the paired desktop, including reconnection.
    ///
    /// Every entry point funnels through one gate and one reconnect loop, because the
    /// previous design let the deep link, the activity, the accessibility service and the
    /// disconnect handler each start their own connect attempt. Each attempt built a fresh
    /// <see cref="TcpTransportConnection"/> and overwrote the previous one without disposing
    /// it, so sockets and receive-loop tasks accumulated for the life of the process.
    /// </summary>
    public static class SyncManager
    {
        private const byte ContentText = 0x00;
        private const byte ContentImage = 0x01;

        private const string PrefsName = "SyncPrefs";
        private const string PrefHostIp = "HostIp";
        private const string PrefHostPubKey = "HostPubKey";
        private const string PrefPaused = "UserPaused";

        /// <summary>How long to wait for Wi-Fi before falling back to Bluetooth.</summary>
        private static readonly TimeSpan TcpConnectTimeout = TimeSpan.FromSeconds(5);

        /// <summary>Upper bound on the retry gap, so a Bluetooth peer is found promptly.</summary>
        private static readonly TimeSpan BleRetryCeiling = TimeSpan.FromSeconds(8);

        private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

        private static readonly object _loopGate = new();
        private static readonly SemaphoreSlim _connectGate = new(1, 1);
        private static readonly SemaphoreSlim _keyGate = new(1, 1);
        private static readonly SemaphoreSlim _disconnectSignal = new(0);
        private static readonly EchoSuppressor _echo = new(TimeSpan.FromSeconds(10));
        private static readonly Random _jitter = new();

        private static ITransportConnection? _transport;

        /// <summary>Which tier the active link is using, for the UI and for send limits.</summary>
        public static TransportKind ActiveTransport { get; private set; } = TransportKind.None;

        public enum TransportKind { None, WiFi, Ble }
        private static byte[]? _aesKey;
        private static CancellationTokenSource? _loopCts;
        private static Task? _loopTask;
        private static volatile bool _networkHintPending;

        public static event Action<string>? OnConnectionStatusChanged;
        public static event Action<string>? OnClipboardReceived;

        public static bool IsConnected => _transport?.IsConnected == true;

        /// <summary>What has synced this session. Never persisted - clipboard traffic is ephemeral.</summary>
        public static readonly SyncActivityLog Activity = new(capacity: 12);

        /// <summary>
        /// Friendly name of the paired computer, once it has announced itself. Only the
        /// Wi-Fi transport carries the hello frame, so BLE links report null.
        /// </summary>
        public static string? PeerName => (_transport as TcpTransportConnection)?.RemoteDeviceName;

        /// <summary>True once a host has been saved by scanning a code or entering it by hand.</summary>
        public static bool IsPaired => !string.IsNullOrEmpty(LoadHost().HostIp);

        /// <summary>Address this device is paired to, for display.</summary>
        public static string PairedAddress => LoadHost().HostIp;

        /// <summary>Name this device announces to the computer.</summary>
        public static string LocalDeviceName
        {
            get
            {
#if ANDROID
                try
                {
                    // The name the user actually chose in Settings, when it is available.
                    var resolver = global::Android.App.Application.Context.ContentResolver;
                    var chosen = global::Android.Provider.Settings.Global.GetString(resolver, "device_name");
                    if (!string.IsNullOrWhiteSpace(chosen)) return chosen!;
                }
                catch { }

                try
                {
                    string manufacturer = global::Android.OS.Build.Manufacturer ?? "";
                    string model = global::Android.OS.Build.Model ?? "Phone";
                    return model.StartsWith(manufacturer, StringComparison.OrdinalIgnoreCase)
                        ? model
                        : $"{Capitalise(manufacturer)} {model}".Trim();
                }
                catch { return "Android phone"; }
#else
                return Environment.MachineName;
#endif
            }
        }

        private static string Capitalise(string value) =>
            string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value.Substring(1);

        // ---------------------------------------------------------------- public API

        /// <summary>Pairs with a host and starts the managed reconnect loop.</summary>
        public static async Task<bool> ConnectAsync(string hostIp, string hostPubKey)
        {
            if (string.IsNullOrWhiteSpace(hostIp)) return false;

            SaveHost(hostIp, hostPubKey);
            // Pairing is an explicit "I want this on", so it clears an earlier Stop.
            SetPaused(false);
            StartLoop();

            // Nudge the loop so a freshly paired host is tried immediately rather than
            // after whatever backoff the previous failures had accumulated.
            SignalRetry();

            // Report the outcome of the first attempt for the benefit of the pairing UI.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
            while (DateTime.UtcNow < deadline)
            {
                if (IsConnected) return true;
                await Task.Delay(200).ConfigureAwait(false);
            }
            return IsConnected;
        }

        /// <summary>
        /// True when the user has explicitly stopped syncing from the notification.
        /// Persisted, so it survives the service or the process being restarted - otherwise
        /// "Stop" would silently undo itself the next time anything reconnected.
        /// </summary>
        public static bool IsPaused
        {
            get
            {
#if ANDROID
                try
                {
                    var prefs = global::Android.App.Application.Context
                        .GetSharedPreferences(PrefsName, FileCreationMode.Private);
                    return prefs?.GetBoolean(PrefPaused, false) ?? false;
                }
                catch { return false; }
#else
                return false;
#endif
            }
        }

        /// <summary>Stops syncing until the user turns it back on. Pairing details are kept.</summary>
        public static async Task PauseAsync()
        {
            SetPaused(true);
            await DisconnectAsync().ConfigureAwait(false);
            Log.Write("Sync", "Paused by the user.");
            Report("Paused");
        }

        /// <summary>Turns syncing back on after <see cref="PauseAsync"/>.</summary>
        public static Task ResumeAsync()
        {
            SetPaused(false);
            Log.Write("Sync", "Resumed by the user.");
            return AutoConnectAsync(isUserInitiated: true);
        }

        private static void SetPaused(bool paused)
        {
#if ANDROID
            try
            {
                var prefs = global::Android.App.Application.Context
                    .GetSharedPreferences(PrefsName, FileCreationMode.Private);
                prefs?.Edit()?.PutBoolean(PrefPaused, paused)?.Apply();
            }
            catch (Exception ex) { Log.Write("Sync", "Saving the paused state failed", ex); }
#endif
        }

        /// <summary>Starts the reconnect loop using previously saved pairing details.</summary>
        public static Task AutoConnectAsync(bool isUserInitiated = false)
        {
            // Every caller funnels through here, so honouring the flag once keeps the app,
            // the accessibility service and the deep link from each reviving a stopped sync.
            if (IsPaused)
            {
                Log.Write("Sync", "Auto-connect skipped: syncing is paused.");
                return Task.CompletedTask;
            }

            var (hostIp, _) = LoadHost();
            if (string.IsNullOrEmpty(hostIp))
            {
                Report("Not paired yet.");
                return Task.CompletedTask;
            }

            StartLoop();
            if (isUserInitiated) SignalRetry();
            return Task.CompletedTask;
        }

        public static Task SendClipboardAsync(string text)
        {
            if (string.IsNullOrEmpty(text)) return Task.CompletedTask;
            return SendAsync(ContentText, System.Text.Encoding.UTF8.GetBytes(text));
        }

        public static Task SendClipboardImageAsync(byte[] imageBytes)
        {
            if (imageBytes == null || imageBytes.Length == 0) return Task.CompletedTask;
            return SendAsync(ContentImage, imageBytes);
        }

        /// <summary>Stops the reconnect loop and tears the connection down. Pairing details are kept.</summary>
        public static async Task DisconnectAsync()
        {
            CancellationTokenSource? cts;
            Task? loop;

            lock (_loopGate)
            {
                cts = _loopCts;
                loop = _loopTask;
                _loopCts = null;
                _loopTask = null;
            }

            try { cts?.Cancel(); } catch { }

            if (loop != null)
            {
                try { await loop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch { /* best effort */ }
            }

            cts?.Dispose();
            RetireTransport();
            _echo.Clear();
            Report("Disconnected.");
        }

        // ---------------------------------------------------------------- send path

        private static async Task SendAsync(byte contentType, byte[] body)
        {
            var transport = _transport;
            if (transport == null || !transport.IsConnected) return;

            // One gate for both "this is our own injection bouncing back" and "this is a
            // repeat notification for a copy we just sent". Android raises
            // OnPrimaryClipChanged several times per clipboard change, so without the
            // second check every copy was transmitted twice.
            var kind = contentType == ContentImage ? SyncItemKind.Image : SyncItemKind.Text;
            if (!_echo.ShouldSend(body, kind))
            {
                Log.Write("Sync", "Suppressed duplicate or echoed clipboard content.");
                return;
            }

            if (contentType == ContentImage && ActiveTransport == TransportKind.Ble)
            {
                Log.Write("Sync", "Skipping image: only Bluetooth is connected and it carries text only.");
                Report("Images need Wi-Fi");
                return;
            }

            byte[] payload = body;
            if (contentType == ContentImage)
            {
                payload = ImageCodec.CompressForTransport(body);
                if (payload.Length != body.Length)
                    Log.Write("Sync", $"Recompressed image {body.Length} -> {payload.Length} bytes.");
            }

            byte[] key;
            try { key = await GetKeyAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Write("Sync", "Key unavailable", ex); return; }

            byte[] encrypted;
            try { encrypted = CryptoEngine.EncryptTagged(contentType, payload, key); }
            catch (Exception ex) { Log.Write("Sync", "Encryption failed", ex); return; }

            if (encrypted.Length > TcpTransportConnection.MaxPayloadBytes)
            {
                Log.Write("Sync", $"Refusing to send {encrypted.Length} byte payload (over the limit).");
                Report("Item too large to sync.");
                return;
            }

            try
            {
                await transport.SendPayloadAsync(encrypted).ConfigureAwait(false);
                Activity.Record(SyncDirection.Sent,
                    contentType == ContentText ? SyncItemKind.Text : SyncItemKind.Image,
                    payload.Length,
                    contentType == ContentText ? System.Text.Encoding.UTF8.GetString(body) : null);
                Log.Write("Sync", $"Sent {(contentType == ContentText ? "text" : "image")} payload, {encrypted.Length} bytes.");
            }
            catch (Exception ex)
            {
                Log.Write("Sync", "Send failed", ex);
            }
        }

        // ---------------------------------------------------------------- receive path

        private static void HandlePayload(byte[] encrypted)
        {
            byte[]? key = Volatile.Read(ref _aesKey);
            if (key == null)
            {
                Log.Write("Sync", "Dropped payload: key derivation has not finished yet.");
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
                Log.Write("Sync", "Decryption failed - the peer is using a different key or the frame was corrupt", ex);
                return;
            }

            // Recorded before injection so the clipboard listener recognises the resulting
            // change as our own write.
            _echo.NoteInbound(body, contentType == ContentImage ? SyncItemKind.Image : SyncItemKind.Text);

            try
            {
                if (contentType == ContentText) ApplyText(body);
                else if (contentType == ContentImage) ApplyImage(body);
                else Log.Write("Sync", $"Ignoring unknown content type {contentType}.");
            }
            catch (Exception ex)
            {
                Log.Write("Sync", "Applying received payload failed", ex);
            }
        }

        private static void ApplyText(byte[] body)
        {
            string text = System.Text.Encoding.UTF8.GetString(body);
            OnClipboardReceived?.Invoke(text);

#if ANDROID
            var clipboard = (ClipboardManager?)global::Android.App.Application.Context.GetSystemService(Context.ClipboardService);
            if (clipboard != null) clipboard.PrimaryClip = ClipData.NewPlainText("Mesh", text);
#endif
            Activity.Record(SyncDirection.Received, SyncItemKind.Text, body.Length, text);
            Log.Write("Sync", $"Received text payload, {body.Length} bytes.");
        }

        private static void ApplyImage(byte[] body)
        {
            OnClipboardReceived?.Invoke("[Image Received]");

#if ANDROID
            var context = global::Android.App.Application.Context;
            var cacheDir = new Java.IO.File(context.CacheDir, "images");
            if (!cacheDir.Exists()) cacheDir.Mkdirs();

            PruneImageCache(cacheDir);

            // A unique name per image: the previous fixed "clipboard.jpg" meant the
            // FileProvider URI never changed, so consumers could serve a cached older image.
            var imageFile = new Java.IO.File(cacheDir, $"clip_{DateTime.UtcNow:yyyyMMddHHmmssfff}.jpg");
            System.IO.File.WriteAllBytes(imageFile.AbsolutePath!, body);

            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(
                context, "com.companyname.androidclient.fileprovider", imageFile);

            var clipboard = (ClipboardManager?)context.GetSystemService(Context.ClipboardService);
            if (clipboard != null)
            {
                clipboard.PrimaryClip = ClipData.NewUri(context.ContentResolver, "Mesh Sync Image", uri);
            }
#endif
            Activity.Record(SyncDirection.Received, SyncItemKind.Image, body.Length);
            Log.Write("Sync", $"Received image payload, {body.Length} bytes.");
        }

#if ANDROID
        /// <summary>Keeps the image cache bounded - nothing ever deleted these before.</summary>
        private static void PruneImageCache(Java.IO.File cacheDir)
        {
            try
            {
                var files = cacheDir.ListFiles();
                if (files == null || files.Length <= 8) return;

                Array.Sort(files, (a, b) => a.LastModified().CompareTo(b.LastModified()));
                for (int i = 0; i < files.Length - 8; i++)
                {
                    try { files[i].Delete(); } catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Sync", "Pruning image cache failed", ex);
            }
        }
#endif

        // ---------------------------------------------------------------- reconnect loop

        private static void StartLoop()
        {
            // A plain lock, not the connect gate: the connect gate is held across a network
            // round trip, and this is called from the UI thread.
            lock (_loopGate)
            {
                if (_loopTask != null && !_loopTask.IsCompleted) return;

                _loopCts?.Dispose();
                _loopCts = new CancellationTokenSource();
                var token = _loopCts.Token;
                _loopTask = Task.Run(() => ReconnectLoopAsync(token));
            }
        }

        private static void SignalRetry()
        {
            _networkHintPending = true;
            try { _disconnectSignal.Release(); } catch (SemaphoreFullException) { }
        }

        /// <summary>Discards queued wake-ups so a burst of them cannot spin the loop.</summary>
        private static void DrainSignal()
        {
            while (_disconnectSignal.CurrentCount > 0)
            {
                if (!_disconnectSignal.Wait(0)) break;
            }
        }

        /// <summary>
        /// Called by the host app when connectivity changes, so a returning Wi-Fi network
        /// reconnects immediately instead of waiting out the current backoff.
        /// </summary>
        public static void NotifyNetworkAvailable()
        {
            Log.Write("Sync", "Network became available - retrying now.");
            SignalRetry();
        }

        private static async Task ReconnectLoopAsync(CancellationToken token)
        {
            int failures = 0;

            while (!token.IsCancellationRequested)
            {
                var (hostIp, hostPubKey) = LoadHost();
                if (string.IsNullOrEmpty(hostIp))
                {
                    // Logged, not just reported: a silent exit here looks identical to a
                    // hung loop when reading the device log.
                    Log.Write("Sync", "Reconnect loop stopping: no paired host saved.");
                    Report("Not paired yet.");
                    return;
                }

                bool connected;

                // Asking the OS first is far cheaper than finding out by timing out. With
                // Wi-Fi off, the doomed TCP attempt was most of the wait before Bluetooth
                // was even tried.
                if (HasUsableNetwork())
                {
                    Log.Write("Sync", $"Attempt {failures + 1}: trying Wi-Fi at {hostIp}.");
                    connected = await TryConnectAsync(hostIp, hostPubKey, token).ConfigureAwait(false);
                }
                else
                {
                    Log.Write("Sync", $"Attempt {failures + 1}: no network, going straight to Bluetooth.");
                    connected = await TryConnectOverBleAsync(token).ConfigureAwait(false);
                    if (!connected) Report("Not reachable over Wi-Fi or Bluetooth");
                }

                if (connected)
                {
                    failures = 0;
                    _networkHintPending = false;
                    DrainSignal();

                    // Park until the connection drops. No polling, so an idle paired phone
                    // does no work at all - the previous loop woke every 3 seconds forever.
                    try { await _disconnectSignal.WaitAsync(token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                failures++;

                if (_networkHintPending)
                {
                    // Connectivity just changed; do not punish the next attempt.
                    _networkHintPending = false;
                    failures = 0;
                }

                // Bluetooth is a local radio, not a network that comes and goes, so backing
                // off for a minute before rescanning made the fallback feel broken - it only
                // appeared after the user poked the service. Retry it briskly instead.
                var delay = BackoffFor(failures);
                if (delay > BleRetryCeiling) delay = BleRetryCeiling;
                Report($"Reconnecting in {delay.TotalSeconds:F0}s...");

                try
                {
                    // A network-available hint cuts the wait short.
                    await _disconnectSignal.WaitAsync(delay, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
            }

            Log.Write("Sync", "Reconnect loop stopped.");
        }

        /// <summary>
        /// Exponential backoff capped at a minute, with jitter so a phone and a laptop
        /// waking together do not retry in lockstep. The old fixed 3 second retry ran all
        /// night whenever the laptop was off, which is exactly the battery drain the
        /// project guidelines call out.
        /// </summary>
        private static TimeSpan BackoffFor(int failures)
        {
            double seconds = MinBackoff.TotalSeconds * Math.Pow(2, Math.Min(failures - 1, 6));
            seconds = Math.Min(seconds, MaxBackoff.TotalSeconds);

            double jitter;
            lock (_jitter) jitter = 0.8 + _jitter.NextDouble() * 0.4;

            return TimeSpan.FromSeconds(seconds * jitter);
        }

        private static async Task<bool> TryConnectAsync(string hostIp, string hostPubKey, CancellationToken token)
        {
            await _connectGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_transport?.IsConnected == true) return true;

                RetireTransport();

                // Derived before the socket opens so the first payload is never dropped
                // for want of a key, and off whatever thread called in - Argon2id costs
                // 64 MB and a few hundred milliseconds and must never touch the UI thread.
                await GetKeyAsync().ConfigureAwait(false);

                var transport = new TcpTransportConnection { LocalDeviceName = LocalDeviceName };
                transport.PayloadReceived += Transport_PayloadReceived;
                transport.ConnectionClosed += Transport_ConnectionClosed;
                transport.PeerIdentified += Transport_PeerIdentified;
                _transport = transport;

                Report($"Connecting to {hostIp}...");

                // Bounded, because a TCP connect to an unreachable host waits for the OS
                // default. Measured at over two minutes with Wi-Fi off, which is two minutes
                // before the Bluetooth fallback is even attempted.
                using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    connectTimeout.CancelAfter(TcpConnectTimeout);
                    await transport.ConnectAsync(hostIp, connectTimeout.Token).ConfigureAwait(false);
                }

                ActiveTransport = TransportKind.WiFi;

                Report("Connected!");
                Log.Write("Sync", $"Connected to {hostIp}. Paired key: {Truncate(hostPubKey)}");
                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The loop itself is shutting down, so do not start anything new.
                RetireTransport();
                return false;
            }
            catch (Exception ex)
            {
                // Everything else is a failed Wi-Fi attempt, including our own connect
                // timeout firing. That timeout surfaces as OperationCanceledException, and
                // catching it as cancellation meant a phone with Wi-Fi already off never
                // reached the Bluetooth fallback at all - it only got there when Wi-Fi
                // dropped mid-session and the socket failed with an unreachable error
                // instead. The guard above is what separates the two.
                bool timedOut = ex is OperationCanceledException;
                Log.Write("Sync", timedOut
                    ? $"Connect to {hostIp} timed out after {TcpConnectTimeout.TotalSeconds:F0}s."
                    : $"Connect to {hostIp} failed: {ex.GetType().Name}: {ex.Message}");

                RetireTransport();

                // No usable network is exactly the case BLE exists for, so fall back to it
                // rather than reporting failure and waiting out a backoff.
                if (await TryConnectOverBleAsync(token).ConfigureAwait(false)) return true;

                Report("Not reachable over Wi-Fi or Bluetooth");
                return false;
            }
            finally
            {
                _connectGate.Release();
            }
        }

        /// <summary>
        /// Finds the computer by its Mesh Sync service UUID and opens a GATT link. Text only:
        /// BLE throughput would make an image take minutes, so images stay on Wi-Fi.
        /// </summary>
        /// <summary>
        /// True when a transport exists that could actually reach the paired computer.
        ///
        /// Specifically Wi-Fi or Ethernet, not merely "some network": the host is a private
        /// LAN address, and mobile data can never route to it. Asking only whether a network
        /// existed answered yes on cellular and spent the TCP timeout proving otherwise
        /// before Bluetooth got a turn.
        /// </summary>
        private static bool HasUsableNetwork()
        {
#if ANDROID
            try
            {
                var manager = (global::Android.Net.ConnectivityManager?)global::Android.App.Application.Context
                    .GetSystemService(Context.ConnectivityService);

                var network = manager?.ActiveNetwork;
                if (network == null) return false;

                var capabilities = manager?.GetNetworkCapabilities(network);
                if (capabilities == null) return false;

                // Deliberately not checking Validated: a local-only network with no internet
                // uplink is exactly the setup this project is built for.
                return capabilities.HasTransport(global::Android.Net.TransportType.Wifi)
                       || capabilities.HasTransport(global::Android.Net.TransportType.Ethernet);
            }
            catch (Exception ex)
            {
                Log.Write("Sync", "Could not read network state, assuming one exists", ex);
                return true;
            }
#else
            return true;
#endif
        }

        private static DateTime _lastBleScanUtc = DateTime.MinValue;

        /// <summary>
        /// Android silently throttles an app that starts and stops BLE scans more than about
        /// five times in thirty seconds - the scan simply returns nothing, with no error. The
        /// reconnect loop retries far more often than that, which is why Bluetooth appeared
        /// to connect only by luck, or only after the service was stopped and started. One
        /// long scan, spaced out, stays under the limit.
        /// </summary>
        private static readonly TimeSpan BleScanCooldown = TimeSpan.FromSeconds(12);

        private static readonly TimeSpan BleScanWindow = TimeSpan.FromSeconds(25);

        private static async Task<bool> TryConnectOverBleAsync(CancellationToken token)
        {
#if ANDROID
            var sinceLastScan = DateTime.UtcNow - _lastBleScanUtc;
            if (sinceLastScan < BleScanCooldown)
            {
                var wait = BleScanCooldown - sinceLastScan;
                Log.Write("Sync", $"Holding off the Bluetooth scan for {wait.TotalSeconds:F0}s to stay under Android's scan throttle.");
                try { await Task.Delay(wait, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return false; }
            }

            Platforms.Android.AndroidBleDiscovery? discovery = null;
            try
            {
                Report("No Wi-Fi. Looking over Bluetooth...");

                _lastBleScanUtc = DateTime.UtcNow;
                discovery = new Platforms.Android.AndroidBleDiscovery();
                string? address = await discovery.FindPeerAsync(BleScanWindow, token).ConfigureAwait(false);

                if (string.IsNullOrEmpty(address))
                {
                    Log.Write("Sync", "No Mesh Sync peer found over BLE.");
                    return false;
                }

                var ble = new Platforms.Android.AndroidBleTransport();
                ble.PayloadReceived += Transport_PayloadReceived;
                ble.ConnectionClosed += Transport_ConnectionClosed;
                _transport = ble;

                await ble.ConnectAsync(address!, token).ConfigureAwait(false);

                ActiveTransport = TransportKind.Ble;
                Report("Connected over Bluetooth");
                Log.Write("Sync", $"Connected to {address} over BLE.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Sync", "BLE connect failed", ex);
                RetireTransport();
                return false;
            }
            finally
            {
                discovery?.Dispose();
            }
#else
            await Task.CompletedTask;
            return false;
#endif
        }

        private static void Transport_PayloadReceived(object? sender, PayloadReceivedEventArgs e)
        {
            // Runs on the transport receive loop. Kept synchronous and total so a failure
            // here can never take the connection down.
            try { HandlePayload(e.EncryptedPayload); }
            catch (Exception ex) { Log.Write("Sync", "Payload handling failed", ex); }
        }

        private static void Transport_ConnectionClosed(object? sender, EventArgs e)
        {
            Report("Disconnected. Retrying...");
            SignalRetry();
        }

        private static void Transport_PeerIdentified(object? sender, PeerIdentifiedEventArgs e)
        {
            // Re-report so dashboards swap the placeholder for the real computer name.
            Report("Connected!");
        }

        /// <summary>Detaches and disposes the current transport. Skipping this was the leak.</summary>
        private static void RetireTransport()
        {
            var transport = Interlocked.Exchange(ref _transport, null);
            ActiveTransport = TransportKind.None;
            if (transport == null) return;

            transport.PayloadReceived -= Transport_PayloadReceived;
            transport.ConnectionClosed -= Transport_ConnectionClosed;
            if (transport is TcpTransportConnection tcp) tcp.PeerIdentified -= Transport_PeerIdentified;
            try { transport.Dispose(); } catch (Exception ex) { Log.Write("Sync", "Disposing transport failed", ex); }
        }

        // ---------------------------------------------------------------- key + prefs

        private static async Task<byte[]> GetKeyAsync()
        {
            var existing = Volatile.Read(ref _aesKey);
            if (existing != null) return existing;

            await _keyGate.WaitAsync().ConfigureAwait(false);
            try
            {
                existing = Volatile.Read(ref _aesKey);
                if (existing != null) return existing;

                // Deliberately off the calling thread: this used to run in a static field
                // initialiser, so the first touch of SyncManager blocked the UI thread for
                // hundreds of milliseconds and spiked 64 MB during app start.
                var key = await Task.Run(() =>
                    CryptoEngine.DeriveKey("MasterPassword123", System.Text.Encoding.UTF8.GetBytes("Salt")))
                    .ConfigureAwait(false);

                Volatile.Write(ref _aesKey, key);
                Log.Write("Sync", "Key derivation complete.");
                return key;
            }
            finally
            {
                _keyGate.Release();
            }
        }

        private static void SaveHost(string hostIp, string hostPubKey)
        {
#if ANDROID
            try
            {
                var prefs = global::Android.App.Application.Context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
                prefs?.Edit()?.PutString(PrefHostIp, hostIp)?.PutString(PrefHostPubKey, hostPubKey)?.Apply();
            }
            catch (Exception ex) { Log.Write("Sync", "Saving pairing details failed", ex); }
#endif
        }

        private static (string HostIp, string HostPubKey) LoadHost()
        {
#if ANDROID
            try
            {
                var prefs = global::Android.App.Application.Context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
                return (prefs?.GetString(PrefHostIp, "") ?? "", prefs?.GetString(PrefHostPubKey, "") ?? "");
            }
            catch (Exception ex)
            {
                Log.Write("Sync", "Loading pairing details failed", ex);
                return ("", "");
            }
#else
            return ("", "");
#endif
        }

        private static void Report(string status)
        {
            try { OnConnectionStatusChanged?.Invoke(status); }
            catch (Exception ex) { Log.Write("Sync", "Status handler threw", ex); }
        }

        private static string Truncate(string value) =>
            string.IsNullOrEmpty(value) ? "(none)" :
            value.Length <= 12 ? value : value.Substring(0, 12) + "...";
    }
}
