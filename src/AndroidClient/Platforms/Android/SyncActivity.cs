using Android.App;
using Android.Content;
using Android.OS;
using Android.Widget;
using CoreLib.Diagnostics;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AndroidClient.Platforms.Android
{
    /// <summary>
    /// Invisible activity launched from the persistent notification. Reading the clipboard
    /// requires window focus on Android 10+, so the work happens once focus arrives.
    /// </summary>
    [Activity(Label = "Sync Clipboard", Theme = "@android:style/Theme.Translucent.NoTitleBar", Exported = true, ExcludeFromRecents = true, TaskAffinity = "")]
    public class SyncActivity : Activity
    {
        private const int MaxImageBytes = 48 * 1024 * 1024;

        private int _handled;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            // Deliberately empty: there is no window focus yet, so the clipboard is unreadable.
        }

        public override void OnWindowFocusChanged(bool hasFocus)
        {
            base.OnWindowFocusChanged(hasFocus);
            if (!hasFocus) return;

            // Focus can be granted more than once; only run the sync for the first.
            if (Interlocked.Exchange(ref _handled, 1) != 0) return;

            _ = SyncClipboardAsync();
        }

        private async Task SyncClipboardAsync()
        {
            string message;

            try
            {
                // Let the OS settle focus before reading.
                await Task.Delay(250).ConfigureAwait(true);

                var clipboard = (ClipboardManager?)GetSystemService(ClipboardService);
                var clip = clipboard?.PrimaryClip;

                if (clipboard?.HasPrimaryClip != true || clip == null || clip.ItemCount == 0)
                {
                    message = "Clipboard is empty";
                }
                else if (await TrySendImageAsync(clip).ConfigureAwait(true))
                {
                    message = "Image sent";
                }
                else
                {
                    var text = clip.GetItemAt(0)?.CoerceToText(this)?.ToString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        await SyncManager.SendClipboardAsync(text).ConfigureAwait(true);
                        message = "Text sent";
                    }
                    else
                    {
                        message = "Clipboard contains unsupported data";
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("SyncActivity", "Clipboard sync failed", ex);
                message = "Failed to read clipboard";
            }

            try { Toast.MakeText(this, message, ToastLength.Short)?.Show(); } catch { }
            Finish();
        }

        private async Task<bool> TrySendImageAsync(ClipData clip)
        {
            for (int i = 0; i < clip.ItemCount; i++)
            {
                var uri = clip.GetItemAt(i)?.Uri;
                if (uri == null) continue;

                try
                {
                    var contentResolver = ContentResolver;
                    if (contentResolver == null) continue;

                    bool isImage = contentResolver.GetType(uri)?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;

                    var description = clip.Description;
                    if (!isImage && description != null)
                    {
                        for (int j = 0; j < description.MimeTypeCount && !isImage; j++)
                        {
                            isImage = description.GetMimeType(j)?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
                        }
                    }

                    if (!isImage) continue;

                    using var stream = contentResolver.OpenInputStream(uri);
                    if (stream == null) continue;

                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms).ConfigureAwait(true);

                    if (ms.Length == 0) continue;
                    if (ms.Length > MaxImageBytes)
                    {
                        Log.Write("SyncActivity", $"Skipping {ms.Length} byte image (over the read limit).");
                        continue;
                    }

                    await SyncManager.SendClipboardImageAsync(ms.ToArray()).ConfigureAwait(true);
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Write("SyncActivity", $"Reading clipboard image at index {i} failed", ex);
                }
            }

            return false;
        }
    }
}
