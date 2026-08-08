using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DeepSeekMonitor.Controls;

/// <summary>Draws the timeline as one continuous, distance-faded canvas (Avalonia port).</summary>
public sealed class TimelineWheelOverlay : Control
{
    public IReadOnlyList<Control> Rows { get; set; } = new List<Control>();
    public IReadOnlyList<double> Opacities { get; set; } = new List<double>();

    static TimelineWheelOverlay()
    {
        AffectsRender<TimelineWheelOverlay>(BoundsProperty);
    }

    public void Refresh()
    {
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        const double x = 7;
        var centers = new List<double>();
        for (var i = 0; i < Rows.Count; i++)
        {
            var row = Rows[i];
            if (!row.IsVisible || row.Bounds.Height <= 0) continue;
            var pt = row.TranslatePoint(new Point(0, 0), this);
            if (pt is Point p) centers.Add(p.Y + 9);
        }

        for (var i = 0; i < centers.Count; i++)
        {
            var opacity = i < Opacities.Count ? Opacities[i] : 1.0;
            var alpha = (byte)(230 * opacity);
            if (i + 1 < centers.Count)
            {
                var nextOpacity = i + 1 < Opacities.Count ? Opacities[i + 1] : 1.0;
                var lineAlpha = (byte)(230 * Math.Min(opacity, nextOpacity));
                context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(lineAlpha, 190, 195, 205)), 1),
                    new Point(x, centers[i] + 3), new Point(x, centers[i + 1]));
            }
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(alpha, 225, 228, 234)), null,
                new Point(x, centers[i]), 3, 3);
        }
    }
}
