using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace ReadmeMedia;

/// <summary>
/// The made-up desktop the windows are placed on: a calm wallpaper, a Windows 11 style taskbar, window frames with
/// rounded corners and a soft shadow, a mouse pointer. Everything is vector-drawn here; windows come in as bitmaps of
/// their own visual tree. Pictures are composed in DIPs and rendered at <see cref="Scale"/>.
/// </summary>
internal static class Stage
{
    /// <summary>Pixels per DIP of every picture (like a 125 % display: crisp text, moderate file size).</summary>
    public const double Scale = 1.25;

    public const double TaskbarHeight = 48;

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    public static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static SolidColorBrush B(string hex, byte alpha = 255)
    {
        var c = C(hex);
        var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    // ───────────── capture and output ─────────────

    /// <summary>An element (a shown window, a hosted page) from its own visual tree, at <see cref="Scale"/>.</summary>
    public static BitmapSource Capture(FrameworkElement element)
    {
        element.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * Scale), (int)Math.Ceiling(element.ActualHeight * Scale),
            96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bmp.Render(element);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Lays out a composition of <paramref name="width"/> × <paramref name="height"/> DIPs and renders it.</summary>
    public static BitmapSource Render(FrameworkElement root, double width, double height)
    {
        root.Width = width;
        root.Height = height;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Round(width * Scale), (int)Math.Round(height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bmp.Render(root);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>An opaque picture as a 24-bit PNG (no alpha channel: smaller files).</summary>
    public static long SavePng(BitmapSource bmp, string path)
    {
        var rgb = new FormatConvertedBitmap(bmp, PixelFormats.Bgr24, null, 0);
        var encoder = new PngBitmapEncoder { Interlace = PngInterlaceOption.Off };
        encoder.Frames.Add(BitmapFrame.Create(rgb));
        using (var fs = File.Create(path)) encoder.Save(fs);
        return new FileInfo(path).Length;
    }

    // ───────────── building blocks ─────────────

    /// <summary>A bitmap placed 1:1 (its pixel size at <see cref="Scale"/>) at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public static Image Place(Canvas canvas, BitmapSource bmp, double x, double y)
    {
        var img = new Image { Source = bmp, Width = bmp.PixelWidth / Scale, Height = bmp.PixelHeight / Scale, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        Canvas.SetLeft(img, x);
        Canvas.SetTop(img, y);
        canvas.Children.Add(img);
        return img;
    }

    /// <summary>A drawing stretched over a rectangle of the canvas.</summary>
    public static void Place(Canvas canvas, Drawing drawing, Rect r)
    {
        var img = new Image { Source = new DrawingImage(drawing), Width = r.Width, Height = r.Height, Stretch = Stretch.Fill };
        Canvas.SetLeft(img, r.X);
        Canvas.SetTop(img, r.Y);
        canvas.Children.Add(img);
    }

    /// <summary>A window on the desktop: its client area with Windows 11 rounded corners, a hairline edge and a soft shadow.</summary>
    public static void Window(Canvas canvas, BitmapSource content, double x, double y, bool dark = true)
    {
        var w = content.PixelWidth / Scale;
        var h = content.PixelHeight / Scale;
        const double radius = 8;
        var image = new Image
        {
            Source = content,
            Width = w,
            Height = h,
            Stretch = Stretch.Fill,
            Clip = new RectangleGeometry(new Rect(0, 0, w, h), radius, radius),
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        var shadow = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(radius),
            Background = B("#000000"),
            Effect = new DropShadowEffect { BlurRadius = 48, ShadowDepth = 14, Direction = 270, Opacity = 0.55, Color = Colors.Black, RenderingBias = RenderingBias.Quality },
        };
        var edge = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(radius),
            BorderThickness = new Thickness(1),
            BorderBrush = dark ? B("#FFFFFF", 0x1F) : B("#000000", 0x24),
        };
        foreach (var e in new FrameworkElement[] { shadow, image, edge })
        {
            Canvas.SetLeft(e, x);
            Canvas.SetTop(e, y);
            canvas.Children.Add(e);
        }
    }

    /// <summary>A canvas with the wallpaper and, optionally, the taskbar (the tray icon looks clicked when <paramref name="trayActive"/>).</summary>
    public static Canvas Desktop(double width, double height, bool taskbar, ImageSource? trayIcon = null, bool trayActive = false, DateTime clock = default)
    {
        var canvas = new Canvas { Width = width, Height = height, ClipToBounds = true, SnapsToDevicePixels = true };
        Place(canvas, Wallpaper(width, height), new Rect(0, 0, width, height));
        if (taskbar) Place(canvas, Taskbar(width, trayIcon, trayActive, clock), new Rect(0, height - TaskbarHeight, width, TaskbarHeight));
        return canvas;
    }

    /// <summary>Where the BetterVitals icon sits in the taskbar's tray (canvas coordinates, its center).</summary>
    public static Point TrayIconCenter(double width, double height) => new(width - TrayFromRight, height - TaskbarHeight / 2);

    private const double TrayFromRight = 168;

    // ───────────── wallpaper and taskbar ─────────────

    /// <summary>Deep night blue with a few soft glows in the theme's colors: calm, so the windows stand out.</summary>
    public static Drawing Wallpaper(double w, double h) => Draw(w, h, dc =>
    {
        var bg = new LinearGradientBrush(
            [new GradientStop(C("#0B1022"), 0), new GradientStop(C("#141331"), 0.55), new GradientStop(C("#0A1626"), 1)],
            new Point(0, 0), new Point(1, 1));
        dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
        Glow(dc, w * 0.16, h * 0.18, w * 0.42, h * 0.55, "#2DD4BF", 0x3A);
        Glow(dc, w * 0.86, h * 0.12, w * 0.46, h * 0.60, "#8B5CF6", 0x58);
        Glow(dc, w * 0.62, h * 1.02, w * 0.52, h * 0.50, "#EC4899", 0x30);
        Glow(dc, w * 0.04, h * 0.92, w * 0.36, h * 0.42, "#3B82F6", 0x3C);
    });

    private static void Glow(DrawingContext dc, double cx, double cy, double rx, double ry, string hex, byte alpha)
    {
        var c = C(hex);
        var b = new RadialGradientBrush(Color.FromArgb(alpha, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B));
        b.Freeze();
        dc.DrawEllipse(b, null, new Point(cx, cy), rx, ry);
    }

    private static Drawing Taskbar(double w, ImageSource? trayIcon, bool trayActive, DateTime clock) => Draw(w, TaskbarHeight, dc =>
    {
        const double h = TaskbarHeight;
        dc.DrawRectangle(B("#141622", 0xF0), null, new Rect(0, 0, w, h));
        dc.DrawRectangle(B("#FFFFFF", 0x18), null, new Rect(0, 0, w, 1));

        // pinned apps in the middle: start, search, files, browser, a game (running, in front)
        const double step = 44, size = 28;
        var apps = 5;
        var x0 = (w - apps * step) / 2 + (step - size) / 2;
        var y0 = (h - size) / 2 - 1;
        for (var i = 0; i < apps; i++)
        {
            var r = new Rect(x0 + i * step, y0, size, size);
            AppIcon(dc, i, r);
            var cx = r.X + size / 2;
            if (i == 4) dc.DrawRoundedRectangle(B("#A5B4FC"), null, new Rect(cx - 8, h - 5, 16, 3), 1.5, 1.5);
            else if (i is 2 or 3) dc.DrawRoundedRectangle(B("#FFFFFF", 0x70), null, new Rect(cx - 3, h - 5, 6, 3), 1.5, 1.5);
        }

        // the tray: chevron, BetterVitals, network, volume; the clock
        var white = B("#FFFFFF", 0xE6);
        var pen = new Pen(white, 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        var icon = new Point(w - TrayFromRight, h / 2);
        dc.DrawGeometry(null, pen, Geometry.Parse(F("M{0},{1} l4.5,-4.5 l4.5,4.5", icon.X - 36, h / 2 + 2)));
        if (trayActive) dc.DrawRoundedRectangle(B("#FFFFFF", 0x1C), null, new Rect(icon.X - 14, 6, 28, h - 12), 5, 5);
        if (trayIcon is not null) dc.DrawImage(trayIcon, new Rect(icon.X - 8, icon.Y - 8, 16, 16));
        var net = new Point(icon.X + 34, h / 2 + 5);
        for (var k = 0; k < 3; k++)
        {
            var rr = 3.2 + k * 3.4;
            dc.DrawGeometry(null, pen, Geometry.Parse(F("M{0},{1} A{2},{2} 0 0 1 {3},{1}", net.X - rr * 0.78, net.Y - rr * 0.62, rr, net.X + rr * 0.78)));
        }
        dc.DrawEllipse(white, null, new Point(net.X, net.Y), 1.3, 1.3);
        var sp = new Point(icon.X + 58, h / 2);
        dc.DrawGeometry(white, null, Geometry.Parse(F("M{0},{1} h3 l4,-4 v12 l-4,-4 h-3 z", sp.X, sp.Y - 2)));
        dc.DrawGeometry(null, pen, Geometry.Parse(F("M{0},{1} q3,4 0,8", sp.X + 10, sp.Y - 4)));
        Text(dc, clock.ToString("h:mm tt", English), w - 16, 8, 11.5, white, right: true);
        Text(dc, clock.ToString("M/d/yyyy", English), w - 16, 25, 11.5, white, right: true);
    });

    private static void AppIcon(DrawingContext dc, int i, Rect r)
    {
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        var white = Brushes.White;
        switch (i)
        {
            case 0: // start
                var blue = Gradient("#5AB2FF", "#2F7CF6");
                const double s = 9.5, g = 1.6;
                dc.DrawRoundedRectangle(blue, null, new Rect(cx - s - g / 2, cy - s - g / 2, s, s), 1.5, 1.5);
                dc.DrawRoundedRectangle(blue, null, new Rect(cx + g / 2, cy - s - g / 2, s, s), 1.5, 1.5);
                dc.DrawRoundedRectangle(blue, null, new Rect(cx - s - g / 2, cy + g / 2, s, s), 1.5, 1.5);
                dc.DrawRoundedRectangle(blue, null, new Rect(cx + g / 2, cy + g / 2, s, s), 1.5, 1.5);
                break;
            case 1: // search
                dc.DrawEllipse(null, new Pen(white, 2), new Point(cx - 2, cy - 2), 6.5, 6.5);
                dc.DrawLine(new Pen(white, 2.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, new Point(cx + 3, cy + 3), new Point(cx + 8, cy + 8));
                break;
            case 2: // files
                dc.DrawRoundedRectangle(B("#F59E0B"), null, new Rect(r.X + 2, r.Y + 5, 12, 7), 2, 2);
                dc.DrawRoundedRectangle(Gradient("#FDE68A", "#FBBF24", 90), null, new Rect(r.X + 2, r.Y + 8, 24, 16), 3, 3);
                break;
            case 3: // browser
                dc.DrawEllipse(Gradient("#22D3EE", "#6366F1"), null, new Point(cx, cy), 12.5, 12.5);
                var p = new Pen(B("#FFFFFF", 0xD0), 1.4);
                dc.DrawEllipse(null, p, new Point(cx, cy), 5.2, 12);
                dc.DrawLine(p, new Point(cx - 12, cy), new Point(cx + 12, cy));
                break;
            default: // a game
                dc.DrawRoundedRectangle(Gradient("#F472B6", "#7C3AED"), null, r, 7, 7);
                dc.DrawRoundedRectangle(white, null, new Rect(cx - 10, cy - 5, 20, 11), 5.5, 5.5);
                var ink = B("#7C3AED");
                dc.DrawRectangle(ink, null, new Rect(cx - 7.4, cy - 0.9, 5.4, 1.8));
                dc.DrawRectangle(ink, null, new Rect(cx - 5.6, cy - 2.7, 1.8, 5.4));
                dc.DrawEllipse(ink, null, new Point(cx + 5, cy - 1.2), 1.3, 1.3);
                dc.DrawEllipse(ink, null, new Point(cx + 3, cy + 1.4), 1.3, 1.3);
                break;
        }
    }

    // ───────────── pointer ─────────────

    private static readonly Geometry Arrow = Geometry.Parse("M0,0 L0,17.5 L4.2,13.6 L7,19.8 L9.9,18.6 L7.2,12.5 L12.6,12.5 Z");

    /// <summary>A mouse pointer whose tip is at <paramref name="tip"/>.</summary>
    public static void Pointer(Canvas canvas, Point tip)
    {
        var d = Draw(16, 24, dc =>
        {
            dc.PushTransform(new TranslateTransform(2.3, 2.7)); // a soft drop shadow
            dc.DrawGeometry(B("#000000", 0x48), null, Arrow);
            dc.Pop();
            dc.PushTransform(new TranslateTransform(1.5, 1.5));
            dc.DrawGeometry(Brushes.White, new Pen(B("#111111"), 1.1) { LineJoin = PenLineJoin.Round }, Arrow);
            dc.Pop();
        });
        Place(canvas, d, new Rect(tip.X - 1.5, tip.Y - 1.5, 16, 24));
    }

    // ───────────── drawing helpers ─────────────

    public static DrawingGroup Draw(double w, double h, Action<DrawingContext> draw)
    {
        var g = new DrawingGroup();
        using (var dc = g.Open())
        {
            // fixes the drawing's bounds to the full rectangle (transparent corners count)
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
            draw(dc);
        }
        g.ClipGeometry = new RectangleGeometry(new Rect(0, 0, w, h));
        g.Freeze();
        return g;
    }

    private static LinearGradientBrush Gradient(string a, string b, double angle = 45)
    {
        var g = new LinearGradientBrush(C(a), C(b), angle);
        g.Freeze();
        return g;
    }

    private static string F(string format, params object[] args) => string.Format(English, format, args);

    private static void Text(DrawingContext dc, string s, double x, double y, double size, Brush brush, bool right = false)
    {
        var ft = new FormattedText(s, English, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, brush, Scale);
        dc.DrawText(ft, new Point(right ? x - ft.Width : x, y));
    }
}
