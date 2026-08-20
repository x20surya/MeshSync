using CoreLib.Diagnostics;

namespace AndroidClient;

/// <summary>
/// First-run wizard. Each step checks its own precondition live and advances on its own,
/// so the user is never told to do something they have already done.
/// </summary>
public partial class SetupPage : ContentPage
{
    private const int StepPair = 0;
    private const int StepSending = 1;
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

    /// <summary>
    /// Asks Android to offer the "Send clipboard" tile.
    ///
    /// <para>Android 13 added a prompt for this, which is far better than telling someone to
    /// open the shade, find the edit button and go hunting. Below that there is no API, so the
    /// step just explains where to find it.</para>
    ///
    /// <para>This matters more than it would have done: with no accessibility service watching
    /// the clipboard, the tile is the closest thing left to sending without thinking about it.
    /// </para>
    /// </summary>
    private static void OfferQuickSettingsTile()
    {
#if ANDROID
        try
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;

            var context = global::Android.App.Application.Context;
            var statusBar = (global::Android.App.StatusBarManager?)
                context.GetSystemService("statusbar");

            statusBar?.RequestAddTileService(
                new global::Android.Content.ComponentName(
                    context, Java.Lang.Class.FromType(typeof(Platforms.Android.SendClipboardTileService))),
                "Send clipboard",
                global::Android.Graphics.Drawables.Icon.CreateWithResource(
                    context, global::Android.Resource.Drawable.IcMenuShare),
                Java.Util.Concurrent.Executors.NewSingleThreadExecutor()!,
                new TileRequestCallback());
        }
        catch (Exception ex)
        {
            Log.Write("Setup", "Could not offer the Quick Settings tile", ex);
        }
#endif
    }

#if ANDROID
    /// <summary>
    /// Android insists on a callback. Nothing useful can be done with the answer - the user
    /// either added it or did not - so this exists to satisfy the signature and say what
    /// happened in the log.
    /// </summary>
    private sealed class TileRequestCallback : Java.Lang.Object, Java.Util.Functions.IConsumer
    {
        public void Accept(Java.Lang.Object? result) =>
            Log.Write("Setup", $"Quick Settings tile request returned {result}.");
    }
#endif

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
        bool done = _step == StepPair && IsPaired;

        var accent = (Color)(Application.Current!.Resources.TryGetValue(
            Application.Current.RequestedTheme == AppTheme.Dark ? "AccentDark" : "AccentLight", out var a)
            ? a : Colors.Teal);

        var idle = (Color)(Application.Current.Resources.TryGetValue(
            Application.Current.RequestedTheme == AppTheme.Dark ? "BorderDark" : "Gray100", out var i)
            ? i : Colors.LightGray);

        Bar1.Color = _step >= StepPair ? accent : idle;
        Bar2.Color = _step >= StepSending ? accent : idle;
        Bar3.Color = _step >= StepScreenshots ? accent : idle;

        StepCounter.Text = $"Step {_step + 1} of 3";

        IconPair.IsVisible = _step == StepPair;
        IconClipboard.IsVisible = _step == StepSending;
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

            case StepSending:
                StepTitle.Text = "Sending from this phone";
                StepBody.Text = "Anything from your other devices arrives here on its own. " +
                                "To send from this phone: tap the Send clipboard tile in your quick settings, " +
                                "highlight text and choose Send to my devices, or share to Mesh Sync from any app.";
                StepNote.Text = "Android does not let apps read the clipboard in the background, and the workaround for that " +
                                "stops banking and UPI apps working - so sending is one tap rather than none.";
                PrimaryButton.Text = "Add the tile";
                SecondaryButton.Text = "Skip";
                SecondaryButton.IsVisible = true;
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

            case StepSending:
                // The tile is the nearest thing left to sending without thinking about it, so
                // offering it here is the difference between the user having it and never
                // finding it. The ongoing notification carries a Sync action too, which is why
                // notification access is asked for in the same breath.
                OfferQuickSettingsTile();
                await RequestNotificationAccessAsync();
                Advance();
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

            case StepSending:
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

        if (!await SyncManager.ConnectAsync(ip, code))
        {
            // Not a failure so much as a step that has not happened yet: the other device
            // refuses the first attempt and asks someone to compare fingerprints.
            await DisplayAlertAsync("Waiting to be allowed in",
                $"Look for a prompt on the other device and check the code it shows is {SyncManager.Security.Identity.ShortFingerprint}.\n\nIf there was no prompt, check the address and key.",
                "OK");
        }

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
