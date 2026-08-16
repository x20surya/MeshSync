using Android.AccessibilityServices;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Views.Accessibility;
using CoreLib;
using CoreLib.Diagnostics;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AndroidClient.Platforms.Android
{
    [Service(Label = "Universal Clipboard Sync", Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE", Exported = true)]
    [IntentFilter(new[] { "android.accessibilityservice.AccessibilityService" })]
    [MetaData("android.accessibilityservice", Resource = "@xml/accessibility_service_config")]
    public class ClipboardAccessibilityService : AccessibilityService, ClipboardManager.IOnPrimaryClipChangedListener
    {
        /// <summary>Shared with <see cref="CloseServiceReceiver"/> so both address the same notification.</summary>
        public const int NotificationId = 1001;

        private const string ChannelId = "clipboard_sync_channel";

        /// <summary>Cap on a single clipboard image read, to avoid an out-of-memory kill.</summary>
        private const int MaxClipboardImageBytes = 48 * 1024 * 1024;

        private ClipboardManager? _clipboardManager;
        private ScreenshotObserver? _screenshotObserver;
        private NetworkWatcher? _networkWatcher;

        protected override void OnServiceConnected()
        {
            base.OnServiceConnected();

            // Route CoreLib diagnostics into logcat so field failures are visible.
            Log.Sink ??= line => global::Android.Util.Log.Info("MeshSync", line);

            // Deliberately not async void: an exception escaping this callback would take
            // the whole accessibility service down.
            try
            {
                _clipboardManager = (ClipboardManager?)GetSystemService(ClipboardService);
                _clipboardManager?.AddPrimaryClipChangedListener(this);

                RegisterScreenshotObserver();

                _networkWatcher = new NetworkWatcher(this);
                _networkWatcher.Start();

                SyncManager.OnConnectionStatusChanged -= SyncManager_OnConnectionStatusChanged;
                SyncManager.OnConnectionStatusChanged += SyncManager_OnConnectionStatusChanged;

                // Drives the live notification text from real sync traffic.
                SyncManager.Activity.Changed -= SyncManager_ActivityChanged;
                SyncManager.Activity.Changed += SyncManager_ActivityChanged;

                CreateNotificationChannel();
                RefreshNotification();

                // Not user-initiated: a service restart must not revive a stopped sync.
                _ = SyncManager.AutoConnectAsync(false);

                Log.Write("Service", "Accessibility service connected.");
            }
            catch (Exception ex)
            {
                Log.Write("Service", "Startup failed", ex);
            }
        }

        private void RegisterScreenshotObserver()
        {
            try
            {
                _screenshotObserver = new ScreenshotObserver(this, new Handler(Looper.MainLooper!));
                ContentResolver?.RegisterContentObserver(
                    global::Android.Provider.MediaStore.Images.Media.ExternalContentUri!,
                    true,
                    _screenshotObserver);
                Log.Write("Service", "Screenshot observer registered.");
            }
            catch (Exception ex)
            {
                Log.Write("Service", "Could not register screenshot observer", ex);
            }
        }

        private void SyncManager_OnConnectionStatusChanged(string status) => RefreshNotification();

        private void SyncManager_ActivityChanged(object? sender, EventArgs e) => RefreshNotification();

        /// <summary>
        /// Repaints the ongoing notification from live state: who we are connected to, and
        /// what moved most recently. This is the only Mesh Sync UI most of the time, so it
        /// should never sit on stale text.
        /// </summary>
        private void RefreshNotification()
        {
            try
            {
                // Honour an explicit Stop. Without this the "Disconnected" status raised while
                // tearing the connection down re-posted the very notification Stop removed.
                if (SyncManager.IsPaused)
                {
                    var manager = (NotificationManager?)GetSystemService(NotificationService);
                    manager?.Cancel(NotificationId);
                    return;
                }

                string title;
                string body;

                if (SyncManager.IsConnected)
                {
                    title = $"Connected to {SyncManager.PeerName ?? "your computer"}";

                    var latest = SyncManager.Activity.Snapshot().FirstOrDefault();
                    body = latest == null
                        ? "Ready - copy something to sync it"
                        : $"{(latest.Direction == SyncDirection.Sent ? "Sent" : "Received")} · {latest.Title} · {latest.RelativeAge}";
                }
                else if (SyncManager.IsPaired)
                {
                    title = "Reconnecting…";
                    body = $"Looking for {SyncManager.PairedAddress}";
                }
                else
                {
                    title = "Mesh Sync";
                    body = "Not paired yet - open the app to pair";
                }

                ShowPersistentNotification(title, body);
            }
            catch (Exception ex)
            {
                Log.Write("Service", "Notification update failed", ex);
            }
        }

        private void CreateNotificationChannel()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;

            var channel = new NotificationChannel(ChannelId, "Clipboard Sync", NotificationImportance.Low)
            {
                Description = "Persistent notification for manual clipboard syncing"
            };
            var notificationManager = (NotificationManager?)GetSystemService(NotificationService);
            notificationManager?.CreateNotificationChannel(channel);
        }

        private void ShowPersistentNotification(string title, string statusText)
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
            builder.SetContentText(statusText);
            builder.SetSmallIcon(global::Android.Resource.Drawable.IcMenuSend);
            builder.SetOngoing(true);
            // Repaints in place instead of re-alerting on every sync.
            builder.SetOnlyAlertOnce(true);
            builder.SetShowWhen(false);
            // Icon-based actions; the (int, string, PendingIntent) overload has been
            // deprecated since API 23.
            builder.AddAction(BuildAction(global::Android.Resource.Drawable.IcMenuShare, "Sync Clipboard", pendingSyncIntent));
            builder.AddAction(BuildAction(global::Android.Resource.Drawable.IcMenuCloseClearCancel, "Stop Service", pendingCloseIntent));

            var notification = builder.Build();
            if (notification == null) return;

            var notificationManager = (NotificationManager?)GetSystemService(NotificationService);
            notificationManager?.Notify(NotificationId, notification);
        }

        private Notification.Action BuildAction(int iconResource, string title, PendingIntent? intent)
        {
            var icon = global::Android.Graphics.Drawables.Icon.CreateWithResource(this, iconResource);
            return new Notification.Action.Builder(icon, title, intent).Build()!;
        }

        public void OnPrimaryClipChanged()
        {
            // Android 14 lets users swipe away even an ongoing notification, and this is a
            // plain accessibility service rather than a foreground service, so the system
            // will not refuse the dismissal. Re-posting on clipboard activity brings it back
            // the moment it is relevant again, which is when the user copies something.
            RefreshNotification();

            // The listener fires on the main thread; hand off immediately so reading the
            // clip and encrypting it never blocks the UI.
            _ = Task.Run(CaptureAndSendClipAsync);
        }

        private async Task CaptureAndSendClipAsync()
        {
            try
            {
                var clipboardManager = _clipboardManager;
                if (clipboardManager?.HasPrimaryClip != true) return;

                var clipData = clipboardManager.PrimaryClip;
                if (clipData == null || clipData.ItemCount == 0) return;

                var item = clipData.GetItemAt(0);
                if (item == null) return;

                var itemUri = item.Uri;
                if (itemUri != null && await TrySendImageAsync(itemUri).ConfigureAwait(false)) return;

                var text = item.CoerceToText(this)?.ToString();
                if (string.IsNullOrEmpty(text)) return;

                Log.Write("Service", $"Captured {text.Length} characters of text.");
                await SyncManager.SendClipboardAsync(text).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write("Service", "Clipboard capture failed", ex);
            }
        }

        private async Task<bool> TrySendImageAsync(global::Android.Net.Uri uri)
        {
            try
            {
                var contentResolver = ContentResolver;
                var mimeType = contentResolver?.GetType(uri);
                if (mimeType == null || !mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return false;

                using var stream = contentResolver!.OpenInputStream(uri);
                if (stream == null) return false;

                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms).ConfigureAwait(false);

                if (ms.Length == 0) return false;
                if (ms.Length > MaxClipboardImageBytes)
                {
                    Log.Write("Service", $"Ignoring {ms.Length} byte clipboard image (over the read limit).");
                    return true;
                }

                byte[] imageBytes = ms.ToArray();
                Log.Write("Service", $"Captured image, {imageBytes.Length} bytes.");
                await SyncManager.SendClipboardImageAsync(imageBytes).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Service", "Processing copied image failed", ex);
                return false;
            }
        }

        public override void OnAccessibilityEvent(AccessibilityEvent? e)
        {
            // Not used: the clipboard listener and MediaStore observer cover both sync paths.
        }

        public override void OnInterrupt() => Log.Write("Service", "Accessibility service interrupted.");

        public override bool OnUnbind(Intent? intent)
        {
            Teardown();
            return base.OnUnbind(intent);
        }

        public override void OnDestroy()
        {
            // OnUnbind is not guaranteed to run on every teardown path, so clean up here too.
            Teardown();
            base.OnDestroy();
        }

        private void Teardown()
        {
            try
            {
                SyncManager.OnConnectionStatusChanged -= SyncManager_OnConnectionStatusChanged;
                SyncManager.Activity.Changed -= SyncManager_ActivityChanged;

                if (_clipboardManager != null)
                {
                    _clipboardManager.RemovePrimaryClipChangedListener(this);
                    _clipboardManager = null;
                }

                if (_screenshotObserver != null)
                {
                    ContentResolver?.UnregisterContentObserver(_screenshotObserver);
                    _screenshotObserver.Dispose();
                    _screenshotObserver = null;
                }

                if (_networkWatcher != null)
                {
                    _networkWatcher.Stop();
                    _networkWatcher.Dispose();
                    _networkWatcher = null;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Service", "Teardown failed", ex);
            }
        }
    }
}
