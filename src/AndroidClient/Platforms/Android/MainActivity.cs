using System.Linq;
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

        // Started from here as well as from the boot receiver, because this is the one moment
        // Android is certain to allow it: starting a foreground service from the background is
        // refused on Android 12 and above, and opening the app is unambiguously the foreground.
        Platforms.Android.SyncForegroundService.Start(this);

        // Always attempt auto-connect when the app is opened
        _ = SyncManager.AutoConnectAsync(true);

        RequestBluetoothPermissions();

        _ = RequestScreenshotPermissionsAsync();
    }

    /// <summary>
    /// Asks for the Bluetooth permissions Android 12 made into runtime grants.
    ///
    /// <para>Declaring them in the manifest is not enough, and the failure is quiet: the
    /// advertiser throws a SecurityException naming the permission, which is logged and
    /// swallowed, so the phone simply never becomes findable and nothing says why. That is
    /// exactly what happened the first time this device tried to advertise - scanning and
    /// connecting had been granted long ago, and advertising had never been needed until the
    /// phone could take the peripheral role.</para>
    ///
    /// <para>Asked for together, because a device that can only scan is a device that can only
    /// ever be the central, and the role rule needs to know which of those is true.</para>
    /// </summary>
    private void RequestBluetoothPermissions()
    {
        try
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(31)) return;

            var wanted = new[]
            {
                global::Android.Manifest.Permission.BluetoothAdvertise,
                global::Android.Manifest.Permission.BluetoothScan,
                global::Android.Manifest.Permission.BluetoothConnect
            };

            var missing = wanted
                .Where(p => CheckSelfPermission(p) != global::Android.Content.PM.Permission.Granted)
                .ToArray();

            if (missing.Length == 0) return;

            CoreLib.Diagnostics.Log.Write("Bluetooth", $"Requesting {missing.Length} Bluetooth permission(s).");
            RequestPermissions(missing, BluetoothPermissionRequest);
        }
        catch (System.Exception ex)
        {
            CoreLib.Diagnostics.Log.Write("Bluetooth", "Could not request Bluetooth permissions", ex);
        }
    }

    private const int BluetoothPermissionRequest = 4801;

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions,
        global::Android.Content.PM.Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        if (requestCode != BluetoothPermissionRequest) return;

        for (int i = 0; i < permissions.Length && i < grantResults.Length; i++)
        {
            bool granted = grantResults[i] == global::Android.Content.PM.Permission.Granted;
            CoreLib.Diagnostics.Log.Write("Bluetooth",
                $"{permissions[i]} {(granted ? "granted" : "refused")}.");
        }

        // Advertising is attempted once per run, so a grant that arrives after that attempt
        // would otherwise not take effect until the next launch.
        SyncManager.RetryBluetoothPeripheral();
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
            string? mesh = uri.GetQueryParameter("mesh");

            if (!string.IsNullOrEmpty(ip) && !string.IsNullOrEmpty(key))
            {
                // Connect via the QR Code deep link!
                _ = SyncManager.ConnectAsync(ip, key, mesh);
                
                // Show a quick visual confirmation
                Android.Widget.Toast.MakeText(this, $"Connecting to {ip}...", Android.Widget.ToastLength.Short)?.Show();
            }
        }
    }
}
