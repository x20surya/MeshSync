using Android.App;
using Android.Content;
using Android.OS;
using Android.Widget;

namespace AndroidClient.Platforms.Android
{
    [Activity(Label = "Sync Clipboard", Theme = "@android:style/Theme.Translucent.NoTitleBar", Exported = true, ExcludeFromRecents = true, TaskAffinity = "")]
    public class SyncActivity : Activity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            // Don't read clipboard here! We don't have window focus yet.
        }

        public override async void OnWindowFocusChanged(bool hasFocus)
        {
            base.OnWindowFocusChanged(hasFocus);

            if (hasFocus)
            {
                // Wait for the OS to fully settle focus and populate the clipboard buffer
                await System.Threading.Tasks.Task.Delay(300);

                try
                {
                    var clipboard = (ClipboardManager?)GetSystemService(ClipboardService);
                    if (clipboard != null && clipboard.HasPrimaryClip)
                    {
                        var item = clipboard.PrimaryClip?.GetItemAt(0);
                        if (item == null)
                        {
                            Toast.MakeText(this, "Clipboard is completely empty", ToastLength.Short)?.Show();
                            Finish();
                            return;
                        }

                        if (item.Uri != null)
                        {
                            System.Console.WriteLine($"[SyncActivity] Found URI: {item.Uri}");
                            var contentResolver = this.ContentResolver;
                            var type = contentResolver?.GetType(item.Uri);
                            System.Console.WriteLine($"[SyncActivity] Resolved Type: {type}");
                            
                            bool isImage = type != null && type.StartsWith("image/");
                            
                            if (!isImage && clipboard.PrimaryClip.Description != null)
                            {
                                for (int i = 0; i < clipboard.PrimaryClip.Description.MimeTypeCount; i++)
                                {
                                    var mime = clipboard.PrimaryClip.Description.GetMimeType(i);
                                    System.Console.WriteLine($"[SyncActivity] Description MIME: {mime}");
                                    if (mime != null && mime.StartsWith("image/")) isImage = true;
                                }
                            }

                            if (isImage)
                            {
                                try
                                {
                                    System.Console.WriteLine("[SyncActivity] Attempting to open InputStream...");
                                    using var stream = contentResolver?.OpenInputStream(item.Uri);
                                    if (stream != null)
                                    {
                                        using var ms = new System.IO.MemoryStream();
                                        stream.CopyTo(ms);
                                        byte[] imageBytes = ms.ToArray();
                                        System.Console.WriteLine($"[SyncActivity] Read {imageBytes.Length} bytes.");
                                        await SyncManager.SendClipboardImageAsync(imageBytes);
                                        Toast.MakeText(this, "Image Pushed to Laptop!", ToastLength.Short)?.Show();
                                        Finish();
                                        return;
                                    }
                                    else
                                    {
                                        System.Console.WriteLine("[SyncActivity] Stream was null.");
                                    }
                                }
                                catch (System.Exception ex)
                                {
                                    System.Console.WriteLine($"[SyncActivity] Stream Error: {ex.Message}");
                                    Toast.MakeText(this, $"Image Read Error: {ex.Message}", ToastLength.Long)?.Show();
                                    Finish();
                                    return;
                                }
                            }
                            else
                            {
                                System.Console.WriteLine("[SyncActivity] URI is not an image.");
                            }
                        }
                        else
                        {
                            System.Console.WriteLine("[SyncActivity] item.Uri is NULL!");
                        }

                        var text = item.CoerceToText(this)?.ToString();

                        if (!string.IsNullOrEmpty(text))
                        {
                            await SyncManager.SendClipboardAsync(text);
                            Toast.MakeText(this, "Text Pushed to Laptop!", ToastLength.Short)?.Show();
                        }
                        else
                        {
                            Toast.MakeText(this, "Clipboard contains unsupported data", ToastLength.Short)?.Show();
                        }
                    }
                    else
                    {
                        Toast.MakeText(this, "Clipboard is completely empty", ToastLength.Short)?.Show();
                    }
                }
                catch (System.Exception ex)
                {
                    Toast.MakeText(this, "Failed to read clipboard", ToastLength.Short)?.Show();
                    System.Console.WriteLine($"[SyncActivity] Error: {ex.Message}");
                }

                Finish(); // Instantly close now that we are done!
            }
        }
    }
}
