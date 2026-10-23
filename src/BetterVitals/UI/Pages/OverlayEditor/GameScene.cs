using System.Windows;
using System.Windows.Media;

namespace BetterVitals.UI.Pages.OverlayEditor;

/// <summary>
/// An artificial, vector-drawn "game frame" behind the overlay preview: dusk sky, sun, two mountain ranges, a lake,
/// a horizon grid and a crosshair. Never a capture of anything. The same picture at any size; <see cref="Viewport"/>
/// (normalized 0–1 rectangle of the frame) lets the close-up show just the part behind the overlay, sharp at any zoom.
/// Theme-independent (it is "the game", not the app). The drawing is built once per aspect ratio and frozen.
/// </summary>
internal sealed class GameScene : FrameworkElement
{
    public static readonly DependencyProperty AspectProperty = DependencyProperty.Register(
        nameof(Aspect), typeof(double), typeof(GameScene), new FrameworkPropertyMetadata(16.0 / 9, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ViewportProperty = DependencyProperty.Register(
        nameof(Viewport), typeof(Rect), typeof(GameScene), new FrameworkPropertyMetadata(new Rect(0, 0, 1, 1), FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Dictionary<int, DrawingGroup> Cache = [];

    /// <summary>Width / height of the frame (the monitor's aspect).</summary>
    public double Aspect { get => (double)GetValue(AspectProperty); set => SetValue(AspectProperty, value); }

    public Rect Viewport { get => (Rect)GetValue(ViewportProperty); set => SetValue(ViewportProperty, value); }

    public GameScene()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var aspect = Math.Clamp(Aspect, 1.0, 4.0);
        var drawing = Drawing(aspect);
        // frame units: width = aspect * 1000, height = 1000
        var vp = Viewport;
        if (vp.Width <= 0 || vp.Height <= 0) vp = new Rect(0, 0, 1, 1);
        var fw = aspect * 1000;
        var sx = ActualWidth / (vp.Width * fw);
        var sy = ActualHeight / (vp.Height * 1000);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        dc.PushTransform(new ScaleTransform(sx, sy));
        dc.PushTransform(new TranslateTransform(-vp.X * fw, -vp.Y * 1000));
        dc.DrawDrawing(drawing);
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private static DrawingGroup Drawing(double aspect)
    {
        var key = (int)Math.Round(aspect * 100);
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var d)) return d;
            d = Build(key / 100.0 * 1000);
            Cache[key] = d;
            return d;
        }
    }

    private static Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }

    private static DrawingGroup Build(double w)
    {
        const double h = 1000;
        const double horizon = 610;
        var g = new DrawingGroup();
        using (var dc = g.Open())
        {
            // sky: deep night blue → violet → warm dusk at the horizon
            var sky = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            sky.GradientStops.Add(new GradientStop(C(0xFF0B1230), 0));
            sky.GradientStops.Add(new GradientStop(C(0xFF2A1F5C), 0.42));
            sky.GradientStops.Add(new GradientStop(C(0xFF7A3A78), 0.72));
            sky.GradientStops.Add(new GradientStop(C(0xFFF08A5D), 1));
            dc.DrawRectangle(Frozen(sky), null, new Rect(0, 0, w, horizon + 2));

            // a few stars
            var star = Frozen(new SolidColorBrush(C(0xB3FFFFFF)));
            var rng = new Random(7);
            for (var i = 0; i < 46; i++)
            {
                var x = rng.NextDouble() * w;
                var y = rng.NextDouble() * horizon * 0.5;
                var r = 0.9 + rng.NextDouble() * 1.6;
                dc.DrawEllipse(star, null, new Point(x, y), r, r);
            }

            // sun with a glow, half under the horizon
            var sunCenter = new Point(w * 0.62, horizon - 40);
            var glow = new RadialGradientBrush(C(0x66FFB56B), C(0x00FFB56B)) { RadiusX = 0.5, RadiusY = 0.5 };
            dc.DrawEllipse(Frozen(glow), null, sunCenter, 330, 330);
            var sun = new LinearGradientBrush(C(0xFFFFE08A), C(0xFFFF7A59), 90);
            dc.DrawEllipse(Frozen(sun), null, sunCenter, 120, 120);
            // synthwave stripes through the sun
            var stripe = Frozen(new SolidColorBrush(C(0xFF7A3A78)));
            for (var i = 0; i < 5; i++)
            {
                var y = sunCenter.Y + 10 + i * 20;
                dc.DrawRectangle(stripe, null, new Rect(sunCenter.X - 130, y, 260, 3 + i * 1.6));
            }

            // far mountains
            dc.DrawGeometry(Frozen(new SolidColorBrush(C(0xFF3B2A6B))), null, Ridge(w, horizon, [(0, 470), (0.09, 400), (0.18, 455), (0.3, 360), (0.41, 450), (0.5, 410), (0.6, 470), (0.71, 380), (0.82, 440), (0.92, 395), (1, 450)]));
            // near mountains
            var near = new LinearGradientBrush(C(0xFF241A45), C(0xFF130F2A), 90);
            dc.DrawGeometry(Frozen(near), null, Ridge(w, horizon, [(0, 520), (0.08, 480), (0.16, 540), (0.27, 470), (0.36, 560), (0.47, 545), (0.55, 575), (0.66, 520), (0.78, 565), (0.88, 505), (1, 540)]));

            // ground: dark, with a glowing perspective grid
            var ground = new LinearGradientBrush(C(0xFF1A1236), C(0xFF07060F), 90);
            dc.DrawRectangle(Frozen(ground), null, new Rect(0, horizon, w, h - horizon));
            var gridPen = new Pen(Frozen(new SolidColorBrush(C(0x66FF5EC8))), 1.6);
            gridPen.Freeze();
            var vanish = new Point(w * 0.5, horizon);
            for (var i = -14; i <= 14; i++)
            {
                var x = w * 0.5 + i * w * 0.09;
                dc.DrawLine(gridPen, vanish with { X = w * 0.5 + i * 18 }, new Point(x, h));
            }
            for (var i = 1; i <= 9; i++)
            {
                var t = Math.Pow(i / 9.0, 2.1);
                var y = horizon + t * (h - horizon);
                dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
            }
            // horizon glow line
            var line = new Pen(Frozen(new SolidColorBrush(C(0xCCFFB0E0))), 2.2);
            line.Freeze();
            dc.DrawLine(line, new Point(0, horizon), new Point(w, horizon));

            // a lone tower on the left
            var tower = Frozen(new SolidColorBrush(C(0xFF0C0A1C)));
            dc.DrawRectangle(tower, null, new Rect(w * 0.16, horizon - 150, 26, 150));
            dc.DrawRectangle(tower, null, new Rect(w * 0.16 - 14, horizon - 162, 54, 16));
            dc.DrawEllipse(Frozen(new SolidColorBrush(C(0xFFFF5E8A))), null, new Point(w * 0.16 + 13, horizon - 170), 5, 5);

            // crosshair in the middle (it is a game, after all)
            var cross = new Pen(Frozen(new SolidColorBrush(C(0xCCFFFFFF))), 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            cross.Freeze();
            var c = new Point(w / 2, h / 2);
            dc.DrawLine(cross, new Point(c.X - 26, c.Y), new Point(c.X - 9, c.Y));
            dc.DrawLine(cross, new Point(c.X + 9, c.Y), new Point(c.X + 26, c.Y));
            dc.DrawLine(cross, new Point(c.X, c.Y - 26), new Point(c.X, c.Y - 9));
            dc.DrawLine(cross, new Point(c.X, c.Y + 9), new Point(c.X, c.Y + 26));
            dc.DrawEllipse(Frozen(new SolidColorBrush(C(0xCCFFFFFF))), null, c, 2.4, 2.4);

            // a hint of a vignette
            var vignette = new RadialGradientBrush(C(0x00000000), C(0x66000000)) { RadiusX = 0.75, RadiusY = 0.75, GradientOrigin = new Point(0.5, 0.45), Center = new Point(0.5, 0.45) };
            dc.DrawRectangle(Frozen(vignette), null, new Rect(0, 0, w, h));
        }
        g.Freeze();
        return g;
    }

    private static Geometry Ridge(double w, double baseY, (double X, double Y)[] points)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(0, baseY + 2), true, true);
            foreach (var (x, y) in points) ctx.LineTo(new Point(x * w, y), true, true);
            ctx.LineTo(new Point(w, baseY + 2), true, true);
        }
        geo.Freeze();
        return geo;
    }
}
