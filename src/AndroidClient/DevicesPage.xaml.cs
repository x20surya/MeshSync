using System.Collections.ObjectModel;
using CoreLib.Diagnostics;
using CoreLib.Identity;

namespace AndroidClient;

/// <summary>
/// Every device in the mesh, and the two ways to add another.
///
/// <para>The desktop grew the same page at the same time, deliberately: the two apps are peers
/// now, so a thing you can do on one should be a thing you can do on the other. Adding a device
/// from the phone is not a lesser path - with symmetric roles the phone can be the one showing
/// the code just as readily.</para>
/// </summary>
public partial class DevicesPage : ContentPage
{
    private readonly ObservableCollection<DeviceRow> _devices = new();
    private readonly ObservableCollection<PendingRow> _pending = new();

    public DevicesPage()
    {
        InitializeComponent();
        DeviceList.ItemsSource = _devices;
        PendingList.ItemsSource = _pending;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        Header.RefreshSubtitle();
        SyncManager.OnConnectionStatusChanged += OnStatusChanged;
        SyncManager.Security.Peers.Changed += OnPeersChanged;
        SyncManager.Security.PairingRequested += OnPairingRequested;
        SyncManager.Security.Pairing.Changed += OnPairingWindowChanged;

        // Showing this page is the statement that a human is here inviting something in. It is
        // the only signal this side gets, so the window follows the page exactly.
        SyncManager.Security.Pairing.Open();

        Render();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        SyncManager.OnConnectionStatusChanged -= OnStatusChanged;
        SyncManager.Security.Peers.Changed -= OnPeersChanged;
        SyncManager.Security.PairingRequested -= OnPairingRequested;
        SyncManager.Security.Pairing.Changed -= OnPairingWindowChanged;

        // Shut the moment the page is not on screen, which also drops anything still waiting -
        // a prompt left behind must not be answerable by whoever picks the phone up next.
        SyncManager.Security.Pairing.Close();
    }

    private void OnStatusChanged(string status) => Dispatcher.Dispatch(Render);

    private void OnPeersChanged() => Dispatcher.Dispatch(Render);

    private void OnPairingRequested(PendingPairing pending) => Dispatcher.Dispatch(Render);

    private void OnPairingWindowChanged() => Dispatcher.Dispatch(Render);

    // ──────────────────────────────────── rendering

    private void Render()
    {
        var peers = SyncManager.Security.Peers;
        _devices.Clear();

        // Only whether anything is reachable is known, not which peer. Honest while the mesh is
        // small, and the next thing to grow when it is not.
        bool anyLive = SyncManager.IsConnected;
        string via = SyncManager.WiFiConnected && SyncManager.BleConnected ? "Wi-Fi + Bluetooth"
                   : SyncManager.WiFiConnected ? "Wi-Fi"
                   : "Bluetooth";

        foreach (var peer in peers.Peers.OrderBy(p => p.Name ?? p.Fingerprint))
        {
            bool live = anyLive && string.Equals(peer.Name, SyncManager.PeerName, StringComparison.OrdinalIgnoreCase);

            _devices.Add(new DeviceRow
            {
                Name = string.IsNullOrWhiteSpace(peer.Name)
                    ? DeviceIdentity.Shorten(peer.Fingerprint)
                    : peer.Name!,
                Detail = live
                    ? $"{via} · {Brief(peer.Fingerprint)}"
                    : $"Last seen {Relative(peer.LastSeenUtc)} · {Brief(peer.Fingerprint)}",
                Fingerprint = peer.Fingerprint,
                Dot = live ? Themed("Accent") : Themed("Faint")
            });
        }

        DevicesEmpty.IsVisible = _devices.Count == 0;
        DevicesSub.Text = _devices.Count switch
        {
            0 => "Nothing paired yet.",
            1 => "One device paired into this mesh.",
            _ => $"{_devices.Count} devices paired into this mesh."
        };

        SelfName.Text = SyncManager.LocalDeviceName;
        SelfAddress.Text = LocalAddress();
        SelfKey.Text = Shorten(SyncManager.Security.Identity.PublicKey);
        SelfFingerprint.Text = SyncManager.Security.Identity.ShortFingerprint;

        RenderPending();
    }

    /// <summary>
    /// Redraws the devices waiting to be allowed in.
    ///
    /// The card stays hidden until something is actually knocking, so the ordinary case - this
    /// page open with nothing connecting - looks exactly as it did before confirmation existed.
    /// </summary>
    private void RenderPending()
    {
        var waiting = SyncManager.Security.PendingPairings;

        _pending.Clear();
        foreach (var candidate in waiting)
        {
            _pending.Add(new PendingRow
            {
                Code = candidate.ShortFingerprint,
                Name = string.IsNullOrWhiteSpace(candidate.Name)
                    ? "It did not say what it is called"
                    : $"Calls itself \"{candidate.Name}\"",
                Fingerprint = candidate.Fingerprint
            });
        }

        PendingCard.IsVisible = _pending.Count > 0;
        PendingHeading.Text = _pending.Count > 1
            ? $"{_pending.Count} devices want to join"
            : "A device wants to join";
    }

    private void OnConfirmPairingClicked(object? sender, EventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not string fingerprint) return;

        if (SyncManager.Security.ConfirmPairing(fingerprint))
        {
            // It was refused and told to come back, so it is waiting on a retry rather than on
            // a socket. Nudging both loops turns a wait into an immediate reconnect.
            SyncManager.NudgeReconnect();
        }

        Render();
    }

    private void OnRejectPairingClicked(object? sender, EventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not string fingerprint) return;

        SyncManager.Security.RejectPairing(fingerprint);
        Render();
    }

    private static string LocalAddress() =>
        CoreLib.Transport.NetworkUtil.GetLocalLanAddress() ?? "No network";

    /// <summary>Two groups of fingerprint: enough to tell devices apart, short enough to fit.</summary>
    private static string Brief(string fingerprint)
    {
        string full = DeviceIdentity.Shorten(fingerprint);
        return full.Length <= 9 ? full : full.Substring(0, 9);
    }

    private static string Shorten(string value) =>
        string.IsNullOrEmpty(value) ? "(none)" :
        value.Length <= 20 ? value : value.Substring(0, 20) + "…";

    private static string Relative(DateTimeOffset when)
    {
        var age = DateTimeOffset.UtcNow - when;

        if (when == default) return "never";
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }

    private Color Themed(string key)
    {
        string suffix = Application.Current?.RequestedTheme == AppTheme.Dark ? "Dark" : "Light";

        return Application.Current?.Resources.TryGetValue(key + suffix, out var value) == true && value is Color colour
            ? colour
            : Colors.Gray;
    }

    // ──────────────────────────────────── actions

    /// <summary>
    /// Forgets a device, which revokes its key rather than merely hiding it from a list.
    /// Confirmed first, because the only way back is to pair it again.
    /// </summary>
    private async void OnForgetClicked(object? sender, EventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not string fingerprint) return;

        var peer = SyncManager.Security.Peers.Find(fingerprint);
        if (peer == null) return;

        string name = string.IsNullOrWhiteSpace(peer.Name)
            ? DeviceIdentity.Shorten(fingerprint)
            : peer.Name!;

        bool confirmed = await DisplayAlertAsync("Forget device",
            $"Forget {name}?\n\nIt will stop syncing immediately, and adding it again means scanning a new code.",
            "Forget", "Cancel");

        if (!confirmed) return;

        SyncManager.Security.Peers.Forget(fingerprint);
        Render();
    }

    private void OnScanClicked(object? sender, EventArgs e) => OpenCamera();

    private void OnManualToggled(object? sender, EventArgs e)
    {
        ManualCard.IsVisible = !ManualCard.IsVisible;
        ManualButton.Text = ManualCard.IsVisible ? "Hide manual entry" : "Enter details instead";
    }

    private async void OnManualAddClicked(object? sender, EventArgs e)
    {
        string address = AddressEntry.Text?.Trim() ?? "";
        string key = KeyEntry.Text?.Trim() ?? "";

        if (address.Length == 0 || key.Length == 0)
        {
            await DisplayAlertAsync("Missing details",
                "Enter both the address and the pairing key shown on the other device.", "OK");
            return;
        }

        if (!await SyncManager.ConnectAsync(address, key))
        {
            await DisplayAlertAsync("Not added yet",
                $"The other device has to allow this one in. Look for a prompt on it and check the code shown there is {SyncManager.Security.Identity.ShortFingerprint}.\n\nIf there was no prompt, check the address and key.",
                "OK");
            return;
        }

        AddressEntry.Text = "";
        KeyEntry.Text = "";
        ManualCard.IsVisible = false;
        ManualButton.Text = "Enter details instead";

        Render();
    }

    private async void OnCopyKeyClicked(object? sender, EventArgs e)
    {
        try
        {
            await Clipboard.Default.SetTextAsync(SyncManager.Security.Identity.PublicKey);
            await DisplayAlertAsync("Copied", "This device's pairing key is on the clipboard.", "OK");
        }
        catch (Exception ex)
        {
            Log.Write("Devices", "Copying the pairing key failed", ex);
        }
    }

    private static void OpenCamera()
    {
#if ANDROID
        try
        {
            // Most camera apps recognise a QR code in the viewfinder and offer the deep link,
            // which lands back in this app through the meshsync:// scheme.
            var intent = new global::Android.Content.Intent("android.media.action.STILL_IMAGE_CAMERA");
            intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            global::Android.App.Application.Context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            Log.Write("Devices", "Could not open the camera", ex);
        }
#endif
    }

    private sealed class DeviceRow
    {
        public string Name { get; init; } = "";
        public string Detail { get; init; } = "";
        public string Fingerprint { get; init; } = "";
        public Color Dot { get; init; } = Colors.Gray;
    }

    /// <summary>One device waiting to be allowed in.</summary>
    private sealed class PendingRow
    {
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string Fingerprint { get; init; } = "";
    }
}
