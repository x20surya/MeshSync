using System;
using System.Collections.Concurrent;
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using CoreLib.Diagnostics;
using CoreLib.Transport;

namespace AndroidClient.Platforms.Android
{
    /// <summary>
    /// Shows a notification that arrived from somewhere else in the mesh.
    ///
    /// <para><b>Why the phone is a display and not only a source.</b> Mirroring used to run one
    /// way: the phone posted, the desktop showed. That is the common case with one phone and one
    /// computer, and it stops making sense the moment there are two phones - the second one is a
    /// paired device that knows what the first one is showing and does nothing with it. Every
    /// other content type in this app already crosses in both directions, and a mesh where one
    /// payload is one-way by construction is a mesh with a hole in it.</para>
    ///
    /// <para><b>These cannot loop.</b> A notification this class posts is posted by this app, and
    /// the listener drops anything from its own package before it reaches the wire. So a mirrored
    /// notification is shown and never mirrored onward.</para>
    /// </summary>
    public static class MirroredNotificationDisplay
    {
        private const string ChannelId = "MeshMirroredChannel";

        /// <summary>
        /// Ids Android needs to cancel by, against the sending device's opaque key.
        ///
        /// The key is a string chosen by whichever device posted it and Android wants an int, so
        /// the two have to be held together for a later dismissal to find the right row. A hash
        /// would collide eventually and cancel the wrong notification; a counter cannot, and
        /// there is never a large number of them live at once.
        /// </summary>
        private static readonly ConcurrentDictionary<string, int> _ids = new(StringComparer.Ordinal);

        private static int _nextId = 6000;

        /// <summary>Posts a notification that came from another device.</summary>
        public static void Show(MirroredNotification notification, string fromDevice)
        {
            try
            {
                var context = global::Android.App.Application.Context;
                EnsureChannel(context);

                int id = _ids.GetOrAdd(notification.Key, _ => System.Threading.Interlocked.Increment(ref _nextId));

                // Says which app and which device, because on a second phone "WhatsApp" alone is
                // ambiguous - it is also installed here.
                string source = string.IsNullOrEmpty(notification.AppName)
                    ? fromDevice
                    : $"{notification.AppName} on {fromDevice}";

                // Set one at a time rather than chained: every one of these bindings is typed as
                // returning a nullable builder, so a fluent chain is a chain of dereferences the
                // compiler is right to complain about.
                var builder = new NotificationCompat.Builder(context, ChannelId);
                builder.SetSmallIcon(global::Android.Resource.Drawable.StatSysDataBluetooth);
                builder.SetContentTitle(string.IsNullOrEmpty(notification.Title) ? source : notification.Title);
                builder.SetContentText(notification.Text);
                builder.SetSubText(source);
                builder.SetStyle(new NotificationCompat.BigTextStyle().BigText(notification.Text));
                builder.SetWhen(notification.PostedUtc.ToUnixTimeMilliseconds());
                builder.SetShowWhen(true);
                builder.SetAutoCancel(true);
                builder.SetOnlyAlertOnce(true);
                builder.SetPriority((int)NotificationPriority.Default);
                builder.SetDeleteIntent(DismissIntent(context, notification.Key, id));

                var built = builder.Build();
                var manager = NotificationManagerCompat.From(context);
                if (built == null || manager == null) return;

                manager.Notify(id, built);
                Log.Write("Notify", $"Showing a notification from {fromDevice}.");
            }
            catch (Exception ex)
            {
                Log.Write("Notify", "Could not show a notification from the mesh", ex);
            }
        }

        /// <summary>Clears one this device is showing on another device's behalf.</summary>
        public static void Dismiss(string key)
        {
            try
            {
                if (!_ids.TryRemove(key, out int id)) return;

                var manager = NotificationManagerCompat.From(global::Android.App.Application.Context);
                manager?.Cancel(id);
            }
            catch (Exception ex)
            {
                Log.Write("Notify", "Could not clear a notification from the mesh", ex);
            }
        }

        /// <summary>Forgets the mapping without cancelling, for a notification the user swiped.</summary>
        public static void Forget(string key) => _ids.TryRemove(key, out _);

        /// <summary>
        /// Fires when the user swipes the notification away.
        ///
        /// A delete intent rather than the notification listener, because the listener drops this
        /// app's own notifications - which is what stops mirroring looping - so it would never
        /// see the dismissal it needs to pass on.
        /// </summary>
        private static PendingIntent? DismissIntent(Context context, string key, int id)
        {
            var intent = new Intent(context, typeof(MirroredDismissReceiver));
            intent.PutExtra(MirroredDismissReceiver.ExtraKey, key);

            return PendingIntent.GetBroadcast(context, id, intent,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        }

        private static void EnsureChannel(Context context)
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;

            var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
            if (manager == null || manager.GetNotificationChannel(ChannelId) != null) return;

            // Its own channel so it can be silenced without silencing the service notification,
            // and Default rather than High: these already made a sound on the device they came
            // from, and the point is to see them, not to be interrupted twice.
            var channel = new NotificationChannel(ChannelId, "From your other devices", NotificationImportance.Default)
            {
                Description = "Notifications mirrored from another device in your mesh."
            };

            manager.CreateNotificationChannel(channel);
        }
    }

    /// <summary>Passes a swipe-away back to the device the notification came from.</summary>
    [BroadcastReceiver(Enabled = true, Exported = false)]
    public class MirroredDismissReceiver : BroadcastReceiver
    {
        public const string ExtraKey = "dev.meshsync.app.MIRRORED_KEY";

        public override void OnReceive(Context? context, Intent? intent)
        {
            try
            {
                string? key = intent?.GetStringExtra(ExtraKey);
                if (string.IsNullOrEmpty(key)) return;

                MirroredNotificationDisplay.Forget(key!);

                Log.Write("Notify", "Dismissed here; telling the device it came from.");
                _ = SyncManager.SendNotificationDismissAsync(key!);
            }
            catch (Exception ex)
            {
                Log.Write("Notify", "Could not pass on a dismissal", ex);
            }
        }
    }
}
