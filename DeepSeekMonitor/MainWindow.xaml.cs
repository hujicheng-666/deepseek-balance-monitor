using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
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

    private static readonly SolidColorBrush SubBrush = Brush("#B7BAC2");
    private static readonly SolidColorBrush AmberBrush = Brush("#E5B568");
    private static readonly SolidColorBrush GreenBrush = Brush("#56C596");
    private static readonly SolidColorBrush CoralBrush = Brush("#ED7C72");
    private static readonly SolidColorBrush BorderNorm = Brush("#777B86");
    private static readonly SolidColorBrush BorderHover = Brush("#D7DBE4");

    private readonly DeepSeekApi _api = new();
    private AppConfig _config = null!;
    private DispatcherTimer _hideTimer = null!;
    private DispatcherTimer _refreshTimer = null!;
    private DispatcherTimer _snapTimer = null!;
    private System.Windows.Forms.NotifyIcon? _tray;

    private string _dockEdge = "";   // "" / left / right
    private bool _docked;
    private bool _peeking;
    private double _dockedCenterY;    // 贴边时保留的垂直中心
    private bool _dragging;
    private bool _serviceView;
    private bool _menuOpen;
    private bool _lowBalanceNotified;

    private readonly List<FrameworkElement> _timelineRows = new();
    private readonly List<TextBlock> _rowTexts = new();
    private List<ServiceEvent> _lastServiceEvents = new();
    private Border _topSpacer = null!;
    private Border _bottomSpacer = null!;

    // 粒子消散
    private bool _dismissing;
    private ParticleDismiss _dismiss = null!;

    public MainWindow()
    {
        InitializeComponent();
        _config = AppConfig.Load();

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HideDelayMs) };
        _hideTimer.Tick += (_, _) => ScheduleHide();

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

    private void ShowAndPeek()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        if (_docked && !_peeking)
        {
            _peeking = true;
            _cancelHide();
            RevealCard();
            AnimateWindowTo(FullX(_dockEdge), _dockedCenterY - CardH / 2, CardW, CardH, showOrbAtEnd: false);
        }
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
        var key = (_config.ApiKey ?? "").Trim();
        if (key.Length == 0)
        {
            SetStatus("no_key", "未设置");
            BalanceText.Text = "--.--";
            BalanceNote.Text = "还没设置 API Key 哦";
            return;
        }

        SetStatus("loading", "看看中");
        BalanceText.Text = "···";

        try
        {
            var data = await _api.FetchBalanceAsync(key);
            Render(data);
        }
        catch (ApiException ex)
        {
            SetStatus("error");
            BalanceText.Foreground = CoralBrush;
            BalanceText.Text = "--.--";
            BalanceNote.Text = "暂时没查到余额";
            LblTopped.Text = "充值 --";
            LblGranted.Text = "赠送 --";
            LblTime.Text = "提示 " + (ex.Message.Length > 22 ? ex.Message[..22] : ex.Message);
        }
        catch (Exception ex)
        {
            // Match the Python implementation's final safety net: a bad
            // response must degrade to an in-widget error instead of taking
            // down the always-on-top window.
            SetStatus("error");
            BalanceText.Foreground = CoralBrush;
            BalanceText.Text = "--.--";
            BalanceNote.Text = "暂时没查到余额";
            LblTopped.Text = "充值 --";
            LblGranted.Text = "赠送 --";
            LblTime.Text = "提示 " + ex.GetType().Name;
        }
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
        BalanceText.Foreground = new SolidColorBrush(Colors.White);
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
            BalanceText.Foreground = new SolidColorBrush(Colors.White);
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
    }

    private void ClearTimeline()
    {
        _timelineRows.Clear();
        _rowTexts.Clear();
        SpTimeline.Children.Clear();
        TimelineOverlay.Rows = _timelineRows;
        TimelineOverlay.Opacities = new List<double>();
        TimelineOverlay.InvalidateVisual();
    }

    private void SetTimelineMessage(string text)
    {
        ClearTimeline();
        SpTimeline.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 10,
            Foreground = SubBrush,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Margin = new Thickness(18, 5, 4, 0),
        });
    }

    private void RenderTimeline(List<ServiceEvent> events)
    {
        ClearTimeline();
        if (events.Count == 0)
        {
            SetTimelineMessage("近期没有服务事件");
            return;
        }

        _topSpacer = new Border { Height = 0 };
        _bottomSpacer = new Border { Height = 0 };
        SpTimeline.Children.Add(_topSpacer);

        foreach (var ev in events)
        {
            var row = new Grid
            {
                // 相邻事件之间留出间距，避免文字挤在一起
                Margin = new Thickness(0, 0, 0, 12),
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.RenderTransform = new ScaleTransform();

            var text = new TextBlock
            {
                FontSize = 10,
                Foreground = SubBrush,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 4, 0),
                Text = (string.IsNullOrEmpty(ev.Time) ? "最近更新" : ev.Time.Replace("T", " ")[..Math.Min(16, ev.Time.Length)])
                     + "\n" + ev.Title + "\n" + ev.Detail,
            };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            SpTimeline.Children.Add(row);
            _timelineRows.Add(row);
            _rowTexts.Add(text);
        }

        SpTimeline.Children.Add(_bottomSpacer);

        // 布局完成后给首尾项预留滚到中轴的空间
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (_timelineRows.Count == 0) return;
            var vh = SvTimeline.ViewportHeight;
            _topSpacer.Height = Math.Max(0, vh / 2 - _timelineRows[0].ActualHeight / 2 - 8);
            _bottomSpacer.Height = Math.Max(0, vh / 2 - _timelineRows[^1].ActualHeight / 2 - 8);
            UpdateTimelineWheel();
        });
    }

    private void SvTimeline_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateTimelineWheel();
    }

    private void UpdateTimelineWheel()
    {
        if (_timelineRows.Count == 0) return;
        double centerY = SvTimeline.ViewportHeight / 2;
        double range = Math.Max(1, SvTimeline.ViewportHeight * 0.62);

        // 视口内存在比视口还高的超长行时，自动居中/淡出会把内容挡住、看不清；
        // 此时退化为普通可读列表：不淡出、不缩放（吸附也在 Snap 里同步禁用）。
        bool plain = HasTallRowInView();

        var opacities = new List<double>(_timelineRows.Count);
        for (int i = 0; i < _timelineRows.Count; i++)
        {
            var row = _timelineRows[i];
            if (plain)
            {
                row.Opacity = 1.0;
                row.RenderTransform = null;
                opacities.Add(1.0);
            }
            else
            {
                double rowCenter = RowCenterInViewport(row);
                double distance = Math.Abs(rowCenter - centerY);
                double ratio = Math.Min(1.0, distance / range);
                double direction = (rowCenter - centerY) / range;
                var opacity = 1.0 - ratio * 0.72;
                row.Opacity = opacity;
                opacities.Add(opacity);
                var sc = new ScaleTransform(1.0, 1.0 - Math.Abs(direction) * 0.48, 9, row.ActualHeight / 2);
                row.RenderTransform = sc;
            }
            // 时间线文字统一使用与「服务 状态正常」一致的次级亮度，不做额外增亮
            _rowTexts[i].Foreground = SubBrush;
        }
        TimelineOverlay.Rows = _timelineRows;
        TimelineOverlay.Opacities = opacities;
        TimelineOverlay.InvalidateVisual();
        _snapTimer.Stop();
        _snapTimer.Start();
    }

    /// <summary>视口内是否有比视口还高（内容会被居中裁掉）的行。</summary>
    private bool HasTallRowInView()
    {
        double vh = SvTimeline.ViewportHeight;
        foreach (var row in _timelineRows)
        {
            if (row.ActualHeight <= vh) continue;
            double top = RowTopInViewport(row);
            if (top < vh && top + row.ActualHeight > 0)
                return true;
        }
        return false;
    }

    private void SnapTimelineToCenter()
    {
        if (_timelineRows.Count == 0) return;
        // 有超长行在视口内时禁用自动吸附：居中会把内容上下都挡住，
        // 也让用户能自由滚动读完长文本，不被拽回。
        if (HasTallRowInView()) return;

        double centerY = SvTimeline.ViewportHeight / 2;
        FrameworkElement? best = null;
        double bestDist = double.MaxValue;
        foreach (var row in _timelineRows)
        {
            double d = Math.Abs(RowCenterInViewport(row) - centerY);
            if (d < bestDist) { bestDist = d; best = row; }
        }
        if (best == null) return;
        double offset = RowCenterInViewport(best) - centerY;
        if (Math.Abs(offset) > 1)
            SvTimeline.ScrollToVerticalOffset(SvTimeline.VerticalOffset + offset);
    }

    private double RowCenterInViewport(FrameworkElement row) =>
        row.TranslatePoint(new Point(0, row.ActualHeight / 2), SpTimeline).Y - SvTimeline.VerticalOffset;

    private double RowTopInViewport(FrameworkElement row) =>
        row.TranslatePoint(new Point(0, 0), SpTimeline).Y - SvTimeline.VerticalOffset;

    // ---------------- 拖拽 / 贴边（与 Python 一致：窗口几何缩放 + 内容随尺寸缩放） ----------------
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            _dragging = true;
            _cancelHide();
            _undock();
            DragMove();
        }
        catch { }
        finally
        {
            _dragging = false;
            MaybeDockOnRelease();
        }
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        Card.BorderBrush = BorderHover;
        if (TopGlow.Background is RadialGradientBrush rg)
            rg.GradientStops[0].Color = Color.FromArgb(0x54, 0xFF, 0xFF, 0xFF);
        if (_docked)
        {
            _cancelHide();
            if (!_peeking)
            {
                _peeking = true;
                RevealCard();
                AnimateWindowTo(FullX(_dockEdge), _dockedCenterY - CardH / 2, CardW, CardH, showOrbAtEnd: false);
            }
        }
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        Card.BorderBrush = BorderNorm;
        if (TopGlow.Background is RadialGradientBrush rg)
            rg.GradientStops[0].Color = Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF);
        if (_docked && !_dragging && !_menuOpen)
            _hideTimer.Start();
    }

    private void ScheduleHide()
    {
        if (!_docked || !_peeking) return;
        var p = Mouse.GetPosition(this);
        if (p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight) return;
        _peeking = false;
        AnimateWindowTo(HiddenX(_dockEdge), _dockedCenterY - OrbSize / 2, OrbSize, OrbSize, showOrbAtEnd: true);
    }

    private void HideToSide() => DockToSide(NearestEdge().edge);

    private void MaybeDockOnRelease()
    {
        var (edge, dist) = NearestEdge();
        if (dist <= DockThreshold)
            DockToSide(edge);
    }

    private void DockToSide(string edge)
    {
        _dockEdge = edge;
        _docked = true;
        _peeking = false;
        _cancelHide();
        var wa = SystemParameters.WorkArea;
        _dockedCenterY = Math.Clamp(Top + CardH / 2, wa.Top + OrbSize / 2, wa.Bottom - OrbSize / 2);
        AnimateWindowTo(HiddenX(edge), _dockedCenterY - OrbSize / 2, OrbSize, OrbSize, showOrbAtEnd: true);
    }

    private bool _dockAnimDone;
    private bool _dockShowOrb;
    private double _dockTargetX;
    private double _dockTargetY;
    private double _dockTargetW;
    private double _dockTargetH;

    private void AnimateWindowTo(double x, double y, double w, double h, bool showOrbAtEnd)
    {
        if (_dismissing) return;
        _dockAnimDone = false;
        _dockShowOrb = showOrbAtEnd;
        _dockTargetX = x;
        _dockTargetY = y;
        _dockTargetW = w;
        _dockTargetH = h;
        if (!showOrbAtEnd)
        {
            // Transparent WPF windows are layered: resizing one per frame is
            // expensive. Animate this cached transform, then resize once.
            CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CardScale.ScaleX = Math.Clamp(ActualWidth / CardW, 0, 1);
            CardScale.ScaleY = Math.Clamp(ActualHeight / CardH, 0, 1);
            Width = CardW;
            Height = CardH;
            RevealCard();
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(280));
        var scaleY = new DoubleAnimation(h / CardH, duration) { EasingFunction = ease };
        scaleY.Completed += (_, _) => OnDockAnimDone();
        BeginAnimation(LeftProperty, new DoubleAnimation(x, duration) { EasingFunction = ease });
        BeginAnimation(TopProperty, new DoubleAnimation(y, duration) { EasingFunction = ease });
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(w / CardW, duration) { EasingFunction = ease });
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
    }

    private void OnDockAnimDone()
    {
        if (_dockAnimDone) return;
        _dockAnimDone = true;
        if (_dockShowOrb)
        {
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            Left = _dockTargetX;
            Top = _dockTargetY;
            Width = _dockTargetW;
            Height = _dockTargetH;
            CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(ShowOrb));
        }
    }

    // 内容随窗口尺寸同步缩放，与 Python 的“卡片画满窗口”一致
    private void RevealCard()
    {
        CardGrid.Visibility = Visibility.Visible;
        Orb.Visibility = Visibility.Collapsed;
    }

    private void ShowCard()
    {
        CardScale.ScaleX = 1;
        CardScale.ScaleY = 1;
        RevealCard();
    }

    private void ShowOrb()
    {
        CardGrid.Visibility = Visibility.Collapsed;
        Orb.Visibility = Visibility.Visible;
        // 强制重绘分层窗口，确保贴边后圆球真正上屏
        Orb.InvalidateVisual();
        UpdateLayout();
    }

    private void _cancelHide() => _hideTimer.Stop();

    private void _undock()
    {
        if (_docked)
        {
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            BeginAnimation(WidthProperty, null);
            BeginAnimation(HeightProperty, null);
            CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            Left = FullX(_dockEdge);
            Top = _dockedCenterY - CardH / 2;
            Width = CardW;
            Height = CardH;
            ShowCard();
        }
        _dockEdge = "";
        _docked = false;
        _peeking = false;
    }

    private (string edge, double dist) NearestEdge()
    {
        var wa = SystemParameters.WorkArea;
        var r = new Rect(Left, Top, ActualWidth, ActualHeight);
        var cand = new[]
        {
            ("left", r.Left - wa.Left),
            ("right", wa.Right - r.Right),
        };
        var best = cand[0];
        foreach (var c in cand) if (c.Item2 < best.Item2) best = c;
        return best;
    }

    private double HiddenX(string edge) =>
        edge == "left" ? SystemParameters.WorkArea.Left - OrbSize / 2
                       : SystemParameters.WorkArea.Right - OrbSize / 2;

    private double FullX(string edge) =>
        edge == "left" ? SystemParameters.WorkArea.Left : SystemParameters.WorkArea.Right - CardW;

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
        AppConfig.Save(_config);
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
    private void MenuRefresh_Click(object sender, RoutedEventArgs e) => Refresh();
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

    private static SolidColorBrush Brush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));
}
