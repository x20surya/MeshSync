using Android.Content;
using Android.Database;
using Android.OS;
using Android.Provider;
using CoreLib.Diagnostics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace AndroidClient.Platforms.Android
{
    /// <summary>
    /// Watches MediaStore for new screenshots and beams them to the paired desktop.
    /// </summary>
    public class ScreenshotObserver : ContentObserver
    {
        /// <summary>How many recently handled images to remember.</summary>
        private const int HistorySize = 32;

        private const int MaxAttempts = 12;
        private const int BaseDelayMs = 120;

        private readonly global::Android.Content.Context _context;
        private readonly Handler _mainHandler;
        private readonly object _gate = new();
        private readonly Queue<long> _handledOrder = new();
        private readonly HashSet<long> _handled = new();

        public ScreenshotObserver(global::Android.Content.Context context, Handler handler) : base(handler)
        {
            _context = context;
            _mainHandler = handler;
        }

        public override void OnChange(bool selfChange, global::Android.Net.Uri? uri)
        {
            base.OnChange(selfChange, uri);
            if (uri == null) return;

            // Deduplicate on the MediaStore row id, not the URI text. One capture produces
            // several notifications and the URI is not spelled the same way each time
            // ("external" vs "external_primary", item vs collection), so string matching let
            // the same screenshot through twice - once read while the file was still being
            // written, and again once it was complete, which is why it arrived as a small
            // truncated image followed by the full one.
            long id = TryGetId(uri);
            if (id < 0) return; // collection-level ping; the per-item one follows

            lock (_gate)
            {
                if (!_handled.Add(id)) return;
                _handledOrder.Enqueue(id);
                while (_handledOrder.Count > HistorySize) _handled.Remove(_handledOrder.Dequeue());
            }

            // OnChange is delivered on the observer's Handler thread; the work below does
            // disk and network I/O, so it must not run there.
            _ = Task.Run(() => ProcessAsync(uri));
        }

        private static long TryGetId(global::Android.Net.Uri uri)
        {
            try
            {
                return ContentUris.ParseId(uri);
            }
            catch
            {
                return -1;
            }
        }

        private async Task ProcessAsync(global::Android.Net.Uri uri)
        {
            try
            {
                var resolver = _context.ContentResolver;
                if (resolver == null) return;

                byte[]? imageBytes = await ReadWhenCompleteAsync(resolver, uri).ConfigureAwait(false);
                if (imageBytes == null || imageBytes.Length == 0) return;

                Log.Write("Screenshot", $"Pushing {imageBytes.Length} bytes.");
                await SyncManager.SendClipboardImageAsync(imageBytes).ConfigureAwait(false);

                if (SyncManager.IsConnected) ShowToast("Screenshot sent");
            }
            catch (Exception ex)
            {
                Log.Write("Screenshot", "Processing failed", ex);
            }
        }

        /// <summary>
        /// Waits for the capture to finish before reading it.
        ///
        /// MediaStore publishes the row as soon as the file is created, so an immediate read
        /// returns whatever has been flushed so far - observed as a 62 byte "screenshot".
        /// The row is only trusted once it is no longer pending and the bytes read match the
        /// size MediaStore reports.
        /// </summary>
        private async Task<byte[]?> ReadWhenCompleteAsync(ContentResolver resolver, global::Android.Net.Uri uri)
        {
            long previousLength = -1;
            bool confirmedScreenshot = false;

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                var meta = QueryMetadata(resolver, uri);
                if (meta == null) return null; // row vanished

                if (!confirmedScreenshot)
                {
                    if (!meta.Value.IsScreenshot) return null;
                    confirmedScreenshot = true;
                    Log.Write("Screenshot", $"Detected {uri}");
                }

                if (meta.Value.IsPending)
                {
                    await Task.Delay(BaseDelayMs * attempt).ConfigureAwait(false);
                    continue;
                }

                byte[]? bytes = TryRead(resolver, uri);

                if (bytes is { Length: > 0 })
                {
                    // MediaStore knows the final size, so this is exact.
                    if (meta.Value.Size > 0 && bytes.Length == meta.Value.Size) return bytes;

                    // Fallback when the size column is unavailable: accept once two
                    // consecutive reads agree, meaning the file stopped growing.
                    if (meta.Value.Size <= 0 && bytes.Length == previousLength) return bytes;

                    previousLength = bytes.Length;
                }

                await Task.Delay(BaseDelayMs * attempt).ConfigureAwait(false);
            }

            Log.Write("Screenshot", "Gave up waiting for the capture to finish being written.");
            return null;
        }

        private readonly record struct ImageMetadata(bool IsScreenshot, bool IsPending, long Size);

        private static ImageMetadata? QueryMetadata(ContentResolver resolver, global::Android.Net.Uri uri)
        {
            try
            {
                bool supportsPending = OperatingSystem.IsAndroidVersionAtLeast(29);

                var columns = new List<string>
                {
                    MediaStore.Images.Media.InterfaceConsts.Data,
                    MediaStore.Images.Media.InterfaceConsts.DisplayName,
                    MediaStore.Images.Media.InterfaceConsts.Size
                };
                if (supportsPending) columns.Add(MediaStore.Images.Media.InterfaceConsts.IsPending);

                // No sort order and no LIMIT: both are rejected for item URIs on Android 11+.
                using var cursor = resolver.Query(uri, columns.ToArray(), null, null, null);
                if (cursor == null || !cursor.MoveToFirst()) return null;

                string path = ReadString(cursor, MediaStore.Images.Media.InterfaceConsts.Data);
                string name = ReadString(cursor, MediaStore.Images.Media.InterfaceConsts.DisplayName);

                // _data is deprecated and can be blank on newer Android, so the file name is
                // checked too - screenshots are named "Screenshot_...".
                bool isScreenshot =
                    path.Contains("Screenshot", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Screenshot", StringComparison.OrdinalIgnoreCase);

                long size = ReadLong(cursor, MediaStore.Images.Media.InterfaceConsts.Size);

                bool isPending = false;
                if (supportsPending)
                {
                    isPending = ReadLong(cursor, MediaStore.Images.Media.InterfaceConsts.IsPending) == 1;
                }

                return new ImageMetadata(isScreenshot, isPending, size);
            }
            catch (Exception ex)
            {
                Log.Write("Screenshot", "Metadata query failed", ex);
                return null;
            }
        }

        private static string ReadString(ICursor cursor, string column)
        {
            int index = cursor.GetColumnIndex(column);
            return index < 0 || cursor.IsNull(index) ? "" : cursor.GetString(index) ?? "";
        }

        private static long ReadLong(ICursor cursor, string column)
        {
            int index = cursor.GetColumnIndex(column);
            return index < 0 || cursor.IsNull(index) ? 0 : cursor.GetLong(index);
        }

        private static byte[]? TryRead(ContentResolver resolver, global::Android.Net.Uri uri)
        {
            try
            {
                using var stream = resolver.OpenInputStream(uri);
                if (stream == null) return null;

                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }
            catch (Exception ex)
            {
                Log.Write("Screenshot", $"Read attempt failed: {ex.Message}");
                return null;
            }
        }

        private void ShowToast(string message)
        {
            try
            {
                // Reuses the observer's existing main-looper handler instead of allocating
                // a new one for every screenshot.
                _mainHandler.Post(() =>
                {
                    try
                    {
                        global::Android.Widget.Toast
                            .MakeText(_context, message, global::Android.Widget.ToastLength.Short)?.Show();
                    }
                    catch { }
                });
            }
            catch (Exception ex)
            {
                Log.Write("Screenshot", "Toast failed", ex);
            }
        }
    }
}
