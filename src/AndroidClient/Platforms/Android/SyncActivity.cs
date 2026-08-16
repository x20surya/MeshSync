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

                        bool foundImage = false;
                        for (int i = 0; i < clipboard.PrimaryClip.ItemCount; i++)
                        {
                            var currentItem = clipboard.PrimaryClip.GetItemAt(i);
                            if (currentItem?.Uri != null)
                            {
                                System.Console.WriteLine($"[SyncActivity] Found URI at index {i}: {currentItem.Uri}");
                                var contentResolver = this.ContentResolver;
                                var type = contentResolver?.GetType(currentItem.Uri);
                                
                                bool isImage = type != null && type.StartsWith("image/");
                                if (!isImage && clipboard.PrimaryClip.Description != null)
                                {
                                    for (int j = 0; j < clipboard.PrimaryClip.Description.MimeTypeCount; j++)
                                    {
                                        var mime = clipboard.PrimaryClip.Description.GetMimeType(j);
                                        if (mime != null && mime.StartsWith("image/")) isImage = true;
                                    }
                                }

                                if (isImage)
                                {
                                    try
                                    {
                                        using var stream = contentResolver?.OpenInputStream(currentItem.Uri);
                                        if (stream != null)
                                        {
                                            using var ms = new System.IO.MemoryStream();
                                            stream.CopyTo(ms);
                                            byte[] imageBytes = ms.ToArray();
                                            await SyncManager.SendClipboardImageAsync(imageBytes);
                                            Toast.MakeText(this, "Image Pushed to Laptop!", ToastLength.Short)?.Show();
                                            foundImage = true;
                                            Finish();
                                            return;
                                        }
                                    }
                                    catch (System.Exception ex)
                                    {
                                        System.Console.WriteLine($"[SyncActivity] Stream Error: {ex.Message}");
                                    }
                                }
                            }
                        }

                        if (!foundImage)
                        {
                            var text = clipboard.PrimaryClip.GetItemAt(0)?.CoerceToText(this)?.ToString();
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
