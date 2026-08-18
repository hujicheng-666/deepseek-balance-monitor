using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using DeepSeekMonitor.Controls;
using DeepSeekMonitor.Models;
using DeepSeekMonitor.Services;

namespace DeepSeekMonitor.Views;

public partial class MainWindow : Window
{
    /// <summary>卡片当前展示的页面：余额 / 服务事件 / 模型定价。</summary>
    private enum CardView { Balance, Service, Pricing }

    private const double CardW = 310;
    private const double CardH = 174;
    private const double OrbSize = 32;
    private const int HideDelayMs = 700;
    private const int DockThreshold = 36;
    private const int DockAnimMs = 280;

    // DeepSeek 官方充值页
    private const string TopUpUrl = "https://platform.deepseek.com/top_up";

    private static readonly IBrush SubBrush = Brush("#B7BAC2");
    private static readonly IBrush AmberBrush = Brush("#E5B568");
    private static readonly IBrush GreenBrush = Brush("#56C596");
    private static readonly IBrush CoralBrush = Brush("#ED7C72");
    private static readonly IBrush BorderNorm = Brush("#777B86");
    private static readonly IBrush BorderHover = Brush("#D7DBE4");
    private static readonly IBrush WhiteBrush = Brush("#FFFFFF");

    private readonly DeepSeekApi _api = new();
    private AppConfig _config = null!;
    private DispatcherTimer _hideTimer = null!;
    private DispatcherTimer _refreshTimer = null!;
    private DispatcherTimer _snapTimer = null!;
    private DispatcherTimer _dockTimer = null!;
    private TrayIcon? _tray;
    private WindowNotificationManager? _notifications;

    private string _dockEdge = "";
    private bool _docked;
    private bool _peeking;
    private bool _pointerInside;
    private double _dockedCenterY;
    private bool _dragging;
    private CardView _view;
    private bool _menuOpen;
    private bool _lowBalanceNotified;
    private bool _dismissing;
    private bool _refreshing;       // 防止余额刷新重入
    private bool _loadingService;   // 防止服务状态加载重入
    private bool _loadingPricing;   // 防止定价页刷新重入
    private PricingSnapshot? _lastPricing; // 最近一次成功抓取的官方定价

    private readonly List<Control> _timelineRows = new();
    private readonly List<TextBlock> _rowTexts = new();
    private List<ServiceEvent> _lastServiceEvents = new();
    private Border _topSpacer = null!;
    private Border _bottomSpacer = null!;

    // 贴边动画状态
    private bool _dockAnimActive;
    private bool _dockShowOrb;
    private double _animT;
    private PixelPoint _fromPos;
    private PixelPoint _toPos;
    private double _fromW;
    private double _fromH;
    private double _toW;
    private double _toH;

    private double Scale => Math.Max(0.25, RenderScaling);

    // Avalonia 的 x:Name 不会为 RenderTransform 生成字段，用属性访问卡片缩放。
    private ScaleTransform CardScale
    {
        get
        {
            if (CardGrid.RenderTransform is not ScaleTransform st)
            {
                st = new ScaleTransform(1, 1);
                CardGrid.RenderTransform = st;
            }
            return st;
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        _config = AppConfig.Load();

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HideDelayMs) };
        _hideTimer.Tick += (_, _) => ScheduleHide();

        _snapTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _snapTimer.Tick += (_, _) => SnapTimelineToCenter();

        _dockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _dockTimer.Tick += DockTick;

        _refreshTimer = new DispatcherTimer();
        _refreshTimer.Tick += (_, _) => Refresh();
        SetInterval(Math.Max(30, _config.RefreshInterval));

        SetupContextMenu();
        SetupTray();

        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            SetStatus("loading", "准备中");
            Dispatcher.UIThread.Post(Refresh);
        }
        else
        {
            SetStatus("no_key", "未设置");
            Dispatcher.UIThread.Post(FirstRunGuide);
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _notifications ??= new WindowNotificationManager(this)
        {
            Position = NotificationPosition.TopRight,
            MaxItems = 1,
        };
        PositionTopRight();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _tray?.Dispose();
        base.OnClosing(e);
    }

    // ---------------- 初始化 ----------------
    private void PositionTopRight()
    {
        var wa = WorkArea();
        double s = Scale;
        Position = new PixelPoint(wa.X + wa.Width - (int)(CardW * s) - (int)(24 * s), wa.Y + (int)(24 * s));
        Width = CardW;
        Height = CardH;
    }

    private PixelRect WorkArea()
    {
        var screen = this.Screens.ScreenFromWindow(this) ?? this.Screens.Primary;
        return screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
    }

    private void SetupTray()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://DeepSeekMonitor/Assets/whale.png"));
            var menu = new NativeMenu();
            var show = new NativeMenuItem("显示");
            show.Command = new ActionCommand(ShowAndPeek);
            var quit = new NativeMenuItem("退出");
            quit.Command = new ActionCommand(Dismiss);
            menu.Items.Add(show);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(quit);

            _tray = new TrayIcon
            {
                Icon = new WindowIcon(stream),
                ToolTipText = "DeepSeek",
                Menu = menu,
                IsVisible = true,
            };
            _tray.Clicked += (_, _) => ShowAndPeek();
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
            AnimateWindowTo(new PixelPoint((int)FullX(_dockEdge), (int)(_dockedCenterY - (CardH * Scale) / 2)), CardW, CardH, false);
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
            Render(await _api.FetchBalanceAsync(key));
        }
        catch (ApiException ex)
        {
            ShowBalanceError(ex.Message.Length > 22 ? ex.Message[..22] : ex.Message);
        }
        catch (Exception ex)
        {
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

        var unit = CurrencyUnit(info.Currency);
        var name = CurrencyName(info.Currency);
        BalanceText.Foreground = WhiteBrush;
        BalanceText.Text = $"{info.TotalBalance:F2} {unit}";
        BalanceNote.Text = $"{name} 可用余额";
        LblTopped.Text = $"充值 {info.ToppedUpBalance:F2} {unit}";
        LblGranted.Text = $"赠送 {info.GrantedBalance:F2} {unit}";
        LblTime.Text = $"更新 {BeijingTime.Now:HH:mm}";

        var low = info.TotalBalance < _config.LowThreshold;
        if (low && _config.LowWarn)
        {
            SetStatus("ok", "余额偏低");
            StatusDot.Foreground = AmberBrush;
            BalanceText.Foreground = CoralBrush;
            BalanceNote.Text = $"余额不多啦，低于 {_config.LowThreshold:F0} {unit} 咯～";
            if (!_lowBalanceNotified)
            {
                _lowBalanceNotified = true;
                _notifications?.Show(new Notification(
                    "DeepSeek 余额偏低",
                    $"当前可用余额 {info.TotalBalance:F2} {unit}，低于提醒阈值 {_config.LowThreshold:F0} {unit}。",
                    NotificationType.Warning,
                    TimeSpan.FromSeconds(8)));
            }
        }
        else
        {
            _lowBalanceNotified = false;
            SetStatus("ok");
            BalanceText.Foreground = WhiteBrush;
            BalanceNote.Text = $"{name} 可用余额";
        }
    }

    private static string CurrencyUnit(string currency) => currency switch
    {
        "CNY" => "元",
        "USD" => "美元",
        _ => currency,
    };

    private static string CurrencyName(string currency) => currency switch
    {
        "CNY" => "人民币",
        "USD" => "美元",
        _ => currency,
    };

    // ---------------- 卡片页切换（余额 / 服务 / 定价） ----------------
    /// <summary>切换卡片页面；再次点击当前页的按钮则回到余额页。</summary>
    private void ShowView(CardView view)
    {
        if (_view == view)
        {
            ShowView(CardView.Balance);
            return;
        }

        _view = view;
        var balance = view == CardView.Balance;
        var service = view == CardView.Service;
        BalanceText.IsVisible = balance;
        BalanceNote.IsVisible = balance;
        BottomRow.IsVisible = balance;
        ServicePanel.IsVisible = service;
        PricingPanel.IsVisible = !balance && !service;
        TitleText.Text = view switch
        {
            CardView.Service => "DeepSeek 服务",
            CardView.Pricing => "模型定价",
            _ => "DeepSeek",
        };
        BtnService.Content = service ? "‹" : "◉";
        ToolTip.SetTip(BtnService, service ? "返回余额" : "查看 DeepSeek 服务状态");
        BtnPricing.Content = view == CardView.Pricing ? "‹" : "$";
        ToolTip.SetTip(BtnPricing, view == CardView.Pricing ? "返回余额" : "查看模型定价");

        if (service)
            CheckServiceStatus();
        else if (view == CardView.Pricing)
            RenderPricing();
        else
            SetStatus(string.IsNullOrWhiteSpace(_config.ApiKey) ? "no_key" : "ok");
    }

    // ---------------- 模型定价视图 ----------------
    /// <summary>先展示已有数据（上次抓取或内置快照），再异步刷新官方价格。</summary>
    private void RenderPricing()
    {
        RenderPricingSnapshot(_lastPricing ?? ModelPricing.Fallback, live: _lastPricing != null);
        if (_loadingPricing) return;
        _loadingPricing = true;
        _ = LoadPricingAsync();
    }

    private async Task LoadPricingAsync()
    {
        try
        {
            var snapshot = await _api.FetchPricingAsync();
            _lastPricing = snapshot;
            if (_view == CardView.Pricing)
                RenderPricingSnapshot(snapshot, live: true);
        }
        catch
        {
            // 抓取失败：保留上次成功数据或内置快照，并标注离线。
            if (_view == CardView.Pricing)
            {
                RenderPricingSnapshot(_lastPricing ?? ModelPricing.Fallback, live: _lastPricing != null);
                PricingMeta.Text = _lastPricing != null
                    ? $"$ / 百万 tokens · 官方页暂不可达，显示上次更新 {BeijingTime.ToBeijing(_lastPricing.FetchedAt):MM-dd HH:mm}"
                    : $"$ / 百万 tokens · 官方页暂不可达，离线快照 {BeijingTime.ToBeijing(ModelPricing.Fallback.FetchedAt):yyyy-MM-dd}";
            }
        }
        finally
        {
            _loadingPricing = false;
        }
    }

    private void RenderPricingSnapshot(PricingSnapshot snapshot, bool live)
    {
        SpPricing.Children.Clear();
        // ScrollViewer 内容在首次布局前宽度未约束，TextBlock 的 Wrap 可能不生效导致横向溢出；
        // 显式限宽（基于视口实际宽度），保证任何情况下都不左右截断。
        var maxW = SvPricing.Viewport.Width > 0 ? SvPricing.Viewport.Width - 7 : 296;

        foreach (var p in snapshot.Models)
        {
            SpPricing.Children.Add(new TextBlock
            {
                Text = $"{p.ApiName} · {p.BaseModel}",
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush("#E9EBF0"), // 柔和白，避免纯白刺眼
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = maxW,
                Margin = new Thickness(0, 0, 4, 2),
            });

            var peakIn = $"{Price(p.PeakCacheHit)}/{Price(p.PeakCacheMiss)}";
            var offIn = $"{Price(p.OffPeakCacheHit)}/{Price(p.OffPeakCacheMiss)}";
            string body;
            if (snapshot.IsPeakNow == true)
                body = $"● 当前高峰 输入 ${peakIn}（命中/未命中）· 输出 ${Price(p.PeakOutput)}\n非高峰 输入 ${offIn} · 输出 ${Price(p.OffPeakOutput)}";
            else if (snapshot.IsPeakNow == false)
                body = $"● 当前非高峰 输入 ${offIn}（命中/未命中）· 输出 ${Price(p.OffPeakOutput)}\n高峰 输入 ${peakIn} · 输出 ${Price(p.PeakOutput)}";
            else
                body = $"高峰 输入 ${peakIn}（命中/未命中）· 输出 ${Price(p.PeakOutput)}\n非高峰 输入 ${offIn} · 输出 ${Price(p.OffPeakOutput)}";
            if (!string.IsNullOrWhiteSpace(p.Concurrency))
                body += $"\n并发上限 {p.Concurrency}";

            SpPricing.Children.Add(new TextBlock
            {
                Text = body,
                FontSize = 10,
                Foreground = SubBrush,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = maxW,
                Margin = new Thickness(0, 0, 4, 12),
            });
        }

        var when = live ? $"更新于 {BeijingTime.Now:HH:mm}" : $"收录于 {BeijingTime.ToBeijing(snapshot.FetchedAt):yyyy-MM-dd}";
        var peak = string.IsNullOrEmpty(snapshot.PeakHoursDisplay) ? "" : $" · 高峰 {snapshot.PeakHoursDisplay}";
        PricingMeta.Text = $"$ / 百万 tokens · {when}{peak}";
    }

    private static string Price(double value)
        => value.ToString("0.######");

    private void OnPricingClick(object? sender, RoutedEventArgs e) => ShowView(CardView.Pricing);

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

    private void ClearTimeline()
    {
        _timelineRows.Clear();
        _rowTexts.Clear();
        SpTimeline.Children.Clear();
        TimelineOverlay.Rows = _timelineRows;
        TimelineOverlay.Opacities = new List<double>();
        TimelineOverlay.Refresh();
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
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var text = new TextBlock
            {
                FontSize = 10,
                Foreground = SubBrush,
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

        Dispatcher.UIThread.Post(() =>
        {
            if (_timelineRows.Count == 0) return;
            var vh = SvTimeline.Viewport.Height;
            _topSpacer.Height = Math.Max(0, vh / 2 - _timelineRows[0].Bounds.Height / 2 - 8);
            _bottomSpacer.Height = Math.Max(0, vh / 2 - _timelineRows[^1].Bounds.Height / 2 - 8);
            UpdateTimelineWheel();
        }, DispatcherPriority.Loaded);
    }

    private void OnTimelineScroll(object? sender, ScrollChangedEventArgs e) => UpdateTimelineWheel();

    private void UpdateTimelineWheel()
    {
        if (_timelineRows.Count == 0) return;
        double centerY = SvTimeline.Viewport.Height / 2;
        double range = Math.Max(1, SvTimeline.Viewport.Height * 0.62);
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
                row.RenderTransform = new ScaleTransform
                {
                    ScaleX = 1.0,
                    ScaleY = 1.0 - Math.Abs(direction) * 0.48,
                };
            }
            _rowTexts[i].Foreground = SubBrush;
        }

        TimelineOverlay.Rows = _timelineRows;
        TimelineOverlay.Opacities = opacities;
        TimelineOverlay.Refresh();

        _snapTimer.Stop();
        _snapTimer.Start();
    }

    private bool HasTallRowInView()
    {
        double vh = SvTimeline.Viewport.Height;
        foreach (var row in _timelineRows)
        {
            if (row.Bounds.Height <= vh) continue;
            double top = RowTopInViewport(row);
            if (top < vh && top + row.Bounds.Height > 0)
                return true;
        }
        return false;
    }

    private void SnapTimelineToCenter()
    {
        if (_timelineRows.Count == 0) return;
        if (HasTallRowInView()) return;

        double centerY = SvTimeline.Viewport.Height / 2;
        Control? best = null;
        double bestDist = double.MaxValue;
        foreach (var row in _timelineRows)
        {
            double d = Math.Abs(RowCenterInViewport(row) - centerY);
            if (d < bestDist) { bestDist = d; best = row; }
        }
        if (best == null) return;
        double offset = RowCenterInViewport(best) - centerY;
        if (Math.Abs(offset) > 1)
            SvTimeline.Offset = new Vector(SvTimeline.Offset.X, SvTimeline.Offset.Y + offset);
    }

    private double RowCenterInViewport(Control row)
    {
        var p = row.TranslatePoint(new Point(0, row.Bounds.Height / 2), SpTimeline);
        return (p?.Y ?? 0) - SvTimeline.Offset.Y;
    }

    private double RowTopInViewport(Control row)
    {
        var p = row.TranslatePoint(new Point(0, 0), SpTimeline);
        return (p?.Y ?? 0) - SvTimeline.Offset.Y;
    }

    // ---------------- 拖拽 / 贴边 ----------------
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Header controls, context-menu items and clickable text (定价页链接)
        // must never start a window drag: doing so cancels their click action.
        if (e.Source is Button || e.Source is MenuItem || e.Source is TextBox ||
            (e.Source is TextBlock tb && Equals(tb.Tag, "clickable")))
            return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragging = true;
            _cancelHide();
            _undock();
            this.BeginMoveDrag(e);
            _dragging = false;
            MaybeDockOnRelease();
        }
    }

    private void OnPointerEntered(object? sender, PointerEventArgs e)
    {
        _pointerInside = true;
        Card.BorderBrush = BorderHover;
        if (TopGlow.Background is RadialGradientBrush rg && rg.GradientStops.Count > 0)
            rg.GradientStops[0].Color = Color.FromArgb(0x54, 0xFF, 0xFF, 0xFF);
        if (_docked)
        {
            _cancelHide();
            if (!_peeking)
            {
                _peeking = true;
                RevealCard();
                AnimateWindowTo(new PixelPoint((int)FullX(_dockEdge), (int)(_dockedCenterY - (CardH * Scale) / 2)), CardW, CardH, false);
            }
        }
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        _pointerInside = false;
        Card.BorderBrush = BorderNorm;
        if (TopGlow.Background is RadialGradientBrush rg && rg.GradientStops.Count > 0)
            rg.GradientStops[0].Color = Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF);
        if (_docked && !_dragging && !_menuOpen)
            _hideTimer.Start();
    }

    private void ScheduleHide()
    {
        if (!_docked || !_peeking) return;
        if (_pointerInside) return;
        _peeking = false;
        AnimateWindowTo(new PixelPoint((int)HiddenX(_dockEdge), (int)(_dockedCenterY - (OrbSize * Scale) / 2)), OrbSize, OrbSize, true);
    }

    private void HideToSide() => DockToSide(NearestEdge().edge);

    private void MaybeDockOnRelease()
    {
        var (edge, dist) = NearestEdge();
        if (dist <= DockThreshold * Scale)
            DockToSide(edge);
    }

    private void DockToSide(string edge)
    {
        _dockEdge = edge;
        _docked = true;
        _peeking = false;
        _cancelHide();
        var wa = WorkArea();
        _dockedCenterY = Math.Clamp(Position.Y + (CardH * Scale) / 2, wa.Y + OrbSize * Scale / 2, wa.Y + wa.Height - OrbSize * Scale / 2);
        AnimateWindowTo(new PixelPoint((int)HiddenX(edge), (int)(_dockedCenterY - (OrbSize * Scale) / 2)), OrbSize, OrbSize, true);
    }

    private void AnimateWindowTo(PixelPoint toPos, double w, double h, bool showOrbAtEnd)
    {
        if (_dismissing) return;
        _dockShowOrb = showOrbAtEnd;
        _fromPos = Position;
        _fromW = Width;
        _fromH = Height;
        _toPos = toPos;
        _toW = w;
        _toH = h;
        _animT = 0;
        _dockAnimActive = true;
        if (!showOrbAtEnd) RevealCard();
        _dockTimer.Start();
    }

    private void DockTick(object? sender, EventArgs e)
    {
        if (!_dockAnimActive) return;
        _animT += _dockTimer.Interval.TotalMilliseconds;
        double t = Math.Min(1, _animT / DockAnimMs);
        double eased = EaseInOutCubic(t);
        Position = new PixelPoint(
            (int)Math.Round(_fromPos.X + (_toPos.X - _fromPos.X) * eased),
            (int)Math.Round(_fromPos.Y + (_toPos.Y - _fromPos.Y) * eased));
        Width = _fromW + (_toW - _fromW) * eased;
        Height = _fromH + (_toH - _fromH) * eased;
        CardScale.ScaleX = Math.Clamp(Width / CardW, 0, 1);
        CardScale.ScaleY = Math.Clamp(Height / CardH, 0, 1);
        if (t >= 1)
        {
            _dockTimer.Stop();
            _dockAnimActive = false;
            OnDockAnimDone();
        }
    }

    private void OnDockAnimDone()
    {
        if (_dockShowOrb)
        {
            Position = _toPos;
            Width = _toW;
            Height = _toH;
            CardScale.ScaleX = Math.Clamp(Width / CardW, 0, 1);
            CardScale.ScaleY = Math.Clamp(Height / CardH, 0, 1);
            ShowOrb();
        }
    }

    private static double EaseInOutCubic(double t)
        => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    private void RevealCard()
    {
        CardGrid.IsVisible = true;
        Orb.IsVisible = false;
    }

    private void ShowOrb()
    {
        CardGrid.IsVisible = false;
        Orb.IsVisible = true;
    }

    private void _cancelHide() => _hideTimer.Stop();

    private void _undock()
    {
        if (_docked)
        {
            _dockAnimActive = false;
            _dockTimer.Stop();
            double s = Scale;
            Position = new PixelPoint((int)FullX(_dockEdge), (int)(_dockedCenterY - (CardH * s) / 2));
            Width = CardW;
            Height = CardH;
            CardScale.ScaleX = 1;
            CardScale.ScaleY = 1;
            RevealCard();
        }
        _dockEdge = "";
        _docked = false;
        _peeking = false;
    }

    private (string edge, double dist) NearestEdge()
    {
        var wa = WorkArea();
        double s = Scale;
        double left = Position.X;
        double right = left + Width * s;
        var cand = new[] { ("left", left - wa.X), ("right", wa.X + wa.Width - right) };
        var best = cand[0];
        foreach (var c in cand)
            if (c.Item2 < best.Item2) best = c;
        return best;
    }

    private double HiddenX(string edge)
    {
        var wa = WorkArea();
        return edge == "left" ? wa.X - (OrbSize * Scale) / 2 : wa.X + wa.Width - (OrbSize * Scale) / 2;
    }

    private double FullX(string edge)
    {
        var wa = WorkArea();
        return edge == "left" ? wa.X : wa.X + wa.Width - CardW * Scale;
    }

    // ---------------- 设置 / 菜单 ----------------
    private void SetupContextMenu()
    {
        var menu = new ContextMenu();

        var refresh = new MenuItem { Header = "立即刷新" };
        refresh.Click += (_, _) => Refresh();
        menu.Items.Add(refresh);

        var topUp = new MenuItem { Header = "去充值" };
        topUp.Click += (_, _) => OpenTopUpPage();
        menu.Items.Add(topUp);

        var pricing = new MenuItem { Header = "查看模型定价" };
        pricing.Click += (_, _) => ShowView(CardView.Pricing);
        menu.Items.Add(pricing);

        var setKey = new MenuItem { Header = "设置 API Key" };
        setKey.Click += (_, _) => AskApiKey();
        menu.Items.Add(setKey);

        var dock = new MenuItem { Header = "收进屏幕侧边" };
        dock.Click += (_, _) => RequestDock();
        menu.Items.Add(dock);

        var interval = new MenuItem { Header = "自动刷新间隔" };
        foreach (var (label, secs) in new[] { ("30 秒", 30), ("1 分钟", 60), ("5 分钟", 300), ("15 分钟", 900) })
        {
            var it = new MenuItem { Header = label, Tag = secs };
            it.Click += (_, _) => SetInterval(secs);
            interval.Items.Add(it);
        }
        menu.Items.Add(interval);

        menu.Items.Add(new Separator());

        var lowWarn = new MenuItem { Header = "余额偏低时提醒" };
        lowWarn.Click += (_, _) =>
        {
            _config.LowWarn = !_config.LowWarn;
            AppConfig.Save(_config);
            lowWarn.IsChecked = _config.LowWarn;
            lowWarn.Header = "余额偏低时提醒";
            Refresh();
        };
        lowWarn.ToggleType = MenuItemToggleType.CheckBox;
        lowWarn.IsChecked = _config.LowWarn;
        menu.Items.Add(lowWarn);

        menu.Items.Add(new Separator());

        var quit = new MenuItem { Header = "退出" };
        quit.Click += (_, _) => Dismiss();
        menu.Items.Add(quit);

        RootPanel.ContextMenu = menu;
        RootPanel.ContextMenu.Opened += (_, _) => _menuOpen = true;
        RootPanel.ContextMenu.Closed += (_, _) => _menuOpen = false;
    }

    private void SetInterval(int secs)
    {
        _config.RefreshInterval = secs;
        AppConfig.Save(_config);
        _refreshTimer.Stop();
        _refreshTimer.Interval = TimeSpan.FromSeconds(secs);
        _refreshTimer.Start();
    }

    private void RequestDock()
    {
        _cancelHide();
        // A ContextMenu owns a native popup on Linux. Defer geometry
        // changes until it has completed closing, otherwise the dock request
        // is swallowed by the popup's close transition.
        Dispatcher.UIThread.Post(HideToSide, DispatcherPriority.Background);
    }

    private void FirstRunGuide()
    {
        // Keep the same flow as WPF: the welcome prompt is separate from the
        // API key entry dialog, whose wording stays unchanged when reopened.
        AskApiKey();
    }

    private async void AskApiKey()
    {
        var dlg = new InputDialog(
            "设置 API Key",
            "粘贴你的 DeepSeek API Key：\n（platform.deepseek.com → API Keys 页面获取）",
            _config.ApiKey);
        var result = await dlg.ShowDialog<bool?>(this);
        if (result != true) return;

        _config.ApiKey = dlg.Value.Trim();
        if (!AppConfig.Save(_config))
            _notifications?.Show(new Notification("保存失败",
                "API Key 可能不会被记住,请检查磁盘空间或目录权限。",
                NotificationType.Warning));
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

    // ---------------- 退出 ----------------
    private void Dismiss()
    {
        if (_dismissing) return;
        _dismissing = true;
        _refreshTimer.Stop();
        _hideTimer.Stop();
        _snapTimer.Stop();
        _dockTimer.Stop();
        _undock();
        var overlay = new ParticleDismissOverlay();
        overlay.Completed += ExitApplication;
        RootPanel.Children.Add(overlay);
        // Capture while the complete card is still visible. Capturing after
        // hiding it produced a transparent bitmap, hence no particle effect.
        overlay.Start(RootPanel, new Size(CardW, CardH));
        CardGrid.IsVisible = false;
        Orb.IsVisible = false;
    }

    private void ExitApplication()
    {
        _tray?.Dispose();
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else
            Close();
    }

    // ---------------- 事件处理 ----------------
    private void OnServiceClick(object? sender, RoutedEventArgs e) => ShowView(CardView.Service);
    private void OnMinClick(object? sender, RoutedEventArgs e) => RequestDock();
    private void OnRefreshClick(object? sender, RoutedEventArgs e) => Refresh();
    private void OnTopUpClick(object? sender, RoutedEventArgs e) => OpenTopUpPage();

    // ---------------- 去充值 ----------------
    private void OpenTopUpPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(TopUpUrl) { UseShellExecute = true });
        }
        catch { /* 打不开浏览器时静默忽略 */ }
    }

    private static IBrush Brush(string hex) => Avalonia.Media.Brush.Parse(hex);

    private sealed class ActionCommand : ICommand
    {
        private readonly Action _action;
        public ActionCommand(Action a) => _action = a;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _action();
    }
}
