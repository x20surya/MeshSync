using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
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
        private readonly TcpTransportConnection _transport;
        private readonly SyncActivityLog _activity;
        private readonly string _ipAddress;
        private readonly string _pairingCode;

        private readonly ObservableCollection<ActivityRow> _rows = new();
        private readonly DispatcherTimer _ageTimer;
        private Storyboard? _spinner;

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

            switch (ThemeManager.Current)
            {
                case ThemeManager.Preference.Light: ThemeLight.IsChecked = true; break;
                case ThemeManager.Preference.Dark: ThemeDark.IsChecked = true; break;
                default: ThemeSystem.IsChecked = true; break;
            }


            PairToggle.Checked += (_, _) => PairPanel.Visibility = Visibility.Visible;
            PairToggle.Unchecked += (_, _) => PairPanel.Visibility = Visibility.Collapsed;
            SettingsToggle.Checked += (_, _) => SettingsPanel.Visibility = Visibility.Visible;
            SettingsToggle.Unchecked += (_, _) => SettingsPanel.Visibility = Visibility.Collapsed;

            // Set only after the handlers are attached, otherwise the chevron flips while
            // the panel stays hidden. Pairing is the only useful action until a peer exists.
            if (!ConnectionState.IsConnected) PairToggle.IsChecked = true;

            // Reads the shared state rather than the TCP transport, so a Bluetooth-only link
            // shows as connected instead of the window claiming to still be waiting.
            ConnectionState.Changed += ConnectionState_Changed;
            _activity.Changed += Activity_Changed;

            // Relative timestamps ("2s ago") go stale silently otherwise.
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

        // ────────────────────────────── status

        private void ConnectionState_Changed() =>
            Dispatcher.BeginInvoke(RefreshStatus);

        private void Activity_Changed(object? sender, EventArgs e) =>
            Dispatcher.BeginInvoke(() => { RefreshActivity(); RefreshStatus(); });

        private void RefreshStatus()
        {
            bool connected = ConnectionState.IsConnected;
            bool overBle = ConnectionState.ActiveLink == LinkKind.Ble;

            IconTick.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            IconSpinner.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
            StatusRing.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;

            var accent = connected ? "B.Accent" : "B.Warn";
            var soft = connected ? "B.AccentSoft" : "B.WarnSoft";
            StatusHalo.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, soft);
            StatusHeadline.SetResourceReference(ForegroundProperty, accent);

            if (connected)
            {
                StatusHeadline.Text = overBle ? "CONNECTED OVER BLUETOOTH" : "CONNECTED";
                string peer = ConnectionState.PeerName ?? "Paired device";

                if (overBle)
                {
                    StatusDetail.Text = $"{peer}  ·  no Wi-Fi needed";
                }
                else
                {
                    string endpoint = _transport.RemoteEndPoint ?? "";
                    int colon = endpoint.LastIndexOf(':');
                    if (colon > 0) endpoint = endpoint.Substring(0, colon);
                    StatusDetail.Text = string.IsNullOrEmpty(endpoint) ? peer : $"{peer}  ·  {endpoint}";
                }

                var last = _activity.LastActivityUtc;
                StatusSub.Text = last.HasValue
                    ? $"Last sync {Relative(last.Value)}"
                    : "Ready - copy something to sync it";

                PairHintBadge.Visibility = Visibility.Collapsed;
                FooterHint.Text = overBle
                    ? "Text only over Bluetooth. Images need Wi-Fi."
                    : "Everything stays on your local network";
            }
            else
            {
                StatusHeadline.Text = "WAITING FOR A DEVICE";
                StatusDetail.Text = "Open Mesh Sync on your phone and scan the pairing code.";
                StatusSub.Text = "";
                PairHintBadge.Visibility = Visibility.Visible;
                FooterHint.Text = "Both devices must be on the same Wi-Fi";
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
                Process.Start(new ProcessStartInfo
                {
                    FileName = Program.LogDirectory,
                    UseShellExecute = true
                });
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
