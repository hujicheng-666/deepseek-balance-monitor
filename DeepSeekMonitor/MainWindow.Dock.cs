using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DeepSeekMonitor;

/// <summary>
/// 贴边 / 悬浮球 / 拖拽动画逻辑(partial,与 MainWindow.xaml.cs 共享同一类)。
/// 透明分层窗口的缩放动画统一走缓存的 CardScale,避免逐帧改窗口几何。
/// </summary>
public partial class MainWindow
{
    private DispatcherTimer _hideTimer = null!;
    private string _dockEdge = "";   // "" / left / right
    private bool _docked;
    private bool _peeking;
    private double _dockedCenterY;    // 贴边时保留的垂直中心
    private bool _dragging;
    private bool _menuOpen;
    private bool _dockAnimDone;
    private bool _dockShowOrb;
    private double _dockTargetX;
    private double _dockTargetY;
    private double _dockTargetW;
    private double _dockTargetH;

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

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 可点击文字（定价页“官方定价页 →”链接）不触发窗口拖拽，
        // 否则会吃掉它的点击事件。
        if (e.OriginalSource is TextBlock { Tag: "clickable" })
            return;
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

    // 内容随窗口尺寸同步缩放,与 Python 的“卡片画满窗口”一致
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
        // 强制重绘分层窗口,确保贴边后圆球真正上屏
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
}
