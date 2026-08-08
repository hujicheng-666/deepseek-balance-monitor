using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace DeepSeekMonitor.Controls;

public sealed class ParticleDismissOverlay : Control
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly List<(int X, int Y, double Vx, double Vy, double Delay)> _particles = new();
    private RenderTargetBitmap? _snapshot;
    private DateTime _started;
    public event Action? Completed;

    public ParticleDismissOverlay()
    {
        IsHitTestVisible = false;
        _timer.Tick += (_, _) => { InvalidateVisual(); if ((DateTime.UtcNow - _started).TotalSeconds >= 1.32) { _timer.Stop(); Completed?.Invoke(); } };
    }

    public void Start(Visual source, Size size)
    {
        var w = Math.Max(2, (int)Math.Ceiling(size.Width));
        var h = Math.Max(2, (int)Math.Ceiling(size.Height));
        _snapshot = new RenderTargetBitmap(new PixelSize(w, h));
        _snapshot.Render(source);
        _particles.Clear();
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        {
            var n = ((x * 17 + y * 31) % 101) / 100d;
            _particles.Add((x, y, 54 + n * 74, (n - .5) * 42 + 18, .03 + x / (double)w * .22 + n * .10));
        }
        _started = DateTime.UtcNow;
        _timer.Start();
    }

    public override void Render(DrawingContext context)
    {
        if (_snapshot is null) return;
        var elapsed = (DateTime.UtcNow - _started).TotalSeconds;
        var full = new Rect(Bounds.Size);
        using (context.PushOpacity(Math.Max(0, 1 - elapsed / .34))) context.DrawImage(_snapshot, full);
        foreach (var p in _particles)
        {
            var local = Math.Clamp((elapsed - p.Delay) / .94, 0, 1);
            if (local <= 0) continue;
            var ease = 1 - (1 - local) * (1 - local);
            var target = new Rect(p.X + p.Vx * ease, p.Y + p.Vy * ease + 22 * local * local, 1, 1);
            using (context.PushOpacity(Math.Pow(1 - local, 1.25)))
                context.DrawImage(_snapshot, new Rect(p.X, p.Y, 1, 1), target);
        }
    }
}
