using System;
using System.Drawing;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace UPSStatus
{
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer _timer = new();
        private const int RefreshIntervalSeconds = 10;
        private int _countdown;
        private bool _refreshing;

        private readonly WinForms.NotifyIcon _notifyIcon;
        private bool _closeFromTray;
        private readonly CancellationTokenSource _cts = new();

        public MainWindow()
        {
            InitializeComponent();

            _notifyIcon = BuildNotifyIcon();

            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Timer_Tick;
            _timer.Start();

            _ = RefreshAsync();
        }

        // ── Tray icon setup ──────────────────────────────────────────────────

        private WinForms.NotifyIcon BuildNotifyIcon()
        {
            var icon = new WinForms.NotifyIcon
            {
                Icon = SystemIcons.Shield,
                Text = "UPS Status Monitor",
                Visible = true,
                ContextMenuStrip = BuildContextMenu(),
            };

            icon.DoubleClick += (_, _) => RestoreWindow();
            return icon;
        }

        private WinForms.ContextMenuStrip BuildContextMenu()
        {
            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Open", null, (_, _) => RestoreWindow());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Close", null, (_, _) => ExitApplication());
            return menu;
        }

        private void RestoreWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void ExitApplication()
        {
            _closeFromTray = true;
            Close();
        }

        // ── Window events ────────────────────────────────────────────────────

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                Hide();
                _notifyIcon.ShowBalloonTip(
                    timeout: 1500,
                    tipTitle: "UPS Status Monitor",
                    tipText: "Minimized to tray. Double-click to restore.",
                    tipIcon: WinForms.ToolTipIcon.Info);
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_closeFromTray)
            {
                e.Cancel = true;
                WindowState = WindowState.Minimized;
                return;
            }

            _cts.Cancel();
            _cts.Dispose();
            _timer.Stop();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        // ── Refresh logic ────────────────────────────────────────────────────

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAsync();
        }

        private async void Timer_Tick(object? sender, EventArgs e)
        {
            _countdown--;
            UpdateNextRefreshText();

            if (_countdown <= 0)
                await RefreshAsync();
        }

        private async System.Threading.Tasks.Task RefreshAsync()
        {
            if (_refreshing)
                return;

            _refreshing = true;
            RefreshButton.IsEnabled = false;
            StatusBarText.Text = "Refreshing…";

            string host = HostBox.Text.Trim();
            if (!int.TryParse(PortBox.Text.Trim(), out int port))
                port = 3551;

            try
            {
                ApcUpsStatus status = await ApcUpsClient.GetStatusAsync(host, port,
                    onRetry: secondsLeft => Dispatcher.Invoke(() =>
                        StatusBarText.Text = $"Connection refused — retrying in {secondsLeft}s…"),
                    cancellationToken: _cts.Token);
                ApplyStatus(status);
                StatusBarText.Text = $"Last updated: {DateTime.Now:HH:mm:ss}";
            }
            catch (OperationCanceledException)
            {
                // App is closing — do nothing
            }
            catch (Exception ex)
            {
                StatusBarText.Text = $"Error: {ex.Message}";
                ClearValues();
            }
            finally
            {
                _countdown = RefreshIntervalSeconds;
                UpdateNextRefreshText();
                RefreshButton.IsEnabled = true;
                _refreshing = false;
            }
        }

        private void ApplyStatus(ApcUpsStatus status)
        {
            bool onBattery = status.Status.Contains("ONBATT", StringComparison.OrdinalIgnoreCase);

            WarningBanner.Visibility = onBattery ? Visibility.Visible : Visibility.Collapsed;

            StatusValue.Text = string.IsNullOrWhiteSpace(status.Status) ? "Unknown" : status.Status;
            StatusValue.Foreground = onBattery
                ? System.Windows.Media.Brushes.DarkRed
                : System.Windows.Media.Brushes.DarkGreen;

            BatteryValue.Text = $"{status.BatteryCharge:0.#}%";
            BatteryBar.Value = (double)status.BatteryCharge;
            BatteryBar.Foreground = status.BatteryCharge < 20
                ? System.Windows.Media.Brushes.OrangeRed
                : System.Windows.Media.Brushes.Green;

            TimeLeftValue.Text = $"{status.TimeLeftMinutes:0.#} min";

            LoadValue.Text = $"{status.LoadPercent:0.#}%";
            LoadBar.Value = (double)status.LoadPercent;

            VoltageValue.Text = $"{status.LineVoltage:0.#} V";

            SelfTestValue.Text = status.RawValues.TryGetValue("SELFTEST", out string? selftest)
                ? selftest : "N/A";
            SelfTestValue.Foreground = (SelfTestValue.Text == "OK")
                ? System.Windows.Media.Brushes.DarkGreen
                : (SelfTestValue.Text == "N/A" ? System.Windows.Media.Brushes.Gray
                : System.Windows.Media.Brushes.OrangeRed);

            NumXfersValue.Text = status.RawValues.TryGetValue("NUMXFERS", out string? numx)
                ? numx : "N/A";

            CumOnBattValue.Text = status.RawValues.TryGetValue("CUMONBATT", out string? cumob)
                ? cumob : "N/A";

            // Keep tray tooltip current
            _notifyIcon.Text = onBattery
                ? $"⚠ UPS ON BATTERY — {status.BatteryCharge:0.#}% / {status.TimeLeftMinutes:0.#} min left"
                : $"UPS OK — Battery {status.BatteryCharge:0.#}% · Load {status.LoadPercent:0.#}%";
        }

        private void ClearValues()
        {
            WarningBanner.Visibility = Visibility.Collapsed;
            StatusValue.Text = "—";
            BatteryValue.Text = "—";
            BatteryBar.Value = 0;
            TimeLeftValue.Text = "—";
            LoadValue.Text = "—";
            LoadBar.Value = 0;
            VoltageValue.Text = "—";
            SelfTestValue.Text = "—";
            SelfTestValue.Foreground = System.Windows.Media.Brushes.Gray;
            NumXfersValue.Text = "—";
            CumOnBattValue.Text = "—";
        }

        private void UpdateNextRefreshText()
        {
            NextRefreshText.Text = _countdown > 0 ? $"Next refresh in {_countdown}s" : "";
        }
    }
}

