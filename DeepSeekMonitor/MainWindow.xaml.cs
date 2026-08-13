using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeepSeekMonitor.Models;
using DeepSeekMonitor.Services;

namespace DeepSeekMonitor;

public partial class MainWindow : Window
{
    // 与 Python 版一致的常量
    private const double CardW = 310;
    private const double CardH = 174;
    private const double OrbSize = 32;
    private const int HideDelayMs = 700;
    private const int DockThreshold = 36;

    // DeepSeek 官方充值页
    private const string TopUpUrl = "https://platform.deepseek.com/top_up";

    private static readonly SolidColorBrush SubBrush = Brush("#B7BAC2");
    private static readonly SolidColorBrush AmberBrush = Brush("#E5B568");
    private static readonly SolidColorBrush GreenBrush = Brush("#56C596");
    private static readonly SolidColorBrush CoralBrush = Brush("#ED7C72");
    private static readonly SolidColorBrush BorderNorm = Brush("#777B86");
    private static readonly SolidColorBrush BorderHover = Brush("#D7DBE4");
    private static readonly SolidColorBrush WhiteBrush = Brush("#FFFFFF");

    private readonly DeepSeekApi _api = new();
    private AppConfig _config = null!;
    private DispatcherTimer _refreshTimer = null!;
    private System.Windows.Forms.NotifyIcon? _tray;

    private bool _serviceView;
    private bool _lowBalanceNotified;
    private bool _refreshing;       // 防止余额刷新重入
    private bool _loadingService;   // 防止服务状态加载重入
    private List<ServiceEvent> _lastServiceEvents = new();

    // 粒子消散
    private bool _dismissing;
    private ParticleDismiss _dismiss = null!;

    public MainWindow()
    {
        InitializeComponent();
        _config = AppConfig.Load();

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HideDelayMs) };
        _hideTimer.Tick += OnHideTimerTick;

        _refreshTimer = new DispatcherTimer();
        _refreshTimer.Tick += (_, _) => Refresh();
        SetInterval(Math.Max(30, _config.RefreshInterval));

        _snapTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _snapTimer.Tick += (_, _) => SnapTimelineToCenter();

        MainMenu.Opened += (_, _) => { _cancelHide(); _menuOpen = true; };
        MainMenu.Closed += (_, _) => { _menuOpen = false; if (_docked) _hideTimer.Start(); };
        MenuLowWarn.IsChecked = _config.LowWarn;

        // 调试：--dump 参数时，加载完成后把窗口渲染成 PNG 并退出
        Loaded += (_, _) =>
        {
            if (Environment.GetCommandLineArgs().Contains("--dump"))
            {
                var rtb = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(this);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = File.Create(System.IO.Path.Combine(AppContext.BaseDirectory, "dump.png"));
                enc.Save(fs);
                Application.Current.Shutdown();
            }
            var args = Environment.GetCommandLineArgs();
            int oi = Array.IndexOf(args, "--orb");
            if (oi >= 0)
            {
                double ox = oi + 1 < args.Length ? double.Parse(args[oi + 1]) : 100;
                double oy = oi + 2 < args.Length ? double.Parse(args[oi + 2]) : 200;
                Left = ox; Top = oy; Width = 32; Height = 32;
                ShowOrb();
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    Visibility = Visibility.Hidden;
                    UpdateLayout();
                    Visibility = Visibility.Visible;
                }));
            }
        };

        PositionTopRight();
        SetupTray();

        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            SetStatus("loading", "准备中");
            Dispatcher.BeginInvoke(() => Refresh());
        }
        else
        {
            SetStatus("no_key", "未设置");
            Dispatcher.BeginInvoke(FirstRunGuide);
        }
    }

    // ---------------- 初始化 ----------------
    private void PositionTopRight()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - CardW - 24;
        Top = wa.Top + 24;
        Width = CardW;
        Height = CardH;
    }

    private void SetupTray()
    {
        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "whale.ico");
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.IO.File.Exists(iconPath) ? new System.Drawing.Icon(iconPath) : System.Drawing.SystemIcons.Application,
                Text = "DeepSeek",
                Visible = true,
            };
            _tray.DoubleClick += (_, _) => ShowAndPeek();
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("显示", null, (_, _) => ShowAndPeek());
            menu.Items.Add("退出", null, (_, _) => Dismiss());
            _tray.ContextMenuStrip = menu;
        }
        catch { /* 托盘失败不阻塞主界面 */ }
    }

    // ---------------- 状态 ----------------
    private void SetStatus(string state, string? text = null)
    {
        var (color, label) = state switch
        {
            "loading" => (AmberBrush, "加载中"),
            "ok" => (GreenBrush, "在线"),
            "error" => (CoralBrush, "出错了"),
            "no_key" => (AmberBrush, "未设置"),
            _ => (GreenBrush, "在线"),
        };
        StatusDot.Foreground = color;
        StatusLabel.Text = string.IsNullOrEmpty(text) ? label : text;
    }

    // ---------------- 余额刷新 ----------------
    private async void Refresh()
    {
        // 防止快速连点 / 定时与手动刷新重叠导致并发请求
        if (_refreshing) return;
        var key = (_config.ApiKey ?? "").Trim();
        if (key.Length == 0)
        {
            SetStatus("no_key", "未设置");
            BalanceText.Text = "--.--";
            BalanceNote.Text = "还没设置 API Key 哦";
            return;
        }

        _refreshing = true;
        SetStatus("loading", "看看中");
        BalanceText.Text = "···";

        try
        {
            var data = await _api.FetchBalanceAsync(key);
            Render(data);
        }
        catch (ApiException ex)
        {
            ShowBalanceError(ex.Message.Length > 22 ? ex.Message[..22] : ex.Message);
        }
        catch (Exception ex)
        {
            // Match the Python implementation's final safety net: a bad
            // response must degrade to an in-widget error instead of taking
            // down the always-on-top window.
            ShowBalanceError(ex.GetType().Name);
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>余额查询失败时统一在悬浮窗内降级展示错误,不弹出崩溃框。</summary>
    private void ShowBalanceError(string hint)
    {
        SetStatus("error");
        BalanceText.Foreground = CoralBrush;
        BalanceText.Text = "--.--";
        BalanceNote.Text = "暂时没查到余额";
        LblTopped.Text = "充值 --";
        LblGranted.Text = "赠送 --";
        LblTime.Text = "提示 " + hint;
    }

    private void Render(BalanceInfo data)
    {
        var info = data.BalanceInfos!.FirstOrDefault();
        if (info == null)
        {
            SetStatus("error");
            BalanceNote.Text = "余额信息是空的";
            return;
        }

        var symbol = info.Currency == "CNY" ? "¥" : "$";
        BalanceText.Foreground = WhiteBrush;
        BalanceText.Text = $"{symbol} {info.TotalBalance:F2}";
        BalanceNote.Text = $"{info.Currency} 可用余额";
        LblTopped.Text = $"充值 {symbol}{info.ToppedUpBalance:F2}";
        LblGranted.Text = $"赠送 {symbol}{info.GrantedBalance:F2}";
        LblTime.Text = $"更新 {DateTime.Now:HH:mm}";

        var low = info.TotalBalance < _config.LowThreshold;
        if (low && _config.LowWarn)
        {
            SetStatus("ok", "余额偏低");
            StatusDot.Foreground = AmberBrush;
            BalanceText.Foreground = CoralBrush;
            BalanceNote.Text = $"余额不多啦，低于 {symbol}{_config.LowThreshold:F0} 咯～";
            if (!_lowBalanceNotified)
            {
                _lowBalanceNotified = true;
                _tray?.ShowBalloonTip(8000, "DeepSeek 余额偏低",
                    $"当前可用余额 {symbol}{info.TotalBalance:F2}，低于提醒阈值 {symbol}{_config.LowThreshold:F0}。",
                    System.Windows.Forms.ToolTipIcon.Warning);
            }
        }
        else
        {
            _lowBalanceNotified = false;
            SetStatus("ok");
            BalanceText.Foreground = WhiteBrush;
            BalanceNote.Text = $"{info.Currency} 可用余额";
        }
    }

    private void SetInterval(int seconds)
    {
        _config.RefreshInterval = seconds;
        AppConfig.Save(_config);

        // Changing Interval alone does not enable a DispatcherTimer.  The
        // original code therefore persisted the user's choice but never
        // performed an automatic refresh.  Restarting also makes a changed
        // interval take effect from this moment instead of retaining the
        // previous schedule.
        _refreshTimer.Stop();
        _refreshTimer.Interval = TimeSpan.FromSeconds(seconds);
        _refreshTimer.Start();
    }

    // ---------------- 服务状态视图 ----------------
    private void ToggleServiceView()
    {
        _serviceView = !_serviceView;
        var show = _serviceView;
        BalanceText.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        BalanceNote.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        BottomRow.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        ServicePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Text = show ? "DeepSeek 服务" : "DeepSeek";
        BtnService.Content = show ? "‹" : "◉";
        BtnService.ToolTip = show ? "返回余额" : "查看 DeepSeek 服务状态";

        if (show)
            CheckServiceStatus();
        else
            SetStatus(string.IsNullOrWhiteSpace(_config.ApiKey) ? "no_key" : "ok");
    }

    private void CheckServiceStatus()
    {
        if (_loadingService) return;
        _loadingService = true;
        SetStatus("loading", "检查服务中");
        SetTimelineMessage("正在加载近期服务事件…");
        _ = LoadServiceEventsAsync();
    }

    private async Task LoadServiceEventsAsync()
    {
        try
        {
            var events = await _api.FetchServiceEventsAsync();
            _lastServiceEvents = events;
            SetStatus("ok", "服务 状态正常");
            RenderTimeline(events.Take(20).ToList());
        }
        catch (ApiException ex)
        {
            if (_lastServiceEvents.Count > 0)
            {
                SetStatus("loading", "服务连接重试中（显示缓存）");
                RenderTimeline(_lastServiceEvents.Take(20).ToList());
            }
            else
            {
                SetStatus("error", "服务状态未知");
                SetTimelineMessage("服务事件暂时无法读取\n" + ex.Message);
            }
        }
        finally
        {
            _loadingService = false;
        }
    }

    // ---------------- 设置 ----------------
    private void FirstRunGuide()
    {
        if (!string.IsNullOrWhiteSpace(_config.ApiKey)) return;
        MessageBox.Show(this,
            "🐳 欢迎使用 DeepSeek！\n\n第一次使用，先粘贴你的 API Key 吧。\n之后随时可以右键悬浮窗重新设置。",
            "欢迎～", MessageBoxButton.OK, MessageBoxImage.Information);
        AskApiKey();
    }

    private void AskApiKey()
    {
        var dlg = new InputDialog("设置 API Key",
            "粘贴你的 DeepSeek API Key：\n（platform.deepseek.com → API Keys 页面获取）", _config.ApiKey)
        { Owner = this };
        if (dlg.ShowDialog() != true) return;

        _config.ApiKey = dlg.Value.Trim();
        if (!AppConfig.Save(_config))
            MessageBox.Show(this, "配置保存失败,API Key 可能不会被记住。\n请把程序放到有写权限的目录后重试。",
                "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        if (string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            SetStatus("no_key", "未设置");
            BalanceText.Text = "--.--";
            BalanceNote.Text = "还没设置 API Key 哦";
        }
        else
        {
            Refresh();
        }
    }

    // ---------------- 事件处理 ----------------
    private void BtnService_Click(object sender, RoutedEventArgs e) => ToggleServiceView();
    private void BtnMin_Click(object sender, RoutedEventArgs e) => HideToSide();
    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => Refresh();
    private void BtnTopUp_Click(object sender, RoutedEventArgs e) => OpenTopUpPage();
    private void MenuRefresh_Click(object sender, RoutedEventArgs e) => Refresh();
    private void MenuTopUp_Click(object sender, RoutedEventArgs e) => OpenTopUpPage();
    private void MenuSetKey_Click(object sender, RoutedEventArgs e) => AskApiKey();
    private void MenuDock_Click(object sender, RoutedEventArgs e) => HideToSide();

    private void MenuInterval_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem item && int.TryParse(item.Tag?.ToString(), out var secs))
            SetInterval(secs);
    }

    private void MenuLowWarn_Click(object sender, RoutedEventArgs e)
    {
        _config.LowWarn = MenuLowWarn.IsChecked;
        AppConfig.Save(_config);
        Refresh();
    }

    private void MenuQuit_Click(object sender, RoutedEventArgs e) => Dismiss();

    // ---------------- 粒子消散退出 ----------------
    private void Dismiss()
    {
        if (_dismissing) return;
        _dismissing = true;
        _refreshTimer.Stop();
        _hideTimer.Stop();
        _tray?.Dispose();
        _tray = null;

        // 先回到完整卡片状态再采样
        _undock();
        CardGrid.Visibility = Visibility.Visible;
        Orb.Visibility = Visibility.Collapsed;
        Width = CardW;
        Height = CardH;

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            try
            {
                _dismiss = new ParticleDismiss(this);
                _dismiss.Start();
            }
            catch
            {
                Application.Current.Shutdown();
            }
        });
    }

    // ---------------- 去充值 ----------------
    private void OpenTopUpPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(TopUpUrl) { UseShellExecute = true });
        }
        catch { /* 打不开浏览器时静默忽略 */ }
    }

    private static SolidColorBrush Brush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    private void OnHideTimerTick(object? sender, EventArgs e) => ScheduleHide();
}
