using System;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using CoreLib.Diagnostics;

namespace AndroidClient.Platforms.Android
{
    /// <summary>
    /// Sends the clipboard, then gets out of the way.
    ///
    /// <para><b>Why an activity at all.</b> Android only lets an app read the clipboard while it
    /// has focus. A Quick Settings tile is a service and has none, so the tile launches this: it
    /// comes to the front for as long as it takes to read one clip, and finishes. Translucent
    /// and excluded from recents, so what the user sees is their own screen with a toast on it.
    /// </para>
    ///
    /// <para>This is the closest thing to the automatic capture that the accessibility service
    /// used to do, and the difference is one tap. That is the price of not being an accessibility
    /// service, and it is worth paying - banking and UPI apps refuse to run while one is
    /// enabled.</para>
    /// </summary>
    [Activity(
        Label = "Send clipboard",
        Theme = "@android:style/Theme.Translucent.NoTitleBar",
        Exported = true,
        ExcludeFromRecents = true,
        NoHistory = true,
        TaskAffinity = "",
        LaunchMode = LaunchMode.SingleTop)]
    public class SendClipboardActivity : Activity
    {
        /// <summary>How long to wait for a link before giving up on this tap.</summary>
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(6);

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            _ = HandleAsync();
        }

        private async Task HandleAsync()
        {
            try
            {
                if (SyncManager.IsPaused)
                {
                    Notify("Mesh Sync is stopped - open the app to resume");
                    return;
                }

                if (!SyncManager.IsPaired)
                {
                    Notify("Pair a device first");
                    return;
                }

                if (!await EnsureConnectedAsync().ConfigureAwait(true))
                {
                    Notify("No devices in range");
                    return;
                }

                var result = await ClipboardCapture.SendCurrentClipAsync(this).ConfigureAwait(true);
                Notify(ClipboardCapture.Describe(result, SyncManager.MeshName));
            }
            catch (Exception ex)
            {
                Log.Write("Clipboard", "Sending the clipboard failed", ex);
                Notify("Could not send");
            }
            finally
            {
                Finish();
            }
        }

        private static async Task<bool> EnsureConnectedAsync()
        {
            if (SyncManager.IsConnected) return true;

            await SyncManager.AutoConnectAsync(isUserInitiated: true).ConfigureAwait(true);

            var deadline = DateTime.UtcNow + ConnectTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (SyncManager.IsConnected) return true;
                await Task.Delay(150).ConfigureAwait(true);
            }

            return SyncManager.IsConnected;
        }

        private void Notify(string message)
        {
            try { Toast.MakeText(this, message, ToastLength.Short)?.Show(); }
            catch (Exception ex) { Log.Write("Clipboard", "Toast failed", ex); }
        }
    }
}
