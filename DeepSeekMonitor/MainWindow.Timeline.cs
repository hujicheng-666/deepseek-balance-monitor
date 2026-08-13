using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DeepSeekMonitor.Models;

namespace DeepSeekMonitor;

/// <summary>
/// 服务状态时间线的渲染 / 滚动中轴聚焦 / 自动吸附逻辑(partial)。
/// </summary>
public partial class MainWindow
{
    private readonly List<FrameworkElement> _timelineRows = new();
    private readonly List<TextBlock> _rowTexts = new();
    private Border _topSpacer = null!;
    private Border _bottomSpacer = null!;
    private DispatcherTimer _snapTimer = null!;

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
                // 相邻事件之间留出间距,避免文字挤在一起
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
                // Time 在解析时已格式化为 yyyy-MM-dd HH:mm(空串表示未知)
                Text = (string.IsNullOrEmpty(ev.Time) ? "最近更新" : ev.Time)
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

        // 视口内存在比视口还高的超长行时,自动居中/淡出会把内容挡住、看不清;
        // 此时退化为普通可读列表:不淡出、不缩放(吸附也在 Snap 里同步禁用)。
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
            // 时间线文字统一使用与「服务 状态正常」一致的次级亮度,不做额外增亮
            _rowTexts[i].Foreground = SubBrush;
        }
        TimelineOverlay.Rows = _timelineRows;
        TimelineOverlay.Opacities = opacities;
        TimelineOverlay.InvalidateVisual();
        _snapTimer.Stop();
        _snapTimer.Start();
    }

    /// <summary>视口内是否有比视口还高(内容会被居中裁掉)的行。</summary>
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
        // 有超长行在视口内时禁用自动吸附:居中会把内容上下都挡住,
        // 也让用户能自由滚动读完长文本,不被拽回。
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
}
