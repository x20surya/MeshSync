using Android.AccessibilityServices;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Views.Accessibility;
using System;

namespace AndroidClient.Platforms.Android
{
    [Service(Label = "Universal Clipboard Sync", Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE", Exported = true)]
    [IntentFilter(new[] { "android.accessibilityservice.AccessibilityService" })]
    [MetaData("android.accessibilityservice", Resource = "@xml/accessibility_service_config")]
    public class ClipboardAccessibilityService : AccessibilityService, ClipboardManager.IOnPrimaryClipChangedListener
    {
        private ClipboardManager? _clipboardManager;

        protected override async void OnServiceConnected()
        {
            base.OnServiceConnected();
            
            // Get the system clipboard service
            _clipboardManager = (ClipboardManager?)GetSystemService(ClipboardService);
            
            if (_clipboardManager != null)
            {
                _clipboardManager.AddPrimaryClipChangedListener(this);
                Console.WriteLine("[Android] Clipboard Accessibility Service Connected! Listening for copies...");
            }

            // Restore notification if we reconnect via the app UI
            AndroidClient.SyncManager.OnConnectionStatusChanged -= SyncManager_OnConnectionStatusChanged;
            AndroidClient.SyncManager.OnConnectionStatusChanged += SyncManager_OnConnectionStatusChanged;

            // Auto-connect to laptop if we have saved preferences
            await AndroidClient.SyncManager.AutoConnectAsync();

            CreateNotificationChannel();
            ShowPersistentNotification();
        }

        private void SyncManager_OnConnectionStatusChanged(string status)
        {
            if (status == "Connected!")
            {
                ShowPersistentNotification();
            }
        }

        private void CreateNotificationChannel()
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var channel = new NotificationChannel("clipboard_sync_channel", "Clipboard Sync", NotificationImportance.Low);
                channel.Description = "Persistent notification for manual clipboard syncing";
                var notificationManager = (NotificationManager?)GetSystemService(NotificationService);
                notificationManager?.CreateNotificationChannel(channel);
            }
        }

        private void ShowPersistentNotification()
        {
            var syncIntent = new Intent(this, typeof(SyncActivity));
            var pendingSyncIntent = PendingIntent.GetActivity(this, 0, syncIntent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

            var closeIntent = new Intent(this, typeof(CloseServiceReceiver));
            var pendingCloseIntent = PendingIntent.GetBroadcast(this, 1, closeIntent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

            var builder = new Notification.Builder(this, "clipboard_sync_channel")
                .SetContentTitle("Mesh Sync Active")
                .SetContentText("Ready to sync clipboards securely.")
                .SetSmallIcon(global::Android.Resource.Drawable.IcMenuSend)
                .SetOngoing(true) // Cannot be swiped away
                .AddAction(global::Android.Resource.Drawable.IcMenuShare, "Sync Clipboard", pendingSyncIntent)
                .AddAction(global::Android.Resource.Drawable.IcMenuCloseClearCancel, "Stop Service", pendingCloseIntent);

            var notificationManager = (NotificationManager?)GetSystemService(NotificationService);
            notificationManager?.Notify(1001, builder.Build());
        }

        public async void OnPrimaryClipChanged()
        {
            if (_clipboardManager?.HasPrimaryClip == true)
            {
                var clipData = _clipboardManager.PrimaryClip;
                if (clipData != null && clipData.ItemCount > 0)
                {
                    var item = clipData.GetItemAt(0);
                    var text = item?.CoerceToText(this)?.ToString();
                    
                    if (!string.IsNullOrEmpty(text))
                    {
                        var preview = text.Length > 20 ? text.Substring(0, 20) + "..." : text;
                        Console.WriteLine($"[Android] Copied text captured: {preview}");
                        
                        // Blast it to the laptop!
                        await AndroidClient.SyncManager.SendClipboardAsync(text);
                    }
                }
            }
        }

        public override void OnAccessibilityEvent(AccessibilityEvent? e)
        {
            // Optional: Can be used to inspect text fields or UI changes if clipboard manager fails on future OS versions
        }

        public override void OnInterrupt()
        {
            Console.WriteLine("[Android] Clipboard Accessibility Service Interrupted");
        }

        public override bool OnUnbind(Intent? intent)
        {
            if (_clipboardManager != null)
            {
                _clipboardManager.RemovePrimaryClipChangedListener(this);
            }
            return base.OnUnbind(intent);
        }
    }
}
