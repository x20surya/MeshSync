using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace AndroidClient;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(new[] { Intent.ActionView },
              Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
              DataScheme = "meshsync", DataHost = "pair")]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        HandleIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleIntent(intent);
    }

    private void HandleIntent(Intent? intent)
    {
        if (intent?.Action == Intent.ActionView && intent.Data != null)
        {
            var uri = intent.Data;
            string? ip = uri.GetQueryParameter("ip");
            string? key = uri.GetQueryParameter("key");

            if (!string.IsNullOrEmpty(ip) && !string.IsNullOrEmpty(key))
            {
                // Connect via the QR Code deep link!
                _ = SyncManager.ConnectAsync(ip, key);
                
                // Show a quick visual confirmation
                Android.Widget.Toast.MakeText(this, $"Connecting to {ip}...", Android.Widget.ToastLength.Short)?.Show();
            }
        }
    }
}
