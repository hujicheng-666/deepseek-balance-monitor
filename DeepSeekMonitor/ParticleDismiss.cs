using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DeepSeekMonitor;

/// <summary>
/// 退出时的「风化消散」：把当前画面采样成 2px 颗粒，
/// 主风场向右、叠加上下乱流与重力，逐批被卷走，约 1.3 秒后退出。
/// </summary>
public class ParticleDismiss
{
    private readonly Window _window;
    private readonly Image _overlay;
    private readonly Image _wholeCard;
    private readonly WriteableBitmap _wb;
    private readonly byte[] _buf;
    private readonly byte[] _source;
    private readonly Particle[] _particles;
    private readonly int _w;
    private readonly int _h;
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _timer;

    private struct Particle
    {
        public int Sx;
        public int Sy;
        public double Vx;
        public double Vy;
        public double Delay;
    }

    public ParticleDismiss(Window window)
    {
        _window = window;
        _w = Math.Max(2, (int)window.ActualWidth);
        _h = Math.Max(2, (int)window.ActualHeight);

        // 采样当前画面
        var rtb = new RenderTargetBitmap(_w, _h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(window);
        _source = new byte[_w * _h * 4];
        rtb.CopyPixels(_source, _w * 4, 0);

        // 以 2px 网格采样颗粒
        // Every source pixel becomes its own fragment, matching the Python
        // version's dense wind-erosion look instead of a sparse stipple.
        const int grain = 1;
        var list = new List<Particle>();
        for (int sy = 0; sy < _h; sy += grain)
        {
            for (int sx = 0; sx < _w; sx += grain)
            {
                double noise = ((sx * 17 + sy * 31) % 101) / 100.0;
                double vx = 54 + noise * 74;            // 主风场向右
                double vy = (noise - 0.5) * 42 + 18;    // 上下乱流 + 重力
                double delay = 0.03 + sx / (double)Math.Max(1, _w) * 0.22 + noise * 0.10;
                list.Add(new Particle { Sx = sx, Sy = sy, Vx = vx, Vy = vy, Delay = delay });
            }
        }
        _particles = list.ToArray();

        _wb = new WriteableBitmap(_w, _h, 96, 96, PixelFormats.Pbgra32, null);
        _buf = new byte[_w * _h * 4];
        _wholeCard = new Image
        {
            Source = rtb,
            Stretch = Stretch.Fill,
            Width = _w,
            Height = _h,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _overlay = new Image
        {
            Source = _wb,
            Stretch = Stretch.Fill,
            Width = _w,
            Height = _h,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        if (window.Content is Grid root)
        {
            root.Children.Add(_wholeCard);
            root.Children.Add(_overlay);
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        if (_window.Content is Grid root)
        {
            foreach (var child in root.Children)
            {
                if (child != _wholeCard && child != _overlay)
                    ((UIElement)child).Visibility = Visibility.Collapsed;
            }
        }
        _clock.Start();
        _timer.Start();
    }

    private void Tick()
    {
        double elapsed = _clock.Elapsed.TotalSeconds;
        if (elapsed >= 1.32)
        {
            _timer.Stop();
            _window.Hide();
            Application.Current.Shutdown();
            return;
        }

        Array.Clear(_buf, 0, _buf.Length);
        // Fade the intact snapshot before only the drifting pixels remain.
        // This overlap avoids the abrupt switch present in the prototype.
        _wholeCard.Opacity = Math.Max(0, 1 - elapsed / 0.34);
        for (int i = 0; i < _particles.Length; i++)
        {
            var p = _particles[i];
            double local = Math.Clamp((elapsed - p.Delay) / 0.94, 0, 1);
            if (local <= 0) continue;

            double ease = 1 - (1 - local) * (1 - local);
            int px = (int)(p.Sx + p.Vx * ease);
            int py = (int)(p.Sy + p.Vy * ease + 22 * local * local);
            if (px < 0 || py < 0 || px >= _w || py >= _h) continue;

            int si = (p.Sy * _w + p.Sx) * 4;
            byte sa = _source[si + 3];
            if (sa == 0) continue;

            double alpha = Math.Pow(1 - local, 1.25);
            int di = (py * _w + px) * 4;
            _buf[di] = (byte)(_source[si] * alpha);
            _buf[di + 1] = (byte)(_source[si + 1] * alpha);
            _buf[di + 2] = (byte)(_source[si + 2] * alpha);
            _buf[di + 3] = (byte)(sa * alpha);
        }
        _wb.WritePixels(new Int32Rect(0, 0, _w, _h), _buf, _w * 4, 0);
    }
}
