using CoreLib.Diagnostics;
using CoreLib.Identity;

namespace AndroidClient;

/// <summary>
/// The phone's half of what the desktop keeps in its Settings pane.
///
/// <para>Not a mirror image, because the two platforms grant things differently: Windows has
/// run-on-startup and a theme, and Android has three runtime permissions that decide whether
/// the app can do its job at all. Both show the mesh name and this device's identity.</para>
/// </summary>
public partial class SettingsPage : ContentPage
{
    private bool _suppressToggle;

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        Header.RefreshSubtitle();
        Render();
    }

    private void Render()
    {
        var security = SyncManager.Security;

        MeshNameEntry.Text = security.Peers.MeshName;

        _suppressToggle = true;
        PausedSwitch.IsToggled = !SyncManager.IsPaused;
        _suppressToggle = false;

        PausedSub.Text = SyncManager.IsPaused
            ? "Turned off - nothing is being synced"
            : "Copy on any device and it appears on the others";

        bool clipboard = IsClipboardServiceOn();
        ClipboardState.Text = clipboard
            ? "On - copies on this phone are picked up"
            : "Off - copying here will not sync until you switch it on";
        ClipboardButton.Text = clipboard ? "Settings" : "Turn on";

        bool advertise = CanAdvertise();
        BluetoothState.Text = advertise
            ? "Granted - this phone can be found without any network"
            : "Not granted - this phone can find others but cannot be found";
        BluetoothButton.Text = advertise ? "Settings" : "Fix";

        PhotosState.Text = "Sends every screenshot you take, automatically";

        DeviceName.Text = SyncManager.LocalDeviceName;
        DeviceFingerprint.Text = security.Identity.ShortFingerprint;
    }

    // ──────────────────────────────────── actions

    private void OnMeshNameCommitted(object? sender, EventArgs e)
    {
        string typed = MeshNameEntry.Text?.Trim() ?? "";
        var peers = SyncManager.Security.Peers;

        if (typed == peers.MeshName) return;

        peers.MeshName = typed;

        // Reflect whatever was actually stored - it is trimmed and length-capped there.
        MeshNameEntry.Text = peers.MeshName;
        Header.RefreshSubtitle();
    }

    private async void OnPausedToggled(object? sender, ToggledEventArgs e)
    {
        if (_suppressToggle) return;

        try
        {
            if (e.Value)
            {
                await SyncManager.ResumeAsync();

#if ANDROID
                // Resuming has to put the foreground service back: stopping took it down, and
                // without it the links would restart with nothing holding them open. Started
                // from a tap, which is the one context Android reliably permits it from.
                Platforms.Android.SyncForegroundService.Start(global::Android.App.Application.Context);
#endif
            }
            else
            {
                await SyncManager.PauseAsync();

#if ANDROID
                Platforms.Android.SyncForegroundService.Stop(global::Android.App.Application.Context);
#endif
            }
        }
        catch (Exception ex)
        {
            Log.Write("Settings", "Could not change the paused state", ex);
        }

        Render();
    }

    private void OnClipboardClicked(object? sender, EventArgs e)
    {
        if (IsClipboardServiceOn()) OpenAppSettings();
        else OpenAccessibilitySettings();
    }

    private void OnAppSettingsClicked(object? sender, EventArgs e) => OpenAppSettings();

    // ──────────────────────────────────── platform hops

    private static bool IsClipboardServiceOn()
    {
#if ANDROID
        try
        {
            string? enabled = global::Android.Provider.Settings.Secure.GetString(
                global::Android.App.Application.Context.ContentResolver,
                global::Android.Provider.Settings.Secure.EnabledAccessibilityServices);

            return enabled != null &&
                   enabled.Contains("ClipboardAccessibilityService", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
#else
        return true;
#endif
    }

    /// <summary>
    /// Whether this phone may advertise, which is what decides if it can ever be the peripheral.
    ///
    /// Two separate things have to be true and they fail differently: the radio has to support
    /// it at all, and Android 12 turned the permission into a runtime grant. Reporting them
    /// together is enough for the user, who can only act on the second.
    /// </summary>
    private static bool CanAdvertise()
    {
#if ANDROID
        try
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(31)) return true;

            var context = global::Android.App.Application.Context;
            return context.CheckSelfPermission(global::Android.Manifest.Permission.BluetoothAdvertise)
                   == global::Android.Content.PM.Permission.Granted;
        }
        catch
        {
            return false;
        }
#else
        return false;
#endif
    }

    private static void OpenAccessibilitySettings()
    {
#if ANDROID
        try
        {
            var intent = new global::Android.Content.Intent(
                global::Android.Provider.Settings.ActionAccessibilitySettings);
            intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            global::Android.App.Application.Context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            Log.Write("Settings", "Could not open accessibility settings", ex);
        }
#endif
    }

    private static void OpenAppSettings()
    {
#if ANDROID
        try
        {
            var context = global::Android.App.Application.Context;
            var intent = new global::Android.Content.Intent(
                global::Android.Provider.Settings.ActionApplicationDetailsSettings,
                global::Android.Net.Uri.Parse("package:" + context.PackageName));

            intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            Log.Write("Settings", "Could not open app settings", ex);
        }
#endif
    }
}
