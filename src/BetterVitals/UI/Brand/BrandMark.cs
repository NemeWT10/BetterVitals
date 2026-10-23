using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterVitals.UI.Brand;

/// <summary>
/// The BetterVitals mark: a rounded tile with a crimson → coral gradient and a white line-style thermometer. From
/// <see cref="FullMinSize"/> px up it is the full mark — a glass thermometer (outline, gap, mercury) with a pulse beside
/// it; below that it is the small mark — a centred thermometer laid out on whole pixels (hollow top, solid mercury and
/// bulb) so it stays crisp in the tray. Shared by the tray icon and tools\IconGen (app.ico); Logo.Image and
/// Logo.Image.Small in UI\Themes\Icons.xaml are the same shapes in vector form.
/// </summary>
internal static class BrandMark
{
    /// <summary>Smallest pixel size drawn as the full mark.</summary>
    public const int FullMinSize = 40;

    // ── full mark, 128 × 128 design units (tile proportions shared with the sibling apps' marks) ──
    private const string TilePath = "M30,0 H98 A30,30 0 0 1 128,30 V98 A30,30 0 0 1 98,128 H30 A30,30 0 0 1 0,98 V30 A30,30 0 0 1 30,0 Z";
    private const string GlossPath = "M30,0 H98 A30,30 0 0 1 128,30 V50 C92,40 40,42 0,60 V30 A30,30 0 0 1 30,0 Z";
    private const double Stroke = 9, TubeX = 48.5, TubeTop = 39.5, TubeR = 11, BulbY = 80.5, BulbR = 19, Gap = 3.5, Level = 54, PulseStroke = 8;
    private static readonly Point[] Pulse = [new(70, 54), new(75, 54), new(80, 39), new(87, 68), new(92, 50), new(95, 54), new(100, 54)];

    // ── small mark, in multiples of the 16 px grid unit ──
    private const double SmallPad = 1.75, SmallLevel = 0.7;

    /// <summary>Tile gradient, top-left → bottom-right.</summary>
    public static readonly Color TileStart = Color.FromRgb(0xE5, 0x19, 0x4A), TileMiddle = Color.FromRgb(0xF4, 0x3F, 0x3F), TileEnd = Color.FromRgb(0xFF, 0x7A, 0x45);

    /// <summary>The mark as a frozen size × size bitmap.</summary>
    public static BitmapSource Render(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) Draw(dc, size);
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Draws the mark into (0, 0, size, size) device pixels.</summary>
    public static void Draw(DrawingContext dc, int size)
    {
        dc.PushTransform(new ScaleTransform(size / 128.0, size / 128.0));
        var tile = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        tile.GradientStops.Add(new GradientStop(TileStart, 0));
        tile.GradientStops.Add(new GradientStop(TileMiddle, 0.55));
        tile.GradientStops.Add(new GradientStop(TileEnd, 1));
        dc.DrawGeometry(tile, null, Geometry.Parse(TilePath));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), null, Geometry.Parse(GlossPath));
        if (size >= FullMinSize) DrawFull(dc);
        dc.Pop();
        if (size < FullMinSize) DrawSmall(dc, size);
    }

    private static void DrawFull(DrawingContext dc)
    {
        var ink = Brushes.White;
        var joint = BulbY - Math.Sqrt(BulbR * BulbR - TubeR * TubeR);
        var glass = new StreamGeometry();
        using (var c = glass.Open())
        {
            c.BeginFigure(new Point(TubeX - TubeR, joint), false, true);
            c.LineTo(new Point(TubeX - TubeR, TubeTop), true, true);
            c.ArcTo(new Point(TubeX + TubeR, TubeTop), new Size(TubeR, TubeR), 0, false, SweepDirection.Clockwise, true, true);
            c.LineTo(new Point(TubeX + TubeR, joint), true, true);
            c.ArcTo(new Point(TubeX - TubeR, joint), new Size(BulbR, BulbR), 0, true, SweepDirection.Clockwise, true, true);
        }
        dc.DrawGeometry(null, RoundPen(ink, Stroke), glass);
        // mercury: a disc in the bulb and a thread up the tube, inset from the glass by the gap
        var disc = BulbR - Stroke / 2 - Gap;
        dc.DrawEllipse(ink, null, new Point(TubeX, BulbY), disc, disc);
        var thread = 2 * (TubeR - Stroke / 2 - Gap);
        dc.DrawLine(RoundPen(ink, thread), new Point(TubeX, BulbY), new Point(TubeX, Level + thread / 2));
        var pulse = new StreamGeometry();
        using (var c = pulse.Open())
        {
            c.BeginFigure(Pulse[0], false, false);
            c.PolyLineTo(Pulse[1..], true, true);
        }
        dc.DrawGeometry(null, RoundPen(ink, PulseStroke), pulse);
    }

    /// <summary>
    /// The thermometer on whole pixels (walls and the top of the mercury on pixel edges): solid bulb and lower tube,
    /// hollow top — the hollow top is what makes it read as a thermometer at 16 px.
    /// </summary>
    private static void DrawSmall(DrawingContext dc, int n)
    {
        var u = n / 16.0;
        var wall = Math.Max(1, Math.Round(u, MidpointRounding.AwayFromZero));
        var hollow = Math.Max(2, Math.Round(2 * u, MidpointRounding.AwayFromZero));
        var width = 2 * wall + hollow;
        var bulb = width + 2 * Math.Round(width / 4, MidpointRounding.AwayFromZero); // same parity as the tube: both centred alike
        var pad = Math.Round(SmallPad * u, MidpointRounding.AwayFromZero);
        var left = Math.Floor((n - width) / 2);
        var cx = left + width / 2;
        var by = n - pad - bulb / 2;
        var capY = pad + width / 2;
        var joint = by - Math.Sqrt((bulb * bulb - width * width) / 4);
        var level = Math.Round(capY + SmallLevel * (joint - capY));

        var ink = Brushes.White;
        var solid = new GeometryGroup { FillRule = FillRule.Nonzero };
        solid.Children.Add(new EllipseGeometry(new Point(cx, by), bulb / 2, bulb / 2));
        solid.Children.Add(new RectangleGeometry(new Rect(left, level, width, by - level)));
        dc.DrawGeometry(ink, null, solid);
        var r = (width - wall) / 2;
        var walls = new StreamGeometry();
        using (var c = walls.Open())
        {
            c.BeginFigure(new Point(cx - r, by), false, false);
            c.LineTo(new Point(cx - r, capY), true, true);
            c.ArcTo(new Point(cx + r, capY), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
            c.LineTo(new Point(cx + r, by), true, true);
        }
        dc.DrawGeometry(null, new Pen(ink, wall), walls);
    }

    private static Pen RoundPen(Brush brush, double width) =>
        new(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
}
