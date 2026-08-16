using Android.App;
using Android.Content;
using CoreLib.Diagnostics;

namespace AndroidClient.Platforms.Android
{
    [BroadcastReceiver(Enabled = true, Exported = false)]
    public class CloseServiceReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (context == null) return;

            // Order matters. PauseAsync records the stop first, so the "Disconnected" status
            // it raises on the way out is ignored by the notification code instead of
            // immediately re-posting the notification this action just removed.
            var pending = GoAsync();

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await SyncManager.PauseAsync().ConfigureAwait(false);

                    var notificationManager =
                        (NotificationManager?)context.GetSystemService(Context.NotificationService);
                    notificationManager?.Cancel(ClipboardAccessibilityService.NotificationId);

                    Log.Write("Service", "User stopped Mesh Sync from the notification.");
                }
                catch (System.Exception ex)
                {
                    Log.Write("Service", "Stopping from the notification failed", ex);
                }
                finally
                {
                    pending?.Finish();
                }
            });
        }
    }
}
