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
                        var text = item?.CoerceToText(this)?.ToString();

                        if (!string.IsNullOrEmpty(text))
                        {
                            await SyncManager.SendClipboardAsync(text);
                            Toast.MakeText(this, "Pushed to Laptop!", ToastLength.Short)?.Show();
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
