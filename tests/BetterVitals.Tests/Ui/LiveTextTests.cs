using Xunit;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterVitals.Tests.Ui.Pages;
using BetterVitals.UI.Controls;

namespace BetterVitals.Tests.Ui;

[Collection(UiCollection.Name)]
public sealed class LiveTextTests
{
    private static readonly string[] Texts = ["0", "7", "56", "100", "1,015", "15001", "2564", "—", "-12,5", "9,5", "0,968", "41,0 / 58,2", "79,3 MB/s", "1.25"];

    /// <summary>Each style as a row of the pages uses it: (font family or null = inherited, size, weight, tabular).</summary>
    public static TheoryData<string?, double, bool, bool> Styles => new()
    {
        { "Segoe UI Variable Display, Segoe UI", 24, true, true },
        { null, 13.5, true, true },
        { null, 12.5, false, true },
        { null, 13, false, false },
    };

    [Theory]
    [MemberData(nameof(Styles))]
    public void Draws_and_measures_exactly_like_a_TextBlock(string? family, double size, bool semibold, bool tabular) => Sta.Run(() =>
    {
        foreach (var text in Texts)
        {
            var (block, live) = (new TextBlock { Text = text }, new LiveText { Text = text });
            foreach (FrameworkElement e in new FrameworkElement[] { block, live })
            {
                e.SetValue(TextElement.FontSizeProperty, size);
                e.SetValue(TextElement.FontWeightProperty, semibold ? FontWeights.SemiBold : FontWeights.Normal);
                if (family is not null) e.SetValue(TextElement.FontFamilyProperty, new FontFamily(family));
                if (tabular) e.SetValue(Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular);
                e.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text");
                e.HorizontalAlignment = HorizontalAlignment.Left;
                e.VerticalAlignment = VerticalAlignment.Top;
            }
            var a = Render(block, out var sizeA);
            var b = Render(live, out var sizeB);
            Assert.Equal(sizeA, sizeB);
            Assert.True(sizeA.Width > 0 && a.Chunk(4).Distinct(new PixelComparer()).Count() > 1, $"'{text}' drew nothing");
            Assert.True(a.SequenceEqual(b), $"'{text}' ({family ?? "inherited"} {size}) draws differently");
        }
    });

    [Fact]
    public void A_text_that_comes_back_is_not_laid_out_again() => Sta.Run(() =>
    {
        LiveText.ClearCache();
        var live = new LiveText();
        using var host = new HiddenHost(live, 200, 60);
        var before = LiveText.Layouts;
        for (var i = 0; i < 30; i++)
        {
            live.Text = (50 + i % 5).ToString();
            host.Root.UpdateLayout();
        }
        Assert.Equal(5, LiveText.Layouts - before);

        // and costs next to nothing
        var alloc = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 30; i++)
        {
            live.Text = (50 + i % 5).ToString();
            host.Root.UpdateLayout();
        }
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - alloc, 0, 30 * 1024);
        LiveText.ClearCache();
    });

    private sealed class PixelComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y) => x!.SequenceEqual(y!);

        public int GetHashCode(byte[] p) => BitConverter.ToInt32(p);
    }

    private static byte[] Render(FrameworkElement e, out Size size)
    {
        using var host = new HiddenHost(e, 240, 60);
        size = e.RenderSize;
        var bmp = new RenderTargetBitmap(300, 75, 120, 120, PixelFormats.Pbgra32);
        bmp.Render(host.Root);
        var pixels = new byte[300 * 75 * 4];
        bmp.CopyPixels(pixels, 300 * 4, 0);
        return pixels;
    }
}
