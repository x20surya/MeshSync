using Android.AccessibilityServices;
using Android.App;
using Android.Content;
using Android.Views.Accessibility;
using System;

namespace AndroidClient.Platforms.Android
{
    [Service(Label = "Universal Clipboard Sync", Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE", Exported = true)]
    [IntentFilter(new[] { "android.accessibilityservice.AccessibilityService" })]
    [MetaData("android.accessibilityservice", Resource = "@xml/accessibility_service_config")]
    public class ClipboardAccessibilityService : AccessibilityService
    {
        private ClipboardManager? _clipboardManager;

        protected override void OnServiceConnected()
        {
            base.OnServiceConnected();
            
            // Get the system clipboard service
            _clipboardManager = (ClipboardManager?)GetSystemService(ClipboardService);
            
            if (_clipboardManager != null)
            {
                _clipboardManager.PrimaryClipChanged += OnPrimaryClipChanged;
                Console.WriteLine("[Android] Clipboard Accessibility Service Connected! Listening for copies...");
            }
        }

        private void OnPrimaryClipChanged(object? sender, EventArgs e)
        {
            if (_clipboardManager?.HasPrimaryClip == true)
            {
                var clipData = _clipboardManager.PrimaryClip;
                if (clipData != null && clipData.ItemCount > 0)
                {
                    var item = clipData.GetItemAt(0);
                    var text = item?.Text;
                    
                    if (!string.IsNullOrEmpty(text))
                    {
                        var preview = text.Length > 20 ? text.Substring(0, 20) + "..." : text;
                        Console.WriteLine($"[Android] Copied text captured: {preview}");
                        
                        // TODO: Encrypt using CoreLib and broadcast over BLE/Wi-Fi!
                    }
                }
            }
        }

        public override void OnAccessibilityEvent(AccessibilityEvent? e)
        {
            // Optional: Can be used to inspect text fields or UI changes if clipboard manager fails on future OS versions
        }

        public override void OnInterrupt()
        {
            Console.WriteLine("[Android] Clipboard Accessibility Service Interrupted");
        }

        public override bool OnUnbind(Intent? intent)
        {
            if (_clipboardManager != null)
            {
                _clipboardManager.PrimaryClipChanged -= OnPrimaryClipChanged;
            }
            return base.OnUnbind(intent);
        }
    }
}
