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
            // it raises on the way out cannot revive anything this action just took down.
            var pending = GoAsync();

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await SyncManager.PauseAsync().ConfigureAwait(false);

                    // Stopping the service is what removes the notification now. Cancelling it
                    // directly would not: a foreground service's notification belongs to the
                    // service, and the system re-posts it for as long as the service is up.
                    SyncForegroundService.Stop(context);

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
