using CoreLib.Diagnostics;

namespace AndroidClient;

/// <summary>
/// First-run wizard. Each step checks its own precondition live and advances on its own,
/// so the user is never told to do something they have already done.
///
/// <para><b>One permission per step, asked after the screen that explains it.</b> The Bluetooth
/// grants and photo access used to fire from <c>MainActivity.OnCreate</c> - two system dialogs
/// stacked on the splash screen before the user had seen anything, and a refusal silently cost
/// radio pairing and screenshot sync with nothing to say so. Photo access was then asked for a
/// second time here, where Android ignored it because it had already been answered.</para>
///
/// <para>Every step is skippable and nothing is turned on without a tap.</para>
/// </summary>
public partial class SetupPage : ContentPage
{
    private const int StepPair = 0;
    private const int StepConnected = 1;
    private const int StepNotifications = 2;
    private const int StepSending = 3;
    private const int StepCount = 4;

    private int _step = StepPair;
    private bool _manualVisible;
    private bool _busy;
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

        // Showing this page is the statement that a human is here inviting something in, which
        // is what the pairing window means. The Devices page does exactly the same.
        SyncManager.Security.Pairing.Open();

        // Permissions and pairing can both be satisfied outside the app - the notification
        // listener grant is a settings screen rather than a dialog - so re-check while this page
        // is on screen rather than only on button taps. This is what lets step 3 notice the
        // grant and move on by itself instead of asking "did that work?".
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

    /// <summary>Whether the notification listener is granted, which is what mirroring runs on.</summary>
    private static bool IsMirroringGranted
    {
#if ANDROID
        get => Platforms.Android.NotificationMirrorService.IsGranted();
#else
        get => false;
#endif
    }

    // ──────────────────────────────────── rendering

    private void Render()
    {
        var accent = (Color)(Application.Current!.Resources.TryGetValue(
            Application.Current.RequestedTheme == AppTheme.Dark ? "AccentDark" : "AccentLight", out var a)
            ? a : Colors.Teal);

        var idle = (Color)(Application.Current.Resources.TryGetValue(
            Application.Current.RequestedTheme == AppTheme.Dark ? "BorderDark" : "Gray100", out var i)
            ? i : Colors.LightGray);

        Bar1.Color = _step >= StepPair ? accent : idle;
        Bar2.Color = _step >= StepConnected ? accent : idle;
        Bar3.Color = _step >= StepNotifications ? accent : idle;
        Bar4.Color = _step >= StepSending ? accent : idle;

        StepCounter.Text = $"Step {_step + 1} of {StepCount}";

        IconPair.IsVisible = _step == StepPair;
        IconLink.IsVisible = _step == StepConnected;
        IconNotify.IsVisible = _step == StepNotifications;
        IconSend.IsVisible = _step == StepSending;

        ManualPanel.IsVisible = _step == StepPair && _manualVisible && !IsPaired;

        switch (_step)
        {
            case StepPair:
                StepTitle.Text = IsPaired ? "Paired" : "Pair with another device";
                StepBody.Text = IsPaired
                    ? $"Joined {SyncManager.MeshName}. You can pair more devices later."
                    : "Open Mesh Sync on your computer, show its pairing code, and scan it here.";
                StepNote.Text = "Nothing ever leaves your own devices. There is no cloud account.";
                PrimaryButton.Text = IsPaired ? "Continue" : "Scan the code";
                SecondaryButton.Text = _manualVisible ? "Hide manual entry" : "Enter details manually";
                SecondaryButton.IsVisible = !IsPaired;
                break;

            case StepConnected:
                StepTitle.Text = "Keep it connected";
                StepBody.Text = "Bluetooth lets your devices find each other with no Wi-Fi at all - " +
                                "on a train, on mobile data, anywhere. It is also how they stay in touch " +
                                "when the network changes.";
                StepNote.Text = "Mesh Sync only looks for its own devices. It never asks where you are.";
                PrimaryButton.Text = "Allow Bluetooth";
                SecondaryButton.Text = "Wi-Fi only, thanks";
                SecondaryButton.IsVisible = true;
                break;

            case StepNotifications:
                StepTitle.Text = "See your phone on your computer";
                StepBody.Text = IsMirroringGranted
                    ? "Mirroring is on. Every app is included - mute any you would rather keep on this phone, in Settings."
                    : "Messages and calls appear on your desktop, and you can answer them from there without picking the phone up.";
                StepNote.Text = "Notifications are passed on as they arrive and are never stored.";
                PrimaryButton.Text = IsMirroringGranted ? "Continue" : "Turn on mirroring";
                SecondaryButton.Text = "Not now";
                SecondaryButton.IsVisible = !IsMirroringGranted;
                break;

            case StepSending:
                StepTitle.Text = "Sending from this phone";
                StepBody.Text = "Anything from your other devices arrives here on its own. " +
                                "To send from this phone: tap the Send clipboard tile in your quick settings, " +
                                "highlight text and choose Send to my devices, or share to Mesh Sync from any app.";
                StepNote.Text = "Android does not let apps read the clipboard in the background, and the workaround for that " +
                                "stops banking and UPI apps working - so sending is one tap rather than none.";
                PrimaryButton.Text = "Add the tile and allow screenshots";
                SecondaryButton.Text = "Skip";
                SecondaryButton.IsVisible = true;
                break;
        }
    }

    // ──────────────────────────────────── actions

    private async void OnPrimaryClicked(object? sender, EventArgs e)
    {
        // Every branch below either opens a system dialog or leaves for a settings screen, and
        // the 700ms poll keeps re-rendering underneath. Without this a second tap while the
        // first is still in flight queues a second request Android will not show.
        if (_busy) return;
        _busy = true;

        try
        {
            switch (_step)
            {
                case StepPair when IsPaired:
                    Advance();
                    break;

                case StepPair:
                    await ScanAndPairAsync();
                    break;

                case StepConnected:
                    await RequestBluetoothAsync();
                    Advance();
                    break;

                case StepNotifications when IsMirroringGranted:
                    Advance();
                    break;

                case StepNotifications:
                    await TurnOnMirroringAsync();
                    break;

                case StepSending:
                    // The tile is the nearest thing left to sending without thinking about it,
                    // so offering it here is the difference between the user having it and never
                    // finding it. Screenshots are the one route that needs no tap at all, which
                    // is why photo access belongs on this screen and nowhere earlier.
                    OfferQuickSettingsTile();
                    await RequestPhotosAsync();
                    await FinishAsync();
                    break;
            }
        }
        finally
        {
            _busy = false;
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

            default:
                Advance();
                break;
        }
    }

    /// <summary>
    /// Opens the scanner and pairs with what it reads.
    ///
    /// The step does not advance on its own afterwards: the poll notices the pairing and
    /// re-renders this step as "Paired", so the user sees that it worked before moving on.
    /// </summary>
    private async Task ScanAndPairAsync()
    {
        var code = await ScanPage.ScanAsync(Navigation);

        if (code == null)
        {
            // Backing out of the scanner is not a failure, but somebody who cannot scan needs
            // the other route offered rather than left behind a second tap.
            _manualVisible = true;
            Render();
            return;
        }

        if (!await SyncManager.ConnectAsync(code))
        {
            // Not a failure so much as a step that has not happened yet: the other device
            // refuses the first attempt and asks someone to compare fingerprints.
            await DisplayAlertAsync("Waiting to be allowed in",
                $"Look for a prompt on the other device and check the code it shows is {SyncManager.Security.Identity.ShortFingerprint}.",
                "OK");
        }

        Render();
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
            await DisplayAlertAsync("Waiting to be allowed in",
                $"Look for a prompt on the other device and check the code it shows is {SyncManager.Security.Identity.ShortFingerprint}.\n\nIf there was no prompt, check the address and key.",
                "OK");
        }

        Render();
    }

    private void Advance()
    {
        if (_step < StepSending)
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

    // ──────────────────────────────────── permission steps

    /// <summary>
    /// The three Bluetooth grants, and the battery exemption behind them.
    ///
    /// Offered together because they answer the same question - will this keep working when I am
    /// not looking at it - and because two dialogs on one screen is still one screen.
    /// </summary>
    private async Task RequestBluetoothAsync()
    {
#if ANDROID
        var outcome = await Platforms.Android.AppPermissions.RequestBluetoothAsync();

        if (outcome == Platforms.Android.AppPermissions.Outcome.Blocked)
        {
            bool open = await DisplayAlertAsync("Bluetooth is turned off for Mesh Sync",
                "Android will not ask again from here. Your devices will still sync over Wi-Fi.",
                "Open settings", "Carry on");

            if (open) Platforms.Android.AppPermissions.OpenAppSettings();
            return;
        }

        if (outcome != Platforms.Android.AppPermissions.Outcome.Granted) return;

        // A grant that arrives after the one advertising attempt this run would otherwise not
        // take effect until the next launch.
        SyncManager.RetryBluetoothPeripheral();

        if (!Platforms.Android.AppPermissions.IsBatteryOptimised()) return;

        bool exempt = await DisplayAlertAsync("One more thing",
            "Android may stop Mesh Sync when the phone has been idle for a while, which is when a sync would silently stop arriving. Letting it run in the background fixes that.",
            "Allow", "Not now");

        if (exempt) Platforms.Android.AppPermissions.RequestBatteryExemption();
#else
        await Task.CompletedTask;
#endif
    }

    /// <summary>
    /// Turns mirroring on, which means the listener grant and the permission to post.
    ///
    /// <para>The stored preference has defaulted to on since schema 2, and every app mirrors
    /// unless muted - so nothing here decides whether mirroring happens. Android's listener
    /// grant does, and until now the wizard never mentioned it: the feature was on for everyone
    /// and running for nobody who had not gone hunting in Settings.</para>
    ///
    /// <para>It is a settings screen rather than a dialog, so the user leaves the app to give
    /// it. The poll notices when they come back and the step advances by itself.</para>
    /// </summary>
    private async Task TurnOnMirroringAsync()
    {
#if ANDROID
        // Asked first, because a mirrored notification this phone receives from elsewhere still
        // has to be posted, and the ongoing sync notification does too.
        await Platforms.Android.AppPermissions.RequestPostNotificationsAsync();

        bool go = await DisplayAlertAsync("Notification access",
            "Android asks for this in its own settings. Find Mesh Sync on the screen that opens and turn it on - then come straight back.",
            "Open settings", "Not now");

        if (go) Platforms.Android.NotificationMirrorService.RequestGrant();
#else
        await Task.CompletedTask;
#endif
    }

    private async Task RequestPhotosAsync()
    {
#if ANDROID
        var outcome = await Platforms.Android.AppPermissions.RequestPhotosAsync();

        if (outcome != Platforms.Android.AppPermissions.Outcome.Blocked) return;

        // Said plainly rather than left as a step that appeared to do nothing, which is what
        // asking a second time used to look like.
        await DisplayAlertAsync("Screenshots stay on this phone",
            "Photo access is turned off for Mesh Sync and Android will not ask again from here. Everything else still works, and you can turn it on later in Settings.",
            "OK");
#else
        await Task.CompletedTask;
#endif
    }
}
