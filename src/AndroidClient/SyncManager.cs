using System;
using System.Threading;
using System.Threading.Tasks;
using CoreLib;
using CoreLib.Diagnostics;
using CoreLib.Identity;
using CoreLib.Transport;

#if ANDROID
using Android.App;
using Android.Content;
#endif

namespace AndroidClient
{
    /// <summary>
    /// Owns the links to every paired device, including reconnection.
    ///
    /// Every entry point funnels through the loops below, because the previous design let the
    /// deep link, the activity, the accessibility service and the disconnect handler each start
    /// their own connect attempt. Each attempt built a fresh transport and overwrote the
    /// previous one without disposing it, so sockets and receive-loop tasks accumulated for the
    /// life of the process.
    ///
    /// <para><b>Bluetooth standby.</b> The tiers are inverted from where they started. Bluetooth
    /// is the standing link and is held whenever a peer is in range; Wi-Fi is raised on demand
    /// and dropped again. Both are held at once when both are wanted, which the previous
    /// single-transport field could not express - whichever tier connected last silently
    /// replaced the other.</para>
    ///
    /// <para>Wi-Fi is wanted when any of these hold: the screen is on, a send needs it, a peer
    /// has asked for it, or Bluetooth is not up. That last one matters most - without it,
    /// losing Bluetooth would leave the phone with no link at all, and standby would be
    /// strictly worse than the arrangement it replaced.</para>
    ///
    /// <para><b>No fixed roles.</b> This phone is not a client. It listens as well as dials over
    /// Wi-Fi, and over Bluetooth it holds both a link it opened and one a peer opened to it -
    /// because a device that cannot advertise has to be the central, so its peer must be the one
    /// advertising. Which applies to a given peer is settled by fingerprint, identically on both
    /// sides.</para>
    /// </summary>
    public static class SyncManager
    {
        private const byte ContentText = SyncContent.Text;
        private const byte ContentImage = SyncContent.Image;

        private const string PrefsName = "SyncPrefs";
        private const string PrefPaused = "UserPaused";

        /// <summary>How long to wait for a Wi-Fi socket before giving up on the attempt.</summary>
        private static readonly TimeSpan TcpConnectTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// How long the pairing screen waits before saying it did not work.
        ///
        /// Sized for a person, not a network: the other device refuses the first attempt and
        /// asks someone to compare two fingerprints, so this has to cover picking up a laptop
        /// and looking at it. Being told it failed while the prompt is still on screen over
        /// there would be worse than waiting.
        /// </summary>
        private static readonly TimeSpan PairingConfirmationWait = TimeSpan.FromSeconds(45);

        /// <summary>
        /// How long a sender waits for Wi-Fi to come up before abandoning an image.
        ///
        /// Longer than the connect timeout because the link may have to be requested first and
        /// the peer needs a moment to dial in. Short enough that a phone with Wi-Fi switched
        /// off does not leave the user watching a spinner.
        /// </summary>
        private static readonly TimeSpan WiFiOnDemandTimeout = TimeSpan.FromSeconds(12);

        /// <summary>
        /// How long a request from the peer keeps Wi-Fi up. Covers the round trip plus the
        /// transfer, and lapses on its own so nothing has to remember to cancel it.
        /// </summary>
        private static readonly TimeSpan WiFiWakeWindow = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Upper bound on the Bluetooth retry gap while the user is present, so the standing
        /// link is re-established promptly rather than after a full backoff.
        /// </summary>
        private static readonly TimeSpan BleRetryCeilingActive = TimeSpan.FromSeconds(8);

        /// <summary>
        /// Upper bound with the screen off. Scanning is the expensive part of the Bluetooth
        /// tier, and retrying every few seconds all night - which is what a single brisk
        /// ceiling would do with the computer switched off - is exactly the drain that holding
        /// a cheap link was supposed to avoid. Nothing is being copied while the screen is off,
        /// so a slower rescan costs nothing that matters, and screen-on signals both loops
        /// immediately anyway.
        /// </summary>
        private static readonly TimeSpan BleRetryCeilingIdle = TimeSpan.FromSeconds(60);

        private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

        private static readonly object _loopGate = new();
        private static readonly object _securityGate = new();

        /// <summary>One wake-up signal per loop, so a Bluetooth event cannot spin the Wi-Fi loop.</summary>
        private static readonly SemaphoreSlim _bleSignal = new(0);

        private static readonly SemaphoreSlim _wifiSignal = new(0);

        private static readonly EchoSuppressor _echo = new(TimeSpan.FromSeconds(10));
        private static readonly Random _jitter = new();

        // ── links ───────────────────────────────────────────────────────────────────────
        // Two fields, not one. Each is owned exclusively by its own loop; everything else
        // signals rather than connecting, so there is no gate to hold across a round trip.

        /// <summary>The Bluetooth link this device opened, as the central.</summary>
        private static ITransportConnection? _bleLink;

        /// <summary>
        /// The Bluetooth link a peer opened to this device, as the central to our peripheral.
        ///
        /// Held alongside rather than instead of the one above, because which of the two roles
        /// a phone takes depends on the peer: a device that cannot advertise must always be the
        /// central, so its peer has to be the one advertising. Two phones would otherwise both
        /// sit scanning for something neither was broadcasting.
        /// </summary>
        private static ITransportConnection? _blePeripheralLink;

        private static int _peripheralStarted;

        /// <summary>
        /// The Wi-Fi tier: one link per paired device, and this phone both listens and dials.
        ///
        /// It was a single <see cref="TcpTransportConnection"/> dialling one hardcoded host,
        /// which made phone-to-phone impossible and meant a second device could only be reached
        /// by dropping the first.
        /// </summary>
        private static MeshLinks? _mesh;

        private static CancellationTokenSource? _loopCts;
        private static Task? _bleLoopTask;
        private static Task? _wifiLoopTask;

        // ── Wi-Fi demand ────────────────────────────────────────────────────────────────

        private static volatile bool _screenOn = true;
        private static int _wifiHolds;
        private static long _wifiWakeUntilTicks;
        private static volatile bool _networkHintPending;

        /// <summary>Stops the "no network" line repeating once per retry while there is none.</summary>
        private static volatile bool _reportedNoNetwork;

        private static PeerSecurity? _security;
        private static string? _lastPeerName;

        /// <summary>
        /// This device's identity and the devices it is paired with.
        ///
        /// Loaded lazily but not off the UI thread deliberately: generating a P-256 keypair is
        /// sub-millisecond, unlike the Argon2id derivation this replaced, which cost 64 MB and
        /// a few hundred milliseconds and had to be pushed onto a worker for exactly that
        /// reason. Reading a small JSON file alongside it is not worth an async ceremony.
        /// </summary>
        public static PeerSecurity Security
        {
            get
            {
                var existing = Volatile.Read(ref _security);
                if (existing != null) return existing;

                lock (_securityGate)
                {
                    existing = Volatile.Read(ref _security);
                    if (existing != null) return existing;

                    var created = PeerSecurity.LoadOrCreate(StorageDirectory(), KeyProtector());
                    Volatile.Write(ref _security, created);
                    Log.Write("Sync", $"Identity {created.Identity.ShortFingerprint}, {created.Peers.Count} paired device(s).");
                    return created;
                }
            }
        }

        /// <summary>
        /// Wraps the private key with a Keystore-held AES key before it reaches the disk.
        ///
        /// Best effort by design: a device whose Keystore refuses stores the key as it always
        /// was and keeps working, rather than refusing to start over a hardening measure.
        /// </summary>
        private static IKeyProtector? KeyProtector()
        {
#if ANDROID
            try { return new Platforms.Android.AndroidKeyProtector(); }
            catch (Exception ex)
            {
                Log.Write("Identity", "The Keystore is unavailable; the identity will be stored unwrapped.", ex);
                return null;
            }
#else
            return null;
#endif
        }

        private static string StorageDirectory()
        {
#if ANDROID
            // App-private, so the key file is unreadable by anything else on the device.
            return global::Android.App.Application.Context.FilesDir?.AbsolutePath
                   ?? System.IO.Path.GetTempPath();
#else
            return System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeshSync");
#endif
        }

        public static event Action<string>? OnConnectionStatusChanged;
        public static event Action<string>? OnClipboardReceived;

        public enum TransportKind { None, WiFi, Ble }

        public static bool BleConnected =>
            _bleLink?.IsConnected == true || _blePeripheralLink?.IsConnected == true;

        /// <summary>Whichever Bluetooth link is live, preferring the one this device opened.</summary>
        private static ITransportConnection? LiveBleLink =>
            _bleLink?.IsConnected == true ? _bleLink :
            _blePeripheralLink?.IsConnected == true ? _blePeripheralLink :
            null;

        /// <summary>
        /// The agreed key of whichever Bluetooth link is live.
        ///
        /// Null until that link's hello has crossed, because the key is now per connection
        /// rather than derived from the peer's identity and known in advance.
        /// </summary>
        private static PeerSession? LiveBleSession => SessionOf(LiveBleLink);

        /// <summary>The agreed key held by a Bluetooth transport, whichever half it is.</summary>
        private static PeerSession? SessionOf(object? link)
        {
#if ANDROID
            return link switch
            {
                Platforms.Android.AndroidBleTransport central => central.Peer,
                Platforms.Android.AndroidBlePeripheral peripheral => peripheral.Peer,
                _ => null
            };
#else
            _ = link;
            return null;
#endif
        }

        /// <summary>
        /// Authorises a Bluetooth peer and agrees this link's key in one step. Both halves of
        /// the tier use it, so a device this phone has not paired with never reaches the point
        /// of having a session to encrypt with.
        /// </summary>
        private static PeerSession? OpenBleSession(string peerPublicKey, string peerEphemeral,
                                                   EphemeralKeyPair localEphemeral) =>
            Security.Authorise(peerPublicKey)
                ? Security.OpenSession(peerPublicKey, localEphemeral, peerEphemeral)
                : null;

        public static bool WiFiConnected => _mesh?.IsConnectedToAny == true;

        public static bool IsConnected => BleConnected || WiFiConnected;

        /// <summary>
        /// Which tier would carry the next item. Wi-Fi wins when both are up because it carries
        /// anything; Bluetooth is what remains when it is not.
        /// </summary>
        public static TransportKind ActiveTransport =>
            WiFiConnected ? TransportKind.WiFi :
            BleConnected ? TransportKind.Ble :
            TransportKind.None;

        /// <summary>What has synced this session. Never persisted - clipboard traffic is ephemeral.</summary>
        public static readonly SyncActivityLog Activity = new(capacity: 12);

        /// <summary>
        /// Friendly name of the paired computer, once it has announced itself.
        ///
        /// Only the Wi-Fi transport carries the hello frame, and under standby Wi-Fi is down
        /// most of the time, so the last name seen is remembered. Without that the dashboard
        /// fell back to "your computer" the moment the socket dropped, which reads as a fault
        /// rather than as the design working.
        /// </summary>
        public static string? PeerName
        {
            get
            {
                var mesh = _mesh;
                if (mesh != null)
                {
                    foreach (var fingerprint in mesh.ConnectedPeers)
                    {
                        string? name = mesh.NameOf(fingerprint);
                        if (!string.IsNullOrWhiteSpace(name)) return name;
                    }
                }

                if (!string.IsNullOrWhiteSpace(_lastPeerName)) return _lastPeerName;

                // Falls back to what the registry recorded, so a device that has only ever been
                // reached over Bluetooth still has a name. Nothing showed one before: the name
                // arrived solely in the Wi-Fi hello, so a Bluetooth-only pair was permanently
                // anonymous and the notification fell back to an IP address instead.
                foreach (var peer in Security.Peers.Peers)
                {
                    if (!string.IsNullOrWhiteSpace(peer.Name)) return peer.Name;
                }

                return null;
            }
        }

        private static FileTransferService? _files;

        /// <summary>
        /// File transfer, both directions.
        ///
        /// Wi-Fi only: at roughly 6.7 KB/s a photograph would take a quarter of an hour over
        /// Bluetooth, so a send that finds only Bluetooth up raises Wi-Fi first rather than
        /// promising something the tier cannot do.
        /// </summary>
        private static FileTransferService Files
        {
            get
            {
                var existing = Volatile.Read(ref _files);
                if (existing != null) return existing;

                lock (_securityGate)
                {
                    existing = Volatile.Read(ref _files);
                    if (existing != null) return existing;

                    var created = new FileTransferService(
                        System.IO.Path.Combine(StorageDirectory(), "incoming"),
                        (fingerprint, contentType, body, token) =>
                            Mesh.SendToAsync(fingerprint, contentType, body, token));

                    created.FileReceived += SaveReceivedFile;
                    created.FileFailed += (name, reason) =>
                    {
                        Log.Write("Sync", $"\"{name}\" did not arrive: {reason}.");
                        Report($"{name} did not arrive");
                    };

                    Volatile.Write(ref _files, created);
                    return created;
                }
            }
        }

        /// <summary>The mesh, created on first use so the identity is loaded before it.</summary>
        private static MeshLinks Mesh
        {
            get
            {
                var existing = Volatile.Read(ref _mesh);
                if (existing != null) return existing;

                lock (_securityGate)
                {
                    existing = Volatile.Read(ref _mesh);
                    if (existing != null) return existing;

                    var created = new MeshLinks(Security) { LocalDeviceName = LocalDeviceName };
                    created.PayloadReceived += Mesh_PayloadReceived;
                    created.PeerConnected += Mesh_PeerConnected;
                    created.PeerDisconnected += Mesh_PeerDisconnected;

                    Volatile.Write(ref _mesh, created);
                    return created;
                }
            }
        }

        /// <summary>True once a device has been paired by scanning a code or entering it by hand.</summary>
        public static bool IsPaired => !Security.Peers.IsEmpty;

        /// <summary>Address of the paired computer, for display. Empty when nothing is paired.</summary>
        public static string PairedAddress => PrimaryPeer()?.LastAddress ?? "";

        /// <summary>What this set of devices is called, learned when this phone joined it.</summary>
        public static string MeshName => Security.Peers.MeshNameOrDefault;

        /// <summary>
        /// The device whose address stands in for the mesh in the UI.
        ///
        /// The Wi-Fi tier holds a link per peer and dials all of them, so this is not "the one
        /// we talk to" - it is only which peer's address is worth showing when a single line of
        /// text has to describe the set.
        /// </summary>
        private static PeerRecord? PrimaryPeer()
        {
            var peers = Security.Peers.Peers;
            if (peers.Count == 0) return null;

            // Whichever answered most recently, so a device that has moved networks is not
            // preferred over one that is demonstrably reachable.
            PeerRecord best = peers[0];
            foreach (var peer in peers)
            {
                if (peer.LastSeenUtc > best.LastSeenUtc) best = peer;
            }
            return best;
        }

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

        /// <summary>
        /// Pairs with a computer and starts the managed reconnect loops.
        ///
        /// The public key is no longer decoration. It used to be scanned, stored and never
        /// consulted while both sides derived from a hardcoded password; it is now the thing
        /// the session key is agreed against, so a code that will not parse is refused here
        /// rather than producing a link that connects and then fails every decryption.
        /// </summary>
        public static async Task<bool> ConnectAsync(string hostIp, string hostPubKey, string? meshName = null)
        {
            if (string.IsNullOrWhiteSpace(hostIp)) return false;

            // Adopted only if this device has not already been given one, so joining a mesh
            // names it and re-pairing later cannot silently rename it underneath the user.
            Security.Peers.AdoptMeshName(meshName);

            if (!Security.Peers.Trust(hostPubKey, name: null, address: hostIp))
            {
                Log.Write("Sync", "Pairing refused: that code is not a usable public key.");
                Report("That pairing key is not valid.");
                return false;
            }

            // Pairing is an explicit "I want this on", so it clears an earlier Stop.
            SetPaused(false);
            StartLoops();

            // Nudge both loops so a freshly paired host is tried immediately rather than
            // after whatever backoff the previous failures had accumulated.
            SignalBle();
            SignalWiFi();

            // Report the outcome of the first attempt for the benefit of the pairing UI.
            //
            // Longer than it needs to be for the connection itself, because the other device
            // now refuses the first attempt and asks a human to compare fingerprints before it
            // will accept. Twelve seconds was enough when the code alone was the whole
            // handshake; it is not enough for someone to pick up a laptop and look.
            var deadline = DateTime.UtcNow + PairingConfirmationWait;
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

        /// <summary>Starts the reconnect loops using previously saved pairing details.</summary>
        public static Task AutoConnectAsync(bool isUserInitiated = false)
        {
            // Every caller funnels through here, so honouring the flag once keeps the app,
            // the accessibility service and the deep link from each reviving a stopped sync.
            if (IsPaused)
            {
                Log.Write("Sync", "Auto-connect skipped: syncing is paused.");
                return Task.CompletedTask;
            }

            if (!IsPaired)
            {
                Report("Not paired yet.");
                return Task.CompletedTask;
            }

            StartLoops();

            if (isUserInitiated)
            {
                SignalBle();
                SignalWiFi();
            }

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

        /// <summary>
        /// Sends a file to every connected device, raising Wi-Fi first if only Bluetooth is up.
        ///
        /// Fanned out rather than sent once: there is a key per connection, so a file goes to
        /// each device as its own stream. That is genuinely N times the bytes, and it is the
        /// cost of a paired device being unable to read another pair's traffic.
        /// </summary>
        public static async Task<bool> SendFileAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return false;
            if (!IsPaired) return false;

            string name = System.IO.Path.GetFileName(path);

            // A file needs the tier that can carry it. Holding a lease keeps Wi-Fi up for the
            // whole transfer even if the screen goes off partway through, which for anything
            // larger than a photograph it very well might.
            Interlocked.Increment(ref _wifiHolds);
            try
            {
                if (!WiFiConnected && !await WaitForWiFiAsync(WiFiOnDemandTimeout).ConfigureAwait(false))
                {
                    Log.Write("Sync", $"Could not raise Wi-Fi, so \"{name}\" was not sent.");
                    Report("Files need Wi-Fi");
                    return false;
                }

                var targets = Mesh.ConnectedPeers;
                if (targets.Count == 0)
                {
                    Report("No devices in range");
                    return false;
                }

                bool anySent = false;

                foreach (string fingerprint in targets)
                {
                    var result = await Files.SendAsync(fingerprint, path).ConfigureAwait(false);

                    if (result == FileSendResult.Sent) anySent = true;
                    else Log.Write("Sync", $"\"{name}\" to {DeviceIdentity.Shorten(fingerprint)}: {result}.");
                }

                if (anySent)
                {
                    try
                    {
                        Activity.Record(SyncDirection.Sent, SyncItemKind.File,
                                        new System.IO.FileInfo(path).Length, name);
                    }
                    catch { }

                    Report($"Sent {name}");
                }
                else
                {
                    Report($"Could not send {name}");
                }

                return anySent;
            }
            finally
            {
                Interlocked.Decrement(ref _wifiHolds);
                SignalWiFi();
            }
        }

        /// <summary>Stops both loops and tears the links down. Pairing details are kept.</summary>
        public static async Task DisconnectAsync()
        {
            CancellationTokenSource? cts;
            Task? ble;
            Task? wifi;

            lock (_loopGate)
            {
                cts = _loopCts;
                ble = _bleLoopTask;
                wifi = _wifiLoopTask;
                _loopCts = null;
                _bleLoopTask = null;
                _wifiLoopTask = null;
            }

            try { cts?.Cancel(); } catch { }

            foreach (var loop in new[] { ble, wifi })
            {
                if (loop == null) continue;
                try { await loop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch { /* best effort */ }
            }

            cts?.Dispose();
            RetireBle();
            RetirePeripheral();
            RetireWiFi();

            // Listening stops too. Dropping the links but staying reachable would let a peer
            // dial straight back in and quietly undo an explicit Stop.
            try { _mesh?.StopListening(); }
            catch (Exception ex) { Log.Write("Sync", "Could not stop listening", ex); }

            _echo.Clear();
            Report("Disconnected.");
        }

        // ---------------------------------------------------------------- screen state

        /// <summary>
        /// The screen came on. Wi-Fi is raised now rather than when something needs sending,
        /// so the connect cost lands while the user is unlocking rather than while they are
        /// waiting for a paste. Bluetooth is nudged too: if the link lapsed overnight this is
        /// the moment to get it back.
        /// </summary>
        public static void NotifyScreenOn()
        {
            if (_screenOn) return;
            _screenOn = true;
            Log.Write("Sync", "Screen on - raising Wi-Fi.");
            SignalWiFi();
            SignalBle();
        }

        /// <summary>
        /// The screen went off. Wi-Fi is dropped unless something still needs it, leaving
        /// Bluetooth to hold presence. This is the half of standby that actually saves
        /// anything: the socket is down all night rather than heartbeating through it.
        /// </summary>
        public static void NotifyScreenOff()
        {
            if (!_screenOn) return;
            _screenOn = false;
            Log.Write("Sync", "Screen off - Wi-Fi no longer held open.");
            SignalWiFi();
        }

        /// <summary>
        /// Wakes both loops at once, for something that has just made a connection possible
        /// which was not before - confirming a device by hand, most of all.
        ///
        /// A confirmed device was refused and told to come back, so it is waiting on a retry
        /// rather than on a socket. Without this it would connect on the next scheduled round,
        /// which reads as the confirmation not having worked.
        /// </summary>
        public static void NudgeReconnect()
        {
            StartLoops();
            SignalBle();
            SignalWiFi();
        }

        /// <summary>
        /// Called by the host app when connectivity changes, so a returning Wi-Fi network
        /// reconnects immediately instead of waiting out the current backoff.
        /// </summary>
        public static void NotifyNetworkAvailable()
        {
            Log.Write("Sync", "Network became available - retrying now.");
            _networkHintPending = true;
            SignalWiFi();
        }

        // ---------------------------------------------------------------- send path

        private static async Task SendAsync(byte contentType, byte[] body)
        {
            if (!IsConnected && !CouldRaiseWiFi()) return;

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

            byte[] payload = body;
            if (contentType == ContentImage)
            {
                payload = ImageCodec.CompressForTransport(body);
                if (payload.Length != body.Length)
                    Log.Write("Sync", $"Recompressed image {body.Length} -> {payload.Length} bytes.");
            }

            if (payload.Length + CryptoEngine.TaggedOverheadBytes > TcpTransportConnection.MaxPayloadBytes)
            {
                Log.Write("Sync", $"Refusing to send {payload.Length} bytes (over the limit).");
                Report("Item too large to sync.");
                return;
            }

            string? textContent = contentType == ContentText
                ? System.Text.Encoding.UTF8.GetString(body)
                : null;

            // Fanned out, not sent: one ciphertext per peer, because the key is per pair.
            // Bluetooth is skipped for any device Wi-Fi already reached, or a phone holding
            // both links would receive every copy twice.
            int sent = WiFiConnected ? await Mesh.BroadcastAsync(contentType, payload).ConfigureAwait(false) : 0;
            sent += await SendOverBluetoothAsync(contentType, payload).ConfigureAwait(false);

            if (sent == 0)
            {
                // Nothing was open, or what was open could not carry it. Raising Wi-Fi is the
                // one remaining option, and is what an image always needs.
                sent = await SendByRaisingWiFiAsync(contentType, payload).ConfigureAwait(false);
            }

            if (sent == 0)
            {
                Log.Write("Sync", "Nothing was reachable, so the item was dropped.");
                // Wi-Fi is raised automatically for an image now, so reaching here means the
                // attempt failed rather than that the user has to go and do something.
                Report(contentType == ContentImage ? "Could not reach Wi-Fi for that" : "No devices in range");
                return;
            }

            Activity.Record(SyncDirection.Sent, kind, payload.Length, textContent);
            Log.Write("Sync", $"Sent {(contentType == ContentText ? "text" : "image")} to {sent} device(s), {payload.Length} bytes.");
        }

        /// <summary>
        /// Sends over Bluetooth, to devices Wi-Fi did not already reach and only when the
        /// payload fits inside the tier's ceiling.
        /// </summary>
        private static async Task<int> SendOverBluetoothAsync(byte contentType, byte[] payload)
        {
            var ble = LiveBleLink;
            if (ble == null) return 0;

            // Sealed with the key this link agreed, so there is nothing left to infer about who
            // the peer is. A link with no session has not finished its handshake and is skipped.
            var session = SessionOf(ble);
            if (session == null) return 0;

            if (_mesh?.IsConnectedTo(session.Fingerprint) == true) return 0;

            byte[]? encrypted = session.Encrypt(contentType, payload);
            if (encrypted == null) return 0;

            if (encrypted.Length > BleProtocol.MaxPayloadBytes)
            {
                Log.Write("Sync", $"{encrypted.Length} bytes exceeds the Bluetooth ceiling; Wi-Fi is needed.");
                return 0;
            }

            try
            {
                await ble.SendPayloadAsync(encrypted).ConfigureAwait(false);
                return 1;
            }
            catch (Exception ex)
            {
                Log.Write("Sync", "Send over Bluetooth failed", ex);
                return 0;
            }
        }

        /// <summary>
        /// Raises Wi-Fi and waits for it rather than refusing outright the way the fallback
        /// arrangement had to. The hold keeps the link up across the transfer even if the
        /// screen goes off midway through it.
        /// </summary>
        private static async Task<int> SendByRaisingWiFiAsync(byte contentType, byte[] payload)
        {
            if (!CouldRaiseWiFi()) return 0;

            Interlocked.Increment(ref _wifiHolds);
            try
            {
                if (!await WaitForWiFiAsync(WiFiOnDemandTimeout).ConfigureAwait(false))
                {
                    Log.Write("Sync", "Could not raise Wi-Fi; the item needs it.");
                    return 0;
                }

                return await Mesh.BroadcastAsync(contentType, payload).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _wifiHolds);
                // Re-evaluate: with the hold gone the link may no longer be wanted.
                SignalWiFi();
            }
        }

        /// <summary>
        /// Whether raising Wi-Fi is even worth attempting. Used to decide that an item is not
        /// worth encrypting rather than to decide how to send it.
        /// </summary>
        private static bool CouldRaiseWiFi() => IsPairedAndRunning() && HasUsableNetwork();

        private static bool IsPairedAndRunning()
        {
            lock (_loopGate)
            {
                if (_wifiLoopTask == null) return false;
            }

            return IsPaired;
        }

        /// <summary>
        /// Asks the Wi-Fi loop for a link and waits for it. The caller must already hold a
        /// Wi-Fi lease, or the loop may decide the link is unwanted and drop it mid-wait.
        /// </summary>
        private static async Task<bool> WaitForWiFiAsync(TimeSpan timeout)
        {
            if (WiFiConnected) return true;

            if (!HasUsableNetwork())
            {
                Log.Write("Sync", "No Wi-Fi or Ethernet transport, so there is nothing to raise.");
                return false;
            }

            SignalWiFi();

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (WiFiConnected) return true;

                var token = CurrentToken();
                if (token.IsCancellationRequested) return false;

                try { await Task.Delay(150, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return false; }
            }

            return false;
        }

        // ---------------------------------------------------------------- receive path

        /// <summary>
        /// Opens a payload that arrived over Bluetooth.
        ///
        /// <paramref name="session"/> is the link's own agreed key, so there is nothing to
        /// search and nothing to infer. This used to try every paired device's key in turn and
        /// fall back to "there is only one device it could be", because the key belonged to the
        /// peer rather than to the connection. It belongs to the connection now.
        /// </summary>
        private static void HandlePayload(byte[] encrypted, PeerSession? session)
        {
            if (session == null)
            {
                Log.Write("Sync", "Dropped a Bluetooth payload that arrived before a key was agreed.");
                return;
            }

            if (!session.TryDecrypt(encrypted, out var decrypted))
            {
                Log.Write("Sync",
                    $"Dropped a payload from {DeviceIdentity.Shorten(session.Fingerprint)}: it does not authenticate under this link's key.");
                return;
            }

            Apply(decrypted.Peer, decrypted.ContentType, decrypted.Body);
        }

        private static void Apply(PeerRecord peer, byte contentType, byte[] body)
        {
            // File frames go straight through: they are not clipboard content, so noting them
            // as an inbound copy would poison the echo suppressor with bytes nobody copied.
            if (Files.Handle(peer.Fingerprint, contentType, body)) return;

            // Recorded before injection so the clipboard listener recognises the resulting
            // change as our own write.
            _echo.NoteInbound(body, contentType == ContentImage ? SyncItemKind.Image : SyncItemKind.Text);

            try
            {
                if (contentType == ContentText) ApplyText(body);
                else if (contentType == ContentImage) ApplyImage(body);
                else if (contentType == SyncContent.Address) NoteAnnouncedAddress(peer, body);
                else if (contentType == SyncContent.Ring) ApplyRing(peer, body);
                else Log.Write("Sync", $"Ignoring unknown content type {contentType}.");
            }
            catch (Exception ex)
            {
                Log.Write("Sync", "Applying received payload failed", ex);
            }
        }

        /// <summary>
        /// Records where the computer says it is reachable, and retries Wi-Fi if that is news.
        ///
        /// This is what replaces the address baked into the QR code. A DHCP lease change used
        /// to break pairing outright - the phone kept dialling an address nothing was listening
        /// on, and the only cure was to rescan. Now Bluetooth, which needs no address at all
        /// because it finds the computer by service UUID, carries the new one across.
        ///
        /// Parsed rather than trusted. It arrives inside an authenticated payload so it can
        /// only have come from a paired device, but the address decides where the next
        /// connection goes, so anything that is not literally an IP address is discarded.
        /// </summary>
        private static void NoteAnnouncedAddress(PeerRecord peer, byte[] body)
        {
            string address = System.Text.Encoding.UTF8.GetString(body).Trim();

            if (!System.Net.IPAddress.TryParse(address, out _))
            {
                Log.Write("Sync", "Ignoring an implausible address announced by a peer.");
                return;
            }

            if (string.Equals(peer.LastAddress, address, StringComparison.OrdinalIgnoreCase)) return;

            Log.Write("Sync", $"{peer.Name ?? "A peer"} moved to {address}; Wi-Fi will use it from now on.");
            Security.Peers.NoteSeen(peer.Fingerprint, address);

            // The Wi-Fi loop may be sitting in a backoff against the old address, and now has
            // a reason to try again immediately.
            SignalWiFi();
        }

        /// <summary>
        /// Sounds an alarm, or stops one.
        ///
        /// Authenticated by having arrived at all: it opened under this connection's key, so it
        /// came from a device this phone is paired with. That is the whole reason ringing is a
        /// content type rather than a two-byte control frame - the latter rides outside the
        /// encrypted path, and anything that knew the service UUID could have made the phone
        /// shriek from across the street.
        /// </summary>
        private static void ApplyRing(PeerRecord peer, byte[] body)
        {
#if ANDROID
            string from = peer.Name ?? DeviceIdentity.Shorten(peer.Fingerprint);

            if (body.Length > 0 && body[0] != 0) Platforms.Android.Ringer.Start(from);
            else Platforms.Android.Ringer.Stop();
#else
            _ = peer;
            _ = body;
#endif
        }

        /// <summary>
        /// Asks a device to make a noise so it can be found.
        ///
        /// Goes over whichever tier is up. One byte fits comfortably in a Bluetooth frame, which
        /// is the point: the moment you most want to find a device is the moment it is not on
        /// any network.
        /// </summary>
        public static async Task<bool> RingAsync(string fingerprint, bool on)
        {
            byte[] body = { on ? (byte)1 : (byte)0 };

            if (_mesh?.IsConnectedTo(fingerprint) == true &&
                await Mesh.SendToAsync(fingerprint, SyncContent.Ring, body).ConfigureAwait(false))
            {
                return true;
            }

            var ble = LiveBleLink;
            var session = SessionOf(ble);
            if (ble == null || session == null ||
                !string.Equals(session.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            byte[]? sealed_ = session.Encrypt(SyncContent.Ring, body);
            if (sealed_ == null) return false;

            try
            {
                await ble.SendPayloadAsync(sealed_).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Sync", "Could not ask the device to ring", ex);
                return false;
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
                context, "dev.meshsync.app.fileprovider", imageFile);

            var clipboard = (ClipboardManager?)context.GetSystemService(Context.ClipboardService);
            if (clipboard != null)
            {
                clipboard.PrimaryClip = ClipData.NewUri(context.ContentResolver, "Mesh Sync Image", uri);
            }
#endif
            Activity.Record(SyncDirection.Received, SyncItemKind.Image, body.Length);
            Log.Write("Sync", $"Received image payload, {body.Length} bytes.");
        }

        /// <summary>
        /// Moves a finished file somewhere the user can actually find it.
        ///
        /// <para>CoreLib deliberately does not know that Downloads exists, because only the app
        /// does. On Android 10 and above that means MediaStore, which puts the file in the
        /// shared Downloads collection with no storage permission at all - the app never sees a
        /// path, only a stream it may write to. Below that there is no MediaStore Downloads
        /// collection, so it goes to the app's own external files directory: less discoverable,
        /// but reachable without asking for broad storage access on an old device.</para>
        /// </summary>
        private static void SaveReceivedFile(ReceivedFile file)
        {
#if ANDROID
            try
            {
                var context = global::Android.App.Application.Context;

                if (OperatingSystem.IsAndroidVersionAtLeast(29))
                {
                    var values = new ContentValues();
                    values.Put(global::Android.Provider.MediaStore.IMediaColumns.DisplayName, file.Name);
                    values.Put(global::Android.Provider.MediaStore.IMediaColumns.MimeType, "application/octet-stream");
                    // Pending while it is being written, so nothing picks up a half-copied file -
                    // the same trap the screenshot observer had to learn about from the other side.
                    values.Put(global::Android.Provider.MediaStore.IMediaColumns.IsPending, 1);

                    var collection = global::Android.Provider.MediaStore.Downloads.ExternalContentUri!;
                    var uri = context.ContentResolver!.Insert(collection, values)
                        ?? throw new InvalidOperationException("MediaStore refused a row for the file.");

                    using (var output = context.ContentResolver.OpenOutputStream(uri, "w")
                           ?? throw new InvalidOperationException("MediaStore gave no stream to write to."))
                    using (var input = System.IO.File.OpenRead(file.Path))
                    {
                        input.CopyTo(output);
                    }

                    values.Clear();
                    values.Put(global::Android.Provider.MediaStore.IMediaColumns.IsPending, 0);
                    context.ContentResolver.Update(uri, values, null, null);
                }
                else
                {
                    var folder = context.GetExternalFilesDir(global::Android.OS.Environment.DirectoryDownloads)
                        ?? throw new InvalidOperationException("No external files directory.");

                    string destination = UniquePath(folder.AbsolutePath!, file.Name);
                    System.IO.File.Copy(file.Path, destination);
                }

                Activity.Record(SyncDirection.Received, SyncItemKind.File, file.Size, file.Name);
                Log.Write("Sync", $"Saved \"{file.Name}\" to Downloads.");
                Report($"Received {file.Name}");
            }
            catch (Exception ex)
            {
                Log.Write("Sync", $"Could not save \"{file.Name}\"", ex);
                Report($"Could not save {file.Name}");
            }
            finally
            {
                try { System.IO.File.Delete(file.Path); } catch { }
            }
#else
            Log.Write("Sync", $"Received \"{file.Name}\" at {file.Path}.");
#endif
        }

#if ANDROID
        /// <summary>A path that is not already taken, so nothing is quietly overwritten.</summary>
        private static string UniquePath(string folder, string name)
        {
            string candidate = System.IO.Path.Combine(folder, name);
            if (!System.IO.File.Exists(candidate)) return candidate;

            string stem = System.IO.Path.GetFileNameWithoutExtension(name);
            string extension = System.IO.Path.GetExtension(name);

            for (int attempt = 2; attempt < 1000; attempt++)
            {
                candidate = System.IO.Path.Combine(folder, $"{stem} ({attempt}){extension}");
                if (!System.IO.File.Exists(candidate)) return candidate;
            }

            return System.IO.Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){extension}");
        }

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

        // ---------------------------------------------------------------- loops

        private static void StartLoops()
        {
            // A plain lock, not a connect gate: this is called from the UI thread and must
            // never block on anything that touches the network.
            lock (_loopGate)
            {
                bool bleAlive = _bleLoopTask != null && !_bleLoopTask.IsCompleted;
                bool wifiAlive = _wifiLoopTask != null && !_wifiLoopTask.IsCompleted;
                if (bleAlive && wifiAlive) return;

                if (!bleAlive && !wifiAlive)
                {
                    _loopCts?.Dispose();
                    _loopCts = new CancellationTokenSource();
                }

                var token = _loopCts!.Token;

                if (!bleAlive) _bleLoopTask = Task.Run(() => BleLoopAsync(token));
                if (!wifiAlive) _wifiLoopTask = Task.Run(() => WiFiLoopAsync(token));
            }
        }

        private static CancellationToken CurrentToken()
        {
            lock (_loopGate) return _loopCts?.Token ?? new CancellationToken(canceled: true);
        }

        private static void SignalBle()
        {
            try { _bleSignal.Release(); } catch (SemaphoreFullException) { }
        }

        private static void SignalWiFi()
        {
            try { _wifiSignal.Release(); } catch (SemaphoreFullException) { }
        }

        /// <summary>Discards queued wake-ups so a burst of them cannot spin a loop.</summary>
        private static void Drain(SemaphoreSlim signal)
        {
            while (signal.CurrentCount > 0)
            {
                if (!signal.Wait(0)) break;
            }
        }

        // ── Bluetooth: the standing link ────────────────────────────────────────────────

        /// <summary>
        /// Holds a Bluetooth link to the computer whenever one can be had.
        ///
        /// This is the inversion at the heart of standby. Bluetooth used to be attempted only
        /// once Wi-Fi had failed; now it is held continuously and Wi-Fi is the one that comes
        /// and goes. A connection interval of a second or two costs microamps between events,
        /// which is what makes holding it viable at all.
        /// </summary>
        private static async Task BleLoopAsync(CancellationToken token)
        {
            int failures = 0;

            // Advertising costs nothing while nobody connects, and it is the only way a peer
            // that cannot advertise itself will ever find this phone. Started once, and only
            // when the radio can actually do it.
            StartPeripheralIfCapable();

            while (!token.IsCancellationRequested)
            {
                if (!IsPairedStill(token)) return;

                if (BleConnected)
                {
                    failures = 0;
                    Drain(_bleSignal);

                    // Park until the link drops. No polling, so a healthy standing link
                    // costs nothing on this side at all.
                    try { await _bleSignal.WaitAsync(token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                bool connected = await TryConnectOverBleAsync(token).ConfigureAwait(false);

                if (connected)
                {
                    failures = 0;
                    ReportLinkState();

                    // Wi-Fi may no longer be needed now that Bluetooth carries presence.
                    SignalWiFi();
                    continue;
                }

                failures++;
                ReportLinkState();

                // Wi-Fi has to cover for the missing standing link, so tell it promptly.
                SignalWiFi();

                var ceiling = _screenOn ? BleRetryCeilingActive : BleRetryCeilingIdle;
                var delay = BackoffFor(failures);
                if (delay > ceiling) delay = ceiling;

                try { await _bleSignal.WaitAsync(delay, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

            Log.Write("Sync", "Bluetooth loop stopped.");
        }

        // ── Wi-Fi: raised on demand ─────────────────────────────────────────────────────

        /// <summary>
        /// Wi-Fi is wanted when the screen is on, when a send is holding it, when the computer
        /// has asked for it, or when Bluetooth is not carrying presence.
        /// </summary>
        private static bool WiFiWanted()
        {
            if (_screenOn) return true;
            if (Volatile.Read(ref _wifiHolds) > 0) return true;
            if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _wifiWakeUntilTicks)) return true;

            // The link of last resort. Without this, losing Bluetooth would leave the phone
            // with nothing, which would make standby a regression rather than an improvement.
            return !BleConnected;
        }

        private static async Task WiFiLoopAsync(CancellationToken token)
        {
            int failures = 0;

            while (!token.IsCancellationRequested)
            {
                if (!IsPairedStill(token)) return;

                bool wanted = WiFiWanted();
                bool up = WiFiConnected;

                if (!wanted && up)
                {
                    Log.Write("Sync", "Wi-Fi is no longer wanted - dropping it. Bluetooth holds presence.");
                    Mesh.DisconnectAll();
                    ReportLinkState();
                    up = false;
                }

                if (wanted && !up)
                {
                    if (!HasUsableNetwork())
                    {
                        // Asking the OS costs nothing; finding out by timing out costs five
                        // seconds, and on cellular it would never have worked anyway.
                        //
                        // Said once per spell rather than once per attempt: with no network at
                        // all the loop keeps retrying on a backoff, and repeating it buries
                        // everything else in the log during exactly the case Bluetooth covers.
                        if (!_reportedNoNetwork)
                        {
                            _reportedNoNetwork = true;
                            Log.Write("Sync", "Wi-Fi wanted but no Wi-Fi or Ethernet transport exists.");
                        }

                        failures++;
                    }
                    else
                    {
                        _reportedNoNetwork = false;
                        // Listening as well as dialling: with symmetric roles a peer may reach
                        // us first, and a phone that only ever dialled could never be found by
                        // another phone.
                        try { await Mesh.StartListeningAsync(token).ConfigureAwait(false); }
                        catch (Exception ex) { Log.Write("Sync", "Could not listen for peers", ex); }

                        int connected = await Mesh.ConnectToAllAsync(TcpConnectTimeout, token).ConfigureAwait(false);

                        if (connected > 0 || WiFiConnected)
                        {
                            failures = 0;
                            _networkHintPending = false;
                            up = true;
                        }
                        else
                        {
                            failures++;
                        }

                        ReportLinkState();
                    }

                    if (_networkHintPending)
                    {
                        // Connectivity just changed; do not punish the next attempt.
                        _networkHintPending = false;
                        failures = 0;
                    }
                }

                if (wanted && !up)
                {
                    var delay = BackoffFor(failures);
                    try { await _wifiSignal.WaitAsync(delay, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                Drain(_wifiSignal);

                // Park until something changes. The only state that lapses on its own is the
                // peer's wake window, so that is the one case that needs a bounded wait -
                // everything else raises a signal, and an idle phone does no work at all.
                TimeSpan wait = Timeout.InfiniteTimeSpan;
                if (up && !_screenOn && Volatile.Read(ref _wifiHolds) == 0)
                {
                    long until = Interlocked.Read(ref _wifiWakeUntilTicks);
                    var remaining = new DateTime(until, DateTimeKind.Utc) - DateTime.UtcNow;
                    if (remaining > TimeSpan.Zero) wait = remaining + TimeSpan.FromMilliseconds(200);
                }

                try
                {
                    if (wait == Timeout.InfiniteTimeSpan) await _wifiSignal.WaitAsync(token).ConfigureAwait(false);
                    else await _wifiSignal.WaitAsync(wait, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
            }

            Log.Write("Sync", "Wi-Fi loop stopped.");
        }

        private static bool IsPairedStill(CancellationToken token)
        {
            if (token.IsCancellationRequested) return false;

            if (IsPaired) return true;

            // Logged, not just reported: a silent exit here looks identical to a hung loop
            // when reading the device log.
            Log.Write("Sync", "Loop stopping: no paired host saved.");
            Report("Not paired yet.");
            return false;
        }

        /// <summary>
        /// Exponential backoff capped at a minute, with jitter so a phone and a laptop
        /// waking together do not retry in lockstep. The old fixed 3 second retry ran all
        /// night whenever the laptop was off, which is exactly the battery drain the
        /// project guidelines call out.
        /// </summary>
        private static TimeSpan BackoffFor(int failures)
        {
            if (failures <= 0) return MinBackoff;

            double seconds = MinBackoff.TotalSeconds * Math.Pow(2, Math.Min(failures - 1, 6));
            seconds = Math.Min(seconds, MaxBackoff.TotalSeconds);

            double jitter;
            lock (_jitter) jitter = 0.8 + _jitter.NextDouble() * 0.4;

            return TimeSpan.FromSeconds(seconds * jitter);
        }

        // ---------------------------------------------------------------- connecting

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
        /// loop retries far more often than that, which is why Bluetooth appeared to connect
        /// only by luck, or only after the service was restarted. One long scan, spaced out,
        /// stays under the limit. Holding the link rather than rebuilding it per use keeps the
        /// scan rate down further.
        /// </summary>
        private static readonly TimeSpan BleScanCooldown = TimeSpan.FromSeconds(12);

        private static readonly TimeSpan BleScanWindow = TimeSpan.FromSeconds(25);

        /// <summary>
        /// Tries advertising again, after a permission grant that arrived too late.
        ///
        /// Advertising is attempted once per run. Android 12 made BLUETOOTH_ADVERTISE a runtime
        /// grant, so on the launch where the user first allows it the attempt has already been
        /// made and refused - and without this the phone would stay unfindable until it was
        /// next started.
        /// </summary>
        public static void RetryBluetoothPeripheral()
        {
            if (_blePeripheralLink != null) return;

            Interlocked.Exchange(ref _peripheralStarted, 0);
            StartPeripheralIfCapable();
        }

        /// <summary>
        /// Publishes the Mesh Sync service so a peer can connect to this phone.
        ///
        /// Deliberately additive and deliberately best-effort: a failure here leaves the phone
        /// exactly as it was, scanning and connecting out, which is how Bluetooth worked before
        /// either role could be taken. Only the peer that needed us to advertise loses out.
        /// </summary>
        private static void StartPeripheralIfCapable()
        {
#if ANDROID
            if (Interlocked.Exchange(ref _peripheralStarted, 1) != 0) return;

            var capability = Platforms.Android.BleCapabilities.Detect();
            if (!capability.HasFlag(BleCapability.Peripheral))
            {
                Log.Write("Sync", "This phone cannot advertise, so it can only ever be the Bluetooth central.");
                return;
            }

            try
            {
                var peripheral = new Platforms.Android.AndroidBlePeripheral
                {
                    LocalPublicKey = Security.Identity.PublicKey,
                    LocalDeviceName = LocalDeviceName,
                    LocalMeshName = Security.Peers.MeshName,
                    OpenSession = OpenBleSession
                };

                peripheral.PayloadReceived += Transport_PayloadReceived;
                peripheral.ConnectionClosed += Ble_ConnectionClosed;
                peripheral.WiFiRequested += Ble_WiFiRequested;
                peripheral.PeerIdentified += Ble_PeerIdentified;
                peripheral.ClientConnected += (_, _) =>
                {
                    Log.Write("Sync", "A peer connected to this phone over Bluetooth.");
                    ReportLinkState();
                    SignalWiFi();
                };

                _blePeripheralLink = peripheral;
                _ = peripheral.StartListeningAsync();
            }
            catch (Exception ex)
            {
                Log.Write("Sync", "Could not start advertising over Bluetooth", ex);
                _blePeripheralLink = null;
            }
#endif
        }

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
                _lastBleScanUtc = DateTime.UtcNow;
                discovery = new Platforms.Android.AndroidBleDiscovery();
                string? address = await discovery.FindPeerAsync(BleScanWindow, token).ConfigureAwait(false);

                if (string.IsNullOrEmpty(address))
                {
                    Log.Write("Sync", "No Mesh Sync peer found over Bluetooth.");
                    return false;
                }

                var ble = new Platforms.Android.AndroidBleTransport
                {
                    // Announced over the link, so the peer knows whose key to seal for.
                    // Without it this tier only worked when exactly one device was paired.
                    LocalPublicKey = Security.Identity.PublicKey,
                    LocalDeviceName = LocalDeviceName,
                    LocalMeshName = Security.Peers.MeshName,
                    OpenSession = OpenBleSession
                };

                ble.PayloadReceived += Transport_PayloadReceived;
                ble.ConnectionClosed += Ble_ConnectionClosed;
                ble.WiFiRequested += Ble_WiFiRequested;
                ble.PeerIdentified += Ble_PeerIdentified;
                _bleLink = ble;

                await ble.ConnectAsync(address!, token).ConfigureAwait(false);

                Log.Write("Sync", $"Bluetooth link up to {address}.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Sync", "Bluetooth connect failed", ex);
                RetireBle();
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

        // ---------------------------------------------------------------- link events

        private static void Transport_PayloadReceived(object? sender, PayloadReceivedEventArgs e)
        {
            // Runs on the transport receive loop. Kept synchronous and total so a failure
            // here can never take the connection down. The sender is the link, so it is also
            // the answer to which key this payload should open under.
            try { HandlePayload(e.EncryptedPayload, SessionOf(sender)); }
            catch (Exception ex) { Log.Write("Sync", "Payload handling failed", ex); }
        }

        private static void Ble_ConnectionClosed(object? sender, EventArgs e)
        {
            Log.Write("Sync", "Bluetooth link closed.");
            ReportLinkState();
            SignalBle();
            // Wi-Fi is now the only thing that can carry presence, so let it know at once.
            SignalWiFi();
        }

        private static void Mesh_PeerDisconnected(string fingerprint)
        {
            Log.Write("Sync", $"Wi-Fi link to {DeviceIdentity.Shorten(fingerprint)} closed.");
            ReportLinkState();
            SignalWiFi();
        }

        private static void Mesh_PeerConnected(PeerRecord peer)
        {
            // Remembered so the name survives the socket being dropped, which under standby is
            // most of the time - otherwise the dashboard falls back to "your computer" the
            // moment Wi-Fi goes away, which reads as a fault rather than as the design working.
            if (!string.IsNullOrWhiteSpace(peer.Name)) _lastPeerName = peer.Name;

            ReportLinkState();
        }

        private static void Mesh_PayloadReceived(object? sender, MeshPayloadEventArgs e)
        {
            try { Apply(e.Peer, e.ContentType, e.Body); }
            catch (Exception ex) { Log.Write("Sync", "Payload handling failed", ex); }
        }

        /// <summary>
        /// The computer said which device it is over Bluetooth.
        ///
        /// Refused if it is not one this phone has paired with: the tier used to accept
        /// anything advertising the service UUID, which was tolerable only because everything
        /// shared one key and nothing could be distinguished anyway.
        /// </summary>
        private static void Ble_PeerIdentified(object? sender, PeerIdentifiedEventArgs e)
        {
            if (!Security.Authorise(e.PublicKey))
            {
                Log.Write("Sync", "The Bluetooth peer is not a paired device - dropping the link.");
                if (ReferenceEquals(sender, _bleLink)) RetireBle();
                SignalBle();
                return;
            }

            // Recorded so there is something to call this device. Bluetooth carries the name
            // in its hello now; without it a pair that has never met over Wi-Fi had no name at
            // all and every notification read "your devices".
            if (!string.IsNullOrWhiteSpace(e.DeviceName))
            {
                _lastPeerName = e.DeviceName;
                Security.Peers.NoteSeen(e.Fingerprint, null, e.DeviceName);
            }

            // Adopted only when this phone has no name of its own, which is what stops two
            // devices that disagree overwriting each other on every reconnect.
            Security.Peers.AdoptMeshName(e.MeshName);

            // Two links to the same peer, which happens when both devices can advertise and
            // both went looking. Settled the same way a TCP collision is: one deterministic
            // rule, computed identically on both sides, so they converge without negotiating.
            ResolveBleCollision(e.Fingerprint);

            ReportLinkState();
        }

        /// <summary>
        /// Drops whichever Bluetooth link the role rule says should not exist.
        ///
        /// Only ever runs when both a link this device opened and one a peer opened are live at
        /// the same time. Both ends compute the same answer from fingerprints they have already
        /// exchanged, so neither has to be in charge and no round trip is needed.
        /// </summary>
        private static void ResolveBleCollision(string peerFingerprint)
        {
#if ANDROID
            if (_bleLink?.IsConnected != true || _blePeripheralLink?.IsConnected != true) return;
            if (string.IsNullOrEmpty(peerFingerprint)) return;

            var role = BleRoleRules.DecideFor(
                Security.Identity.Fingerprint,
                Platforms.Android.BleCapabilities.Detect(),
                peerFingerprint,
                BleCapability.Both);

            if (role == BleRole.Peripheral)
            {
                Log.Write("Sync", "Two Bluetooth links to one peer; keeping the one it opened.");
                RetireBle();
            }
            else
            {
                Log.Write("Sync", "Two Bluetooth links to one peer; keeping the one this phone opened.");
                try { _blePeripheralLink?.DisconnectAsync(); }
                catch (Exception ex) { Log.Write("Sync", "Dropping the inbound Bluetooth link failed", ex); }
            }
#endif
        }

        /// <summary>
        /// A peer has something Bluetooth cannot carry. It may not be able to dial us, so the
        /// only way an image copied over there reaches this phone is for us to raise Wi-Fi in
        /// response to this.
        /// </summary>
        private static void Ble_WiFiRequested(object? sender, EventArgs e)
        {
            var until = DateTime.UtcNow.Add(WiFiWakeWindow).Ticks;
            Interlocked.Exchange(ref _wifiWakeUntilTicks, until);

            Log.Write("Sync", $"The computer asked for Wi-Fi; holding it up for {WiFiWakeWindow.TotalSeconds:F0}s.");
            SignalWiFi();
        }

        /// <summary>Detaches and disposes the Bluetooth link. Skipping this was the leak.</summary>
        private static void RetireBle()
        {
            var transport = Interlocked.Exchange(ref _bleLink, null);
            if (transport == null) return;

            transport.PayloadReceived -= Transport_PayloadReceived;
            transport.ConnectionClosed -= Ble_ConnectionClosed;

#if ANDROID
            if (transport is Platforms.Android.AndroidBleTransport ble)
            {
                ble.WiFiRequested -= Ble_WiFiRequested;
                ble.PeerIdentified -= Ble_PeerIdentified;
            }
#endif

            try { transport.Dispose(); } catch (Exception ex) { Log.Write("Sync", "Disposing the Bluetooth link failed", ex); }
        }

        /// <summary>Stops advertising and tears down the inbound Bluetooth link.</summary>
        private static void RetirePeripheral()
        {
            var transport = Interlocked.Exchange(ref _blePeripheralLink, null);
            Interlocked.Exchange(ref _peripheralStarted, 0);

            if (transport == null) return;

            transport.PayloadReceived -= Transport_PayloadReceived;
            transport.ConnectionClosed -= Ble_ConnectionClosed;

#if ANDROID
            if (transport is Platforms.Android.AndroidBlePeripheral peripheral)
            {
                peripheral.WiFiRequested -= Ble_WiFiRequested;
                peripheral.PeerIdentified -= Ble_PeerIdentified;
            }
#endif

            // Closed rather than merely stopped: a GATT service left registered by a process
            // that has gone is the desktop side's worst Bluetooth failure, and there is no
            // reason to reproduce it here.
            try { transport.Dispose(); }
            catch (Exception ex) { Log.Write("Sync", "Disposing the Bluetooth peripheral failed", ex); }
        }

        /// <summary>Drops every Wi-Fi link, leaving Bluetooth to hold presence.</summary>
        private static void RetireWiFi()
        {
            try { _mesh?.DisconnectAll(); }
            catch (Exception ex) { Log.Write("Sync", "Dropping the Wi-Fi links failed", ex); }
        }

        // ---------------------------------------------------------------- status

        /// <summary>
        /// Reports the combined state of both links, so the notification and the dashboard say
        /// something true when only one of them is up - which under standby is the normal case.
        /// </summary>
        private static void ReportLinkState()
        {
            bool ble = BleConnected;
            bool wifi = WiFiConnected;

            if (wifi && ble) Report("Connected");
            else if (wifi) Report("Connected over Wi-Fi");
            else if (ble) Report("Connected over Bluetooth");
            else Report("No devices in range");
        }

        private static void Report(string status)
        {
            try { OnConnectionStatusChanged?.Invoke(status); }
            catch (Exception ex) { Log.Write("Sync", "Status handler threw", ex); }
        }
    }
}
