using Android.App;
using Android.Content;

namespace AndroidClient.Platforms.Android
{
    [BroadcastReceiver(Enabled = true, Exported = false)]
    public class CloseServiceReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (context != null)
            {
                var notificationManager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
                notificationManager?.Cancel(1001);

                _ = SyncManager.DisconnectAsync();
                System.Console.WriteLine("[Android] User stopped Mesh Sync via Notification.");
            }
        }
    }
}
