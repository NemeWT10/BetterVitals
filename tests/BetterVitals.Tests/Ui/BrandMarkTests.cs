using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterVitals.UI.Brand;
using Xunit;

namespace BetterVitals.Tests.Ui;

/// <summary>The brand mark: one drawing (BrandMark) behind the tray icon, app.ico and the Logo.Image vectors.</summary>
[Collection(UiCollection.Name)]
public sealed class BrandMarkTests
{
    private static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    [Fact]
    public void Draws_at_every_tray_size_without_spilling_out_of_the_tile() => Sta.Run(() =>
    {
        for (var size = 16; size <= 64; size++)
        {
            var px = Pixels(BrandMark.Render(size));
            Assert.Equal(0, px[3]); // rounded corner stays transparent
            var centre = ((size / 2) * size + size / 2) * 4;
            Assert.Equal(255, px[centre + 3]);
            // the glyph is white and present
            Assert.Contains(Enumerable.Range(0, size * size), i => px[i * 4] > 250 && px[i * 4 + 1] > 250 && px[i * 4 + 2] > 250);
        }
    });

    [Fact]
    public void Logo_vectors_match_the_drawn_mark() => Sta.Run(() =>
    {
        var icons = (ResourceDictionary)Application.LoadComponent(new Uri("/BetterVitals;component/UI/Themes/Icons.xaml", UriKind.Relative));
        AssertSimilar(BrandMark.Render(128), RenderImage((ImageSource)icons["Logo.Image"], 128), "Logo.Image");
        AssertSimilar(BrandMark.Render(32), RenderImage((ImageSource)icons["Logo.Image.Small"], 32), "Logo.Image.Small");
    });

    [Fact]
    public void App_icon_is_current_and_has_every_shell_size() => Sta.Run(() =>
    {
        var path = Path.Combine(RepoRoot(), "src", "BetterVitals", "Assets", "app.ico");
        var decoder = new IconBitmapDecoder(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Assert.Equal(IcoSizes, decoder.Frames.Select(f => f.PixelWidth).Order().ToArray());
        foreach (var frame in decoder.Frames)
            AssertSimilar(BrandMark.Render(frame.PixelWidth), frame, $"app.ico {frame.PixelWidth} px (run tools\\IconGen)");
    });

    private static BitmapSource RenderImage(ImageSource image, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawImage(image, new Rect(0, 0, size, size));
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var straight = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var px = new byte[straight.PixelWidth * straight.PixelHeight * 4];
        straight.CopyPixels(px, straight.PixelWidth * 4, 0);
        return px;
    }

    /// <summary>Same picture: only anti-aliasing may differ, on a sliver of the pixels.</summary>
    private static void AssertSimilar(BitmapSource expected, BitmapSource actual, string what)
    {
        Assert.Equal(expected.PixelWidth, actual.PixelWidth);
        var a = Pixels(expected);
        var b = Pixels(actual);
        var off = 0;
        for (var i = 0; i < a.Length; i += 4)
        {
            var d = 0;
            for (var c = 0; c < 4; c++) d = Math.Max(d, Math.Abs(a[i + c] - b[i + c]));
            if (d > 32) off++;
        }
        Assert.True(off <= a.Length / 4 / 100, $"{what}: {off} of {a.Length / 4} pixels differ");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BetterVitals.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (BetterVitals.sln) not found.");
    }
}
