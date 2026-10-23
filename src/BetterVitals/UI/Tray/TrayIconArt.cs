using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterVitals.UI.Brand;

namespace BetterVitals.UI.Tray;

/// <summary>
/// The tray icon, drawn in code at the exact pixel size the shell asks for (16 px at 100 %, 24 px at 150 %, 32 px at
/// 200 %…): the brand mark (<see cref="BrandMark"/>, the same drawing as app.ico), on whole pixels at tray sizes.
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
    public static BitmapSource Render(int size) => BrandMark.Render(size);
}
