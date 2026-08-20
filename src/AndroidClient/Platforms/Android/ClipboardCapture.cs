using System;
using System.IO;
using System.Threading.Tasks;
using Android.Content;
using CoreLib.Diagnostics;

namespace AndroidClient.Platforms.Android
{
    /// <summary>What came of trying to send the clipboard, in terms a toast can say.</summary>
    public enum ClipboardSendResult
    {
        Sent,
        Empty,
        TooLarge,
        Unreadable
    }

    /// <summary>
    /// Reads the clipboard and sends it.
    ///
    /// <para><b>Why this is not a background service any more.</b> It used to live inside an
    /// accessibility service, because that is the only way Android will let an app read the
    /// clipboard while it is not in front. That worked, and it made the app incompatible with
    /// the phone: UPI and banking apps in India refuse to run at all while any accessibility
    /// service is enabled, since that is the route screen-reading fraud takes. A clipboard tool
    /// that stops you paying for things is not one worth having.</para>
    ///
    /// <para>So capture is user-initiated now, and the caller has to be in the foreground for
    /// the read to return anything - which the Quick Settings tile, the share sheet and the text
    /// selection menu all arrange in their own way. Android shows its "pasted from" notice for
    /// these reads, which is correct: the user did ask.</para>
    /// </summary>
    public static class ClipboardCapture
    {
        /// <summary>Cap on a single clipboard image, to avoid an out-of-memory kill.</summary>
        private const int MaxClipboardImageBytes = 48 * 1024 * 1024;

        /// <summary>What was on the clipboard, lifted out while the reader still had focus.</summary>
        public readonly struct CapturedClip
        {
            public string? Text { get; init; }
            public global::Android.Net.Uri? Uri { get; init; }
            public bool IsEmpty => Text == null && Uri == null;
        }

        /// <summary>
        /// Takes a copy of the clipboard. Must be called while the caller genuinely holds window
        /// focus, not merely while it is starting up.
        ///
        /// <para><b>The rule this exists to obey.</b> Since Android 10 the clipboard may only be
        /// read by the app the user is currently looking at. An activity does not qualify during
        /// <c>OnCreate</c> - focus arrives later, at <c>OnWindowFocusChanged</c> - and a read made
        /// too early does not throw. It returns an empty clip, which is indistinguishable from the
        /// clipboard actually being empty. That silent-empty is why this is a separate call: it
        /// forces the caller to say when it is reading, and the only correct answer is "the moment
        /// I was given focus".</para>
        ///
        /// <para>Reading is also kept apart from sending because sending can take seconds - it may
        /// have to raise a Bluetooth link first - and focus will not survive that wait. Grab first,
        /// send after.</para>
        /// </summary>
        public static CapturedClip Capture(Context context)
        {
            try
            {
                var clipboard = (ClipboardManager?)context.GetSystemService(Context.ClipboardService);
                if (clipboard?.HasPrimaryClip != true)
                {
                    Log.Write("Clipboard", "The clipboard is empty, or this app was not allowed to read it.");
                    return default;
                }

                var clip = clipboard.PrimaryClip;
                if (clip == null || clip.ItemCount == 0) return default;

                var item = clip.GetItemAt(0);
                if (item == null) return default;

                return new CapturedClip
                {
                    Uri = item.Uri,
                    Text = item.CoerceToText(context)?.ToString() is { Length: > 0 } t ? t : null
                };
            }
            catch (Exception ex)
            {
                Log.Write("Clipboard", "Reading the clipboard failed", ex);
                return default;
            }
        }

        /// <summary>Sends a clip captured earlier. Safe to call once focus has been lost.</summary>
        public static async Task<ClipboardSendResult> SendAsync(Context context, CapturedClip clip)
        {
            try
            {
                if (clip.IsEmpty) return ClipboardSendResult.Empty;

                if (clip.Uri != null)
                {
                    var imageResult = await TrySendImageAsync(context, clip.Uri).ConfigureAwait(false);
                    if (imageResult != null) return imageResult.Value;
                }

                if (clip.Text == null) return ClipboardSendResult.Empty;

                Log.Write("Clipboard", $"Sending {clip.Text.Length} characters from the clipboard.");
                await SyncManager.SendClipboardAsync(clip.Text).ConfigureAwait(false);
                return ClipboardSendResult.Sent;
            }
            catch (Exception ex)
            {
                Log.Write("Clipboard", "Sending the clipboard failed", ex);
                return ClipboardSendResult.Unreadable;
            }
        }

        /// <summary>Null when the clip is not an image, so the caller falls through to text.</summary>
        private static async Task<ClipboardSendResult?> TrySendImageAsync(Context context, global::Android.Net.Uri uri)
        {
            try
            {
                var resolver = context.ContentResolver;
                string? mimeType = resolver?.GetType(uri);
                if (mimeType == null || !mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;

                using var stream = resolver!.OpenInputStream(uri);
                if (stream == null) return null;

                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer).ConfigureAwait(false);

                if (buffer.Length == 0) return null;
                if (buffer.Length > MaxClipboardImageBytes)
                {
                    Log.Write("Clipboard", $"Ignoring a {buffer.Length} byte clipboard image (over the read limit).");
                    return ClipboardSendResult.TooLarge;
                }

                byte[] bytes = buffer.ToArray();
                Log.Write("Clipboard", $"Sending a clipboard image, {bytes.Length} bytes.");
                await SyncManager.SendClipboardImageAsync(bytes).ConfigureAwait(false);
                return ClipboardSendResult.Sent;
            }
            catch (Exception ex)
            {
                Log.Write("Clipboard", "Processing a copied image failed", ex);
                return null;
            }
        }

        /// <summary>Something short enough for a toast, and true.</summary>
        public static string Describe(ClipboardSendResult result, string meshName) => result switch
        {
            ClipboardSendResult.Sent => $"Sent to {meshName}",
            ClipboardSendResult.Empty => "Nothing on the clipboard",
            ClipboardSendResult.TooLarge => "That image is too large",
            _ => "Could not read the clipboard"
        };
    }
}
