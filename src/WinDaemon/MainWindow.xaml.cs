using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
using CoreLib.Transport;
using QRCoder;

namespace WinDaemon
{
    public partial class MainWindow : Window
    {
        private const double SidebarWide = 196;
        private const double SidebarNarrow = 62;

        private readonly TcpTransportConnection _transport;
        private readonly SyncActivityLog _activity;
        private readonly string _ipAddress;
        private readonly string _pairingCode;

        private readonly ObservableCollection<ActivityRow> _rows = new();
        private readonly DispatcherTimer _ageTimer;
        private Storyboard? _spinner;
        private bool _sidebarCollapsed;
        private bool _suppressModeEvent;

        public event Action? ExitRequested;

        public MainWindow(string ipAddress, string pairingCode,
                          TcpTransportConnection transport, SyncActivityLog activity)
        {
            InitializeComponent();

            _ipAddress = ipAddress;
            _pairingCode = pairingCode;
            _transport = transport;
            _activity = activity;

            ActivityList.ItemsSource = _rows;

            IpText.Text = ipAddress;
            CodeText.Text = Shorten(pairingCode);
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
            TransportMode.SelectedIndex = TransportSettings.Current switch
            {
                TransportPreference.WiFi => 1,
                TransportPreference.Ble => 2,
                _ => 0
            };
            _suppressModeEvent = false;

            ConnectionState.Changed += ConnectionState_Changed;
            _activity.Changed += Activity_Changed;

            // Relative timestamps go stale silently otherwise.
            _ageTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            _ageTimer.Tick += (_, _) => RefreshActivity();
            _ageTimer.Start();

            Loaded += (_, _) =>
            {
                StartSpinner();
                RefreshStatus();
                RefreshActivity();
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
            PageDevices.Visibility = section == "Devices" ? Visibility.Visible : Visibility.Collapsed;
            PageSettings.Visibility = section == "Settings" ? Visibility.Visible : Visibility.Collapsed;
            PageAbout.Visibility = section == "About" ? Visibility.Visible : Visibility.Collapsed;

            if (section == "Activity") RefreshActivity();
        }

        private void BtnCollapse_Click(object sender, RoutedEventArgs e)
        {
            _sidebarCollapsed = !_sidebarCollapsed;
            Sidebar.Width = _sidebarCollapsed ? SidebarNarrow : SidebarWide;
            BrandText.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;

            // Labels go, icons stay, so the rail still works when narrow.
            foreach (var nav in new[] { NavHome, NavActivity, NavDevices, NavSettings, NavAbout })
            {
                if (nav.Content is System.Windows.Controls.Panel panel && panel.Children.Count > 1)
                {
                    panel.Children[1].Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
                }
            }

            SidebarStatus.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        }

        // ────────────────────────────── status

        private void ConnectionState_Changed() => Dispatcher.BeginInvoke(RefreshStatus);

        private void Activity_Changed(object? sender, EventArgs e) =>
            Dispatcher.BeginInvoke(() => { RefreshActivity(); RefreshStatus(); });

        private void RefreshStatus()
        {
            bool connected = ConnectionState.IsConnected;
            bool overBle = ConnectionState.ActiveLink == LinkKind.Ble;

            IconTick.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            IconSpinner.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
            StatusRing.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;

            string accent = connected ? "B.Accent" : "B.Warn";
            string soft = connected ? "B.AccentSoft" : "B.WarnSoft";
            StatusHalo.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, soft);
            StatusHeadline.SetResourceReference(ForegroundProperty, accent);
            SidebarDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, accent);

            string peer = ConnectionState.PeerName ?? "your phone";

            if (connected)
            {
                StatusHeadline.Text = overBle ? "Connected over Bluetooth" : "Connected";
                StatusDetail.Text = overBle
                    ? $"{peer}. No Wi-Fi needed - text syncs directly over Bluetooth."
                    : $"{peer}. Copy on either device and it appears on the other.";

                var last = _activity.LastActivityUtc;
                StatusSub.Text = last.HasValue ? $"Last sync {Relative(last.Value)}" : "Ready - copy something to sync it";

                BtnPrimary.Content = "Pair another device";
                SidebarStatus.Text = overBle ? "Bluetooth" : "Connected";
                FooterHint.Text = overBle
                    ? "Text only over Bluetooth. Images need Wi-Fi."
                    : "Everything stays on your local network";
            }
            else
            {
                StatusHeadline.Text = "Waiting for a device";
                StatusDetail.Text = "Open Mesh Sync on your phone and scan the pairing code.";
                StatusSub.Text = "";
                BtnPrimary.Content = "Pair a device";
                SidebarStatus.Text = "Waiting";
                FooterHint.Text = "Both devices must be on the same Wi-Fi, or in Bluetooth range";
            }

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

            TransportSettings.Set(preference);

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
                string payload = $"meshsync://pair?ip={Uri.EscapeDataString(_ipAddress)}&key={Uri.EscapeDataString(_pairingCode)}";
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
            ConnectionState.Changed -= ConnectionState_Changed;
            _activity.Changed -= Activity_Changed;
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
    }
}
