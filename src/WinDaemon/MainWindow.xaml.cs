using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CoreLib;
using CoreLib.Diagnostics;
using CoreLib.Identity;
using CoreLib.Transport;
using QRCoder;

namespace WinDaemon
{
    public partial class MainWindow : Window
    {
        private const double SidebarWide = 196;
        private const double SidebarNarrow = 62;

        private readonly SyncActivityLog _activity;
        private readonly string _ipAddress;
        private readonly string _pairingCode;

        private readonly ObservableCollection<ActivityRow> _rows = new();
        private readonly DispatcherTimer _ageTimer;
        private Storyboard? _spinner;
        private bool _sidebarCollapsed;
        private bool _suppressModeEvent;

        public event Action? ExitRequested;

        // The transport used to be passed in and never touched. It went when the single
        // connection became a link per peer: the window reads LinkState, which is the
        // one place that knows whether anything is reachable and over which tier.
        public MainWindow(string ipAddress, string pairingCode, SyncActivityLog activity)
        {
            InitializeComponent();

            _ipAddress = ipAddress;
            _pairingCode = pairingCode;
            _activity = activity;

            ActivityList.ItemsSource = _rows;
            DeviceList.ItemsSource = _devices;
            PendingList.ItemsSource = _pending;
            NotificationList.ItemsSource = _notifications;

            IpText.Text = ipAddress;
            CodeText.Text = Shorten(pairingCode);
            SelfFingerprint.Text = Program.Security?.Identity.ShortFingerprint ?? "unavailable";
            MeshNameBox.Text = Program.Security?.Peers.MeshName ?? "";
            RenderQrCode();

            StartupSwitch.IsChecked = Program.IsStartupEnabled();
            AboutVersion.Text = $"Version {AppVersion()}";

            switch (ThemeManager.Current)
            {
                case ThemeManager.Preference.Light: ThemeLight.IsChecked = true; break;
                case ThemeManager.Preference.Dark: ThemeDark.IsChecked = true; break;
                default: ThemeSystem.IsChecked = true; break;
            }

            _suppressModeEvent = true;
            TransportMode.SelectedIndex = Program.Transports.Current switch
            {
                TransportPreference.WiFi => 1,
                TransportPreference.Ble => 2,
                _ => 0
            };
            _suppressModeEvent = false;

            Program.Links.Changed += LinkState_Changed;
            _activity.Changed += Activity_Changed;
            Ringer.Changed += Ringer_Changed;
            MirroredNotifications.Changed += Notifications_Changed;

            // The list has to follow pairing as well as connectivity: a device added from
            // another window, or forgotten, changes it without any link going up or down.
            if (Program.Security != null)
            {
                Program.Security.Peers.Changed += Peers_Changed;
                Program.Security.PairingRequested += Pairing_Requested;
                Program.Security.Pairing.Changed += Pairing_WindowChanged;
            }

            // Relative timestamps go stale silently otherwise.
            _ageTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            _ageTimer.Tick += (_, _) =>
            {
                RefreshActivity();

                // Held open for as long as the code is actually on screen. Opening it once on
                // navigation is not enough: the window lapses after a few minutes, so anyone
                // who left the QR up while installing the phone app would find pairing quietly
                // refused with the code still in front of them.
                if (PageDevices?.Visibility == Visibility.Visible) Program.Security?.Pairing.Open();
            };

            _ageTimer.Start();

            Loaded += (_, _) =>
            {
                StartSpinner();
                RefreshStatus();
                RefreshActivity();
                RefreshDevices();
                RefreshPending();
                RefreshRinging();
                RefreshNotifications();
            };
        }

        // ────────────────────────────── navigation

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (PageHome == null) return; // fires once during template application

            string section = (sender as FrameworkElement)?.Tag as string ?? "Home";
            SectionTitle.Text = section;

            PageHome.Visibility = section == "Home" ? Visibility.Visible : Visibility.Collapsed;
            PageActivity.Visibility = section == "Activity" ? Visibility.Visible : Visibility.Collapsed;
            PageNotifications.Visibility = section == "Notifications" ? Visibility.Visible : Visibility.Collapsed;
            PageFiles.Visibility = section == "Files" ? Visibility.Visible : Visibility.Collapsed;
            PageDevices.Visibility = section == "Devices" ? Visibility.Visible : Visibility.Collapsed;

            // Browsing starts at the device list every time it is opened. A listing goes stale
            // the moment the other device changes anything, and there is no way to be told.
            if (section == "Files") ShowBrowseDevices();
            PageSettings.Visibility = section == "Settings" ? Visibility.Visible : Visibility.Collapsed;
            PageAbout.Visibility = section == "About" ? Visibility.Visible : Visibility.Collapsed;

            if (section == "Activity") RefreshActivity();
            if (section == "Notifications") RefreshNotifications();

            // Showing the pairing code is what tells this device a new peer has been invited.
            // The listener has no other way to know the stranger now knocking is the one the
            // user just pointed a camera at, so the window follows the page exactly: open
            // while the code is on screen, shut the moment it is not.
            if (section == "Devices") Program.Security?.Pairing.Open();
            else Program.Security?.Pairing.Close();
        }

        // ────────────────────────────── devices

        private readonly ObservableCollection<DeviceRow> _devices = new();

        /// <summary>
        /// Redraws the device list from the registry and the live link state.
        ///
        /// Only one device can be reported as connected, because <see cref="LinkState"/>
        /// tracks whether anything is reachable rather than which peers are. That is honest
        /// enough while the mesh is small and is the next thing to grow when it is not.
        /// </summary>
        private void RefreshDevices()
        {
            var peers = Program.Security?.Peers;
            _devices.Clear();

            if (peers != null)
            {
                string? connectedName = Program.Links.IsConnected ? Program.Links.PeerName : null;
                var accent = (System.Windows.Media.Brush)FindResource("B.Accent");
                var faint = (System.Windows.Media.Brush)FindResource("B.TextFaint");

                foreach (var peer in peers.Peers.OrderBy(p => p.Name ?? p.Fingerprint))
                {
                    bool live = connectedName != null &&
                                string.Equals(connectedName, peer.Name, StringComparison.OrdinalIgnoreCase);

                    string via = Program.Links.ActiveLink == LinkKind.Ble ? "Bluetooth" : "Wi-Fi";

                    _devices.Add(new DeviceRow
                    {
                        Name = string.IsNullOrWhiteSpace(peer.Name)
                            ? CoreLib.Identity.DeviceIdentity.Shorten(peer.Fingerprint)
                            : peer.Name!,
                        Detail = live
                            ? $"Connected over {via} · {CoreLib.Identity.DeviceIdentity.Shorten(peer.Fingerprint)}"
                            : $"Last seen {Relative(peer.LastSeenUtc.UtcDateTime)} · {CoreLib.Identity.DeviceIdentity.Shorten(peer.Fingerprint)}",
                        // The desktop row is wide enough for the full short form; the phone's
                        // is not, which is why it trims further.
                        Fingerprint = peer.Fingerprint,
                        Dot = live ? accent : faint,
                        // Only offered for a device that is actually reachable. Ringing something
                        // that cannot hear you is a button that does nothing.
                        RingVisibility = live ? Visibility.Visible : Visibility.Collapsed
                    });
                }
            }

            DevicesEmpty.Visibility = _devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            DevicesSub.Text = _devices.Count switch
            {
                0 => "Nothing paired yet.",
                1 => "One device paired into this mesh.",
                _ => $"{_devices.Count} devices paired into this mesh."
            };
        }

        // ────────────────────────────── pairing confirmation

        private readonly ObservableCollection<PendingRow> _pending = new();

        private void Pairing_Requested(CoreLib.Identity.PendingPairing pending) =>
            Dispatcher.BeginInvoke(RefreshPending);

        private void Pairing_WindowChanged() => Dispatcher.BeginInvoke(RefreshPending);

        /// <summary>
        /// Redraws the list of devices waiting to be allowed in.
        ///
        /// The card stays hidden until something is actually knocking, so the ordinary case -
        /// showing the code to a device that has not connected yet - looks exactly as it did.
        /// </summary>
        private void RefreshPending()
        {
            var waiting = Program.Security?.PendingPairings ?? Array.Empty<CoreLib.Identity.PendingPairing>();

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

            PendingCard.Visibility = _pending.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            PendingHeading.Text = _pending.Count > 1
                ? $"{_pending.Count} devices want to join"
                : "A device wants to join";
        }

        private void BtnConfirmPairing_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string fingerprint) return;

            if (Program.Security?.ConfirmPairing(fingerprint) == true)
            {
                // Refused and told to come back, so it is waiting on a retry rather than on a
                // socket. Nudging the dial loop turns "up to twenty seconds" into "now", which
                // is the difference between the confirmation looking like it worked and not.
                Program.SignalDial();
            }

            RefreshPending();
            RefreshDevices();
            RefreshStatus();
        }

        private void BtnRejectPairing_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string fingerprint) return;

            Program.Security?.RejectPairing(fingerprint);
            RefreshPending();
        }

        /// <summary>
        /// Forgets a device, which revokes its key rather than merely hiding it from a list.
        /// Confirmed first, because the only way back is to pair it again.
        /// </summary>
        private void BtnForgetDevice_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string fingerprint) return;

            var peers = Program.Security?.Peers;
            var peer = peers?.Find(fingerprint);
            if (peers == null || peer == null) return;

            string name = string.IsNullOrWhiteSpace(peer.Name)
                ? CoreLib.Identity.DeviceIdentity.Shorten(fingerprint)
                : peer.Name!;

            // Fully qualified: WinForms is referenced for the tray icon, so MessageBox is
            // ambiguous between it and WPF.
            var answer = System.Windows.MessageBox.Show(
                $"Forget {name}?\n\nIt will stop syncing immediately, and pairing it again means scanning a new code.",
                "Mesh Sync", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes) return;

            peers.Forget(fingerprint);
            RefreshDevices();
            RefreshStatus();
        }

        // ────────────────────────────── mesh name

        /// <summary>
        /// Saves the mesh name, and redraws the QR because the code carries it.
        ///
        /// Committed on losing focus rather than on every keystroke: the registry writes to disk
        /// on each change, and a name is typed a character at a time.
        /// </summary>
        private void MeshNameBox_LostFocus(object sender, RoutedEventArgs e) => CommitMeshName();

        private void MeshNameBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;

            CommitMeshName();
            System.Windows.Input.Keyboard.ClearFocus();
        }

        private void CommitMeshName()
        {
            var peers = Program.Security?.Peers;
            if (peers == null) return;

            string typed = MeshNameBox.Text.Trim();
            if (typed == peers.MeshName) return;

            peers.MeshName = typed;

            // Reflect whatever was actually stored - it is trimmed and length-capped there.
            MeshNameBox.Text = peers.MeshName;

            // The pairing code embeds the name, so a device scanning it now joins under the
            // new one rather than the name it happened to have when the page was opened.
            RenderQrCode();
            RefreshStatus();
        }

        private void BtnCollapse_Click(object sender, RoutedEventArgs e)
        {
            _sidebarCollapsed = !_sidebarCollapsed;
            Sidebar.Width = _sidebarCollapsed ? SidebarNarrow : SidebarWide;

            // The whole brand block goes, not just the wordmark. At 62px the mark and the
            // collapse button both wanted the same 14px and drew on top of each other.
            BrandBlock.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
            BtnCollapse.HorizontalAlignment = _sidebarCollapsed
                ? System.Windows.HorizontalAlignment.Center
                : System.Windows.HorizontalAlignment.Right;
            BtnCollapse.ToolTip = _sidebarCollapsed ? "Expand the sidebar" : "Collapse the sidebar";

            // Labels go, icons stay, so the rail still works when narrow.
            foreach (var nav in new[] { NavHome, NavActivity, NavDevices, NavSettings, NavAbout })
            {
                if (nav.Content is System.Windows.Controls.Panel panel && panel.Children.Count > 1)
                {
                    panel.Children[1].Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
                }
            }

            SidebarStatus.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
            SidebarDot.Margin = _sidebarCollapsed ? new Thickness(0) : new Thickness(0, 0, 9, 0);
            SidebarDot.HorizontalAlignment = _sidebarCollapsed
                ? System.Windows.HorizontalAlignment.Center
                : System.Windows.HorizontalAlignment.Left;
        }

        // ────────────────────────────── status

        private void LinkState_Changed() =>
            Dispatcher.BeginInvoke(() => { RefreshStatus(); RefreshDevices(); });

        private void Peers_Changed() => Dispatcher.BeginInvoke(() => { RefreshDevices(); RefreshStatus(); });

        private void Activity_Changed(object? sender, EventArgs e) =>
            Dispatcher.BeginInvoke(() => { RefreshActivity(); RefreshStatus(); });

        private void RefreshStatus()
        {
            bool connected = Program.Links.IsConnected;
            bool overBle = Program.Links.ActiveLink == LinkKind.Ble;

            IconTick.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            IconSpinner.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
            StatusRing.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;

            string accent = connected ? "B.Accent" : "B.Warn";
            string soft = connected ? "B.AccentSoft" : "B.WarnSoft";
            StatusHalo.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, soft);
            StatusHeadline.SetResourceReference(ForegroundProperty, accent);
            SidebarDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, accent);

            // The mesh, not whichever device happened to answer. With more than two devices the
            // peer name is arbitrary - it names one of several - and it reads as though the app
            // pairs with a single machine, which is exactly the model this stopped being.
            var peers = Program.Security?.Peers;
            string mesh = peers?.MeshNameOrDefault ?? "your mesh";
            int paired = peers?.Count ?? 0;

            if (connected)
            {
                StatusHeadline.Text = overBle ? "Connected over Bluetooth" : "Connected";
                StatusDetail.Text = mesh;

                var last = _activity.LastActivityUtc;
                StatusSub.Text = last.HasValue
                    ? $"Last sync {Relative(last.Value)}"
                    : overBle
                        ? "No network needed - text syncs straight over Bluetooth"
                        : "Ready - copy something to sync it";

                BtnPrimary.Content = "Add another device";
                SidebarStatus.Text = overBle ? "Bluetooth" : "Connected";
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
                SidebarStatus.Text = "Waiting";
                FooterHint.Text = "Same Wi-Fi, or within Bluetooth range";
            }
            else
            {
                StatusHeadline.Text = "No devices yet";
                StatusDetail.Text = mesh;
                StatusSub.Text = "Open Mesh Sync on another device and scan the pairing code";
                BtnPrimary.Content = "Add a device";
                SidebarStatus.Text = "Waiting";
                FooterHint.Text = "Nothing ever leaves your own devices";
            }

            // Sending a file needs a Wi-Fi link, not merely a Bluetooth one, so the button and
            // the drop hint appear only when there is somewhere for a file to actually go.
            var sendable = connected && !overBle ? Visibility.Visible : Visibility.Collapsed;
            BtnSendFile.Visibility = sendable;
            DropHint.Visibility = sendable;

            SentCount.Text = _activity.SentCount.ToString();
            ReceivedCount.Text = _activity.ReceivedCount.ToString();
        }

        private void StartSpinner()
        {
            var animation = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2.4))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            _spinner = new Storyboard();
            _spinner.Children.Add(animation);
            Storyboard.SetTarget(animation, IconSpinner);
            Storyboard.SetTargetProperty(animation,
                new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
            _spinner.Begin(this, true);
        }

        // ────────────────────────────── activity

        private void RefreshActivity()
        {
            var snapshot = _activity.Snapshot();

            _rows.Clear();
            foreach (var entry in snapshot)
            {
                _rows.Add(new ActivityRow
                {
                    Glyph = entry.Kind == SyncItemKind.Image ? "▣" : "⧉",
                    Title = string.IsNullOrWhiteSpace(entry.Title) ? "(empty)" : entry.Title,
                    Sub = $"{(entry.Direction == SyncDirection.Sent ? "Sent to phone" : "From phone")} · {entry.SizeLabel}",
                    Age = entry.RelativeAge
                });
            }

            ActivityEmpty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string Relative(DateTime atUtc)
        {
            var elapsed = DateTime.UtcNow - atUtc;
            if (elapsed.TotalSeconds < 5) return "just now";
            if (elapsed.TotalMinutes < 1) return $"{(int)elapsed.TotalSeconds}s ago";
            if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes}m ago";
            return $"{(int)elapsed.TotalHours}h ago";
        }

        private static string AppVersion()
        {
            try
            {
                var name = Assembly.GetExecutingAssembly().GetName();
                return name.Version?.ToString(3) ?? "1.0.0";
            }
            catch
            {
                return "1.0.0";
            }
        }

        // ────────────────────────────── transport mode

        private void TransportMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressModeEvent || TransportHint == null) return;

            var preference = TransportMode.SelectedIndex switch
            {
                1 => TransportPreference.WiFi,
                2 => TransportPreference.Ble,
                _ => TransportPreference.Both
            };

            Program.Transports.Set(preference);

            TransportHint.Text = preference switch
            {
                TransportPreference.WiFi =>
                    "Bluetooth is off. Nothing will sync when there is no network.",
                TransportPreference.Ble =>
                    "Wi-Fi is off. Text syncs with no network at all, but images will not send.",
                _ =>
                    "Wi-Fi carries text and images. Bluetooth carries text with no network at all."
            };
        }

        // ────────────────────────────── pairing

        private void RenderQrCode()
        {
            try
            {
                // The mesh name rides along, so a device that scans this joins something with
                // a name rather than pairing with an anonymous address.
                string mesh = Program.Security?.Peers.MeshName ?? "";
                string payload =
                    $"meshsync://pair?ip={Uri.EscapeDataString(_ipAddress)}" +
                    $"&key={Uri.EscapeDataString(_pairingCode)}" +
                    (mesh.Length > 0 ? $"&mesh={Uri.EscapeDataString(mesh)}" : "");
                using var generator = new QRCodeGenerator();
                var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
                using var qr = new QRCode(data);
                using var bitmap = qr.GetGraphic(10, System.Drawing.Color.FromArgb(0x26, 0x25, 0x23),
                                                 System.Drawing.Color.White, true);

                using var stream = new MemoryStream();
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                stream.Position = 0;

                var source = new BitmapImage();
                source.BeginInit();
                source.CacheOption = BitmapCacheOption.OnLoad;
                source.StreamSource = stream;
                source.EndInit();
                source.Freeze();

                QrImage.Source = source;
            }
            catch (Exception ex)
            {
                Log.Write("UI", "QR code generation failed", ex);
                QrImage.Visibility = Visibility.Collapsed;
                QrError.Visibility = Visibility.Visible;
            }
        }

        /// <summary>The pairing key is a ~100 character Base64 blob; showing it raw is noise.</summary>
        private static string Shorten(string code) =>
            string.IsNullOrEmpty(code) ? "unavailable"
            : code.Length <= 22 ? code
            : $"{code.Substring(0, 10)}…{code.Substring(code.Length - 8)}";

        private void BtnPrimary_Click(object sender, RoutedEventArgs e)
        {
            NavDevices.IsChecked = true;
        }

        // ────────────────────────────── mirrored notifications

        private readonly ObservableCollection<NotificationRow> _notifications = new();

        private void Notifications_Changed() => Dispatcher.BeginInvoke(RefreshNotifications);

        private void RefreshNotifications()
        {
            var snapshot = MirroredNotifications.Snapshot();

            _notifications.Clear();
            foreach (var entry in snapshot)
            {
                var n = entry.Notification;

                _notifications.Add(new NotificationRow
                {
                    Heading = n.Title.Length > 0 ? n.Title : n.AppName,
                    Body = n.Text,
                    Source = n.Title.Length > 0
                        ? $"{n.AppName} · {entry.PeerName} · {entry.RelativeAge}"
                        : $"{entry.PeerName} · {entry.RelativeAge}",
                    Key = n.Key
                });
            }

            NotificationsEmpty.Visibility = _notifications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnClearNotifications.Visibility = _notifications.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            // A count in the sidebar, because the page is otherwise the only way to find out
            // something arrived and nobody navigates speculatively.
            NavNotificationsLabel.Text = _notifications.Count == 0
                ? "Notifications"
                : $"Notifications ({_notifications.Count})";
        }

        private void BtnDismissNotification_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string key) return;
            MirroredNotifications.Remove(key);
        }

        private void BtnClearNotifications_Click(object sender, RoutedEventArgs e) =>
            MirroredNotifications.Clear();

        // ────────────────────────────── finding a device

        private void Ringer_Changed() => Dispatcher.BeginInvoke(RefreshRinging);

        private void RefreshRinging() =>
            RingBanner.Visibility = Ringer.IsRinging ? Visibility.Visible : Visibility.Collapsed;

        private void BtnStopRinging_Click(object sender, RoutedEventArgs e) => Ringer.Stop();

        private async void BtnRingDevice_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string fingerprint) return;
            if (sender is not System.Windows.Controls.Button button) return;

            button.IsEnabled = false;
            try
            {
                if (!await Program.RingAsync(fingerprint, on: true))
                {
                    System.Windows.MessageBox.Show(
                        "That device could not be reached just now.",
                        "Mesh Sync", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Modal on purpose. The dialog is the way to stop it, so it has to stay in front
                // of the person who started it rather than behind whatever they clicked next.
                System.Windows.MessageBox.Show(
                    "That device is ringing. Close this to stop it.\n\nIt stops on its own after a minute.",
                    "Mesh Sync", MessageBoxButton.OK, MessageBoxImage.Information);

                await Program.RingAsync(fingerprint, on: false);
            }
            finally
            {
                button.IsEnabled = true;
            }
        }

        // ────────────────────────────── sending files

        private void BtnSendFile_Click(object sender, RoutedEventArgs e) => Program.PromptForFileToSend();

        /// <summary>
        /// Accepts a drag only when it is files and there is somewhere for them to go, so the
        /// cursor tells the truth rather than promising something that will be dropped.
        /// </summary>
        private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            bool usable = Program.Links.IsConnected && e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop);

            e.Effects = usable ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, System.Windows.DragEventArgs e)
        {
            e.Handled = true;

            if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] paths) return;

            foreach (string path in paths)
            {
                // Folders are skipped rather than walked. Sending a directory is a different
                // feature with its own questions - what about the tree, what about the names -
                // and pretending otherwise by silently sending the first file inside would be
                // worse than doing nothing.
                if (Directory.Exists(path))
                {
                    Log.Write("UI", $"Skipping \"{System.IO.Path.GetFileName(path)}\": folders cannot be sent.");
                    continue;
                }

                if (File.Exists(path)) _ = Program.SendFileAsync(path);
            }
        }

        private void BtnCopyCode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Windows.Clipboard.SetText(_pairingCode);
                if (sender is System.Windows.Controls.Button button)
                {
                    button.Content = "Copied";
                    var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
                    reset.Tick += (_, _) => { button.Content = "Copy"; reset.Stop(); };
                    reset.Start();
                }
            }
            catch (Exception ex)
            {
                Log.Write("UI", "Copying the pairing code failed", ex);
            }
        }

        // ────────────────────────────── settings

        private void StartupSwitch_Click(object sender, RoutedEventArgs e)
        {
            bool desired = StartupSwitch.IsChecked == true;
            bool applied = Program.SetStartupEnabled(desired);
            if (applied != desired) StartupSwitch.IsChecked = applied;
        }

        private void ThemeChoice_Click(object sender, RoutedEventArgs e)
        {
            var preference =
                ThemeLight.IsChecked == true ? ThemeManager.Preference.Light :
                ThemeDark.IsChecked == true ? ThemeManager.Preference.Dark :
                ThemeManager.Preference.System;

            ThemeManager.SetPreference(preference);
            RenderQrCode(); // the QR is drawn with themed ink
        }

        private void BtnOpenLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = Program.LogDirectory, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Write("UI", "Opening the log folder failed", ex);
            }
        }

        private void BtnQuit_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();

        // ────────────────────────────── chrome

        private void BtnMinimise_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void BtnClose_Click(object sender, RoutedEventArgs e) => HideToTray();

        private void BtnHide_Click(object sender, RoutedEventArgs e) => HideToTray();

        private void HideToTray()
        {
            Hide();
            Program.NotifyHiddenToTray();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // The window is a view onto a background service, never the service's lifetime.
            e.Cancel = true;
            HideToTray();
        }

        public void ShowDashboard()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
        }

        public void Teardown()
        {
            Program.Links.Changed -= LinkState_Changed;
            _activity.Changed -= Activity_Changed;
            Ringer.Changed -= Ringer_Changed;
            MirroredNotifications.Changed -= Notifications_Changed;

            if (Program.Security != null)
            {
                Program.Security.Peers.Changed -= Peers_Changed;
                Program.Security.PairingRequested -= Pairing_Requested;
                Program.Security.Pairing.Changed -= Pairing_WindowChanged;
            }

            _ageTimer.Stop();
            _spinner?.Stop(this);
        }

        private sealed class ActivityRow
        {
            public string Glyph { get; init; } = "";
            public string Title { get; init; } = "";
            public string Sub { get; init; } = "";
            public string Age { get; init; } = "";
        }

        /// <summary>One mirrored notification, as the Notifications page shows it.</summary>
        private sealed class NotificationRow
        {
            public string Heading { get; init; } = "";
            public string Body { get; init; } = "";
            public string Source { get; init; } = "";
            public string Key { get; init; } = "";
        }

        /// <summary>One device waiting to be allowed in.</summary>
        private sealed class PendingRow
        {
            public string Code { get; init; } = "";
            public string Name { get; init; } = "";
            public string Fingerprint { get; init; } = "";
        }

        /// <summary>One paired device, as the Devices page shows it.</summary>
        private sealed class DeviceRow
        {
            public string Name { get; init; } = "";
            public string Detail { get; init; } = "";
            public string Fingerprint { get; init; } = "";
            public Visibility RingVisibility { get; init; } = Visibility.Collapsed;
            // Fully qualified: WinForms is referenced for the tray icon, so a bare Brush is
            // ambiguous between System.Drawing and System.Windows.Media.
            public System.Windows.Media.Brush Dot { get; init; } = System.Windows.Media.Brushes.Gray;
        }
        // ──────────────────────────────────── files on other devices

        private readonly System.Collections.ObjectModel.ObservableCollection<BrowseRow> _browseRows = new();

        /// <summary>Empty until a device is chosen.</summary>
        private string _browsePeer = "";
        private string _browsePeerName = "";

        /// <summary>Empty while listing a device's shared folders.</summary>
        private string _browseFolderId = "";
        private string _browseFolderName = "";

        /// <summary>Where we are inside that folder. Empty is its top.</summary>
        private string _browsePath = "";

        private void ShowBrowseDevices()
        {
            _browsePeer = _browsePeerName = _browseFolderId = _browseFolderName = _browsePath = "";

            _browseRows.Clear();

            var peers = Program.Security?.Peers;

            if (peers != null)
            {
                foreach (var peer in peers.Peers.OrderBy(p => p.Name ?? p.Fingerprint))
                {
                    _browseRows.Add(new BrowseRow
                    {
                        Name = string.IsNullOrWhiteSpace(peer.Name)
                            ? DeviceIdentity.Shorten(peer.Fingerprint)
                            : peer.Name!,
                        Sub = DeviceIdentity.Shorten(peer.Fingerprint),
                        Action = "Browse",
                        Kind = BrowseRowKind.Device,
                        Target = peer.Fingerprint
                    });
                }
            }

            if (FileList.ItemsSource == null) FileList.ItemsSource = _browseRows;

            FilesCrumb.Text = _browseRows.Count == 0 ? "Nothing paired yet." : "Choose a device.";
            BtnFilesUp.Visibility = Visibility.Collapsed;
            FilesTruncated.Visibility = Visibility.Collapsed;
            FilesEmpty.Text = "Pair a device first.";
            FilesEmpty.Visibility = _browseRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task ShowBrowseListingAsync()
        {
            FilesBusy.Visibility = Visibility.Visible;
            FilesTruncated.Visibility = Visibility.Collapsed;
            FilesEmpty.Visibility = Visibility.Collapsed;

            try
            {
                var reply = await Program.Browse.BrowseAsync(_browsePeer, _browseFolderId, _browsePath);

                _browseRows.Clear();

                foreach (var entry in reply.Entries)
                {
                    _browseRows.Add(new BrowseRow
                    {
                        Name = entry.Name,
                        Sub = entry.IsDirectory
                            ? "Folder"
                            : $"{entry.SizeLabel} · {entry.ModifiedUtc.ToLocalTime():d MMM yyyy}",
                        Action = entry.IsDirectory ? "Open" : "Get",
                        Kind = _browseFolderId.Length == 0 ? BrowseRowKind.Folder
                             : entry.IsDirectory ? BrowseRowKind.Directory
                             : BrowseRowKind.File,
                        Target = _browseFolderId.Length == 0 ? entry.Id : entry.Name
                    });
                }

                FilesTruncated.Visibility = reply.Truncated ? Visibility.Visible : Visibility.Collapsed;

                FilesEmpty.Text = reply.Status switch
                {
                    BrowseStatus.NotAllowed => "That folder is not shared.",
                    BrowseStatus.NoSuchFolder => "That folder is no longer shared.",
                    BrowseStatus.NotFound when _browseFolderId.Length == 0 => $"{_browsePeerName} has not shared anything, or did not answer.",
                    BrowseStatus.NotFound => "That device did not answer.",
                    _ when _browseFolderId.Length == 0 => $"{_browsePeerName} has not shared anything.",
                    _ => "This folder is empty."
                };
            }
            catch (Exception ex)
            {
                Log.Write("Browse", "Listing failed", ex);
                _browseRows.Clear();
                FilesEmpty.Text = "That listing could not be read.";
            }
            finally
            {
                FilesBusy.Visibility = Visibility.Collapsed;
                FilesEmpty.Visibility = _browseRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                FilesCrumb.Text = _browseFolderId.Length == 0
                    ? $"{_browsePeerName} · shared folders"
                    : _browsePath.Length == 0
                        ? $"{_browsePeerName} · {_browseFolderName}"
                        : $"{_browsePeerName} · {_browseFolderName}/{_browsePath}";

                BtnFilesUp.Visibility = _browsePeer.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private async void BtnFileEntry_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string token) return;

            var row = _browseRows.FirstOrDefault(r => r.Token == token);
            if (row == null) return;

            switch (row.Kind)
            {
                case BrowseRowKind.Device:
                    _browsePeer = row.Target;
                    _browsePeerName = row.Name;
                    _browseFolderId = _browseFolderName = _browsePath = "";
                    await ShowBrowseListingAsync();
                    break;

                case BrowseRowKind.Folder:
                    _browseFolderId = row.Target;
                    _browseFolderName = row.Name;
                    _browsePath = "";
                    await ShowBrowseListingAsync();
                    break;

                case BrowseRowKind.Directory:
                    _browsePath = _browsePath.Length == 0 ? row.Target : $"{_browsePath}/{row.Target}";
                    await ShowBrowseListingAsync();
                    break;

                case BrowseRowKind.File:
                    string relative = _browsePath.Length == 0 ? row.Target : $"{_browsePath}/{row.Target}";

                    if (await Program.Browse.FetchAsync(_browsePeer, _browseFolderId, relative))
                    {
                        FilesCrumb.Text = $"Asked for \"{row.Name}\". It will appear in your Downloads.";
                    }
                    break;
            }
        }

        private async void BtnFilesUp_Click(object sender, RoutedEventArgs e)
        {
            if (_browsePath.Length > 0)
            {
                int cut = _browsePath.LastIndexOf('/');
                _browsePath = cut < 0 ? "" : _browsePath[..cut];
                await ShowBrowseListingAsync();
                return;
            }

            if (_browseFolderId.Length > 0)
            {
                _browseFolderId = _browseFolderName = "";
                await ShowBrowseListingAsync();
                return;
            }

            ShowBrowseDevices();
        }

        private enum BrowseRowKind { Device, Folder, Directory, File }

        private sealed class BrowseRow
        {
            /// <summary>Identifies the row to its own button, since a WPF tag carries one value.</summary>
            public string Token { get; } = Guid.NewGuid().ToString("N");

            public string Name { get; init; } = "";
            public string Sub { get; init; } = "";
            public string Action { get; init; } = "";
            public BrowseRowKind Kind { get; init; }
            public string Target { get; init; } = "";
        }

    }
}
