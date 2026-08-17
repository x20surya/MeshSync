using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using CoreLib.Diagnostics;
using System;
using System.Threading.Tasks;

namespace AndroidClient.Platforms.Android
{
    /// <summary>
    /// Adds "Send to my devices" to the text selection toolbar that appears alongside
    /// Copy / Paste / Select all, in any app.
    ///
    /// Android offers every activity with a PROCESS_TEXT filter as an item in that toolbar,
    /// so this needs no permissions and, unlike the clipboard route, never overwrites what
    /// the user already had copied. It also works without the accessibility service, which
    /// makes it the one sync path that keeps working if the user declines that permission.
    ///
    /// No result is returned, so the selected text is left exactly as it was.
    /// </summary>
    [Activity(
        Label = "Send to my devices",
        Theme = "@android:style/Theme.Translucent.NoTitleBar",
        Exported = true,
        ExcludeFromRecents = true,
        NoHistory = true,
        TaskAffinity = "",
        LaunchMode = LaunchMode.SingleTop)]
    // Position in the selection toolbar is not ours to choose. The framework reserves the
    // first row for Copy / Share / Select all and appends every app action after them, so
    // this item can land behind the overflow when another app (Gemini) also claims the menu.
    // android:priority does not help - the system clamps it to 0 for non-system apps, which
    // was confirmed on device. ShareTargetActivity exists to cover that case, because Share
    // is one of the reserved first-row buttons.
    [IntentFilter(
        new[] { Intent.ActionProcessText },
        Categories = new[] { Intent.CategoryDefault },
        DataMimeType = "text/plain")]
    public class ProcessTextActivity : Activity
    {
        /// <summary>How long a cold start is given to reach the computer before giving up.</summary>
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
                string? text = ExtractSelectedText();
                if (string.IsNullOrWhiteSpace(text))
                {
                    Notify("Nothing selected");
                    return;
                }

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
                    Notify("Your computer is not reachable");
                    return;
                }

                await SyncManager.SendClipboardAsync(text!).ConfigureAwait(true);

                string peer = SyncManager.PeerName ?? "your devices";
                Notify($"Sent to {peer}");
                Log.Write("ProcessText", $"Sent {text!.Length} characters from the selection toolbar.");
            }
            catch (Exception ex)
            {
                Log.Write("ProcessText", "Sending the selection failed", ex);
                Notify("Could not send");
            }
            finally
            {
                // Finish without a result so the host app leaves the selection untouched.
                Finish();
            }
        }

        private string? ExtractSelectedText()
        {
            // The extra is a CharSequence; reading it as a string alone returns null for the
            // styled text that browsers and editors hand over.
            var sequence = Intent?.GetCharSequenceExtra(Intent.ExtraProcessText);
            if (sequence != null) return sequence.ToString();

            return Intent?.GetStringExtra(Intent.ExtraProcessText);
        }

        /// <summary>
        /// Normally the accessibility service already holds the connection and this returns
        /// at once. When the app was not running at all, this is a cold start, so give the
        /// reconnect loop a bounded moment to reach the computer.
        /// </summary>
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
            catch (Exception ex) { Log.Write("ProcessText", "Toast failed", ex); }
        }
    }
}
