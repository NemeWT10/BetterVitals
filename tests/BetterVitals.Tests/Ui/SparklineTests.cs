using Xunit;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterVitals.Tests.Ui.Pages;
using BetterVitals.UI.Controls;

namespace BetterVitals.Tests.Ui;

[Collection(UiCollection.Name)]
public sealed class SparklineTests
{
    [Fact]
    public void A_line_scrolled_out_of_view_redraws_when_it_scrolls_back_in() => Sta.Run(() =>
    {
        var top = Series(1);
        var bottom = Series(2);
        var shown = new Sparkline { Height = 26, Series = top, Stroke = Brushes.SteelBlue };
        var below = new Sparkline { Height = 26, Series = bottom, Stroke = Brushes.SteelBlue };
        var stack = new StackPanel();
        stack.Children.Add(shown);
        stack.Children.Add(new Border { Height = 900 });
        stack.Children.Add(below);
        var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        using var host = new HiddenHost(scroll, 200, 300);

        var (r0, b0) = (shown.Renders, below.Renders);
        for (var i = 0; i < 5; i++)
        {
            top.Push(i);
            bottom.Push(i * 3);
            host.Root.UpdateLayout();
        }
        Assert.Equal(r0 + 5, shown.Renders); // on screen: every push
        Assert.Equal(b0, below.Renders);     // out of view: none

        scroll.ScrollToEnd();
        host.Root.UpdateLayout();
        Assert.Equal(b0 + 1, below.Renders); // once, before the frame that shows it
        // and it shows the latest values, exactly like a line that redrew every push
        var fresh = new Sparkline { Height = 26, Width = below.ActualWidth, Series = bottom, Stroke = Brushes.SteelBlue };
        using var other = new HiddenHost(fresh, below.ActualWidth, 26);
        Assert.True(Pixels(below).SequenceEqual(Pixels(fresh)));
    });

    private static SparkSeries Series(int seed)
    {
        var s = new SparkSeries(60);
        s.PushRange(Enumerable.Range(0, 60).Select(i => (float)Math.Sin(i * 0.3 + seed)));
        return s;
    }

    private static byte[] Pixels(FrameworkElement e)
    {
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(e.ActualWidth), (int)Math.Ceiling(e.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(e), null, new Rect(0, 0, e.ActualWidth, e.ActualHeight));
        bmp.Render(dv);
        var px = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
        bmp.CopyPixels(px, bmp.PixelWidth * 4, 0);
        return px;
    }
}
