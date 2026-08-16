using Android.Database;
using Android.Net;
using Android.OS;
using Android.Provider;
using System;
using System.IO;

namespace AndroidClient.Platforms.Android
{
    public class ScreenshotObserver : ContentObserver
    {
        private readonly Android.Content.Context _context;
        private DateTime _lastScreenshotTime = DateTime.MinValue;

        public ScreenshotObserver(Android.Content.Context context, Handler handler) : base(handler)
        {
            _context = context;
        }

        public override async void OnChange(bool selfChange, Uri? uri)
        {
            base.OnChange(selfChange, uri);

            if (uri == null) return;

            try
            {
                var contentResolver = _context.ContentResolver;
                if (contentResolver == null) return;

                string[] projection = {
                    MediaStore.Images.Media.InterfaceConsts.Data,
                    MediaStore.Images.Media.InterfaceConsts.DateAdded
                };

                using var cursor = contentResolver.Query(
                    uri,
                    projection,
                    null,
                    null,
                    MediaStore.Images.Media.InterfaceConsts.DateAdded + " DESC LIMIT 1"
                );

                if (cursor != null && cursor.MoveToFirst())
                {
                    string path = cursor.GetString(cursor.GetColumnIndexOrThrow(MediaStore.Images.Media.InterfaceConsts.Data)) ?? "";
                    if (path.Contains("Screenshots", StringComparison.OrdinalIgnoreCase))
                    {
                        // Prevent duplicate triggers for the same screenshot
                        if ((DateTime.Now - _lastScreenshotTime).TotalSeconds < 2) return;
                        _lastScreenshotTime = DateTime.Now;

                        Console.WriteLine($"[Android] Screenshot detected: {path}");
                        
                        // Wait briefly to ensure file is fully written by the system
                        await System.Threading.Tasks.Task.Delay(500);

                        using var stream = contentResolver.OpenInputStream(uri);
                        if (stream != null)
                        {
                            using var ms = new MemoryStream();
                            stream.CopyTo(ms);
                            byte[] imageBytes = ms.ToArray();
                            Console.WriteLine($"[Android] Pushing screenshot: {imageBytes.Length} bytes");
                            await AndroidClient.SyncManager.SendClipboardImageAsync(imageBytes);
                            
                            // Optional: notify user it pushed
                            var handler = new Handler(Looper.MainLooper!);
                            handler.Post(() => {
                                Android.Widget.Toast.MakeText(_context, "Screenshot beamed to PC!", Android.Widget.ToastLength.Short)?.Show();
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Android] ScreenshotObserver Error: {ex.Message}");
            }
        }
    }
}
