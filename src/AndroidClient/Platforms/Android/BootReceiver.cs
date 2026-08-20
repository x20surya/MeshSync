using Android.App;
using Android.Content;
using CoreLib.Diagnostics;

namespace AndroidClient.Platforms.Android
{
    /// <summary>
    /// Brings the links back after a restart.
    ///
    /// <para><b>Why this had to be written.</b> Nothing needed it while the accessibility service
    /// existed: Android rebinds an enabled accessibility service on boot, and that service
    /// started everything else. Removing it quietly removed boot persistence too, which would
    /// have shown up as a phone that stopped syncing after a restart until the app was opened by
    /// hand - and would have looked like a bug in the transport rather than a missing
    /// receiver.</para>
    ///
    /// <para>Starting a foreground service from the background is refused on Android 12 and
    /// above, but receiving <c>BOOT_COMPLETED</c> is one of the exemptions, which is exactly what
    /// makes this the right place to do it. A refusal is logged rather than thrown either way -
    /// the app still works when the user next opens it.</para>
    /// </summary>
    [BroadcastReceiver(Enabled = true, Exported = true, DirectBootAware = false)]
    [IntentFilter(new[] { Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced })]
    public class BootReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (context == null) return;

            string action = intent?.Action ?? "";
            if (action != Intent.ActionBootCompleted && action != Intent.ActionMyPackageReplaced) return;

            // Diagnostics need a sink before anything else can report a failure, and on this
            // path nothing else has run yet to install one.
            Log.Sink ??= line => global::Android.Util.Log.Info("MeshSync", line);

            if (!SyncManager.IsPaired)
            {
                Log.Write("Service", "Not starting after boot: nothing is paired yet.");
                return;
            }

            Log.Write("Service", action == Intent.ActionBootCompleted
                ? "The phone restarted; bringing the links back."
                : "The app was updated; bringing the links back.");

            SyncForegroundService.Start(context);
        }
    }
}
