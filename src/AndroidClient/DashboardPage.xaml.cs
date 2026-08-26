using System.Collections.ObjectModel;
using CoreLib;
using CoreLib.Diagnostics;

namespace AndroidClient;

public partial class DashboardPage : ContentPage
{
    private readonly ObservableCollection<ActivityRow> _rows = new();
    private IDispatcherTimer? _refresh;

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

    /// <summary>
    /// What is worth saying on the way past, one thing at a time.
    ///
    /// <para>There used to be a warning here about clipboard access being off, because the app
    /// watched the clipboard through an accessibility service. Nothing watches it now, so there
    /// is nothing to warn about - sending from this phone is something the user does rather than
    /// something that fails quietly.</para>
    ///
    /// <para><b>This is also the only route to the new setup steps for anyone already using the
    /// app.</b> The wizard runs once and stores a flag, so every existing install would otherwise
    /// never be offered notification mirroring or the battery exemption. Re-running a wizard on
    /// somebody already set up is worse than the gap it closes, so the offer is made here
    /// instead - one card, in priority order, and the optional ones dismissible for good.</para>
    /// </summary>
    private void RenderWarning()
    {
        _warning = NextWarning();

        switch (_warning)
        {
            case Warning.Notifications:
                WarningTitle.Text = "Notifications are off";
                WarningBody.Text = "Turn them on to see sync status and the quick Sync button.";
                WarningAction.Text = "Fix";
                WarningDismiss.IsVisible = false;
                break;

            case Warning.Mirroring:
                WarningTitle.Text = "Mirror your notifications";
                WarningBody.Text = "Messages and calls can appear on your computer, and be answered from there.";
                WarningAction.Text = "Turn on";
                WarningDismiss.IsVisible = true;
                break;

            case Warning.Battery:
                WarningTitle.Text = "Android may stop Mesh Sync";
                WarningBody.Text = "Letting it run in the background keeps syncing working while the phone is idle.";
                WarningAction.Text = "Allow";
                WarningDismiss.IsVisible = true;
                break;
        }

        WarningCard.IsVisible = _warning != Warning.None;
    }

    /// <summary>What the warning card is currently offering.</summary>
    private enum Warning { None, Notifications, Mirroring, Battery }

    private Warning _warning = Warning.None;

    /// <summary>
    /// The most important outstanding thing, or none.
    ///
    /// Notifications first, because without them the app is invisible while it runs; then
    /// mirroring, which is a whole feature nobody is otherwise offered; then the battery
    /// exemption, which only degrades reliability.
    /// </summary>
    private static Warning NextWarning()
    {
#if ANDROID
        if (!AreNotificationsEnabled()) return Warning.Notifications;

        if (!Platforms.Android.NotificationMirrorService.IsGranted() && !IsDismissed(DismissedMirroring))
            return Warning.Mirroring;

        if (Platforms.Android.AppPermissions.IsBatteryOptimised() && !IsDismissed(DismissedBattery))
            return Warning.Battery;
#endif
        return Warning.None;
    }

    private const string DismissedMirroring = "DismissedMirroringPrompt";
    private const string DismissedBattery = "DismissedBatteryPrompt";

    private static bool IsDismissed(string key)
    {
#if ANDROID
        try
        {
            var prefs = global::Android.App.Application.Context
                .GetSharedPreferences("SyncPrefs", global::Android.Content.FileCreationMode.Private);
            return prefs?.GetBoolean(key, false) ?? false;
        }
        catch
        {
            return false;
        }
#else
        return true;
#endif
    }

    private static void Dismiss(string key)
    {
#if ANDROID
        try
        {
            var prefs = global::Android.App.Application.Context
                .GetSharedPreferences("SyncPrefs", global::Android.Content.FileCreationMode.Private);
            prefs?.Edit()?.PutBoolean(key, true)?.Apply();
        }
        catch (Exception ex)
        {
            Log.Write("Dashboard", "Could not remember a dismissed prompt", ex);
        }
#endif
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
                Glyph = entry.Kind switch
                {
                    SyncItemKind.Image => "▣",
                    SyncItemKind.File => "⭳",
                    _ => "⧉"
                },
                Title = string.IsNullOrWhiteSpace(entry.Title) ? "(empty)" : entry.Title,
                Sub = $"{(entry.Direction == SyncDirection.Sent ? "Sent" : "Received")} · {entry.SizeLabel}",
                Age = entry.RelativeAge,
                Location = entry.Location
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
            switch (_warning)
            {
                case Warning.Notifications:
                    // Ask inline first; only fall back to Settings if the prompt is no longer
                    // offered, which is what Android does once it has been refused twice.
                    if (await Platforms.Android.AppPermissions.RequestPostNotificationsAsync()
                        == Platforms.Android.AppPermissions.Outcome.Granted)
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

                case Warning.Mirroring:
                    bool go = await DisplayAlertAsync("Notification access",
                        "Android asks for this in its own settings. Find Mesh Sync on the screen that opens and turn it on - then come straight back.",
                        "Open settings", "Not now");

                    if (go) Platforms.Android.NotificationMirrorService.RequestGrant();
                    return;

                case Warning.Battery:
                    Platforms.Android.AppPermissions.RequestBatteryExemption();
                    return;
            }
        }
        catch (Exception ex)
        {
            Log.Write("Dashboard", "Could not act on the warning", ex);
        }
#else
        await Task.CompletedTask;
#endif
    }

    /// <summary>Remembered, so a refusal is not re-asked on every visit to the dashboard.</summary>
    private void OnWarningDismissClicked(object? sender, EventArgs e)
    {
        switch (_warning)
        {
            case Warning.Mirroring: Dismiss(DismissedMirroring); break;
            case Warning.Battery: Dismiss(DismissedBattery); break;
        }

        RenderWarning();
    }

    /// <summary>
    /// Picks a file and sends it to the mesh.
    ///
    /// <para>The share sheet could already do this, and asking the user to leave the app, find
    /// the file in another one and share it back is not the same as the button the desktop has
    /// had all along. <c>SyncManager.SendFileAsync</c> existed and nothing called it.</para>
    ///
    /// <para>The picker hands back a stream rather than a path, because the file may live in a
    /// provider with no path to give - Drive, or another app's private storage. So it is copied
    /// into the cache first and sent from there.</para>
    /// </summary>
    private async void OnSendFileClicked(object? sender, EventArgs e)
    {
        if (!SyncManager.IsConnected)
        {
            await DisplayAlertAsync("Not connected",
                "Your computer is not reachable right now. Files need Wi-Fi, so check that both devices are on the same network.", "OK");
            return;
        }

        try
        {
            var picked = await FilePicker.Default.PickAsync();
            if (picked == null) return;

            string staged = System.IO.Path.Combine(FileSystem.CacheDirectory, picked.FileName);

            using (var source = await picked.OpenReadAsync())
            using (var destination = System.IO.File.Create(staged))
            {
                await source.CopyToAsync(destination);
            }

            SendFileButton.IsEnabled = false;
            try
            {
                if (!await SyncManager.SendFileAsync(staged))
                {
                    await DisplayAlertAsync("Not sent", $"\"{picked.FileName}\" could not be sent just now.", "OK");
                }
            }
            finally
            {
                SendFileButton.IsEnabled = true;
                try { System.IO.File.Delete(staged); } catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Write("Files", "Picking a file to send failed", ex);
            await DisplayAlertAsync("Could not send", "That file could not be read.", "OK");
        }
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

    /// <summary>
    /// Opens a received file, which is the other half of being able to send one.
    ///
    /// A file that arrives and cannot be reached from the app that received it has not really
    /// arrived. Rows that are not received files carry no location and quietly do nothing.
    /// </summary>
    private async void OnActivityRowTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Element)?.BindingContext is not ActivityRow row || !row.CanOpen) return;

#if ANDROID
        if (!Platforms.Android.ReceivedFiles.Open(row.Location))
        {
            await DisplayAlertAsync("Cannot open",
                $"Nothing on this phone will open \"{row.Title}\". It is in your Downloads folder.", "OK");
        }
#else
        await Task.CompletedTask;
#endif
    }

    private sealed class ActivityRow
    {
        public string Glyph { get; init; } = "";
        public string Title { get; init; } = "";
        public string Sub { get; init; } = "";
        public string Age { get; init; } = "";

        /// <summary>Where the file went, for a received file. Empty for everything else.</summary>
        public string Location { get; init; } = "";

        public bool CanOpen => Location.Length > 0;
    }
}
