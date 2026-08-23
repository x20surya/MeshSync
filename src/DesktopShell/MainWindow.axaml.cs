using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Threading;
using CoreLib;
using CoreLib.Diagnostics;
using CoreLib.Identity;
using CoreLib.Transport;
using Avalonia.Platform.Storage;
using DesktopCore;
using DesktopCore.Platform;
using QRCoder;

namespace DesktopShell;

/// <summary>One paired device, as the list shows it.</summary>
public sealed class DeviceRow
{
    public required string Name { get; init; }
    public required string Fingerprint { get; init; }
    public required string Detail { get; init; }
    public required bool Connected { get; init; }
    public required IBrush Dot { get; init; }
}

/// <summary>A device waiting for its fingerprint to be compared.</summary>
public sealed class PendingRow
{
    public required string Heading { get; init; }
    public required string Fingerprint { get; init; }
}

/// <summary>One mirrored phone notification, as the list shows it.</summary>
public sealed class NotificationRow
{
    public required string Key { get; init; }
    public required string Heading { get; init; }
    public required string Body { get; init; }
    public required string Source { get; init; }

    /// <summary>Whether the app that posted it offered a reply action. The sender's answer.</summary>
    public required bool CanReply { get; init; }

    /// <summary>The app's own word for the button - "Reply" on most of them.</summary>
    public required string ReplyLabel { get; init; }

    public required string ReplyPlaceholder { get; init; }

    /// <summary>
    /// What has been typed but not sent. Settable and kept across refreshes, because the list
    /// redraws on a timer and losing half a sentence to a two-second tick is unforgivable.
    /// </summary>
    public string Draft { get; set; } = "";

    public string ReplyStatus { get; set; } = "";

    public bool HasReplyStatus => ReplyStatus.Length > 0;
}

/// <summary>One file that has arrived this session.</summary>
public sealed class FileRow
{
    public required string Name { get; init; }
    public required string Sub { get; init; }
    public required string Path { get; init; }
}

/// <summary>One line of the in-memory activity list. Shaped like the Windows daemon's row.</summary>
public sealed class ActivityRow
{
    public required string Glyph { get; init; }
    public required string Title { get; init; }
    public required string Sub { get; init; }
    public required string Age { get; init; }
}

/// <summary>
/// The same window the Windows daemon has, on Avalonia: a 196px sidebar with the mark and the
/// nav, a 42px title bar, and one page at a time. The palette, the type sizes and the card
/// idiom are carried across unchanged, so the two builds are recognisably one product rather
/// than two apps that happen to share a protocol.
/// </summary>
public partial class MainWindow : Window
{
    private Daemon _daemon = null!;
    private DispatcherTimer? _refresh;
    private string _lastPairingUri = "";
    private bool _meshNameEdited;

    /// <summary>
    /// The page actually being shown.
    ///
    /// <para>Held rather than worked out from which RadioButton is checked, because during a
    /// change <em>two</em> of them are: Avalonia unchecks the old one after the new one raises
    /// its event, so anything that scans for "the checked button" finds whichever comes first in
    /// the scan and that is often the one being left. The symptom is a sidebar that shows the
    /// previous page on every click, which looks random and is not.</para>
    /// </summary>
    private string _currentTag = "Home";

    // Cached because the sweep runs every second on the UI thread and enumerating every network
    // interface there is exactly the kind of work that makes a window feel like it is ignoring
    // clicks. The address only changes when a lease does.
    private string? _cachedAddress;
    private DateTime _addressCheckedUtc = DateTime.MinValue;

    // Rebuilding four ItemsSource lists a second churns the visual tree for no reason. Each list
    // is only rebuilt when its contents actually differ.
    private string _devicesSignature = "";
    private string _pendingSignature = "";
    private string _activitySignature = "";
    private string _filesSignature = "";
    private string _notificationsSignature = "";

    /// <summary>
    /// Half-typed replies, by notification key.
    ///
    /// <para>The rows are rebuilt whenever the set of notifications changes, and a reply being
    /// composed must survive that - a message arriving from someone else is exactly when the
    /// list changes and exactly when you are most likely to be mid-sentence.</para>
    /// </summary>
    private readonly Dictionary<string, string> _drafts = new(StringComparer.Ordinal);

    /// <summary>For the XAML loader. <see cref="Attach"/> is what wires up a running device.</summary>
    public MainWindow() => InitializeComponent();

    public void Attach(Daemon daemon)
    {
        _daemon = daemon;

        SettingsName.Text = daemon.DeviceName;
        SettingsFingerprint.Text = daemon.Security.Identity.Fingerprint;
        SettingsData.Text = daemon.DataDirectory;
        AboutVersion.Text = $"GPL-3.0. Built on .NET {Environment.Version}, Avalonia 12.";
        MeshNameBox.Text = daemon.Security.Peers.MeshName ?? "";
        MeshNameBox.TextChanged += (_, _) => _meshNameEdited = true;

        // Polled rather than event-driven. The events exist, but they arrive on transport
        // threads and several fire for one visible change; a one-second sweep renders each
        // change once and cannot leave the list disagreeing with the links.
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refresh.Tick += (_, _) => Refresh();
        _refresh.Start();

        // Immediately, rather than on the next tick of the timer above. A link coming up is the
        // one change a person is actually watching for, and the Windows window does the same.
        _daemon.Links.Changed += () => Dispatcher.UIThread.Post(Refresh);

        _daemon.Ringer.StateChanged += ringing => Dispatcher.UIThread.Post(() =>
        {
            RingBanner.IsVisible = ringing;
            RingBannerDetail.Text = ringing
                ? "A device in your mesh asked this computer to make a noise."
                : "";
        });

        // Set before the handler is allowed to act, so restoring the stored preference does not
        // read as the user having just chosen it.
        TransportMode.SelectedIndex = _daemon.Transports.Current switch
        {
            TransportPreference.WiFi => 1,
            TransportPreference.Ble => 2,
            _ => 0,
        };
        _transportReady = true;

        StartupSwitch.IsChecked = Autostart.IsEnabled;

        ShowSelectedPage();
        Refresh();
    }

    /// <summary>
    /// Closing hides rather than quits, because the app's whole job is to keep holding links
    /// while you are doing something else. Quit is on the tray menu, deliberately.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!e.IsProgrammatic)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _refresh?.Stop();
        base.OnClosing(e);
    }

    // ──────────────────────────────── navigation

    /// <summary>
    /// Checks the things that were broken, from the live visual tree.
    ///
    /// A screenshot cannot catch a dead nav row, because switching pages in code bypasses the
    /// pointer entirely. What made the rows dead was the templated Border having no background,
    /// which is not hit-testable in Avalonia, so that is what this looks at.
    /// </summary>
    public string SelfTest()
    {
        var report = new System.Text.StringBuilder();
        var nav = new (string Name, RadioButton Button)[]
        {
            ("Home", NavHome), ("Activity", NavActivity), ("Notifications", NavNotifications),
            ("Files", NavFiles), ("Devices", NavDevices), ("Settings", NavSettings), ("About", NavAbout),
        };

        foreach (var (name, button) in nav)
        {
            var border = button.GetVisualDescendants().OfType<Border>().FirstOrDefault();
            var bounds = button.Bounds;

            // What actually decides a click: ask the window what is under the pointer at the
            // centre of the row, and at its far right where there is only padding. A row that
            // only answers on its label is the bug that made navigation feel random.
            var centre = button.TranslatePoint(new Point(bounds.Width / 2, bounds.Height / 2), this);
            var farRight = button.TranslatePoint(new Point(bounds.Width - 6, bounds.Height / 2), this);

            var hitCentre = centre.HasValue ? this.InputHitTest(centre.Value) : null;
            var hitRight = farRight.HasValue ? this.InputHitTest(farRight.Value) : null;

            bool centreOk = Reaches(hitCentre, button);
            bool rightOk = Reaches(hitRight, button);

            report.AppendLine(
                $"  {name,-14} centre={(centreOk ? "hits" : "MISSES")}  padding={(rightOk ? "hits" : "MISSES")}  " +
                $"border={border?.Bounds.Width:F0}x{border?.Bounds.Height:F0}  " +
                $"hit={hitCentre?.GetType().Name ?? "nothing"}");
        }

        // Clicked in sequence, never returning to Home between steps. The earlier version of
        // this test always went back to Home first, and Home was the fallback branch of the old
        // lookup - so every transition it tried was the one case that happened to work, and it
        // passed while the sidebar was visibly broken.
        report.AppendLine();
        SelectView(0);

        var walk = new[] { "Activity", "Notifications", "Files", "Devices", "Settings",
                           "About", "Home", "Notifications", "Activity", "Devices" };

        foreach (string want in walk)
        {
            var button = nav.First(n => n.Name == want).Button;
            var bounds = button.Bounds;
            var at = button.TranslatePoint(new Point(bounds.Width / 2, bounds.Height / 2), this);
            if (!at.HasValue) { report.AppendLine($"  click {want,-14} -> could not locate"); continue; }

            string before = SectionTitle.Text ?? "";

            try
            {
                var pointer = new Pointer(1, PointerType.Mouse, true);

                button.RaiseEvent(new PointerPressedEventArgs(button, pointer, this, at.Value, 0,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
                    KeyModifiers.None));
                button.RaiseEvent(new PointerReleasedEventArgs(button, pointer, this, at.Value, 0,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
                    KeyModifiers.None, MouseButton.Left));
            }
            catch (Exception ex)
            {
                report.AppendLine($"  click {want,-14} -> threw {ex.GetType().Name}");
                continue;
            }

            string after = SectionTitle.Text ?? "";
            report.AppendLine($"  {before,-14} + click {want,-14} -> {after,-14} {(after == want ? "OK" : "WRONG")}");
        }

        report.AppendLine();

        // Every page must actually exist and toggle, not just the one that happens to be shown.
        foreach (var (index, name) in new[] { (0, "Home"), (1, "Activity"), (2, "Notifications"),
                                              (3, "Files"), (4, "Devices"), (5, "Settings"), (6, "About") })
        {
            SelectView(index);
            var visible = new Control[] { PageHome, PageActivity, PageNotifications, PageFiles,
                                          PageDevices, PageSettings, PageAbout }
                          .Where(c => c.IsVisible).ToList();

            report.AppendLine($"  view {index} ({name,-14}) -> title=\"{SectionTitle.Text}\" visiblePages={visible.Count}");
        }

        SelectView(0);
        return report.ToString();
    }

    /// <summary>Whether a hit-test result sits inside the given control.</summary>
    private static bool Reaches(IInputElement? hit, Control target)
    {
        for (Visual? v = hit as Visual; v != null; v = v.GetVisualParent())
        {
            if (ReferenceEquals(v, target)) return true;
        }

        return false;
    }

    /// <summary>Switches page from outside, for the screenshot mode.</summary>
    public void SelectView(int index)
    {
        var (target, tag) = index switch
        {
            1 => (NavActivity, "Activity"),
            2 => (NavNotifications, "Notifications"),
            3 => (NavFiles, "Files"),
            4 => (NavDevices, "Devices"),
            5 => (NavSettings, "Settings"),
            6 => (NavAbout, "About"),
            _ => (NavHome, "Home"),
        };

        target.IsChecked = true;

        // Named explicitly rather than read back off the group, for the same reason as above.
        ShowPage(tag);
        Refresh();
    }

    private void OnNavChanged(object? sender, RoutedEventArgs e)
    {
        // The sender is the button that became checked. Asking it is exact; asking the group
        // which one is checked is a race.
        if (sender is RadioButton { IsChecked: true } button && button.Tag is string tag)
        {
            ShowPage(tag);
        }
    }

    private string SelectedTag => _currentTag;

    private void ShowSelectedPage() => ShowPage(_currentTag);

    private void ShowPage(string tag)
    {
        _currentTag = tag;

        PageHome.IsVisible = tag == "Home";
        PageActivity.IsVisible = tag == "Activity";
        PageNotifications.IsVisible = tag == "Notifications";
        PageFiles.IsVisible = tag == "Files";
        PageDevices.IsVisible = tag == "Devices";
        PageSettings.IsVisible = tag == "Settings";
        PageAbout.IsVisible = tag == "About";

        SectionTitle.Text = tag;

        if (_daemon is not null)
        {
            bool dark = ActualThemeVariant == ThemeVariant.Dark;
            if (tag == "Devices") { RefreshDevices(_daemon.Security, dark); RefreshPairing(); }
            if (tag == "Activity") RefreshActivity();
            if (tag == "Files") RefreshFiles();
        }

        // Showing the code is the signal that a human is standing here inviting something in,
        // so opening the page is what opens the window rather than a separate button.
        if (tag == "Devices") _daemon?.Security.Pairing.Open();
    }

    // ──────────────────────────────── refresh

    private void Refresh()
    {
        if (_daemon is null) return;

        var security = _daemon.Security;
        bool dark = ActualThemeVariant == ThemeVariant.Dark;

        int total = security.Peers.Count;

        // One question to one place. This used to compare a transport's connection count against
        // a Bluetooth flag and reach its own conclusion, which is how the sidebar could say
        // "Bluetooth" while every row on the Devices page called the same peer disconnected.
        bool anyLink = _daemon.Links.IsConnected;
        bool overBle = _daemon.Links.ActiveLink == LinkKind.Ble;

        SidebarDot.Fill = anyLink ? Accent(dark) : Warn(dark);
        SidebarStatus.Text = anyLink ? (overBle ? "Bluetooth" : "Connected") : "Waiting";

        RefreshHome(security, anyLink, overBle, total, dark);

        // Only what is on screen. The others are rebuilt the moment they are shown.
        string tag = SelectedTag;
        if (tag == "Devices") RefreshDevices(security, dark);
        if (tag == "Activity") RefreshActivity();
        if (tag == "Files") RefreshFiles();

        // Notifications carry a count on the nav item, so they are tracked from every page.
        RefreshNotifications();

        // Pending pairings must surface wherever you are, or a device knocking while you are on
        // Home is invisible until you happen to look.
        RefreshPending(security);

        RingBanner.IsVisible = _daemon.Ringer.IsRinging;
        StartupSwitch.IsChecked = Autostart.IsEnabled;

        SettingsAddress.Text = $"{LanAddress() ?? "no LAN address"} port {_daemon.Port}";
        SettingsClipboard.Text = ClipboardLine();
        SettingsKey.Text = _daemon.KeyProtectionStatus;

        if (PageDevices.IsVisible) RefreshPairing();
    }

    private void RefreshHome(CoreLib.Identity.PeerSecurity security, bool connected, bool overBle,
                             int paired, bool dark)
    {
        StatusRing.IsVisible = connected;
        IconTick.IsVisible = connected;
        IconSpinner.IsVisible = !connected;

        StatusRing.Stroke = Accent(dark);
        IconTick.Stroke = Accent(dark);
        StatusHeadline.Foreground = connected ? Accent(dark) : Warn(dark);

        // The mesh, not whichever device happened to answer. With more than two devices the peer
        // name is arbitrary - it names one of several - and it reads as though the app pairs with
        // a single machine, which is exactly the model this stopped being. Device names belong on
        // the Devices page, where they are the subject rather than the status.
        string mesh = security.Peers.MeshNameOrDefault;

        if (connected)
        {
            StatusHeadline.Text = overBle ? "Connected over Bluetooth" : "Connected";
            StatusDetail.Text = mesh;

            var last = _daemon.Activity.LastActivityUtc;
            StatusSub.Text = last.HasValue
                ? $"Last sync {Relative(last.Value)}"
                : overBle
                    ? "No network needed - text syncs straight over Bluetooth"
                    : "Ready - copy something to sync it";

            BtnPrimary.Content = "Add another device";
            FooterHint.Text = overBle
                ? "Text only over Bluetooth. Images need Wi-Fi."
                : "Nothing ever leaves your own devices";
        }
        else if (paired > 0)
        {
            StatusHeadline.Text = "Reconnecting";
            StatusDetail.Text = mesh;
            StatusSub.Text = paired == 1
                ? "Waiting for the other device to come back"
                : $"Waiting for any of {paired} devices to come back";

            BtnPrimary.Content = "Add another device";
            FooterHint.Text = "Same Wi-Fi, or within Bluetooth range";
        }
        else
        {
            StatusHeadline.Text = "No devices yet";
            StatusDetail.Text = mesh;
            StatusSub.Text = "Open Mesh Sync on another device and scan the pairing code";

            BtnPrimary.Content = "Add a device";
            FooterHint.Text = "Nothing ever leaves your own devices";
        }

        SentCount.Text = _daemon.Activity.SentCount.ToString();
        ReceivedCount.Text = _daemon.Activity.ReceivedCount.ToString();
        ClipboardNote.Text = ClipboardLine();
    }

    /// <summary>"2s", "4m", "3h" - the same shape the activity list uses.</summary>
    private static string Relative(DateTime atUtc)
    {
        var elapsed = DateTime.UtcNow - atUtc;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed.TotalSeconds < 5) return "just now";
        if (elapsed.TotalMinutes < 1) return $"{(int)elapsed.TotalSeconds}s ago";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours}h ago";
        return $"{(int)elapsed.TotalDays}d ago";
    }

    /// <summary>The LAN address, looked up at most every ten seconds rather than every sweep.</summary>
    private string? LanAddress()
    {
        if (DateTime.UtcNow - _addressCheckedUtc > TimeSpan.FromSeconds(10))
        {
            _cachedAddress = NetworkUtil.GetLocalLanAddress();
            _addressCheckedUtc = DateTime.UtcNow;
        }

        return _cachedAddress;
    }

    /// <summary>Devices waiting to be confirmed, shown from whichever page you are on.</summary>
    private void RefreshPending(CoreLib.Identity.PeerSecurity security)
    {
        var pending = security.PendingPairings;
        string signature = string.Join("|", pending.Select(p => p.Fingerprint));

        if (signature == _pendingSignature) return;
        _pendingSignature = signature;

        PendingList.ItemsSource = pending.Select(p => new PendingRow
        {
            Heading = p.Name ?? "A device",
            Fingerprint = p.ShortFingerprint,
        }).ToList();

        PendingCard.IsVisible = pending.Count > 0;

        // A device knocking is the one thing worth interrupting for: it expires in minutes and
        // there is nothing to see on any other page.
        if (pending.Count > 0 && SelectedTag != "Devices") SelectView(4);
    }

    private void RefreshDevices(CoreLib.Identity.PeerSecurity security, bool dark)
    {
        var rows = security.Peers.Peers.Select(peer =>
        {
            // Both tiers, because a device paired over Bluetooth alone is connected - it just is
            // not connected over Wi-Fi. Asking only the socket made every such device read as
            // last seen twenty minutes ago while the sidebar said Bluetooth.
            bool wifi = _daemon.Mesh.IsConnectedTo(peer.Fingerprint);
            bool ble = _daemon.IsBluetoothConnectedTo(peer.Fingerprint);
            bool up = wifi || ble;

            return new DeviceRow
            {
                Name = _daemon.Mesh.NameOf(peer.Fingerprint) ?? peer.Name ?? "Unnamed device",
                Fingerprint = DeviceIdentity.Shorten(peer.Fingerprint),
                Detail = wifi
                    ? $"Connected over Wi-Fi, {peer.LastAddress ?? "unknown address"}"
                    : ble
                        ? "Connected over Bluetooth - text only"
                        : $"Last seen {Ago(peer.LastSeenUtc)}, {peer.LastAddress ?? "no recorded address"}",
                Connected = up,
                Dot = up ? Accent(dark) : Faint(dark),
            };
        }).ToList();

        string signature = string.Join("|", rows.Select(r => $"{r.Fingerprint}:{r.Connected}:{r.Name}:{r.Detail}"));
        if (signature != _devicesSignature)
        {
            _devicesSignature = signature;
            DeviceList.ItemsSource = rows;
            DevicesEmpty.IsVisible = rows.Count == 0;
        }

        // Not overwritten once it has been typed into, or the sweep would fight the caret.
        if (!_meshNameEdited) MeshNameBox.Text = security.Peers.MeshName ?? "";
    }

    private void RefreshActivity()
    {
        var entries = _daemon.Activity.Snapshot();

        string signature = string.Join("|", entries.Select(e => $"{e.AtUtc.Ticks}:{e.SizeBytes}"));
        if (signature == _activitySignature) return;
        _activitySignature = signature;

        ActivityList.ItemsSource = entries.Select(e => new ActivityRow
        {
            Glyph = e.Kind == SyncItemKind.Image ? "\u25A3" : "\u29C9",
            Title = string.IsNullOrWhiteSpace(e.Title) ? "(empty)" : e.Title,
            // "Sent"/"Received" rather than the Windows daemon's "to phone"/"From phone":
            // a mesh has more than one other device and naming one of them would be a guess.
            Sub = $"{(e.Direction == SyncDirection.Sent ? "Sent" : "Received")} \u00B7 {e.SizeLabel}",
            Age = e.RelativeAge,
        }).ToList();

        ActivityEmpty.IsVisible = entries.Count == 0;
    }

    private void RefreshPairing()
    {
        string uri = _daemon.PairingUri;

        // Regenerated only when it changes. The address can move under a DHCP lease, and
        // re-encoding a QR every second for a string that has not changed is pure waste.
        if (uri != _lastPairingUri)
        {
            _lastPairingUri = uri;
            QrImage.Source = RenderQr(uri);
        }

        var remaining = _daemon.Security.Pairing.Remaining;
        PairWindowText.Text = remaining > TimeSpan.Zero
            ? $"Accepting new devices for {remaining.Minutes}m {remaining.Seconds:00}s"
            : "Not accepting new devices. Leave and return to this page to start again.";
    }

    private string ClipboardLine()
    {
        var bridge = _daemon.ClipboardBridge;

        if (!bridge.IsAvailable)
        {
            return "Clipboard sync is off: no helper is installed. Install wl-clipboard to turn it on.";
        }

        return bridge.SupportsWatching
            ? $"Clipboard sync is on, watched through {bridge.Name}."
            : $"Clipboard sync is on, polled through {bridge.Name}.";
    }

    /// <summary>
    /// A PNG rather than the System.Drawing renderer the Windows daemon uses, because that half
    /// of QRCoder is Windows only. This half is portable.
    /// </summary>
    private static Bitmap? RenderQr(string payload)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
            byte[] png = new PngByteQRCode(data).GetGraphic(8);

            using var stream = new MemoryStream(png);
            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Log.Write("Shell", "Could not render the pairing code", ex);
            return null;
        }
    }

    private static IBrush Accent(bool dark) => new SolidColorBrush(Color.Parse(dark ? "#4FA894" : "#2F7A6B"));
    private static IBrush Warn(bool dark) => new SolidColorBrush(Color.Parse(dark ? "#D69A5A" : "#B0722F"));
    private static IBrush Faint(bool dark) => new SolidColorBrush(Color.Parse(dark ? "#726E68" : "#A39D94"));

    private static string Ago(DateTimeOffset when)
    {
        if (when == default) return "never";

        var span = DateTimeOffset.UtcNow - when;
        if (span < TimeSpan.FromMinutes(1)) return "moments ago";
        if (span < TimeSpan.FromHours(1)) return $"{span.TotalMinutes:F0} minutes ago";
        if (span < TimeSpan.FromDays(1)) return $"{span.TotalHours:F0} hours ago";
        return $"{span.TotalDays:F0} days ago";
    }

    // ──────────────────────────────── commands


    private void RefreshNotifications()
    {
        var entries = _daemon.Notifications.Snapshot();

        // Rebuilt only when the set actually moves. It used to be rebuilt on every tick, which
        // was harmless until a row grew a text box in it.
        string signature = string.Join("|", entries.Select(e => $"{e.Key}:{e.Age}"));

        if (signature != _notificationsSignature)
        {
            _notificationsSignature = signature;

            // Drafts for notifications that have gone are dropped rather than left to
            // accumulate against keys nothing will ever answer to.
            var live = entries.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
            foreach (string stale in _drafts.Keys.Where(k => !live.Contains(k)).ToList()) _drafts.Remove(stale);

            NotificationList.ItemsSource = entries.Select(e => new NotificationRow
            {
                Key = e.Key,
                Heading = string.IsNullOrWhiteSpace(e.Title) ? e.AppName : e.Title,
                Body = e.Text,
                Source = $"{e.AppName} on {e.From} \u00B7 {e.Age}",
                CanReply = e.CanReply,
                ReplyLabel = string.IsNullOrWhiteSpace(e.ReplyLabel) ? "Reply" : e.ReplyLabel,
                ReplyPlaceholder = $"Reply to {(string.IsNullOrWhiteSpace(e.Title) ? e.AppName : e.Title)}\u2026",
                Draft = _drafts.TryGetValue(e.Key, out string? draft) ? draft : "",
            }).ToList();
        }

        NotificationsEmpty.IsVisible = entries.Count == 0;

        // The Windows daemon puts the count on the nav item, which is the only place it shows
        // when the window is on another page.
        NavNotificationsLabel.Text = entries.Count == 0 ? "Notifications" : $"Notifications ({entries.Count})";
    }

    private void RefreshFiles()
    {
        var files = _daemon.ReceivedFiles;

        string signature = string.Join("|", files.Select(f => $"{f.Name}:{f.Size}"));
        if (signature != _filesSignature)
        {
            _filesSignature = signature;
            FileList.ItemsSource = files.Select(f => new FileRow
        {
            Name = f.Name,
            Sub = $"{f.Size / 1024.0:F1} KB \u00B7 in your Downloads",
            Path = f.Path,
            }).ToList();

            FilesEmpty.IsVisible = files.Count == 0;
        }

        // Every reachable device, not only the ones already on Wi-Fi. A file does need the
        // socket, but a device holding a Bluetooth link can be asked to raise Wi-Fi - which
        // SendFileAsync does - so leaving it out of the list offered no way to find that out.
        var targets = _daemon.Security.Peers.Peers
            .Where(p => _daemon.IsConnectedTo(p.Fingerprint))
            .Select(p => _daemon.Mesh.NameOf(p.Fingerprint) ?? p.Name ?? DeviceIdentity.Shorten(p.Fingerprint))
            .ToList();

        if (!FileTargetBox.Items.Cast<object?>().Select(i => i?.ToString()).SequenceEqual(targets))
        {
            object? previous = FileTargetBox.SelectedItem;
            FileTargetBox.ItemsSource = targets;
            FileTargetBox.SelectedItem = targets.Contains(previous?.ToString() ?? "")
                ? previous
                : targets.FirstOrDefault();
        }
    }

    private bool _transportReady;

    /// <summary>
    /// Applies a connection preference the moment it is chosen.
    ///
    /// The daemon starts and stops each tier in place, so this means something immediately rather
    /// than at the next start - which is what the same control does on Windows.
    /// </summary>
    private void OnTransportModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_transportReady || _daemon is null) return;

        var preference = (TransportMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "WiFi" => TransportPreference.WiFi,
            "Ble" => TransportPreference.Ble,
            _ => TransportPreference.Both,
        };

        _daemon.Transports.Set(preference);
        Refresh();
    }

    private void OnStopRinging(object? sender, RoutedEventArgs e) => _daemon.Ringer.Stop();

    /// <summary>
    /// Sends what is in the box, through the app that posted the notification.
    ///
    /// <para>Nothing here talks to WhatsApp. The phone pulls the reply action the notification
    /// already carried, which is what the notification shade does. The outcome is reported on the
    /// row rather than in a dialog, because a reply that did not go is worth seeing next to the
    /// text that did not go with it.</para>
    /// </summary>
    private async void OnReply(object? sender, RoutedEventArgs e)
    {
        string key = TagOf(sender) ?? "";
        if (key.Length == 0) return;

        var rows = NotificationList.ItemsSource as IReadOnlyList<NotificationRow>;
        var row = rows?.FirstOrDefault(r => r.Key == key);
        if (row == null) return;

        string text = (row.Draft ?? "").Trim();
        if (text.Length == 0) return;

        var (ok, message) = await _daemon.ReplyToNotificationAsync(key, text).ConfigureAwait(true);

        row.ReplyStatus = message;

        if (ok)
        {
            row.Draft = "";
            _drafts.Remove(key);
        }
        else
        {
            _drafts[key] = text;
        }

        // The row objects are plain and do not notify, so the list is rebuilt to show the
        // outcome. Cheap: there are only ever a handful of these.
        _notificationsSignature = "";
        var kept = rows!.ToList();
        NotificationList.ItemsSource = null;
        NotificationList.ItemsSource = kept;
    }

    /// <summary>Enter sends, because that is what a reply box does everywhere else.</summary>
    private void OnReplyKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return && e.Key != Key.Enter) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

        e.Handled = true;

        if (sender is TextBox box && box.Tag is string key)
        {
            var rows = NotificationList.ItemsSource as IReadOnlyList<NotificationRow>;
            var row = rows?.FirstOrDefault(r => r.Key == key);
            if (row != null) row.Draft = box.Text ?? "";

            OnReply(box, new RoutedEventArgs());
        }
    }

    private async void OnClearNotifications(object? sender, RoutedEventArgs e)
    {
        await _daemon.DismissAllNotificationsAsync().ConfigureAwait(true);
        Refresh();
    }

    private async void OnDismissNotification(object? sender, RoutedEventArgs e)
    {
        string? key = TagOf(sender);
        if (key == null) return;

        await _daemon.DismissNotificationAsync(key).ConfigureAwait(true);
        Refresh();
    }

    private async void OnSendFile(object? sender, RoutedEventArgs e)
    {
        string? target = FileTargetBox.SelectedItem?.ToString();
        if (target == null)
        {
            FileSendStatus.Text = "Nothing is connected. A file needs Wi-Fi up on both ends.";
            return;
        }

        var peer = _daemon.FindPeer(target);
        if (peer == null) { FileSendStatus.Text = "That device is no longer paired."; return; }

        var storage = GetTopLevel(this)?.StorageProvider;
        if (storage == null) return;

        var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Send a file",
            AllowMultiple = false,
        }).ConfigureAwait(true);

        string? path = picked.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;

        FileSendStatus.Text = $"Sending {System.IO.Path.GetFileName(path)}...";

        var result = await _daemon.SendFileAsync(peer.Fingerprint, path).ConfigureAwait(true);

        FileSendStatus.Text = result switch
        {
            FileSendResult.Sent => $"{System.IO.Path.GetFileName(path)} arrived.",
            FileSendResult.Refused => "That device turned it down.",
            FileSendResult.NoAnswer => "That device did not answer.",
            FileSendResult.Unreachable => "That device is not reachable over Wi-Fi.",
            FileSendResult.TooLarge => "That file is too large to offer.",
            _ => "The transfer failed. Nothing is queued anywhere; try it again.",
        };

        Refresh();
    }

    private void OnOpenFile(object? sender, RoutedEventArgs e)
    {
        string? path = TagOf(sender);
        if (path == null) return;

        try
        {
            string folder = System.IO.Path.GetDirectoryName(path) ?? path;

            // xdg-open on Linux, open on macOS. Both take a directory and show it.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                OperatingSystem.IsMacOS() ? "open" : "xdg-open", folder)
            { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Write("Shell", "Could not open the folder", ex);
        }
    }

    private void OnStartupChanged(object? sender, RoutedEventArgs e)
    {
        if (_daemon is null) return;

        bool wanted = StartupSwitch.IsChecked == true;
        if (wanted != Autostart.IsEnabled) Autostart.Set(wanted);
    }

    private static string? TagOf(object? sender) => (sender as Button)?.Tag as string;

    private void OnHide(object? sender, RoutedEventArgs e) => Hide();

    // 4, not 2. Two is Notifications, and this is the Home page's primary "Pair a device"
    // button - so it used to land on the one page that cannot pair. ShowPage opens the pairing
    // window only for "Devices", which is why the miss was silent: the phone knocked forever
    // against "not a paired device, and pairing is not open" and nothing here looked wrong.
    private void OnGoToDevices(object? sender, RoutedEventArgs e) => SelectView(4);

    /// <summary>
    /// Drags the window by its caption, and maximises on a double click.
    ///
    /// <para>The desktop's title bar is gone - this window draws its own at 42px, the same bar
    /// the Windows daemon draws through <c>WindowChrome</c> - so moving and maximising stopped
    /// being the window manager's job and have to happen here.</para>
    ///
    /// <para>Caption buttons are unaffected: Avalonia's Button marks the pointer press handled,
    /// so it never reaches this and a click on Minimise cannot start a drag.</para>
    /// </summary>
    private void OnCaptionPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        BeginMoveDrag(e);
    }

    private void OnMinimise(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private async void OnSend(object? sender, RoutedEventArgs e)
    {
        string text = SendBox.Text ?? "";
        if (text.Trim().Length == 0) return;

        int sent = await _daemon.SendTextAsync(text).ConfigureAwait(true);

        SendBox.Text = "";
        StatusSub.Text = sent > 0
            ? $"Sent to {sent} device(s)."
            : "Nothing is connected, so nothing was sent - and nothing is queued anywhere.";
    }

    private async void OnRing(object? sender, RoutedEventArgs e)
    {
        var peer = _daemon.FindPeer(TagOf(sender) ?? "");
        if (peer == null) return;

        await _daemon.RingAsync(peer.Fingerprint, on: true).ConfigureAwait(true);
    }

    private void OnForget(object? sender, RoutedEventArgs e)
    {
        var peer = _daemon.FindPeer(TagOf(sender) ?? "");
        if (peer == null) return;

        _daemon.Security.Peers.Forget(peer.Fingerprint);
        Refresh();
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        Log.Write("Shell", _daemon.Confirm(TagOf(sender) ?? "").Message);
        Refresh();
    }

    private void OnReject(object? sender, RoutedEventArgs e)
    {
        _daemon.Reject(TagOf(sender) ?? "");
        Refresh();
    }

    private void OnJoin(object? sender, RoutedEventArgs e)
    {
        var result = _daemon.Join(JoinBox.Text ?? "");

        JoinResult.Text = result.Message;
        if (result.Ok) JoinBox.Text = "";
        Refresh();
    }

    private void OnMeshNameCommitted(object? sender, RoutedEventArgs e)
    {
        string name = (MeshNameBox.Text ?? "").Trim();
        if (name.Length == 0) { _meshNameEdited = false; return; }

        _daemon.Security.Peers.MeshName = name;
        _meshNameEdited = false;
    }

    private async void OnCopyUri(object? sender, RoutedEventArgs e)
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;

        await clipboard.SetTextAsync(_daemon.PairingUri).ConfigureAwait(true);
        CopyUriButton.Content = "Copied";
    }
}
