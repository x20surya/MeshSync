using System;
using System.Linq;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using CoreLib;
using CoreLib.Diagnostics;

namespace AndroidClient.Platforms.Android
{
    /// <summary>
    /// Holds the links open, and is the reason they survive.
    ///
    /// <para><b>What was there before.</b> Nothing. The connection was kept alive purely by the
    /// accessibility service, which posted an ongoing notification that <em>looked</em> like a
    /// foreground service and had no <c>startForeground</c> behind it. Android will not keep a
    /// socket - still less a GATT connection - alive in that context, and Doze eventually killed
    /// it. The heartbeat and backoff made recovery quick, which is what disguised the problem.</para>
    ///
    /// <para><b>Why it matters more now.</b> Bluetooth standby holds a link continuously rather
    /// than dialling one when needed. Android is markedly harder on background GATT connections
    /// than on idle sockets, so without this, inverting the tiers would have made things worse
    /// rather than better. <c>connectedDevice</c> is the type that describes exactly what is
    /// being held.</para>
    ///
    /// <para>The service also owns the ongoing notification. It used to be posted by the
    /// accessibility service with a plain <c>Notify</c>, which meant Android 14 let the user
    /// swipe it away and left the app with no visible sign it was running. A foreground
    /// service's notification cannot be dismissed while the service is up.</para>
    /// </summary>
    [Service(
        Exported = false,
        ForegroundServiceType = ForegroundService.TypeConnectedDevice)]
    public sealed class SyncForegroundService : Service
    {
        public const int NotificationId = 1001;
        private const string ChannelId = "MeshSyncChannel";

        /// <summary>Set while the service is up, so callers can avoid a pointless start.</summary>
        private static volatile bool _running;

        public static bool IsRunning => _running;

        // ── what this service watches ───────────────────────────────────────────────────
        //
        // All three used to live in the accessibility service, which was the wrong host for
        // any of them: none needs accessibility, and hanging them there meant refusing that
        // permission silently cost screenshot sync, reconnect-on-network-change and the
        // screen-following Wi-Fi logic as well as the thing it was actually about.
        //
        // They belong here, in the service that already holds the links for the same lifetime.

        private ScreenshotObserver? _screenshotObserver;
        private NetworkWatcher? _networkWatcher;
        private ScreenStateWatcher? _screenWatcher;

        /// <summary>
        /// Starts the service, tolerating a refusal.
        ///
        /// Android 12 forbids starting a foreground service from the background in most
        /// circumstances. Being refused is not fatal - syncing still works exactly as it did
        /// before this service existed - so it is logged and the app carries on rather than
        /// crashing on a restriction it cannot control.
        /// </summary>
        public static void Start(Context context)
        {
            if (SyncManager.IsPaused)
            {
                Log.Write("Service", "Not starting the foreground service: syncing is paused.");
                return;
            }

            try
            {
                var intent = new Intent(context, typeof(SyncForegroundService));

                if (OperatingSystem.IsAndroidVersionAtLeast(26)) context.StartForegroundService(intent);
                else context.StartService(intent);
            }
            catch (Exception ex)
            {
                Log.Write("Service",
                    "Could not start the foreground service; the links will run unprotected and Doze may kill them.", ex);
            }
        }

        public static void Stop(Context context)
        {
            try { context.StopService(new Intent(context, typeof(SyncForegroundService))); }
            catch (Exception ex) { Log.Write("Service", "Could not stop the foreground service", ex); }
        }

        public override IBinder? OnBind(Intent? intent) => null;

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            try
            {
                CreateNotificationChannel();

                // Posted before anything else can fail. Android kills a service that has not
                // called startForeground within a few seconds of being started this way, and
                // the resulting crash is far harder to read than a notification with no text.
                if (!EnterForeground()) return StartCommandResult.NotSticky;

                _running = true;

                // Diagnostics need a sink before anything can report a failure. The
                // accessibility service used to install it; on a boot start nothing else has.
                Log.Sink ??= line => global::Android.Util.Log.Info("MeshSync", line);

                StartWatchers();

                SyncManager.OnConnectionStatusChanged -= OnStatusChanged;
                SyncManager.OnConnectionStatusChanged += OnStatusChanged;
                SyncManager.Activity.Changed -= OnActivityChanged;
                SyncManager.Activity.Changed += OnActivityChanged;

                // Not user-initiated: a service restart must not revive a stopped sync.
                _ = SyncManager.AutoConnectAsync(false);

                Refresh();
                Log.Write("Service", "Foreground service running; the links are held by it now.");
            }
            catch (Exception ex)
            {
                Log.Write("Service", "Foreground service start failed", ex);
                return StartCommandResult.NotSticky;
            }

            // Sticky, so Android brings it back after killing it for memory. The links
            // reconnect on their own from there.
            return StartCommandResult.Sticky;
        }

        /// <summary>
        /// Calls startForeground with the type Android expects for a held device connection.
        ///
        /// Returns false when the system refuses, which it does if the app is in the background
        /// on Android 12+ or lacks a permission the declared type requires. Refusing to start is
        /// recoverable; throwing out of OnStartCommand is not.
        /// </summary>
        private bool EnterForeground()
        {
            var notification = Build("Mesh Sync", "Starting…");
            if (notification == null) return false;

            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(29))
                {
                    StartForeground(NotificationId, notification, ForegroundService.TypeConnectedDevice);
                }
                else
                {
#pragma warning disable CA1416 // The untyped overload is the only one below API 29.
                    StartForeground(NotificationId, notification);
#pragma warning restore CA1416
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Service", "The system refused startForeground; running without it.", ex);
                return false;
            }
        }

        /// <summary>
        /// Starts the three things that watch the phone rather than the links.
        ///
        /// Each is best-effort and independent: a screenshot observer that will not register
        /// must not stop the screen watcher, because losing the screen watcher would leave Wi-Fi
        /// held open all night, which is the drain standby exists to avoid.
        /// </summary>
        private void StartWatchers()
        {
            try
            {
                // Intercepts screenshots without touching the clipboard, which is what lets them
                // sync without the user copying anything.
                _screenshotObserver = new ScreenshotObserver(this, new Handler(Looper.MainLooper!));
                ContentResolver?.RegisterContentObserver(
                    global::Android.Provider.MediaStore.Images.Media.ExternalContentUri!,
                    true,
                    _screenshotObserver);
                Log.Write("Service", "Screenshot observer registered.");
            }
            catch (Exception ex) { Log.Write("Service", "Could not register the screenshot observer", ex); }

            try
            {
                _networkWatcher = new NetworkWatcher(this);
                _networkWatcher.Start();
            }
            catch (Exception ex) { Log.Write("Service", "Could not watch the network", ex); }

            try
            {
                // Decides whether the Wi-Fi link is held open, now that Bluetooth is the
                // standing link rather than the fallback.
                _screenWatcher = new ScreenStateWatcher(this);
                _screenWatcher.Start();
            }
            catch (Exception ex) { Log.Write("Service", "Could not watch the screen state", ex); }
        }

        private void StopWatchers()
        {
            try
            {
                if (_screenshotObserver != null) ContentResolver?.UnregisterContentObserver(_screenshotObserver);
            }
            catch (Exception ex) { Log.Write("Service", "Unregistering the screenshot observer failed", ex); }
            _screenshotObserver = null;

            try { _networkWatcher?.Stop(); } catch (Exception ex) { Log.Write("Service", "Stopping the network watcher failed", ex); }
            _networkWatcher = null;

            try { _screenWatcher?.Stop(); } catch (Exception ex) { Log.Write("Service", "Stopping the screen watcher failed", ex); }
            _screenWatcher = null;
        }

        public override void OnDestroy()
        {
            _running = false;

            StopWatchers();

            SyncManager.OnConnectionStatusChanged -= OnStatusChanged;
            SyncManager.Activity.Changed -= OnActivityChanged;

            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(24)) StopForeground(StopForegroundFlags.Remove);
                else
                {
#pragma warning disable CA1416, CS0618 // The boolean overload is the only one below API 24.
                    StopForeground(true);
#pragma warning restore CA1416, CS0618
                }
            }
            catch (Exception ex) { Log.Write("Service", "Leaving the foreground failed", ex); }

            Log.Write("Service", "Foreground service stopped.");
            base.OnDestroy();
        }

        // ──────────────────────────────── notification

        private void OnStatusChanged(string status) => Refresh();

        private void OnActivityChanged(object? sender, EventArgs e) => Refresh();

        /// <summary>
        /// Repaints from live state: which links are up, who is on the other end, and what
        /// moved most recently. This is the only Mesh Sync UI most of the time, so it should
        /// never sit on stale text.
        /// </summary>
        private void Refresh()
        {
            try
            {
                if (!_running) return;

                string title;
                string body;

                if (SyncManager.IsConnected)
                {
                    // The mesh, not whichever device answered. With three devices the peer
                    // name names one of them arbitrarily and reads as though this pairs with a
                    // single machine.
                    title = $"Connected to {SyncManager.MeshName}";

                    var latest = SyncManager.Activity.Snapshot().FirstOrDefault();
                    body = latest == null
                        ? $"{LinkSummary()} · copy something to sync it"
                        : $"{(latest.Direction == SyncDirection.Sent ? "Sent" : "Received")} · {latest.Title} · {latest.RelativeAge}";
                }
                else if (SyncManager.IsPaired)
                {
                    // Named, not addressed. An address is a hint that moves with the DHCP lease
                    // and means nothing to the person reading it; the device is what they
                    // recognise, and is what stays the same when the address does not.
                    title = "Reconnecting…";
                    body = $"Looking for {SyncManager.MeshName}";
                }
                else
                {
                    title = "Mesh Sync";
                    body = "Not paired yet - open the app to pair";
                }

                var notification = Build(title, body);
                if (notification == null) return;

                var manager = (NotificationManager?)GetSystemService(NotificationService);
                manager?.Notify(NotificationId, notification);
            }
            catch (Exception ex)
            {
                Log.Write("Service", "Notification update failed", ex);
            }
        }

        /// <summary>
        /// Names the links that are actually up. Under standby that is usually Bluetooth alone,
        /// and saying so is the difference between the user reading it as working and reading
        /// it as degraded.
        /// </summary>
        private static string LinkSummary()
        {
            bool ble = SyncManager.BleConnected;
            bool wifi = SyncManager.WiFiConnected;

            if (ble && wifi) return "Wi-Fi and Bluetooth";
            if (wifi) return "Over Wi-Fi";
            if (ble) return "Over Bluetooth";
            return "Not connected";
        }

        private void CreateNotificationChannel()
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;

            var channel = new NotificationChannel(ChannelId, "Clipboard Sync", NotificationImportance.Low)
            {
                Description = "Keeps the link to your paired devices open"
            };

            var manager = (NotificationManager?)GetSystemService(NotificationService);
            manager?.CreateNotificationChannel(channel);
        }

        private Notification? Build(string title, string body)
        {
            var syncIntent = new Intent(this, typeof(SyncActivity));
            var pendingSyncIntent = PendingIntent.GetActivity(this, 0, syncIntent,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

            var closeIntent = new Intent(this, typeof(CloseServiceReceiver));
            var pendingCloseIntent = PendingIntent.GetBroadcast(this, 1, closeIntent,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

            // Built with discrete calls rather than a fluent chain: each builder method is
            // bound as returning a nullable Builder, so chaining warns on every hop.
            var builder = new Notification.Builder(this, ChannelId);
            builder.SetContentTitle(title);
            builder.SetContentText(body);
            builder.SetSmallIcon(global::Android.Resource.Drawable.IcMenuSend);
            builder.SetOngoing(true);
            // Repaints in place instead of re-alerting on every sync.
            builder.SetOnlyAlertOnce(true);
            builder.SetShowWhen(false);
            // Icon-based actions; the (int, string, PendingIntent) overload has been
            // deprecated since API 23.
            builder.AddAction(Action(global::Android.Resource.Drawable.IcMenuShare, "Sync Clipboard", pendingSyncIntent));
            builder.AddAction(Action(global::Android.Resource.Drawable.IcMenuCloseClearCancel, "Stop Service", pendingCloseIntent));

            return builder.Build();
        }

        private Notification.Action Action(int iconResource, string title, PendingIntent? intent)
        {
            var icon = global::Android.Graphics.Drawables.Icon.CreateWithResource(this, iconResource);
            return new Notification.Action.Builder(icon, title, intent).Build()!;
        }
    }
}
