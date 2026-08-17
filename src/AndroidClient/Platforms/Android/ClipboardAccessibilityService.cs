using Android.AccessibilityServices;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Views.Accessibility;
using CoreLib;
using CoreLib.Diagnostics;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AndroidClient.Platforms.Android
{
    [Service(Label = "Universal Clipboard Sync", Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE", Exported = true)]
    [IntentFilter(new[] { "android.accessibilityservice.AccessibilityService" })]
    [MetaData("android.accessibilityservice", Resource = "@xml/accessibility_service_config")]
    public class ClipboardAccessibilityService : AccessibilityService, ClipboardManager.IOnPrimaryClipChangedListener
    {
        /// <summary>Cap on a single clipboard image read, to avoid an out-of-memory kill.</summary>
        private const int MaxClipboardImageBytes = 48 * 1024 * 1024;

        private ClipboardManager? _clipboardManager;
        private ScreenshotObserver? _screenshotObserver;
        private NetworkWatcher? _networkWatcher;
        private ScreenStateWatcher? _screenWatcher;

        protected override void OnServiceConnected()
        {
            base.OnServiceConnected();

            // Route CoreLib diagnostics into logcat so field failures are visible.
            Log.Sink ??= line => global::Android.Util.Log.Info("MeshSync", line);

            // Deliberately not async void: an exception escaping this callback would take
            // the whole accessibility service down.
            try
            {
                _clipboardManager = (ClipboardManager?)GetSystemService(ClipboardService);
                _clipboardManager?.AddPrimaryClipChangedListener(this);

                RegisterScreenshotObserver();

                _networkWatcher = new NetworkWatcher(this);
                _networkWatcher.Start();

                // Decides whether the Wi-Fi link is held open, now that Bluetooth is the
                // standing link rather than the fallback.
                _screenWatcher = new ScreenStateWatcher(this);
                _screenWatcher.Start();

                // The links, and the notification that reports them, belong to the foreground
                // service now. This one is back to what it is actually for - watching the
                // clipboard - rather than doubling as the thing keeping a socket alive, which
                // it was never able to do.
                SyncForegroundService.Start(this);

                Log.Write("Service", "Accessibility service connected.");
            }
            catch (Exception ex)
            {
                Log.Write("Service", "Startup failed", ex);
            }
        }

        private void RegisterScreenshotObserver()
        {
            try
            {
                _screenshotObserver = new ScreenshotObserver(this, new Handler(Looper.MainLooper!));
                ContentResolver?.RegisterContentObserver(
                    global::Android.Provider.MediaStore.Images.Media.ExternalContentUri!,
                    true,
                    _screenshotObserver);
                Log.Write("Service", "Screenshot observer registered.");
            }
            catch (Exception ex)
            {
                Log.Write("Service", "Could not register screenshot observer", ex);
            }
        }

        public void OnPrimaryClipChanged()
        {
            // A copy is proof the user is here, so it is also the cheapest moment to notice
            // the foreground service is gone - killed for memory, or refused at startup - and
            // put it back. It used to re-post a notification here for the same reason, because
            // Android 14 lets a plain ongoing notification be swiped away. A real foreground
            // service's notification cannot be, so this is now about the service itself.
            if (!SyncForegroundService.IsRunning) SyncForegroundService.Start(this);

            // The listener fires on the main thread; hand off immediately so reading the
            // clip and encrypting it never blocks the UI.
            _ = Task.Run(CaptureAndSendClipAsync);
        }

        private async Task CaptureAndSendClipAsync()
        {
            try
            {
                var clipboardManager = _clipboardManager;
                if (clipboardManager?.HasPrimaryClip != true) return;

                var clipData = clipboardManager.PrimaryClip;
                if (clipData == null || clipData.ItemCount == 0) return;

                var item = clipData.GetItemAt(0);
                if (item == null) return;

                var itemUri = item.Uri;
                if (itemUri != null && await TrySendImageAsync(itemUri).ConfigureAwait(false)) return;

                var text = item.CoerceToText(this)?.ToString();
                if (string.IsNullOrEmpty(text)) return;

                Log.Write("Service", $"Captured {text.Length} characters of text.");
                await SyncManager.SendClipboardAsync(text).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write("Service", "Clipboard capture failed", ex);
            }
        }

        private async Task<bool> TrySendImageAsync(global::Android.Net.Uri uri)
        {
            try
            {
                var contentResolver = ContentResolver;
                var mimeType = contentResolver?.GetType(uri);
                if (mimeType == null || !mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return false;

                using var stream = contentResolver!.OpenInputStream(uri);
                if (stream == null) return false;

                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms).ConfigureAwait(false);

                if (ms.Length == 0) return false;
                if (ms.Length > MaxClipboardImageBytes)
                {
                    Log.Write("Service", $"Ignoring {ms.Length} byte clipboard image (over the read limit).");
                    return true;
                }

                byte[] imageBytes = ms.ToArray();
                Log.Write("Service", $"Captured image, {imageBytes.Length} bytes.");
                await SyncManager.SendClipboardImageAsync(imageBytes).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Service", "Processing copied image failed", ex);
                return false;
            }
        }

        public override void OnAccessibilityEvent(AccessibilityEvent? e)
        {
            // Not used: the clipboard listener and MediaStore observer cover both sync paths.
        }

        public override void OnInterrupt() => Log.Write("Service", "Accessibility service interrupted.");

        public override bool OnUnbind(Intent? intent)
        {
            Teardown();
            return base.OnUnbind(intent);
        }

        public override void OnDestroy()
        {
            // OnUnbind is not guaranteed to run on every teardown path, so clean up here too.
            Teardown();
            base.OnDestroy();
        }

        private void Teardown()
        {
            try
            {
                if (_clipboardManager != null)
                {
                    _clipboardManager.RemovePrimaryClipChangedListener(this);
                    _clipboardManager = null;
                }

                if (_screenshotObserver != null)
                {
                    ContentResolver?.UnregisterContentObserver(_screenshotObserver);
                    _screenshotObserver.Dispose();
                    _screenshotObserver = null;
                }

                if (_networkWatcher != null)
                {
                    _networkWatcher.Stop();
                    _networkWatcher.Dispose();
                    _networkWatcher = null;
                }

                if (_screenWatcher != null)
                {
                    _screenWatcher.Stop();
                    _screenWatcher.Dispose();
                    _screenWatcher = null;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Service", "Teardown failed", ex);
            }
        }
    }
}
