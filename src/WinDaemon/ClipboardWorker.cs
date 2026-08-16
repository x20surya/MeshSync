using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using CoreLib.Diagnostics;

namespace WinDaemon
{
    /// <summary>
    /// Owns a single dedicated STA thread for every Win32 clipboard interaction.
    ///
    /// Previously each received payload spawned a fresh STA thread and blocked the transport
    /// receive loop on <c>Thread.Join()</c>, and clipboard reads happened inline inside
    /// <c>WndProc</c> behind a <c>Thread.Sleep(50)</c>, stalling the whole message pump on
    /// every copy. Clipboard calls can block for seconds when another process holds the
    /// clipboard lock, so they belong nowhere near the UI thread or the network loop.
    /// </summary>
    public sealed class ClipboardWorker : IDisposable
    {
        private const int MaxAttempts = 5;
        private const int RetryDelayMs = 60;

        private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
        private readonly Thread _thread;
        private volatile bool _disposed;

        public ClipboardWorker()
        {
            _thread = new Thread(Pump)
            {
                IsBackground = true,
                Name = "MeshSync.Clipboard"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        private void Pump()
        {
            try
            {
                foreach (var work in _queue.GetConsumingEnumerable())
                {
                    try { work(); }
                    catch (Exception ex) { Log.Write("Clipboard", "Work item failed", ex); }
                }
            }
            catch (ObjectDisposedException) { /* shutting down */ }
            catch (InvalidOperationException) { /* completed */ }
        }

        private void Post(Action work)
        {
            if (_disposed) return;
            try { _queue.Add(work); }
            catch (Exception ex) { Log.Write("Clipboard", "Could not queue work", ex); }
        }

        /// <summary>Reads the current clipboard contents and reports them, off the message pump.</summary>
        public void CaptureAsync(Action<ClipboardCapture> onCaptured, int settleDelayMs = 60)
        {
            Post(() =>
            {
                // Applications populate the clipboard in stages; a short settle avoids
                // reading a half-published clipboard. It costs nothing here because this
                // is a background thread, not the UI thread.
                if (settleDelayMs > 0) Thread.Sleep(settleDelayMs);

                var capture = WithRetry(() =>
                {
                    if (Clipboard.ContainsImage())
                    {
                        using var img = Clipboard.GetImage();
                        if (img == null) return ClipboardCapture.None;

                        using var ms = new MemoryStream();
                        SaveJpeg(img, ms);
                        return ClipboardCapture.Image(ms.ToArray());
                    }

                    if (Clipboard.ContainsText())
                    {
                        string text = Clipboard.GetText();
                        return string.IsNullOrEmpty(text) ? ClipboardCapture.None : ClipboardCapture.Text(text);
                    }

                    return ClipboardCapture.None;
                }, ClipboardCapture.None);

                if (capture.Kind != ClipboardKind.None) onCaptured(capture);
            });
        }

        /// <summary>Puts text on the clipboard.</summary>
        public void SetText(string text) => Post(() =>
            WithRetry<object?>(() =>
            {
                if (string.IsNullOrEmpty(text)) Clipboard.Clear();
                else Clipboard.SetText(text);
                return null;
            }, null));

        /// <summary>Puts an encoded image on the clipboard.</summary>
        public void SetImage(byte[] encoded) => Post(() =>
            WithRetry<object?>(() =>
            {
                using var ms = new MemoryStream(encoded, writable: false);
                using var decoded = Image.FromStream(ms);
                // GDI+ requires the source stream to outlive an Image created from it.
                // Copying into an independent Bitmap decouples the two so the clipboard
                // never ends up referencing an image backed by a disposed stream.
                using var owned = new Bitmap(decoded);
                Clipboard.SetImage(owned);
                return null;
            }, null));

        /// <summary>
        /// Clipboard APIs throw <see cref="ExternalException"/> whenever another process holds
        /// the clipboard lock, which happens routinely with Office and browsers. The old code
        /// swallowed that in a bare catch, so those copies silently never synced.
        /// </summary>
        private static T WithRetry<T>(Func<T> action, T fallback)
        {
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    return action();
                }
                catch (System.Runtime.InteropServices.ExternalException) when (attempt < MaxAttempts)
                {
                    Thread.Sleep(RetryDelayMs * attempt);
                }
                catch (Exception ex)
                {
                    Log.Write("Clipboard", $"Operation failed on attempt {attempt}", ex);
                    return fallback;
                }
            }

            Log.Write("Clipboard", $"Operation gave up after {MaxAttempts} attempts (clipboard locked by another process).");
            return fallback;
        }

        private static void SaveJpeg(Image image, Stream target)
        {
            // Explicit quality: the default encoder setting produces needlessly large frames
            // for screenshots, and every extra megabyte is one the phone has to receive.
            var codec = GetJpegCodec();
            if (codec == null)
            {
                image.Save(target, ImageFormat.Jpeg);
                return;
            }

            using var parameters = new EncoderParameters(1);
            using var quality = new EncoderParameter(Encoder.Quality, 85L);
            parameters.Param[0] = quality;
            image.Save(target, codec, parameters);
        }

        private static ImageCodecInfo? GetJpegCodec()
        {
            foreach (var codec in ImageCodecInfo.GetImageEncoders())
            {
                if (codec.FormatID == ImageFormat.Jpeg.Guid) return codec;
            }
            return null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _queue.CompleteAdding(); } catch { }
            try { _thread.Join(TimeSpan.FromSeconds(2)); } catch { }
            _queue.Dispose();
        }
    }

    public enum ClipboardKind { None, Text, Image }

    public readonly struct ClipboardCapture
    {
        public ClipboardKind Kind { get; }
        public string? TextValue { get; }
        public byte[]? ImageValue { get; }

        private ClipboardCapture(ClipboardKind kind, string? text, byte[]? image)
        {
            Kind = kind;
            TextValue = text;
            ImageValue = image;
        }

        public static ClipboardCapture None => new(ClipboardKind.None, null, null);
        public static ClipboardCapture Text(string value) => new(ClipboardKind.Text, value, null);
        public static ClipboardCapture Image(byte[] value) => new(ClipboardKind.Image, null, value);
    }
}
