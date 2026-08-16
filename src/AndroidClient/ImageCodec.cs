using System;
using CoreLib.Diagnostics;

#if ANDROID
using Android.Graphics;
#endif

namespace AndroidClient
{
    /// <summary>
    /// Downscales and re-encodes images before they go on the wire.
    ///
    /// Screenshots are captured as full-resolution PNG, so the client was pushing several
    /// megabytes per beam even though the architecture calls for JPEG/WEBP compression.
    /// Re-encoding typically cuts a phone screenshot by 80-95%, which is the difference
    /// between an instant paste and a multi-second stall on a busy Wi-Fi network.
    /// </summary>
    public static class ImageCodec
    {
        /// <summary>Longest edge, in pixels, kept after downscaling.</summary>
        public const int MaxDimension = 2560;

        /// <summary>Images already smaller than this are passed through untouched.</summary>
        public const int PassThroughBytes = 256 * 1024;

        public const int JpegQuality = 85;

        public static byte[] CompressForTransport(byte[] original)
        {
            if (original == null || original.Length == 0) return Array.Empty<byte>();
            if (original.Length <= PassThroughBytes) return original;

#if ANDROID
            try
            {
                // Bounds-only pass: reads the header without allocating the pixel buffer.
                var boundsOptions = new BitmapFactory.Options { InJustDecodeBounds = true };
                BitmapFactory.DecodeByteArray(original, 0, original.Length, boundsOptions);

                int width = boundsOptions.OutWidth;
                int height = boundsOptions.OutHeight;
                if (width <= 0 || height <= 0) return original;

                var decodeOptions = new BitmapFactory.Options
                {
                    InSampleSize = CalculateSampleSize(width, height, MaxDimension),
                    InPreferredConfig = Bitmap.Config.Argb8888
                };

                using var bitmap = BitmapFactory.DecodeByteArray(original, 0, original.Length, decodeOptions);
                if (bitmap == null) return original;

                using var output = new System.IO.MemoryStream();
                bool ok = bitmap.Compress(Bitmap.CompressFormat.Jpeg!, JpegQuality, output);

                // Bitmap holds native memory that the GC does not account for; releasing it
                // eagerly keeps large screenshots from stacking up against the heap limit.
                bitmap.Recycle();

                if (!ok) return original;

                byte[] compressed = output.ToArray();
                return compressed.Length > 0 && compressed.Length < original.Length ? compressed : original;
            }
            catch (Exception ex)
            {
                Log.Write("ImageCodec", "Recompression failed, sending original", ex);
                return original;
            }
#else
            return original;
#endif
        }

        /// <summary>
        /// Largest power-of-two subsample that keeps both edges within <paramref name="maxDimension"/>.
        /// Android decodes directly at this reduced size, so the full-resolution bitmap is
        /// never materialised in memory.
        /// </summary>
        internal static int CalculateSampleSize(int width, int height, int maxDimension)
        {
            int sampleSize = 1;
            while (width / (sampleSize * 2) >= maxDimension || height / (sampleSize * 2) >= maxDimension)
            {
                sampleSize *= 2;
                if (sampleSize >= 16) break;
            }
            return sampleSize;
        }
    }
}
