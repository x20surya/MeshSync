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

        // Route CoreLib diagnostics into logcat.
        CoreLib.Diagnostics.Log.Sink ??= line => global::Android.Util.Log.Info("MeshSync", line);

        HandleIntent(Intent);

        // Always attempt auto-connect when the app is opened
        _ = SyncManager.AutoConnectAsync(true);

        _ = RequestScreenshotPermissionsAsync();
    }

    private async System.Threading.Tasks.Task RequestScreenshotPermissionsAsync()
    {
        try 
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            {
                await Microsoft.Maui.ApplicationModel.Permissions.RequestAsync<Microsoft.Maui.ApplicationModel.Permissions.Photos>();
            }
            else
            {
                await Microsoft.Maui.ApplicationModel.Permissions.RequestAsync<Microsoft.Maui.ApplicationModel.Permissions.StorageRead>();
            }
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"[Android] Failed to request permissions: {ex.Message}");
        }
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
