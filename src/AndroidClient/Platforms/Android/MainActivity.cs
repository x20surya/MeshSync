using CoreLib.Identity;
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
    }

    /// <summary>
    /// Routes a runtime permission answer to MAUI, which is what asks for all of them now.
    ///
    /// <para>This used to fire three Bluetooth requests and a photo request straight from
    /// <c>OnCreate</c>: two system dialogs stacked on the splash screen before the user had seen
    /// a word of explanation, and a refusal quietly cost radio pairing and screenshot sync with
    /// nothing anywhere to say so. Both now belong to the step of the wizard that explains them -
    /// see <see cref="AndroidClient.Platforms.Android.AppPermissions"/>.</para>
    /// </summary>
    public override void OnRequestPermissionsResult(int requestCode, string[] permissions,
        global::Android.Content.PM.Permission[] grantResults)
    {
        Microsoft.Maui.ApplicationModel.Platform.OnRequestPermissionsResult(
            requestCode, permissions, grantResults);

        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleIntent(intent);
    }

    /// <summary>
    /// Pairs from a <c>meshsync://</c> link, which is what a code scanned outside this app
    /// produces.
    ///
    /// <para>This used to read <c>ip</c> and <c>key</c> straight out of the URI and announce
    /// "Connecting to..." before either had been looked at, so a damaged or foreign code
    /// produced a link that connected and then failed every decryption, with a toast saying it
    /// was working. It goes through the same validator the scanner uses now, and says what is
    /// wrong when something is.</para>
    /// </summary>
    private void HandleIntent(Intent? intent)
    {
        if (intent?.Action != Intent.ActionView || intent.Data == null) return;

        if (!PairingCode.TryParse(intent.Data.ToString(), out var code, out string error))
        {
            CoreLib.Diagnostics.Log.Write("Pairing", $"Refused a pairing link: {error}");
            Android.Widget.Toast.MakeText(this, error, Android.Widget.ToastLength.Long)?.Show();
            return;
        }

        _ = SyncManager.ConnectAsync(code!);

        Android.Widget.Toast.MakeText(this,
            "Pairing - check the code on your other device.",
            Android.Widget.ToastLength.Short)?.Show();
    }
}
