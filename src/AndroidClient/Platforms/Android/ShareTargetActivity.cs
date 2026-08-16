using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using CoreLib.Diagnostics;
using System;
using System.IO;
using System.Threading.Tasks;

namespace AndroidClient.Platforms.Android
{
    /// <summary>
    /// Makes Mesh Sync a target of the system share sheet, for text and for images.
    ///
    /// This complements <see cref="ProcessTextActivity"/> rather than replacing it. The
    /// selection toolbar reserves its first row for Copy / Share / Select all and appends
    /// app actions after them, so "Send to PC" can end up behind the overflow. Share is one
    /// of those reserved first-row buttons, so routing through it is always two taps from a
    /// selection - and unlike the text toolbar it works from any app's share button and
    /// carries images as well as text.
    ///
    /// Android also learns frequently used share targets and promotes them to the top row.
    /// </summary>
    [Activity(
        Label = "Send to PC",
        Theme = "@android:style/Theme.Translucent.NoTitleBar",
        Exported = true,
        ExcludeFromRecents = true,
        NoHistory = true,
        TaskAffinity = "",
        LaunchMode = LaunchMode.SingleTop)]
    [IntentFilter(
        new[] { Intent.ActionSend },
        Categories = new[] { Intent.CategoryDefault },
        DataMimeTypes = new[] { "text/plain", "image/*" })]
    public class ShareTargetActivity : Activity
    {
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(6);

        /// <summary>Ceiling on a shared image, to avoid an out-of-memory kill on a huge file.</summary>
        private const long MaxImageBytes = 48L * 1024 * 1024;

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
                    Notify("Pair with your computer first");
                    return;
                }

                if (!await EnsureConnectedAsync().ConfigureAwait(true))
                {
                    Notify("Your computer is not reachable");
                    return;
                }

                string peer = SyncManager.PeerName ?? "your computer";

                if (await TrySendSharedImageAsync().ConfigureAwait(true))
                {
                    Notify($"Image sent to {peer}");
                    return;
                }

                string? text = Intent?.GetStringExtra(Intent.ExtraText);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    await SyncManager.SendClipboardAsync(text!).ConfigureAwait(true);
                    Notify($"Sent to {peer}");
                    Log.Write("Share", $"Sent {text!.Length} characters from the share sheet.");
                    return;
                }

                Notify("Nothing to send");
            }
            catch (Exception ex)
            {
                Log.Write("Share", "Sending the shared item failed", ex);
                Notify("Could not send");
            }
            finally
            {
                Finish();
            }
        }

        private async Task<bool> TrySendSharedImageAsync()
        {
            try
            {
                var uri = GetStreamExtra();
                if (uri == null) return false;

                var resolver = ContentResolver;
                if (resolver == null) return false;

                // The sharing app grants us read access on the intent, so this needs no
                // storage permission of our own.
                using var stream = resolver.OpenInputStream(uri);
                if (stream == null) return false;

                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms).ConfigureAwait(true);

                if (ms.Length == 0) return false;
                if (ms.Length > MaxImageBytes)
                {
                    Log.Write("Share", $"Refusing a {ms.Length} byte image (over the read limit).");
                    Notify("That image is too large");
                    return true; // handled, just not sent
                }

                byte[] bytes = ms.ToArray();
                Log.Write("Share", $"Sending shared image, {bytes.Length} bytes.");
                await SyncManager.SendClipboardImageAsync(bytes).ConfigureAwait(true);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Share", "Reading the shared image failed", ex);
                return false;
            }
        }

        private global::Android.Net.Uri? GetStreamExtra()
        {
            if (Intent == null) return null;

            // GetParcelableExtra is deprecated in favour of the typed overload on API 33+.
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                return Intent.GetParcelableExtra(Intent.ExtraStream, Java.Lang.Class.FromType(typeof(global::Android.Net.Uri)))
                    as global::Android.Net.Uri;
            }

#pragma warning disable CA1422 // The typed overload does not exist below API 33.
            return Intent.GetParcelableExtra(Intent.ExtraStream) as global::Android.Net.Uri;
#pragma warning restore CA1422
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
            catch (Exception ex) { Log.Write("Share", "Toast failed", ex); }
        }
    }
}
