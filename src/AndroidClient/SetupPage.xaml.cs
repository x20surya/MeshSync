using CoreLib.Diagnostics;

namespace AndroidClient;

/// <summary>
/// First-run wizard. Each step checks its own precondition live and advances on its own,
/// so the user is never told to do something they have already done.
/// </summary>
public partial class SetupPage : ContentPage
{
    private const int StepPair = 0;
    private const int StepClipboard = 1;
    private const int StepScreenshots = 2;

    private int _step = StepPair;
    private bool _manualVisible;
    private IDispatcherTimer? _poll;

    public SetupPage()
    {
        InitializeComponent();

        // A ScrollView sizes its content to the content's natural height, so
        // VerticalOptions="Center" alone would not centre anything. Matching the canvas
        // to the viewport gives centring for short steps and scrolling for tall ones.
        StepScroll.SizeChanged += (_, _) =>
        {
            if (StepScroll.Height > 0) StepCanvas.MinimumHeightRequest = StepScroll.Height;
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Render();

        // Permissions and pairing can both be satisfied outside the app, so re-check
        // while this page is on screen rather than only on button taps.
        _poll = Dispatcher.CreateTimer();
        _poll.Interval = TimeSpan.FromMilliseconds(700);
        _poll.Tick += (_, _) => Render();
        _poll.Start();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _poll?.Stop();
        _poll = null;
    }

    // ──────────────────────────────────── state

    private static bool IsPaired => SyncManager.IsPaired;

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
        catch (Exception ex)
        {
            Log.Write("Setup", "Could not read accessibility settings", ex);
            return false;
        }
#else
        return false;
#endif
    }

    private static async Task<bool> HasPhotoAccessAsync()
    {
        try
        {
#if ANDROID
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
                return await Permissions.CheckStatusAsync<Permissions.Photos>() == PermissionStatus.Granted;

            return await Permissions.CheckStatusAsync<Permissions.StorageRead>() == PermissionStatus.Granted;
#else
            return false;
#endif
        }
        catch
        {
            return false;
        }
    }

    // ──────────────────────────────────── rendering

    private void Render()
    {
        bool done = _step switch
        {
            StepPair => IsPaired,
            StepClipboard => IsClipboardServiceOn(),
            _ => false
        };

        var accent = (Color)(Application.Current!.Resources.TryGetValue(
            Application.Current.RequestedTheme == AppTheme.Dark ? "AccentDark" : "AccentLight", out var a)
            ? a : Colors.Teal);

        var idle = (Color)(Application.Current.Resources.TryGetValue(
            Application.Current.RequestedTheme == AppTheme.Dark ? "BorderDark" : "Gray100", out var i)
            ? i : Colors.LightGray);

        Bar1.Color = _step >= StepPair ? accent : idle;
        Bar2.Color = _step >= StepClipboard ? accent : idle;
        Bar3.Color = _step >= StepScreenshots ? accent : idle;

        StepCounter.Text = $"Step {_step + 1} of 3";

        IconPair.IsVisible = _step == StepPair;
        IconClipboard.IsVisible = _step == StepClipboard;
        IconImage.IsVisible = _step == StepScreenshots;

        ManualPanel.IsVisible = _step == StepPair && _manualVisible;

        switch (_step)
        {
            case StepPair:
                StepTitle.Text = IsPaired ? "Paired" : "Pair with another device";
                StepBody.Text = IsPaired
                    ? $"Joined {SyncManager.MeshName}. You can pair more devices later."
                    : "Open Mesh Sync on the other device and point your camera at the code it shows.";
                StepNote.Text = "Nothing ever leaves your own devices. There is no cloud account.";
                PrimaryButton.Text = IsPaired ? "Continue" : "Open camera";
                SecondaryButton.Text = _manualVisible ? "Hide manual entry" : "Enter details manually";
                SecondaryButton.IsVisible = !IsPaired;
                break;

            case StepClipboard:
                bool on = IsClipboardServiceOn();
                StepTitle.Text = on ? "Clipboard access is on" : "Let it see your clipboard";
                StepBody.Text = on
                    ? "Mesh Sync will now notice whenever you copy something."
                    : "Android needs you to switch on the Mesh Sync accessibility service so the app can tell when you copy something.";
                StepNote.Text = "Each pair of devices has its own key, so a copy is encrypted for the device it is going to. Nothing is stored.";
                PrimaryButton.Text = on ? "Continue" : "Open Android settings";
                SecondaryButton.Text = "Skip for now";
                SecondaryButton.IsVisible = !on;
                break;

            case StepScreenshots:
                StepTitle.Text = "Send screenshots too";
                StepBody.Text = "Allow access to your photos and every screenshot you take will appear on your other devices automatically.";
                StepNote.Text = "Only screenshots are read, and only to send them to your own computer.";
                PrimaryButton.Text = "Allow photo access";
                SecondaryButton.Text = "Not now";
                SecondaryButton.IsVisible = true;
                break;
        }

        // A completed step should offer to move on, not repeat itself.
        if (done && _step == StepPair) PrimaryButton.Text = "Continue";
    }

    // ──────────────────────────────────── actions

    private async void OnPrimaryClicked(object? sender, EventArgs e)
    {
        switch (_step)
        {
            case StepPair when IsPaired:
                Advance();
                break;

            case StepPair:
                OpenCamera();
                break;

            case StepClipboard when IsClipboardServiceOn():
                // The ongoing notification is the app's main surface once it is backgrounded,
                // and it carries the "Sync Clipboard" action, so ask here where it makes sense.
                await RequestNotificationAccessAsync();
                Advance();
                break;

            case StepClipboard:
                OpenAccessibilitySettings();
                break;

            case StepScreenshots:
                await RequestPhotoAccessAsync();
                await FinishAsync();
                break;
        }
    }

    private void OnSecondaryClicked(object? sender, EventArgs e)
    {
        switch (_step)
        {
            case StepPair:
                _manualVisible = !_manualVisible;
                Render();
                break;

            case StepClipboard:
                Advance();
                break;

            case StepScreenshots:
                _ = FinishAsync();
                break;
        }
    }

    private async void OnManualPairClicked(object? sender, EventArgs e)
    {
        string ip = IpEntry.Text?.Trim() ?? "";
        string code = CodeEntry.Text?.Trim() ?? "";

        if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(code))
        {
            await DisplayAlertAsync("Missing details",
                "Enter both the address and the pairing key shown on the other device.", "OK");
            return;
        }

        await SyncManager.ConnectAsync(ip, code);
        Render();
    }

    private void Advance()
    {
        if (_step < StepScreenshots)
        {
            _step++;
            Render();
        }
        else
        {
            _ = FinishAsync();
        }
    }

    private async Task FinishAsync()
    {
        MarkSetupComplete();
        await Shell.Current.GoToAsync("//dashboard");
    }

    private static void MarkSetupComplete()
    {
#if ANDROID
        try
        {
            var prefs = global::Android.App.Application.Context
                .GetSharedPreferences("SyncPrefs", global::Android.Content.FileCreationMode.Private);
            prefs?.Edit()?.PutBoolean("SetupComplete", true)?.Apply();
        }
        catch (Exception ex)
        {
            Log.Write("Setup", "Could not save setup state", ex);
        }
#endif
    }

    public static bool IsSetupComplete()
    {
#if ANDROID
        try
        {
            var prefs = global::Android.App.Application.Context
                .GetSharedPreferences("SyncPrefs", global::Android.Content.FileCreationMode.Private);
            return prefs?.GetBoolean("SetupComplete", false) ?? false;
        }
        catch
        {
            return false;
        }
#else
        return true;
#endif
    }

    // ──────────────────────────────────── platform hops

    private static void OpenCamera()
    {
#if ANDROID
        try
        {
            // Most camera apps recognise a QR code in the viewfinder and offer the deep link.
            var intent = new global::Android.Content.Intent("android.media.action.STILL_IMAGE_CAMERA");
            intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            global::Android.App.Application.Context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            Log.Write("Setup", "Could not open the camera", ex);
        }
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
            Log.Write("Setup", "Could not open accessibility settings", ex);
        }
#endif
    }

    /// <summary>
    /// Android 13+ will not show any notification without this, which silently disabled the
    /// ongoing sync notification entirely when the old page that asked for it was removed.
    /// </summary>
    private static async Task RequestNotificationAccessAsync()
    {
        try
        {
#if ANDROID
            if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;

            if (await Permissions.CheckStatusAsync<Permissions.PostNotifications>() != PermissionStatus.Granted)
                await Permissions.RequestAsync<Permissions.PostNotifications>();
#else
            await Task.CompletedTask;
#endif
        }
        catch (Exception ex)
        {
            Log.Write("Setup", "Requesting notification access failed", ex);
        }
    }

    private static async Task RequestPhotoAccessAsync()
    {
        try
        {
#if ANDROID
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
                await Permissions.RequestAsync<Permissions.Photos>();
            else
                await Permissions.RequestAsync<Permissions.StorageRead>();
#else
            await Task.CompletedTask;
#endif
        }
        catch (Exception ex)
        {
            Log.Write("Setup", "Requesting photo access failed", ex);
        }
    }
}
