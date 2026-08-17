using System.Collections.ObjectModel;
using CoreLib;
using CoreLib.Diagnostics;

namespace AndroidClient;

public partial class DashboardPage : ContentPage
{
    private enum WarningKind { None, Accessibility, Notifications }

    private readonly ObservableCollection<ActivityRow> _rows = new();
    private IDispatcherTimer? _refresh;
    private WarningKind _warning = WarningKind.None;

    public DashboardPage()
    {
        InitializeComponent();
        ActivityList.ItemsSource = _rows;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        SyncManager.OnConnectionStatusChanged += OnStatusChanged;
        SyncManager.Activity.Changed += OnActivityChanged;

        _ = SyncManager.AutoConnectAsync(true);

        RenderStatus();
        RenderActivity();
        Header.RefreshSubtitle();

        // Keeps relative timestamps and the permission warning honest without user input.
        _refresh = Dispatcher.CreateTimer();
        _refresh.Interval = TimeSpan.FromSeconds(5);
        _refresh.Tick += (_, _) => { RenderStatus(); RenderActivity(); };
        _refresh.Start();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        SyncManager.OnConnectionStatusChanged -= OnStatusChanged;
        SyncManager.Activity.Changed -= OnActivityChanged;
        _refresh?.Stop();
        _refresh = null;
    }

    private void OnStatusChanged(string status) =>
        MainThread.BeginInvokeOnMainThread(RenderStatus);

    private void OnActivityChanged(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() => { RenderActivity(); RenderStatus(); });

    // ──────────────────────────────────── status

    private void RenderStatus()
    {
        bool connected = SyncManager.IsConnected;
        bool paired = SyncManager.IsPaired;
        bool pausedByUser = SyncManager.IsPaused;

        StatusTick.IsVisible = connected && !pausedByUser;
        StatusRing.IsVisible = connected && !pausedByUser;
        StatusDots.IsVisible = !connected || pausedByUser;

        string haloKey = connected && !pausedByUser ? "AccentSoft" : "WarnSoft";
        string inkKey = connected && !pausedByUser ? "Accent" : "Warn";
        StatusHalo.Fill = new SolidColorBrush(Themed(haloKey));
        StatusHeadline.TextColor = Themed(inkKey);

        // Stopping from the notification is sticky, so the app has to both say so and
        // offer the way back - otherwise syncing looks broken with no visible cause.
        SendNowButton.Text = pausedByUser ? "Resume syncing" : "Send clipboard now";

        if (pausedByUser)
        {
            StatusHeadline.Text = "STOPPED";
            StatusDetail.Text = "Syncing is turned off";
            StatusSub.Text = "Tap Resume syncing to turn it back on";
            SentCount.Text = SyncManager.Activity.SentCount.ToString();
            ReceivedCount.Text = SyncManager.Activity.ReceivedCount.ToString();
            RenderWarning();
            return;
        }

        if (connected)
        {
            StatusHeadline.Text = "CONNECTED";
            StatusDetail.Text = SyncManager.MeshName;

            var last = SyncManager.Activity.LastActivityUtc;
            StatusSub.Text = last.HasValue ? $"Last sync {Relative(last.Value)}" : "Ready when you copy something";
        }
        else if (paired)
        {
            StatusHeadline.Text = "RECONNECTING";
            // Named rather than addressed. The address is a hint that changes with the
            // lease; the device is the thing that stays the same.
            StatusDetail.Text = SyncManager.MeshName;
            StatusSub.Text = "Bluetooth needs no network - Wi-Fi is used when there is one";
        }
        else
        {
            StatusHeadline.Text = "NOT PAIRED";
            StatusDetail.Text = "No devices paired yet";
            StatusSub.Text = "Scan the code on another device to start a mesh";
            StatusSub.Text = "";
        }

        SentCount.Text = SyncManager.Activity.SentCount.ToString();
        ReceivedCount.Text = SyncManager.Activity.ReceivedCount.ToString();

        RenderWarning();
    }

    /// <summary>Highest-severity unmet requirement wins, so the user is given one clear next step.</summary>
    private void RenderWarning()
    {
        if (!IsClipboardServiceOn())
        {
            _warning = WarningKind.Accessibility;
            WarningTitle.Text = "Clipboard access is off";
            WarningBody.Text = "Copying on this phone will not sync until you switch it on.";
            WarningAction.Text = "Fix";
            WarningCard.IsVisible = true;
            return;
        }

        if (!AreNotificationsEnabled())
        {
            _warning = WarningKind.Notifications;
            WarningTitle.Text = "Notifications are off";
            WarningBody.Text = "Turn them on to see sync status and the quick Sync button.";
            WarningAction.Text = "Fix";
            WarningCard.IsVisible = true;
            return;
        }

        _warning = WarningKind.None;
        WarningCard.IsVisible = false;
    }

    private static bool AreNotificationsEnabled()
    {
#if ANDROID
        try
        {
            var manager = AndroidX.Core.App.NotificationManagerCompat
                .From(global::Android.App.Application.Context);
            return manager?.AreNotificationsEnabled() ?? true;
        }
        catch
        {
            return true; // never nag on the strength of a failed check
        }
#else
        return true;
#endif
    }

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

    private static Color Themed(string baseKey)
    {
        string key = baseKey + (Application.Current?.RequestedTheme == AppTheme.Dark ? "Dark" : "Light");
        return Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color color
            ? color
            : Colors.Gray;
    }

    private static string Relative(DateTime atUtc)
    {
        var elapsed = DateTime.UtcNow - atUtc;
        if (elapsed.TotalSeconds < 5) return "just now";
        if (elapsed.TotalMinutes < 1) return $"{(int)elapsed.TotalSeconds}s ago";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes}m ago";
        return $"{(int)elapsed.TotalHours}h ago";
    }

    // ──────────────────────────────────── activity

    private void RenderActivity()
    {
        var snapshot = SyncManager.Activity.Snapshot();

        _rows.Clear();
        foreach (var entry in snapshot)
        {
            _rows.Add(new ActivityRow
            {
                Glyph = entry.Kind == SyncItemKind.Image ? "▣" : "⧉",
                Title = string.IsNullOrWhiteSpace(entry.Title) ? "(empty)" : entry.Title,
                Sub = $"{(entry.Direction == SyncDirection.Sent ? "Sent" : "Received")} · {entry.SizeLabel}",
                Age = entry.RelativeAge
            });
        }

        ActivityEmpty.IsVisible = _rows.Count == 0;
        ActivityList.IsVisible = _rows.Count > 0;
    }

    // ──────────────────────────────────── actions

    private async void OnWarningActionClicked(object? sender, EventArgs e)
    {
#if ANDROID
        try
        {
            if (_warning == WarningKind.Notifications)
            {
                // Ask inline first; only fall back to Settings if the prompt is no longer offered.
                if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
                    await Permissions.RequestAsync<Permissions.PostNotifications>() == PermissionStatus.Granted)
                {
                    RenderWarning();
                    return;
                }

                var settings = new global::Android.Content.Intent(
                    global::Android.Provider.Settings.ActionAppNotificationSettings);
                settings.PutExtra(global::Android.Provider.Settings.ExtraAppPackage,
                    global::Android.App.Application.Context.PackageName);
                settings.AddFlags(global::Android.Content.ActivityFlags.NewTask);
                global::Android.App.Application.Context.StartActivity(settings);
                return;
            }

            var intent = new global::Android.Content.Intent(
                global::Android.Provider.Settings.ActionAccessibilitySettings);
            intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            global::Android.App.Application.Context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            Log.Write("Dashboard", "Could not open settings", ex);
        }
#else
        await Task.CompletedTask;
#endif
    }

    private async void OnSendNowClicked(object? sender, EventArgs e)
    {
        if (SyncManager.IsPaused)
        {
            await SyncManager.ResumeAsync();

#if ANDROID
            // Stopping took the foreground service down with it, so resuming has to put it
            // back - otherwise sync would restart with nothing holding it open. Started from
            // here rather than inside ResumeAsync because this is a tap, which is the one
            // context Android reliably permits a foreground service to start from.
            Platforms.Android.SyncForegroundService.Start(global::Android.App.Application.Context);
#endif

            RenderStatus();
            return;
        }

        if (!SyncManager.IsConnected)
        {
            await DisplayAlertAsync("Not connected",
                "Your computer is not reachable right now. Check that both devices are on the same Wi-Fi.", "OK");
            return;
        }

        try
        {
            string? text = await Clipboard.Default.GetTextAsync();
            if (string.IsNullOrEmpty(text))
            {
                await DisplayAlertAsync("Clipboard is empty", "Copy something first, then try again.", "OK");
                return;
            }

            await SyncManager.SendClipboardAsync(text);
        }
        catch (Exception ex)
        {
            Log.Write("Dashboard", "Manual send failed", ex);
            await DisplayAlertAsync("Could not send", ex.Message, "OK");
        }
    }

    private async void OnRepairClicked(object? sender, EventArgs e) =>
        await Shell.Current.GoToAsync("//setup");

    private sealed class ActivityRow
    {
        public string Glyph { get; init; } = "";
        public string Title { get; init; } = "";
        public string Sub { get; init; } = "";
        public string Age { get; init; } = "";
    }
}
