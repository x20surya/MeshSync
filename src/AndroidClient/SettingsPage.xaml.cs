using System.Collections.ObjectModel;
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

        bool advertise = CanAdvertise();
        BluetoothState.Text = advertise
            ? "Granted - this phone can be found without any network"
            : "Not granted - this phone can find others but cannot be found";
        BluetoothButton.Text = advertise ? "Settings" : "Fix";

        PhotosState.Text = "Sends every screenshot you take, automatically";

        DeviceName.Text = SyncManager.LocalDeviceName;
        DeviceFingerprint.Text = security.Identity.ShortFingerprint;

        RenderNotifications();
    }

    // ──────────────────────────────────── notification mirroring

    private readonly ObservableCollection<NotificationAppRow> _notificationApps = new();

    /// <summary>
    /// Two separate things, shown as two: whether Android will tell us about notifications at
    /// all, and which apps we pass on. The grant is Android's to give and the allowlist is the
    /// user's to choose, and conflating them is how an app ends up mirroring everything because
    /// someone tapped Allow once.
    /// </summary>
    private void RenderNotifications()
    {
#if ANDROID
        bool granted = Platforms.Android.NotificationMirrorService.IsGranted();
        bool enabled = Platforms.Android.NotificationMirrorSettings.IsEnabled;

        _suppressToggle = true;
        NotificationsSwitch.IsToggled = enabled && granted;
        _suppressToggle = false;

        NotificationsState.Text = !granted
            ? "Needs notification access before it can mirror anything"
            : enabled
                ? "On - the apps below appear on your other devices"
                : "Off - nothing is being mirrored";

        NotificationsGrantButton.IsVisible = !granted;
        NotificationsAppsSection.IsVisible = granted && enabled;

        if (NotificationAppList.ItemsSource == null) NotificationAppList.ItemsSource = _notificationApps;

        _notificationApps.Clear();

        if (granted && enabled)
        {
            var allowed = Platforms.Android.NotificationMirrorSettings.Allowed();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            // Everything currently notifying, plus anything already allowed, so a choice never
            // vanishes from the list merely because that app happens to be quiet right now.
            foreach (var (package, name) in Platforms.Android.NotificationMirrorService.RecentApps())
            {
                if (!seen.Add(package)) continue;
                _notificationApps.Add(new NotificationAppRow
                {
                    Package = package,
                    Name = name,
                    Allowed = allowed.Contains(package, StringComparer.Ordinal)
                });
            }

            foreach (string package in allowed)
            {
                if (!seen.Add(package)) continue;
                _notificationApps.Add(new NotificationAppRow { Package = package, Name = package, Allowed = true });
            }
        }

        NotificationsAppsEmpty.IsVisible = _notificationApps.Count == 0;
#else
        NotificationsState.Text = "Only available on Android";
        NotificationsSwitch.IsEnabled = false;
        NotificationsGrantButton.IsVisible = false;
        NotificationsAppsSection.IsVisible = false;
#endif
    }

    private void OnNotificationsToggled(object? sender, ToggledEventArgs e)
    {
        if (_suppressToggle) return;

#if ANDROID
        if (e.Value && !Platforms.Android.NotificationMirrorService.IsGranted())
        {
            // Cannot be switched on without the grant, so ask for it rather than leaving a
            // switch that says on while nothing happens.
            _suppressToggle = true;
            NotificationsSwitch.IsToggled = false;
            _suppressToggle = false;

            Platforms.Android.NotificationMirrorService.RequestGrant();
            return;
        }

        Platforms.Android.NotificationMirrorSettings.IsEnabled = e.Value;
        Render();
#endif
    }

    private void OnNotificationsGrantClicked(object? sender, EventArgs e)
    {
#if ANDROID
        Platforms.Android.NotificationMirrorService.RequestGrant();
#endif
    }

    private void OnNotificationAppToggled(object? sender, ToggledEventArgs e)
    {
        if (_suppressToggle) return;
        if ((sender as Switch)?.ClassId is not string package || package.Length == 0) return;

#if ANDROID
        Platforms.Android.NotificationMirrorSettings.SetAllowed(package, e.Value);
        Log.Write("Notify", e.Value ? "An app was added to notification mirroring." : "An app was removed from notification mirroring.");
#endif
    }

    /// <summary>One app in the mirroring allowlist.</summary>
    private sealed class NotificationAppRow
    {
        public string Package { get; init; } = "";
        public string Name { get; init; } = "";
        public bool Allowed { get; init; }
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

    private void OnAppSettingsClicked(object? sender, EventArgs e) => OpenAppSettings();

    // ──────────────────────────────────── platform hops

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
