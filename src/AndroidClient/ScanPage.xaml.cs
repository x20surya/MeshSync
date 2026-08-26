using CoreLib.Identity;
using CoreLib.Diagnostics;
using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace AndroidClient;

/// <summary>
/// The pairing scanner, inside the app.
///
/// <para>Pairing used to fire <c>android.media.action.STILL_IMAGE_CAMERA</c> and hope the camera
/// app that answered happened to recognise a QR code and offer the <c>meshsync://</c> link. On a
/// stock OEM camera, or one with scanning switched off, it does not - so the very first thing the
/// app asked of a new user was the least reliable thing it did, and the failure was a photo of a
/// QR code rather than an error.</para>
///
/// <para>One page for both callers. Setup and the Devices page ask the same question, so they get
/// the same answer surface, and a future swap of the reader for hand-rolled CameraX is confined
/// to this file.</para>
/// </summary>
public partial class ScanPage : ContentPage
{
    private readonly TaskCompletionSource<PairingCode?> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Guards against the second decode. The reader keeps analysing frames until it is told to
    /// stop, and a QR code held in front of a camera decodes many times a second - so without
    /// this the page pops once per frame and pairing is attempted repeatedly.
    /// </summary>
    private int _handled;

    private bool _asked;

    /// <summary>
    /// The reader, once there is a camera to give it. Null until the permission is granted.
    /// See <see cref="StartReader"/> for why it is not simply declared in the XAML.
    /// </summary>
    private CameraBarcodeReaderView? _reader;

    /// <summary>What the page is closing with. Read by <c>OnDisappearing</c>, which is the one
    /// place guaranteed to run whether the page was dismissed by this class or by the user.</summary>
    private PairingCode? _outcome;

    private ScanPage() => InitializeComponent();

    /// <summary>
    /// Shows the scanner and returns what was scanned, or null if the user backed out or refused
    /// the camera.
    ///
    /// Modal rather than pushed, so the drawer and the flyout are out of reach: the viewfinder is
    /// a single-purpose screen and there is nowhere else to be while it is open.
    /// </summary>
    public static async Task<PairingCode?> ScanAsync(INavigation navigation)
    {
        var page = new ScanPage();
        await navigation.PushModalAsync(page, animated: true);
        return await page._result.Task;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // Asked here rather than in the constructor: the rationale is this screen, so the screen
        // has to exist behind the system dialog. Asking any earlier is asking before explaining.
        if (!_asked)
        {
            _asked = true;
            await EnsureCameraAsync();
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopReader();

        // A dismissal this class did not initiate - a swipe, or the shell being navigated
        // elsewhere - still has to answer the caller, or the await above never returns.
        _result.TrySetResult(_outcome);
    }

    /// <summary>The hardware back button is a refusal, not a crash.</summary>
    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(null);
        return true;
    }

    // ──────────────────────────────────── the reader

    /// <summary>
    /// Builds the reader and puts it on screen.
    ///
    /// <para><b>Built here rather than declared in the XAML.</b> The view binds CameraX when its
    /// handler is created, and a handler created while <c>CAMERA</c> is refused binds to nothing.
    /// Granting the permission afterwards does not send it back through that path, so the page
    /// sat showing a black rectangle with no error in logcat to say why. Creating the view only
    /// once the grant is in hand means the one code path that starts a camera always has one.</para>
    /// </summary>
    private void StartReader()
    {
        if (_reader != null) return;

        _reader = new CameraBarcodeReaderView
        {
            Options = new BarcodeReaderOptions
            {
                // Only the one format, because only the one format is ever shown. Every other
                // symbology is frames spent decoding something this app has no use for.
                Formats = BarcodeFormat.QrCode,

                // A pairing code is read off a laptop screen held at whatever angle is comfortable.
                AutoRotate = true,
                TryHarder = true,
                Multiple = false,
            },
            IsDetecting = true,
        };

        _reader.BarcodesDetected += OnBarcodesDetected;
        PreviewHost.Add(_reader);
    }

    /// <summary>
    /// Releases the camera. Left running, the preview keeps the camera open behind whatever comes
    /// next, which is both a battery cost and a camera indicator the user cannot explain.
    /// </summary>
    private void StopReader()
    {
        if (_reader == null) return;

        _reader.BarcodesDetected -= OnBarcodesDetected;
        _reader.IsDetecting = false;
        try { _reader.IsTorchOn = false; } catch { /* not every camera has one */ }

        PreviewHost.Remove(_reader);
        _reader = null;
    }

    // ──────────────────────────────────── permission

    private async Task EnsureCameraAsync()
    {
        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.Camera>();

            if (status != PermissionStatus.Granted)
                status = await Permissions.RequestAsync<Permissions.Camera>();

            if (status == PermissionStatus.Granted)
            {
                DeniedPanel.IsVisible = false;
                StartReader();
                return;
            }

            DeniedPanel.IsVisible = true;

            // Android stops showing the dialog once it has been refused twice, so a second
            // "Allow the camera" would do nothing at all and read as a broken button. When the
            // system says it will no longer offer a rationale, the only honest button left is
            // one that opens app settings.
            bool canAskAgain = Permissions.ShouldShowRationale<Permissions.Camera>();

            DeniedTitle.Text = canAskAgain
                ? "Mesh Sync needs the camera to scan"
                : "The camera is turned off for Mesh Sync";

            DeniedBody.Text = canAskAgain
                ? "It is used to read the pairing code and nothing else. No photo is taken and nothing is uploaded."
                : "Android will not ask again from here. Turn the camera on in app settings, or pair by typing the details in.";

            DeniedPrimary.Text = canAskAgain ? "Allow the camera" : "Open app settings";
        }
        catch (Exception ex)
        {
            Log.Write("Scan", "Requesting the camera failed", ex);
            DeniedPanel.IsVisible = true;
        }
    }

    private async void OnDeniedPrimaryClicked(object? sender, EventArgs e)
    {
        if (Permissions.ShouldShowRationale<Permissions.Camera>())
        {
            await EnsureCameraAsync();
            return;
        }

        try
        {
            // Granting from here comes back through OnAppearing, which re-checks.
            _asked = false;
            AppInfo.Current.ShowSettingsUI();
        }
        catch (Exception ex)
        {
            Log.Write("Scan", "Could not open app settings", ex);
        }
    }

    // ──────────────────────────────────── scanning

    private void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        string? value = e.Results?.FirstOrDefault()?.Value;
        if (string.IsNullOrWhiteSpace(value)) return;

        if (!PairingCode.TryParse(value, out var code, out string error))
        {
            // Not a pairing code. Reporting it and carrying on is the right answer: the user is
            // pointing a camera at a screen, and the most likely next thing they do is aim
            // better. Closing on a wrong code would make the scanner feel broken.
            MainThread.BeginInvokeOnMainThread(() =>
            {
                HintLabel.Text = error;
                HintLabel.TextColor = Color.FromArgb("#D69A5A");
            });
            return;
        }

        // First decode wins. See _handled.
        if (Interlocked.Exchange(ref _handled, 1) != 0) return;

        MainThread.BeginInvokeOnMainThread(() => _ = CloseAsync(code));
    }

    private void OnTorchClicked(object? sender, EventArgs e)
    {
        if (_reader == null) return;

        try
        {
            _reader.IsTorchOn = !_reader.IsTorchOn;
            TorchButton.Text = _reader.IsTorchOn ? "Torch off" : "Torch";
        }
        catch (Exception ex)
        {
            Log.Write("Scan", "The torch is not available on this camera", ex);
        }
    }

    private void OnManualClicked(object? sender, EventArgs e) => _ = CloseAsync(null);

    private async Task CloseAsync(PairingCode? code)
    {
        // Recorded before the pop, because OnDisappearing is what completes the caller and it
        // runs during it. Setting the result here instead would let the caller resume before the
        // page underneath is back, and that page is the one holding the pairing window open.
        _outcome = code;
        StopReader();

        try
        {
            await Navigation.PopModalAsync(animated: true);
        }
        catch (Exception ex)
        {
            Log.Write("Scan", "Closing the scanner failed", ex);
            _result.TrySetResult(code);
        }
    }
}
