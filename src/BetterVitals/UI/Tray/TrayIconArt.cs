using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterVitals.UI.Tray;

/// <summary>
/// The tray icon, drawn in code at the exact pixel size the shell asks for (16 px at 100 %, 24 px at 150 %, 32 px at
/// 200 %…): the brand mark — a pulse line on the accent gradient. Temporary until a designed .ico exists.
/// </summary>
internal static class TrayIconArt
{
    /// <summary>A new HICON of <paramref name="size"/>×<paramref name="size"/> px; the caller destroys it (DestroyIcon).</summary>
    public static nint Create(int size)
    {
        size = Math.Clamp(size, 16, 256);
        var bitmap = Render(size);
        var pixels = new byte[size * size * 4];
        new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, size * 4, 0);

        var header = new Native.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var color = Native.CreateDIBSection(0, ref header, 0, out var bits, 0, 0);
        if (color == 0) return 0;
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        var mask = Native.CreateBitmap(size, size, 1, 1, 0);
        try
        {
            var info = new Native.ICONINFO { fIcon = 1, hbmColor = color, hbmMask = mask };
            return Native.CreateIconIndirect(ref info);
        }
        finally
        {
            Native.DeleteObject(color);
            Native.DeleteObject(mask);
        }
    }

    /// <summary>The mark as a bitmap (also used by the snapshot icon sheet).</summary>
    public static BitmapSource Render(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var s = size / 32.0;
            var gradient = new LinearGradientBrush(
                [new GradientStop(Color.FromRgb(0x8B, 0x5C, 0xFF), 0), new GradientStop(Color.FromRgb(0x5B, 0x7B, 0xFF), 0.55), new GradientStop(Color.FromRgb(0x22, 0xD3, 0xEE), 1)],
                new Point(0, 0), new Point(1, 1));
            gradient.Freeze();
            var inset = size <= 16 ? 0.5 : 1.0;
            dc.DrawRoundedRectangle(gradient, null, new Rect(inset, inset, size - 2 * inset, size - 2 * inset), 8 * s, 8 * s);
            // the pulse: thick enough to read at 16 px
            var pen = new Pen(Brushes.White, Math.Max(1.6, 2.6 * s)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            pen.Freeze();
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(5.5 * s, 17 * s), false, false);
                c.PolyLineTo([new Point(11 * s, 17 * s), new Point(13.5 * s, 10 * s), new Point(18 * s, 23 * s), new Point(20.5 * s, 15 * s), new Point(22 * s, 17 * s), new Point(26.5 * s, 17 * s)], true, true);
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }
}
