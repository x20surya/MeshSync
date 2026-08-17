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
    /// app actions after them, so "Send to my devices" can end up behind the overflow. Share is one
    /// of those reserved first-row buttons, so routing through it is always two taps from a
    /// selection - and unlike the text toolbar it works from any app's share button and
    /// carries images as well as text.
    ///
    /// Android also learns frequently used share targets and promotes them to the top row.
    /// </summary>
    [Activity(
        Label = "Send to my devices",
        Theme = "@android:style/Theme.Translucent.NoTitleBar",
        Exported = true,
        ExcludeFromRecents = true,
        NoHistory = true,
        TaskAffinity = "",
        LaunchMode = LaunchMode.SingleTop)]
    [IntentFilter(
        new[] { Intent.ActionSend },
        Categories = new[] { Intent.CategoryDefault },
        // */* so anything shareable can be sent as a file. Text and images keep their own
        // entries because they take the clipboard path rather than the file one, and listing
        // them explicitly is what makes Android rank this target properly for them.
        DataMimeTypes = new[] { "text/plain", "image/*", "*/*" })]
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
                    Notify("Pair a device first");
                    return;
                }

                if (!await EnsureConnectedAsync().ConfigureAwait(true))
                {
                    Notify("Your computer is not reachable");
                    return;
                }

                string peer = SyncManager.MeshName;

                // An image goes on the clipboard, so it can be pasted straight into whatever the
                // user is doing. Anything else is a file, and belongs in Downloads rather than
                // on a clipboard that cannot hold it.
                if (IsImageShare() && await TrySendSharedImageAsync().ConfigureAwait(true))
                {
                    Notify($"Image sent to {peer}");
                    return;
                }

                string? text = Intent?.GetStringExtra(Intent.ExtraText);
                if (!string.IsNullOrWhiteSpace(text) && GetStreamExtra() == null)
                {
                    await SyncManager.SendClipboardAsync(text!).ConfigureAwait(true);
                    Notify($"Sent to {peer}");
                    Log.Write("Share", $"Sent {text!.Length} characters from the share sheet.");
                    return;
                }

                if (await TrySendSharedFileAsync(peer).ConfigureAwait(true)) return;

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

        private bool IsImageShare() => Intent?.Type?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;

        /// <summary>
        /// Copies whatever was shared into the cache and sends it as a file.
        ///
        /// <para>The copy is unavoidable. The sharing app grants read access on the intent and
        /// nothing else, so there is no path to hand the transfer - only a stream that is valid
        /// while this activity lives, which is not long enough to send a video over. Copying it
        /// first means the transfer owns its own bytes and the activity can finish.</para>
        /// </summary>
        private async Task<bool> TrySendSharedFileAsync(string peer)
        {
            string? staged = null;

            try
            {
                var uri = GetStreamExtra();
                if (uri == null) return false;

                var resolver = ContentResolver;
                if (resolver == null) return false;

                string name = ResolveName(uri);

                var cache = new Java.IO.File(CacheDir, "outgoing");
                if (!cache.Exists()) cache.Mkdirs();

                staged = Path.Combine(cache.AbsolutePath!, $"{Guid.NewGuid():N}-{name}");

                using (var input = resolver.OpenInputStream(uri))
                {
                    if (input == null) return false;

                    using var output = File.Create(staged);
                    await input.CopyToAsync(output).ConfigureAwait(true);
                }

                var staging = new FileInfo(staged);
                if (staging.Length == 0)
                {
                    Log.Write("Share", $"\"{name}\" was empty; nothing to send.");
                    return false;
                }

                Notify($"Sending {name}…");
                Log.Write("Share", $"Sending shared file \"{name}\", {staging.Length} bytes.");

                bool sent = await SyncManager.SendFileAsync(staged).ConfigureAwait(true);
                Notify(sent ? $"{name} sent to {peer}" : $"Could not send {name}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Share", "Sending the shared file failed", ex);
                Notify("Could not send that file");
                return true;
            }
            finally
            {
                if (staged != null)
                {
                    try { File.Delete(staged); } catch { }
                }
            }
        }

        /// <summary>
        /// Asks the provider what the thing is called.
        ///
        /// A content URI carries no filename - the last path segment is usually a row id - so
        /// the display name has to be queried. Falling back to the segment gives something
        /// rather than nothing when the provider will not say.
        /// </summary>
        private string ResolveName(global::Android.Net.Uri uri)
        {
            try
            {
                using var cursor = ContentResolver?.Query(uri, null, null, null, null);
                if (cursor != null && cursor.MoveToFirst())
                {
                    int column = cursor.GetColumnIndex(global::Android.Provider.IOpenableColumns.DisplayName);
                    if (column >= 0)
                    {
                        string? name = cursor.GetString(column);
                        if (!string.IsNullOrWhiteSpace(name)) return name!;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Share", "Could not read the shared file's name", ex);
            }

            return uri.LastPathSegment ?? "shared-file";
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
