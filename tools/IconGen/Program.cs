using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterVitals.UI.Brand;

namespace BetterVitals.Tools.IconGen;

/// <summary>
/// Renders the BetterVitals mark (UI\Brand\BrandMark.cs, the same code the tray icon uses) into
/// src\BetterVitals\Assets\app.ico + app-256.png, and preview sheets into tests\output\icon.
///   IconGen [--with &lt;image&gt;]...   (--with: other marks to show beside it on the series strip)
/// Frames below 256 px are classic 32-bit DIBs (best compatibility with the shell and Shell_NotifyIcon), 256 px is PNG.
/// </summary>
internal static class Program
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    [STAThread]
    private static int Main(string[] args)
    {
        var root = FindRoot();
        var assets = Path.Combine(root, "src", "BetterVitals", "Assets");
        var preview = Path.Combine(root, "tests", "output", "icon");
        Directory.CreateDirectory(assets);
        Directory.CreateDirectory(preview);
        var others = new List<string>();
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--with") others.Add(Path.GetFullPath(args[++i]));

        var frames = Sizes.Select(s => (Size: s, Bitmap: BrandMark.Render(s))).ToList();
        var ico = Path.Combine(assets, "app.ico");
        WriteIco(ico, frames);
        File.WriteAllBytes(Path.Combine(assets, "app-256.png"), Png(frames[^1].Bitmap));
        foreach (var (size, bitmap) in frames) File.WriteAllBytes(Path.Combine(preview, $"icon-{size}.png"), Png(bitmap));
        WriteSheet(Path.Combine(preview, "sheet.png"));
        WriteSeries(Path.Combine(preview, "series.png"), others);
        Console.WriteLine($"{ico}: {frames.Count} frames, {new FileInfo(ico).Length} bytes; previews in {preview}");
        return 0;
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BetterVitals.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("BetterVitals.sln not found above " + AppContext.BaseDirectory);
    }

    // ───────────── previews ─────────────

    private static readonly Color Dark = Color.FromRgb(0x20, 0x20, 0x20), Light = Color.FromRgb(0xEE, 0xEE, 0xEE);

    /// <summary>Every tray / shell size at 1:1 on a dark and a light taskbar strip, then the tray sizes magnified ×8.</summary>
    private static void WriteSheet(string path)
    {
        int[] native = [16, 20, 24, 28, 32, 36, 40, 48, 64, 96, 128, 256];
        int[] zoomed = [16, 20, 24, 32];
        const int zoom = 8, gap = 16;
        var stripH = 256 + 2 * gap;
        var zoomH = 32 * zoom + 2 * gap;
        var width = Math.Max(native.Sum(s => s + gap) + gap, zoomed.Sum(s => s * zoom + gap) + gap);
        Save(path, width, 2 * stripH + zoomH, dc =>
        {
            for (var strip = 0; strip < 2; strip++)
            {
                var y0 = strip * stripH;
                dc.DrawRectangle(new SolidColorBrush(strip == 0 ? Dark : Light), null, new Rect(0, y0, width, stripH));
                double x = gap;
                foreach (var s in native)
                {
                    dc.DrawImage(BrandMark.Render(s), new Rect(x, y0 + stripH - gap - s, s, s));
                    x += s + gap;
                }
            }
            var z0 = 2 * stripH;
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)), null, new Rect(0, z0, width, zoomH));
            double zx = gap;
            foreach (var s in zoomed)
            {
                var group = new DrawingGroup();
                RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.NearestNeighbor);
                group.Children.Add(new ImageDrawing(BrandMark.Render(s), new Rect(zx, z0 + gap, s * zoom, s * zoom)));
                dc.DrawDrawing(group);
                zx += s * zoom + gap;
            }
        });
    }

    /// <summary>The mark beside other marks (--with) at 128 / 48 / 32 / 24 px, on dark and light.</summary>
    private static void WriteSeries(string path, List<string> others)
    {
        int[] sizes = [128, 48, 32, 24];
        const int gap = 16;
        var marks = others.Select(Load).Append(BrandMark.Render).ToList();
        var groupW = sizes.Sum(s => s + gap) + 2 * gap;
        var stripH = 128 + 2 * gap;
        Save(path, marks.Count * groupW + gap, 2 * stripH, dc =>
        {
            for (var strip = 0; strip < 2; strip++)
            {
                var y0 = strip * stripH;
                dc.DrawRectangle(new SolidColorBrush(strip == 0 ? Dark : Light), null, new Rect(0, y0, marks.Count * groupW + gap, stripH));
                double x = 2 * gap;
                foreach (var mark in marks)
                {
                    foreach (var s in sizes)
                    {
                        dc.DrawImage(mark(s), new Rect(x, y0 + stripH - gap - s, s, s));
                        x += s + gap;
                    }
                    x += 2 * gap;
                }
            }
        });
    }

    /// <summary>An image file (.ico: the frame closest to the size; anything else: scaled).</summary>
    private static Func<int, BitmapSource> Load(string file)
    {
        var decoder = BitmapDecoder.Create(new Uri(file), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return size => decoder.Frames.OrderBy(f => f.PixelWidth < size ? 10000 - f.PixelWidth : f.PixelWidth - size).First();
    }

    private static void Save(string path, double width, double height, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) draw(dc);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        File.WriteAllBytes(path, Png(bitmap));
    }

    // ───────────── .ico ─────────────

    private static void WriteIco(string path, List<(int Size, BitmapSource Bitmap)> frames)
    {
        var payloads = frames.Select(f => f.Size >= 256 ? Png(f.Bitmap) : Dib(f.Bitmap)).ToList();
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)frames.Count);
        var offset = 6 + 16 * frames.Count;
        for (var i = 0; i < frames.Count; i++)
        {
            var s = frames[i].Size;
            w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(payloads[i].Length);
            w.Write(offset);
            offset += payloads[i].Length;
        }
        foreach (var p in payloads) w.Write(p);
    }

    private static byte[] Png(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>ICO DIB entry: BITMAPINFOHEADER (double height) + bottom-up straight-alpha BGRA + 1-bit AND mask.</summary>
    private static byte[] Dib(BitmapSource bitmap)
    {
        var s = bitmap.PixelWidth;
        var px = new byte[s * s * 4];
        new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0).CopyPixels(px, s * 4, 0);
        var maskStride = (s + 31) / 32 * 4;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(40);
        w.Write(s);
        w.Write(s * 2);
        w.Write((short)1);
        w.Write((short)32);
        w.Write(0);
        w.Write(px.Length + maskStride * s);
        w.Write(0);
        w.Write(0);
        w.Write(0);
        w.Write(0);
        for (var y = s - 1; y >= 0; y--) w.Write(px, y * s * 4, s * 4);
        var mask = new byte[maskStride * s];
        for (var y = 0; y < s; y++)
            for (var x = 0; x < s; x++)
                if (px[(y * s + x) * 4 + 3] == 0) mask[(s - 1 - y) * maskStride + x / 8] |= (byte)(0x80 >> (x % 8));
        w.Write(mask);
        return ms.ToArray();
    }
}
