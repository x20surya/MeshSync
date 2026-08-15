using System;
using System.Drawing;
using System.Windows.Forms;
using QRCoder;

namespace WinDaemon
{
    public class MainForm : Form
    {
        private NotifyIcon _trayIcon;
        private bool _isExiting = false;
        private Label _lblStatus;

        public MainForm(string ipAddress, string pairingCode, CoreLib.Transport.TcpTransportConnection tcpTransport)
        {
            this.Text = "Mesh Sync Dashboard";
            this.Size = new Size(450, 600);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.BackColor = Color.White;

            var lblTitle = new Label
            {
                Text = "Mesh Sync Dashboard",
                Font = new Font("Segoe UI", 16, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(100, 20),
                ForeColor = Color.FromArgb(33, 33, 33)
            };
            this.Controls.Add(lblTitle);

            _lblStatus = new Label
            {
                Text = "Status: Waiting for connection...",
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                AutoSize = false,
                Size = new Size(350, 30),
                Location = new Point(40, 55),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.DarkOrange
            };
            this.Controls.Add(_lblStatus);

            var lblInstructions = new Label
            {
                Text = "Scan this QR code using your standard camera app to securely connect your Android device.",
                AutoSize = false,
                Size = new Size(350, 45),
                Location = new Point(40, 90),
                TextAlign = ContentAlignment.TopCenter,
                Font = new Font("Segoe UI", 10, FontStyle.Regular),
                ForeColor = Color.FromArgb(97, 97, 97)
            };
            this.Controls.Add(lblInstructions);

            var picQrCode = new PictureBox
            {
                Size = new Size(250, 250),
                Location = new Point(90, 140),
                SizeMode = PictureBoxSizeMode.Zoom
            };
            this.Controls.Add(picQrCode);

            try
            {
                // Use a Deep Link URI so standard Camera apps prompt to open Mesh Sync!
                string qrPayload = $"meshsync://pair?ip={ipAddress}&key={pairingCode}";
                using (var qrGenerator = new QRCodeGenerator())
                {
                    var qrCodeData = qrGenerator.CreateQrCode(qrPayload, QRCodeGenerator.ECCLevel.Q);
                    var qrCode = new QRCode(qrCodeData);
                    picQrCode.Image = qrCode.GetGraphic(20, Color.Black, Color.White, true);
                }
            }
            catch { }

            var txtDetails = new TextBox
            {
                Text = $"IP Address: {ipAddress}\r\nPairing Code: {pairingCode}",
                Multiline = true,
                ReadOnly = true,
                Size = new Size(350, 60),
                Location = new Point(40, 410),
                Font = new Font("Consolas", 9, FontStyle.Regular),
                TextAlign = HorizontalAlignment.Center,
                BorderStyle = BorderStyle.None,
                BackColor = Color.White
            };
            this.Controls.Add(txtDetails);

            var btnHide = new Button
            {
                Text = "Hide to Tray",
                Size = new Size(120, 40),
                Location = new Point(155, 480),
                Font = new Font("Segoe UI", 10, FontStyle.Regular),
                FlatStyle = FlatStyle.System
            };
            btnHide.Click += (s, e) => this.Hide();
            this.Controls.Add(btnHide);

            // Tray Icon Setup
            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Open Dashboard", null, (s, e) => ShowDashboard());
            contextMenu.Items.Add("Toggle Run on Startup", null, (s, e) => Program.ToggleStartup());
            contextMenu.Items.Add("-");
            contextMenu.Items.Add("Quit Mesh Sync", null, (s, e) => ExitApp());

            _trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Shield,
                ContextMenuStrip = contextMenu,
                Visible = true,
                Text = "Mesh Sync"
            };
            _trayIcon.DoubleClick += (s, e) => ShowDashboard();

            this.FormClosing += MainForm_FormClosing;

            // Wire up Transport events to update the UI Status Label dynamically
            tcpTransport.ClientConnected += (s, e) =>
            {
                this.Invoke((MethodInvoker)delegate
                {
                    _lblStatus.Text = "Status: Connected!";
                    _lblStatus.ForeColor = Color.ForestGreen;
                    _trayIcon.ShowBalloonTip(2000, "Mesh Sync", "Device Connected Successfully!", ToolTipIcon.Info);
                });
            };

            tcpTransport.ConnectionClosed += (s, e) =>
            {
                this.Invoke((MethodInvoker)delegate
                {
                    _lblStatus.Text = "Status: Disconnected. Waiting for connection...";
                    _lblStatus.ForeColor = Color.DarkOrange;
                });
            };
        }

        public void ShowDashboard()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.Activate();
        }

        private void ExitApp()
        {
            _isExiting = true;
            _trayIcon.Visible = false;
            Application.Exit();
        }

        private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            if (!_isExiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
                _trayIcon.ShowBalloonTip(2000, "Mesh Sync", "Running in the background. Double-click to open.", ToolTipIcon.Info);
            }
        }
    }
}
