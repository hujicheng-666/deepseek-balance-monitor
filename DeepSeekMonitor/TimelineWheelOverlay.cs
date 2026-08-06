using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeepSeekMonitor;

/// <summary>Draws the timeline as one continuous, distance-faded canvas.</summary>
public sealed class TimelineWheelOverlay : FrameworkElement
{
    public IReadOnlyList<FrameworkElement> Rows { get; set; } = new List<FrameworkElement>();
    public IReadOnlyList<double> Opacities { get; set; } = new List<double>();

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        const double x = 7;
        var centers = new List<double>();
        for (var i = 0; i < Rows.Count; i++)
        {
            var row = Rows[i];
            if (!row.IsVisible || row.ActualHeight <= 0) continue;
            centers.Add(row.TranslatePoint(new Point(0, 0), this).Y + 9);
        }

        for (var i = 0; i < centers.Count; i++)
        {
            var opacity = i < Opacities.Count ? Opacities[i] : 1.0;
            var alpha = (byte)(230 * opacity);
            if (i + 1 < centers.Count)
            {
                var nextOpacity = i + 1 < Opacities.Count ? Opacities[i + 1] : 1.0;
                var lineAlpha = (byte)(230 * System.Math.Min(opacity, nextOpacity));
                drawingContext.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(lineAlpha, 190, 195, 205)), 1),
                    new Point(x, centers[i] + 3), new Point(x, centers[i + 1]));
            }
            drawingContext.DrawEllipse(new SolidColorBrush(Color.FromArgb(alpha, 225, 228, 234)), null,
                new Point(x, centers[i]), 3, 3);
        }
    }
}
