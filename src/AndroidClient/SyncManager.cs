﻿using System;
using System.Threading;
using System.Threading.Tasks;
using CoreLib;
using CoreLib.Diagnostics;
using CoreLib.Identity;
using CoreLib.Transport;
using CoreLib.Transport.Ble;
using CoreLib.Transport.Fabric;

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
    /// deep link, the activity, the background service and the disconnect handler each start
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

        /// <summary>
        /// Upper bound with the screen off. Scanning is the expensive part of the Bluetooth
        /// tier, and retrying every few seconds all night - which is what a single brisk
        /// ceiling would do with the computer switched off - is exactly the drain that holding
        /// a cheap link was supposed to avoid. Nothing is being copied while the screen is off,
        /// so a slower rescan costs nothing that matters, and screen-on signals both loops
        /// immediately anyway.
        /// </summary>


        private static readonly object _loopGate = new();
        private static readonly object _securityGate = new();

        /// <summary>One wake-up signal per loop, so a Bluetooth event cannot spin the Wi-Fi loop.</summary>
        
        
        private static readonly EchoSuppressor _echo = new(TimeSpan.FromSeconds(10));
        
        // ── links ───────────────────────────────────────────────────────────────────────
        // Two fields, not one. Each is owned exclusively by its own loop; everything else
        // signals rather than connecting, so there is no gate to hold across a round trip.

        /// <summary>Scans, connects and rotates on one adapter. Null until the radio starts.</summary>
        private static Platforms.Android.AndroidBleRadio? _radio;

        private static BleRadioScheduler? _scheduler;

        private static MeshDiscovery? _discovery;

        private static LinkSupervisor? _supervisor;

        /// <summary>
        /// The Bluetooth link a peer opened to this device, as the central to our peripheral.
        ///
        /// Held alongside rather than instead of the one above, because which of the two roles
        /// a phone takes depends on the peer: a device that cannot advertise must always be the
        /// central, so its peer has to be the one advertising. Two phones would otherwise both
        /// sit scanning for something neither was broadcasting.
        /// </summary>
        private static Platforms.Android.AndroidBlePeripheralRoute? _inbound;

        private static int _peripheralStarted;

        /// <summary>What this phone's radio can do, taken from what actually started.</summary>
        private static BleCapability _bleCapability = BleCapability.Central;

        /// <summary>
        /// The Wi-Fi tier: one link per paired device, and this phone both listens and dials.
        ///
        /// It was a single <see cref="TcpTransportConnection"/> dialling one hardcoded host,
        /// which made phone-to-phone impossible and meant a second device could only be reached
        /// by dropping the first.
        /// </summary>
        private static MeshFabric? _fabric;
        private static WiFiRouteProvider? _wifi;

        private static CancellationTokenSource? _loopCts;
        private static Task? _supervisorTask;
        private static Task? _schedulerTask;

        /// <summary>Peers with a send in flight that needs a socket, and peers that asked for one.</summary>
        private static readonly HashSet<string> _wifiHolds = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> _wifiWake = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _demandGate = new();

        // ── Wi-Fi demand ────────────────────────────────────────────────────────────────

        private static volatile bool _screenOn = true;

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

        /// <summary>
        /// True when anything at all is reachable over the radio.
        ///
        /// <para><b>This used to be the whole of the standby logic and it was the bug.</b> It read
        /// <c>_bleLink?.IsConnected</c>, and a central link stayed <c>IsConnected</c> after a
        /// failed key agreement - so a device from somebody else's mesh answering pings made this
        /// true, parked the Bluetooth loop on a semaphore with no timeout, and made
        /// <c>WiFiWanted()</c> conclude Wi-Fi was unnecessary. A phone with no working link to
        /// anything, indefinitely, saying "Connected over Bluetooth".</para>
        ///
        /// <para>It reads the fabric now, where a route reaches <c>Established</c> only through a
        /// session and <c>Handshaking</c> has a deadline.</para>
        /// </summary>
        public static bool BleConnected =>
            _fabric?.Links.Any(l => l.LiveRoutes.Any(r => r.Kind != RouteKind.WiFi)) == true;

        /// <summary>True when this peer is reachable over the radio specifically.</summary>
        public static bool BleConnectedTo(string fingerprint) =>
            _fabric?.LinkTo(fingerprint)?.LiveRoutes.Any(r => r.Kind != RouteKind.WiFi) == true;

        /// <summary>True when this peer has a socket, as opposed to any link at all.</summary>
        public static bool WiFiConnectedTo(string fingerprint) =>
            _fabric?.LinkTo(fingerprint)?.RouteOf(RouteKind.WiFi)?.State == RouteState.Established;

        /// <summary>
        /// Authorises a Bluetooth peer and agrees this link's key in one step. Both halves of
        /// the tier use it, so a device this phone has not paired with never reaches the point
        /// of having a session to encrypt with.
        /// </summary>
        private static PeerSession? OpenBleSession(string peerPublicKey, string peerName,
                                                   string peerEphemeral, EphemeralKeyPair localEphemeral) =>
            Security.Authorise(peerPublicKey, peerName)
                ? Security.OpenSession(peerPublicKey, localEphemeral, peerEphemeral)
                : null;

        public static bool WiFiConnected =>
            _fabric?.Links.Any(l => l.RouteOf(RouteKind.WiFi)?.State == RouteState.Established) == true;

        /// <summary>Everything about reachability, for a diagnostics view or a log line.</summary>
        public static MeshHealth? Health => _fabric == null || _supervisor == null ? null : MeshHealth.Of(
            _fabric, CoreLib.Transport.Fabric.SystemClock.Instance,
            _supervisor.LastPassUtc, _supervisor.Passes, _supervisor.Restarts,
            _radio?.Status ?? "no adapter", _scheduler?.LiveCentralLinks ?? 0,
            _scheduler == null ? 0 : _fabric.Timings.MaxBleCentralLinks,
            _scheduler?.IsAdvertising ?? false, _scheduler?.LastRound ?? default,
            RoutePolicy.Plan(Security.Peers.Peers, CurrentConditions(), DateTime.UtcNow).Routes);

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
                var fabric = _fabric;
                if (fabric != null)
                {
                    foreach (var fingerprint in fabric.ConnectedPeers)
                    {
                        string? name = fabric.LinkTo(fingerprint)?.Peer.Name;
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

        private static BrowseService? _browse;

        /// <summary>
        /// Browsing the mesh's shared folders, and answering when it browses this phone.
        ///
        /// <para>Created on first use like <see cref="Files"/>, and for the same reason: the
        /// transport it talks through does not exist until sync has started.</para>
        ///
        /// <para>Downloads is shared to begin with, because it is where everything this app
        /// receives already goes. A browse feature that shows nothing until somebody finds a
        /// settings screen is the same mistake notification mirroring made.</para>
        /// </summary>
        public static BrowseService Browse
        {
            get
            {
                var existing = Volatile.Read(ref _browse);
                if (existing != null) return existing;

                lock (_securityGate)
                {
                    existing = Volatile.Read(ref _browse);
                    if (existing != null) return existing;

                    var created = new BrowseService
                    {
                        Send = (fingerprint, contentType, body) =>
                            Fabric.SendToAsync(fingerprint, contentType, body),
                        SendFile = async (fingerprint, path) =>
                            await Files.SendAsync(fingerprint, path).ConfigureAwait(false)
                    };

#if ANDROID
                    string downloads = global::Android.OS.Environment
                        .GetExternalStoragePublicDirectory(global::Android.OS.Environment.DirectoryDownloads)?.AbsolutePath ?? "";

                    if (downloads.Length > 0) created.Shared.Add(downloads, "Downloads");
#endif

                    Volatile.Write(ref _browse, created);
                    return created;
                }
            }
        }

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
                            Fabric.SendToAsync(fingerprint, contentType, body, token));

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
        /// <summary>
        /// Every way this phone can reach every device it is paired with.
        ///
        /// <para>Replaces a <c>MeshLinks</c> holding the sockets and two nullable radio fields
        /// beside it. One route table, so "is this peer reachable, and over what" has one answer -
        /// which is what lets Wi-Fi demand be asked per peer instead of for the whole phone.</para>
        /// </summary>
        private static MeshFabric Fabric
        {
            get
            {
                var existing = Volatile.Read(ref _fabric);
                if (existing != null) return existing;

                lock (_securityGate)
                {
                    existing = Volatile.Read(ref _fabric);
                    if (existing != null) return existing;

                    var created = new MeshFabric(Security, () => _bleCapability);
                    created.PayloadReceived += Fabric_PayloadReceived;
                    created.PeerConnected += Fabric_PeerConnected;
                    created.PeerDisconnected += Fabric_PeerDisconnected;

                    _wifi = new WiFiRouteProvider(Security)
                    {
                        LocalDeviceName = LocalDeviceName,
                        LocalCapability = () => _bleCapability,
                    };
                    created.AddProvider(_wifi);

                    _discovery = new MeshDiscovery(Security);

                    _supervisor = new LinkSupervisor(created, CurrentConditions)
                    {
                        WantedCentralPeersChanged = peers => _scheduler?.SetWanted(peers),
                        AdvertisingWanted = wanted => _ = ApplyAdvertisingAsync(wanted),
                        ProbingWanted = probing => _scheduler?.SetProbing(probing),
                    };

                    // A device refused a moment ago for not being paired is the same device being
                    // confirmed now, and it must not sit out a cooldown after that.
                    Security.Peers.Changed += () =>
                    {
                        _scheduler?.Cooldowns.Clear();

                        // Minted here as well as at startup: a fresh install has nothing paired
                        // when it starts, and there is no point advertising a beacon for a mesh
                        // of one. The first pairing is the moment this phone becomes a mesh.
                        if (_discovery?.MintIfDue() != null) _ = ApplyAdvertisingAsync(_scheduler?.IsAdvertising ?? false);

                        _supervisor?.Signal();
                    };

                    Volatile.Write(ref _fabric, created);
                    return created;
                }
            }
        }

        /// <summary>
        /// Everything about this phone that decides which routes it wants.
        ///
        /// <para>Gathered here and handed to <c>RoutePolicy</c> whole. Every one of these used to
        /// be a condition inside <c>WiFiWanted()</c> or one of the two loops, and none of them
        /// could be asserted on without a radio in the room.</para>
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
                LocalFingerprint = Security.Identity.Fingerprint,
                ScreenOn = _screenOn,
                HasUsableNetwork = HasUsableNetwork(),
                Transport = TransportPreference.Both,
                LocalCapability = _bleCapability,
                PeerCapabilities = Security.Peers.Capabilities,
                WiFiHolds = holds,
                WiFiWakeUntilUtc = wake,
                PairingOpen = Security.Pairing.IsOpen,
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
            catch (Exception ex) { Log.Write("Sync", "Applying the advertisement failed", ex); }
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
            // Every caller funnels through here, so honouring the flag once keeps the app, the
            // foreground service, the tile and the deep link from each reviving a stopped sync.
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
            //
            // Held for every paired device, because a file is fanned out to all of them. Per peer
            // is still the shape: the holds are a set, and dropping one does not drop the rest.
            var held = Security.Peers.Peers.Select(peer => peer.Fingerprint).ToList();
            lock (_demandGate) foreach (string fingerprint in held) _wifiHolds.Add(fingerprint);
            try
            {
                if (!WiFiConnected && !await WaitForWiFiAsync(WiFiOnDemandTimeout).ConfigureAwait(false))
                {
                    Log.Write("Sync", $"Could not raise Wi-Fi, so \"{name}\" was not sent.");
                    Report("Files need Wi-Fi");
                    return false;
                }

                var targets = Fabric.ConnectedPeers;
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
                lock (_demandGate) foreach (string fingerprint in held) _wifiHolds.Remove(fingerprint);
                SignalWiFi();
            }
        }

        /// <summary>Stops the supervisor and the radio, and tears the links down. Pairing is kept.</summary>
        public static async Task DisconnectAsync()
        {
            CancellationTokenSource? cts;
            Task? ble;
            Task? wifi;

            lock (_loopGate)
            {
                cts = _loopCts;
                ble = _schedulerTask;
                wifi = _supervisorTask;
                _loopCts = null;
                _schedulerTask = null;
                _supervisorTask = null;
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
            try { _wifi?.StopListening(); }
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

            // Once per peer, not once per link, and it is structural now: a peer reachable over
            // both tiers has one PeerLink, which picks the route - Wi-Fi first because it carries
            // anything, the radio when that is what exists. Skipping Bluetooth for a peer Wi-Fi
            // already reached used to be a check that had to be remembered.
            int sent = await Fabric.BroadcastAsync(contentType, payload).ConfigureAwait(false);

            // Whatever no live route could carry - an image, at 6.7 KB/s over the radio - asks
            // its peer for Wi-Fi and follows up over that.
            sent += await SendByRaisingWiFiAsync(contentType, payload).ConfigureAwait(false);

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
        /// Raises Wi-Fi and waits for it rather than refusing outright the way the fallback
        /// arrangement had to. The hold keeps the link up across the transfer even if the
        /// screen goes off midway through it.
        /// </summary>
        private static async Task<int> SendByRaisingWiFiAsync(byte contentType, byte[] payload)
        {
            if (!CouldRaiseWiFi()) return 0;

            // Per peer, and that is the change. A peer whose only live route cannot carry this
            // asks for a socket - for itself. The hold used to be one counter for the phone, so
            // one peer needing Wi-Fi held it up for every peer, and one peer no longer needing
            // it dropped the socket to all of them.
            var needing = Fabric.NeedingWiFiFor(payload.Length).Select(l => l.Fingerprint).ToList();
            if (needing.Count == 0) return 0;

            lock (_demandGate) foreach (string fingerprint in needing) _wifiHolds.Add(fingerprint);

            try
            {
                foreach (string fingerprint in needing) await AskPeerForWiFiAsync(fingerprint).ConfigureAwait(false);

                _supervisor?.Signal();

                var deadline = DateTime.UtcNow + WiFiOnDemandTimeout;
                while (DateTime.UtcNow < deadline && needing.Any(f => !WiFiConnectedTo(f)))
                {
                    await Task.Delay(200).ConfigureAwait(false);
                }

                int sent = 0;

                foreach (string fingerprint in needing)
                {
                    if (!WiFiConnectedTo(fingerprint))
                    {
                        Log.Write("Sync",
                            $"{DeviceIdentity.Shorten(fingerprint)} did not come up on Wi-Fi; the item was dropped for it.");
                        continue;
                    }

                    if (await Fabric.SendToAsync(fingerprint, contentType, payload).ConfigureAwait(false)) sent++;
                }

                return sent;
            }
            finally
            {
                lock (_demandGate) foreach (string fingerprint in needing) _wifiHolds.Remove(fingerprint);

                // Re-evaluate: with the holds gone the sockets may no longer be wanted.
                _supervisor?.Signal();
            }
        }

        /// <summary>Asks one peer, over whichever radio half is carrying it, to raise Wi-Fi.</summary>
        private static async Task AskPeerForWiFiAsync(string fingerprint)
        {
#if ANDROID
            var link = _fabric?.LinkTo(fingerprint);
            if (link == null) return;

            foreach (var route in link.LiveRoutes)
            {
                if (route is Platforms.Android.AndroidBleTransport central)
                {
                    await central.RequestWiFiAsync().ConfigureAwait(false);
                    return;
                }
            }
#else
            await Task.CompletedTask;
            _ = fingerprint;
#endif
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
                if (_supervisorTask == null) return false;
            }

            return IsPaired;
        }

        /// <summary>
        /// Asks the Wi-Fi loop for a link and waits for it. The caller must already hold a
        /// Wi-Fi lease, or the loop may decide the link is unwanted and drop it mid-wait.
        /// </summary>
        /// <summary>
        /// Takes a mesh discovery key a peer offered, if it should win.
        ///
        /// Lowest key wins, so two halves of a mesh that minted separately converge in one
        /// exchange - and a device that adopts a new one re-advertises within an epoch.
        /// </summary>
        private static void AdoptMeshKey(PeerRecord peer, byte[] body)
        {
            if (_discovery?.Adopt(body) != true) return;

            Log.Write("Sync", $"Adopted the mesh discovery key {peer.Name ?? "a peer"} offered.");
            _ = ApplyAdvertisingAsync(_scheduler?.IsAdvertising ?? false);

            // Everyone else this phone can reach has to hear about it too, or a mesh of three
            // converges only as far as the two that happened to meet first.
            foreach (string other in Fabric.ConnectedPeers)
            {
                if (!string.Equals(other, peer.Fingerprint, StringComparison.OrdinalIgnoreCase))
                    OfferMeshKey(other);
            }
        }

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

            // A listing is not something anybody copied, so it must not reach the echo
            // suppressor either.
            if (Browse.Handle(peer.Fingerprint, contentType, body)) return;

            // Recorded before injection so the clipboard listener recognises the resulting
            // change as our own write.
            _echo.NoteInbound(body, contentType == ContentImage ? SyncItemKind.Image : SyncItemKind.Text);

            try
            {
                if (contentType == ContentText) ApplyText(body);
                else if (contentType == ContentImage) ApplyImage(body);
                else if (contentType == SyncContent.Address) NoteAnnouncedAddress(peer, body);
                else if (contentType == SyncContent.Ring) ApplyRing(peer, body);
                else if (contentType == SyncContent.NotificationDismiss) ApplyNotificationDismiss(body);
                else if (contentType == SyncContent.NotificationReply) ApplyNotificationReply(body);
                else if (contentType == SyncContent.Notification) ApplyNotification(peer, body);
                else if (contentType == SyncContent.MeshKeyOffer) AdoptMeshKey(peer, body);
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
        /// Clears a notification here because the other device cleared it there.
        ///
        /// The half that makes mirroring feel finished. Without it the desktop is a second inbox
        /// that has to be emptied separately, which is worse than not mirroring at all.
        /// </summary>
        /// <summary>
        /// A paired device answered one of this phone's notifications, so send it.
        ///
        /// <para>The reply goes out through the app that posted the notification, by pulling the
        /// reply action the notification already carried. Nothing is typed into any app and no
        /// credential is involved - see <see cref="Platforms.Android.NotificationMirrorService.ReplyTo"/>.</para>
        ///
        /// <para>Authenticated by the time it gets here: it arrived inside an encrypted payload
        /// on a session agreed with a paired device. That matters more for this than for anything
        /// else the mesh carries, because the effect of a forged one is a message sent as you.</para>
        /// </summary>
        private static void ApplyNotificationReply(byte[] body)
        {
#if ANDROID
            if (!NotificationProtocol.TryParseReply(body, out string key, out string text)) return;

            // That one arrived, never what it said.
            Log.Write("Sync", "A paired device replied to a notification; sending it.");
            Platforms.Android.NotificationMirrorService.ReplyTo(key, text);
#else
            _ = body;
#endif
        }

        private static void ApplyNotificationDismiss(byte[] body)
        {
#if ANDROID
            if (!NotificationProtocol.TryParseDismiss(body, out string key)) return;

            Log.Write("Sync", "A peer dismissed a notification; clearing it here too.");

            // Either half can be the right one and neither knows which: the key may name a
            // notification this phone posted and mirrored out, or one it is showing on another
            // device's behalf. Both are addressed by the same opaque key, and clearing the wrong
            // one is a no-op.
            Platforms.Android.NotificationMirrorService.DismissByKey(key);
            Platforms.Android.MirroredNotificationDisplay.Dismiss(key);
#else
            _ = body;
#endif
        }

        /// <summary>
        /// Shows a notification another device posted.
        ///
        /// The phone used to drop these on the floor, on the reasoning that it was the source of
        /// notifications rather than a display of them. True with one phone and one computer;
        /// false the moment a second phone joins, and out of step with every other content type,
        /// which all cross both ways.
        /// </summary>
        private static void ApplyNotification(PeerRecord peer, byte[] body)
        {
#if ANDROID
            if (!NotificationProtocol.TryParse(body, out var notification) || notification == null) return;

            string from = peer.Name ?? DeviceIdentity.Shorten(peer.Fingerprint);
            Platforms.Android.MirroredNotificationDisplay.Show(notification, from);
#else
            _ = peer;
            _ = body;
#endif
        }

        /// <summary>
        /// Mirrors one notification to every connected device.
        ///
        /// <para>Small enough for Bluetooth, which is the point: notifications keep arriving when
        /// there is no network at all. Nothing is recorded in the activity log - clipboard
        /// traffic is ephemeral by rule and this is more private still.</para>
        ///
        /// <para>Quietly does nothing when nothing is connected. A notification is not worth
        /// raising Wi-Fi for, and it will be stale by the time the link comes up.</para>
        /// </summary>
        public static async Task SendNotificationAsync(MirroredNotification notification)
        {
            // Notifications are not queued: one that arrives while nothing is connected is stale
            // by the time a link comes back, and the phone is still showing it anyway. Said out
            // loud because otherwise this is indistinguishable from mirroring being broken.
            if (!IsConnected)
            {
                Log.Write("Notify", "Nothing is connected, so that notification was not mirrored.");
                return;
            }

            byte[] body = NotificationProtocol.Build(notification);

            // One call for both tiers: the peer link picks the route, so a notification reaches
            // a device on the radio alone without this having to know that.
            await Fabric.BroadcastAsync(SyncContent.Notification, body).ConfigureAwait(false);
        }

        /// <summary>Tells the mesh a notification has gone, so it goes there too.</summary>
        public static async Task SendNotificationDismissAsync(string key)
        {
            if (!IsConnected) return;

            byte[] body = NotificationProtocol.BuildDismiss(key);

            await Fabric.BroadcastAsync(SyncContent.NotificationDismiss, body).ConfigureAwait(false);
        }

        /// <summary>
        /// Asks a device to make a noise so it can be found.
        ///
        /// Goes over whichever tier is up. One byte fits comfortably in a Bluetooth frame, which
        /// is the point: the moment you most want to find a device is the moment it is not on
        /// any network.
        /// </summary>
        public static Task<bool> RingAsync(string fingerprint, bool on) =>
            Fabric.SendToAsync(fingerprint, SyncContent.Ring, [on ? (byte)1 : (byte)0]);

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
            // Kept so the activity row can reopen it later. On Android 10 and above this is the
            // only handle that will ever exist - MediaStore does not tell the app where it put
            // the file, and there is no way to ask afterwards.
            string location = "";

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

                    location = uri.ToString() ?? "";
                }
                else
                {
                    var folder = context.GetExternalFilesDir(global::Android.OS.Environment.DirectoryDownloads)
                        ?? throw new InvalidOperationException("No external files directory.");

                    string destination = UniquePath(folder.AbsolutePath!, file.Name);
                    System.IO.File.Copy(file.Path, destination);

                    location = global::Android.Net.Uri.FromFile(new Java.IO.File(destination))?.ToString() ?? "";
                }

                Activity.Record(SyncDirection.Received, SyncItemKind.File, file.Size, file.Name, location);
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

        /// <summary>
        /// Brings up the fabric, the supervisor and the radio.
        ///
        /// <para><b>What this replaces.</b> Two loops signalling each other through semaphores -
        /// one holding the radio, one raising and dropping Wi-Fi - each correct about its own
        /// slice and neither able to see the whole. That is how a radio link to one peer came to
        /// drop the socket to every other, and how the phone came to park on a link to a device
        /// from somebody else's mesh.</para>
        /// </summary>
        private static void StartLoops()
        {
            // A plain lock, not a connect gate: this is called from the UI thread and must never
            // block on anything that touches the network.
            lock (_loopGate)
            {
                bool supervising = _supervisorTask != null && !_supervisorTask.IsCompleted;
                bool scanning = _schedulerTask != null && !_schedulerTask.IsCompleted;
                if (supervising && scanning) return;

                if (!supervising && !scanning)
                {
                    _loopCts?.Dispose();
                    _loopCts = new CancellationTokenSource();
                }

                var token = _loopCts!.Token;

                // Touching Fabric builds the supervisor and the providers on first use.
                _ = Fabric;

                StartRadioIfCapable();

                // Minted on the first run that has peers and no key, then offered over the links
                // that already exist - which is what makes the upgrade cost no re-pair.
                _discovery?.MintIfDue();

                if (!supervising) _supervisorTask = Task.Run(() => _supervisor!.RunAsync(token));
                if (!scanning && _scheduler != null) _schedulerTask = Task.Run(() => _scheduler.RunAsync(token));

                _ = Task.Run(() => InboundHeartbeatAsync(token));
            }
        }

        /// <summary>
        /// Starts the radio, and reports honestly what it can do.
        ///
        /// <para>Advertising is a hardware capability on Android and scanning is not, and
        /// declaring <c>BLUETOOTH_ADVERTISE</c> is not requesting it - it is a runtime grant on
        /// Android 12+ and the failure is quiet. So the capability handed to the arbiter comes
        /// from whether the peripheral half actually started.</para>
        /// </summary>
        private static void StartRadioIfCapable()
        {
#if ANDROID
            if (_scheduler != null) return;

            var radio = new Platforms.Android.AndroidBleRadio { Prepare = PrepareLink };

            StartPeripheralIfCapable(radio);

            radio.Capability = _bleCapability;

            _radio = radio;
            _scheduler = new BleRadioScheduler(radio)
            {
                // Before there is a mesh key this accepts everything, which is how every build
                // before this one behaved. A beacon that verifies is a fast path; a beacon that
                // is somebody else's is the one case worth refusing outright.
                BeaconFilter = _discovery!.Accepts,
                BeaconRank = _discovery.RankOf,
            };

            Fabric.AddProvider(_scheduler.CentralRoutes);
            Fabric.AddProvider(_scheduler.InboundRoutes);
#endif
        }

        /// <summary>Gives a new outbound radio link its identity, its key agreement and its handlers.</summary>
        private static void PrepareLink(Platforms.Android.AndroidBleTransport link)
        {
#if ANDROID
            link.LocalPublicKey = Security.Identity.PublicKey;
            link.LocalDeviceName = LocalDeviceName;
            link.LocalMeshName = Security.Peers.MeshName;
            link.LocalCapability = _bleCapability;
            link.OpenSession = OpenBleSession;

            link.Identified += (_, e) => OnRadioIdentified(e);
            link.WiFiRequested += l => RaiseWiFiFor(l.PeerFingerprint);
#endif
        }

        /// <summary>A peer proved who it is over the radio.</summary>
        private static void OnRadioIdentified(PeerIdentifiedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.DeviceName)) _lastPeerName = e.DeviceName;

            Security.Peers.NoteSeen(e.Fingerprint, null, e.DeviceName, e.Capability);

            // Adopted only when this phone has no name of its own, which is what stops two
            // devices that disagree overwriting each other on every reconnect.
            Security.Peers.AdoptMeshName(e.MeshName);

            ReportLinkState();
            _supervisor?.Signal();
        }

        /// <summary>
        /// A peer has something Bluetooth cannot carry, so raise Wi-Fi for it.
        ///
        /// <para><b>Per peer.</b> The window used to be a single timestamp for the whole phone,
        /// so one peer asking held Wi-Fi up for every peer.</para>
        /// </summary>
        private static void RaiseWiFiFor(string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(fingerprint)) return;

            lock (_demandGate) _wifiWake[fingerprint] = DateTime.UtcNow.Add(WiFiWakeWindow);

            Log.Write("Sync",
                $"{DeviceIdentity.Shorten(fingerprint)} asked for Wi-Fi; holding it up for {WiFiWakeWindow.TotalSeconds:F0}s.");

            _supervisor?.Signal();
        }

        /// <summary>Runs the inbound half's liveness check, which has no loop of its own.</summary>
        private static async Task InboundHeartbeatAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try { _inbound?.CheckHeartbeat(); }
                catch (Exception ex) { Log.Write("Sync", "The inbound liveness check failed", ex); }

                try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        private static CancellationToken CurrentToken()
        {
            lock (_loopGate) return _loopCts?.Token ?? new CancellationToken(canceled: true);
        }

        /// <summary>Asks the supervisor to reconcile now rather than at the next interval.</summary>
        private static void SignalBle() => _supervisor?.Signal();

        /// <summary>Asks the supervisor to reconcile now rather than at the next interval.</summary>
        private static void SignalWiFi() => _supervisor?.Signal();


        // ---------------------------------------------------------------- connecting

        /// <summary>
        /// True when a transport exists that could actually reach the paired computer.
        ///
        /// Specifically Wi-Fi or Ethernet, not merely "some network": the host is a private
        /// LAN address, and mobile data can never route to it. Asking only whether a network
        /// existed answered yes on cellular and spent the TCP timeout proving otherwise
        /// before Bluetooth got a turn.
        ///
        /// <para><b>The hotspot case, which the capability check alone gets wrong.</b> When the
        /// phone is the hotspot and the computer is a client of it, the computer is one hop away
        /// over the phone's own access-point interface - the best possible case for the Wi-Fi
        /// tier. But <c>ActiveNetwork</c> reports cellular, because that is how the *phone*
        /// reaches the internet, and tethering is not surfaced as a Wi-Fi transport at all. The
        /// capability check therefore answered "no network" in the one topology where the peer
        /// was closest, and images and file transfer silently fell back to Bluetooth or were
        /// dropped as not worth encrypting.</para>
        ///
        /// <para>So a negative from the capability check is not taken as final: the interface
        /// list is consulted for an access-point interface holding a private address. See
        /// <see cref="HasTetheringInterface"/>.</para>
        /// </summary>
        private static bool HasUsableNetwork()
        {
#if ANDROID
            try
            {
                var manager = (global::Android.Net.ConnectivityManager?)global::Android.App.Application.Context
                    .GetSystemService(Context.ConnectivityService);

                var network = manager?.ActiveNetwork;
                var capabilities = network == null ? null : manager?.GetNetworkCapabilities(network);

                // Deliberately not checking Validated: a local-only network with no internet
                // uplink is exactly the setup this project is built for.
                if (capabilities != null
                    && (capabilities.HasTransport(global::Android.Net.TransportType.Wifi)
                        || capabilities.HasTransport(global::Android.Net.TransportType.Ethernet)))
                {
                    return true;
                }

                return HasTetheringInterface();
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

#if ANDROID
        /// <summary>
        /// Whether this phone is running an access point that a peer could already be sitting on.
        ///
        /// <para>Android does not expose "I am a hotspot" through any supported API - the
        /// <c>WifiManager</c> call for it has been hidden since API 26 - so the interface list is
        /// the honest way to ask. An access point interface is up, is not loopback, carries a
        /// private IPv4, and is named by one of the handful of conventions the OEMs use.</para>
        ///
        /// <para>The name check is what keeps this from answering yes on plain cellular, where
        /// <c>rmnet</c> also carries a private address handed out by the carrier. Matching on
        /// names is unlovely, but the alternative - treating any private IPv4 as reachable - puts
        /// the five second TCP timeout back on the cellular path that the capability check was
        /// added to avoid.</para>
        /// </summary>
        private static bool HasTetheringInterface()
        {
            // Every access-point interface name in use across the OEMs, lowercased.
            string[] apPrefixes = ["ap", "swlan", "softap", "wlan1"];

            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                string name = nic.Name.ToLowerInvariant();
                if (!apPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal))) continue;

                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    if (!IsPrivateV4(address.Address)) continue;

                    Log.Write("Sync", $"Acting as a hotspot on {nic.Name}, so the Wi-Fi tier is worth raising.");
                    return true;
                }
            }

            return false;
        }

        private static bool IsPrivateV4(System.Net.IPAddress address)
        {
            byte[] o = address.GetAddressBytes();
            return o[0] switch
            {
                10 => true,
                172 => o[1] >= 16 && o[1] <= 31,
                192 => o[1] == 168,
                _ => false
            };
        }
#endif

        /// <summary>
        /// Tries advertising again, after a permission grant that arrived too late.
        ///
        /// Advertising is attempted once per run. Android 12 made BLUETOOTH_ADVERTISE a runtime
        /// grant, so on the launch where the user first allows it the attempt has already been
        /// made and refused - and without this the phone would stay unfindable until it was next
        /// started.
        /// </summary>
        public static void RetryBluetoothPeripheral()
        {
#if ANDROID
            if (_radio?.Peripheral != null) return;

            Interlocked.Exchange(ref _peripheralStarted, 0);
            if (_radio != null) StartPeripheralIfCapable(_radio);

            _radio!.Capability = _bleCapability;
            _supervisor?.Signal();
#endif
        }

        /// <summary>
        /// Publishes the Mesh Sync service so a peer can connect to this phone.
        ///
        /// <para>Deliberately additive and deliberately best-effort: a failure here leaves the
        /// phone exactly as it was, scanning and connecting out, which is how Bluetooth worked
        /// before either role could be taken. Only the peer that needed us to advertise loses
        /// out - and capability-first arbitration then correctly makes this phone the central
        /// for every peer, so nothing deadlocks.</para>
        /// </summary>
        private static void StartPeripheralIfCapable(Platforms.Android.AndroidBleRadio radio)
        {
#if ANDROID
            if (Interlocked.Exchange(ref _peripheralStarted, 1) != 0) return;

            var capability = Platforms.Android.BleCapabilities.Detect();
            _bleCapability = capability;

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
                    LocalCapability = capability,
                    OpenSession = OpenBleSession,
                };

                peripheral.ClientConnected += (_, _) =>
                {
                    // A route, not a connection. It sits in Handshaking until the hello crosses,
                    // and is closed on the shared deadline if it never does.
                    var route = new Platforms.Android.AndroidBlePeripheralRoute(peripheral);
                    route.Identified += (_, e) => OnRadioIdentified(e);

                    _inbound = route;
                    Log.Write("Sync", "A peer connected to this phone over Bluetooth.");
                    radio.PublishInbound(route);
                };

                peripheral.WiFiRequested += (_, _) => RaiseWiFiFor(peripheral.RemoteFingerprint);

                radio.Peripheral = peripheral;
                _ = peripheral.StartListeningAsync();
            }
            catch (Exception ex)
            {
                // Honest rather than optimistic: a peripheral that did not start must not be
                // reported to the arbiter as one that did, or this phone would be told to
                // advertise, would not, and would not scan either.
                Log.Write("Sync", "Could not start advertising over Bluetooth", ex);
                _bleCapability = BleCapability.Central;
                radio.Peripheral = null;
            }
#endif
        }

        /// <summary>Retires the radio and the fabric's routes with it.</summary>
        private static void RetireBle()
        {
            var scheduler = Interlocked.Exchange(ref _scheduler, null);
            var radio = Interlocked.Exchange(ref _radio, null);
            var inbound = Interlocked.Exchange(ref _inbound, null);

            Interlocked.Exchange(ref _peripheralStarted, 0);

            try { inbound?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception ex) { Log.Write("Sync", "Disposing the inbound Bluetooth link failed", ex); }

            // Closed rather than merely stopped: a GATT service left registered by a process that
            // has gone is the desktop side's worst Bluetooth failure, and there is no reason to
            // reproduce it here.
            try { scheduler?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception ex) { Log.Write("Sync", "Disposing the Bluetooth scheduler failed", ex); }

            try { radio?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception ex) { Log.Write("Sync", "Disposing the radio failed", ex); }
        }

        /// <summary>Stops advertising and tears down the inbound Bluetooth link.</summary>
        private static void RetirePeripheral() => RetireBle();

        /// <summary>
        /// Drops every Wi-Fi route, leaving the radio to hold presence.
        ///
        /// Used by an explicit Stop rather than by policy: policy closes routes one peer at a
        /// time now, which is the difference between putting Wi-Fi away for a peer the radio is
        /// carrying and dropping the socket to a device the radio never reached.
        /// </summary>
        private static void RetireWiFi()
        {
            try
            {
                foreach (var link in _fabric?.Links ?? Array.Empty<PeerLink>())
                {
                    _ = link.CloseAsync(RouteKind.WiFi, "the user asked this device to stop");
                }
            }
            catch (Exception ex) { Log.Write("Sync", "Dropping the Wi-Fi links failed", ex); }
        }

        // ---------------------------------------------------------------- link events

        /// <summary>
        /// A payload arrived from a paired device, already opened.
        ///
        /// <para>One handler for both tiers. The radio used to have its own, which is how the two
        /// ended up dispatching differently.</para>
        /// </summary>
        private static void Fabric_PayloadReceived(PeerLink link, RoutePayload payload)
        {
            // Runs on a transport's receive loop. Kept total so a failure here can never take the
            // connection down with it.
            try { Apply(payload.Peer, payload.ContentType, payload.Body); }
            catch (Exception ex) { Log.Write("Sync", "Payload handling failed", ex); }
        }

        private static void Fabric_PeerConnected(PeerLink link, IPeerRoute route)
        {
            // Remembered so the name survives the socket being dropped, which under standby is
            // most of the time - otherwise the dashboard falls back to "your computer" the moment
            // Wi-Fi goes away, which reads as a fault rather than as the design working.
            if (!string.IsNullOrWhiteSpace(link.Peer.Name)) _lastPeerName = link.Peer.Name;

            Log.Write("Sync", $"{link.Peer.Name ?? DeviceIdentity.Shorten(link.Fingerprint)} connected over {route.Kind}.");

            // The mesh key rides the links that already exist, which is what makes the upgrade
            // cost no re-pair. Offered on every connect: it is 32 bytes, and a peer that already
            // holds a lower one simply keeps it.
            OfferMeshKey(link.Fingerprint);

            ReportLinkState();
        }

        private static void Fabric_PeerDisconnected(PeerLink link, RouteKind kind, string reason)
        {
            Log.Write("Sync", $"{DeviceIdentity.Shorten(link.Fingerprint)} lost its {kind} route: {reason}.");

            ReportLinkState();
            _supervisor?.Signal();
        }

        /// <summary>Offers this phone's mesh discovery key to one peer, over the ordinary path.</summary>
        private static void OfferMeshKey(string fingerprint)
        {
            var key = Security.Peers.MeshKey;
            if (key == null) return;

            _ = Task.Run(async () =>
            {
                try { await Fabric.SendToAsync(fingerprint, SyncContent.MeshKeyOffer, key).ConfigureAwait(false); }
                catch (Exception ex) { Log.Write("Sync", "Offering the mesh key failed", ex); }
            });
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
