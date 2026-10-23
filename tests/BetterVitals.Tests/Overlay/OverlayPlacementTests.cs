using BetterVitals.Core.Overlay;
using BetterVitals.Overlay;
using Xunit;

namespace BetterVitals.Tests.Overlay;

public class OverlayPlacementTests
{
    public static TheoryData<OverlayAnchor, int, double, double> Cases()
    {
        var data = new TheoryData<OverlayAnchor, int, double, double>();
        foreach (var anchor in Enum.GetValues<OverlayAnchor>())
        foreach (var height in new[] { 1080, 1440, 2160, 2880 })
        foreach (var (ox, oy) in new[] { (0.0, 0.0), (10.0, 5.0), (-8.0, 30.0) })
            data.Add(anchor, height, ox, oy);
        return data;
    }

    [Theory]
    [InlineData(1080, 1.0)]
    [InlineData(1440, 1.3333333)]
    [InlineData(2160, 2.0)]
    [InlineData(2880, 2.6666667)]
    public void Auto_scale_is_monitor_height_over_1080(int height, double expected)
    {
        Assert.Equal(expected, OverlayPlacementMath.Scale(new OverlayStyle(), height), 5);
        Assert.Equal(expected * 1.5, OverlayPlacementMath.Scale(new OverlayStyle { ScalePercent = 150 }, height), 5);
        Assert.Equal(0.75, OverlayPlacementMath.Scale(new OverlayStyle { AutoScale = false, ScalePercent = 75 }, height), 5);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Position_matches_anchor_offsets_and_margin(OverlayAnchor anchor, int height, double ox, double oy)
    {
        var width = height * 16 / 9;
        var area = new PixelRect(-width, 40, 0, 40 + height); // a secondary monitor left of the primary, below a top bar
        var s = height / 1080.0;
        int w = (int)(200 * s), h = (int)(120 * s);
        var p = new OverlayPlacement { Anchor = anchor, OffsetX = ox, OffsetY = oy, Margin = 24 };
        var (x, y) = OverlayPlacementMath.Position(p, area, w, h, s);

        var col = (int)anchor % 3;
        var row = (int)anchor / 3;
        var ex = col switch
        {
            0 => area.Left + 24 * s + ox * s,
            1 => area.Left + (area.Width - w) / 2.0 + ox * s,
            _ => area.Right - 24 * s - ox * s - w,
        };
        var ey = row switch
        {
            0 => area.Top + 24 * s + oy * s,
            1 => area.Top + (area.Height - h) / 2.0 + oy * s,
            _ => area.Bottom - 24 * s - oy * s - h,
        };
        Assert.InRange(x, Math.Round(ex) - 1, Math.Round(ex) + 1);
        Assert.InRange(y, Math.Round(ey) - 1, Math.Round(ey) + 1);

        // Inverse (edit mode commit) reproduces the same pixels.
        var back = OverlayPlacementMath.FromPosition(x, y, w, h, area, s, p);
        var (x2, y2) = OverlayPlacementMath.Position(back, area, w, h, s);
        Assert.InRange(x2, x - 1, x + 1);
        Assert.InRange(y2, y - 1, y + 1);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Same_reference_offsets_land_at_the_same_relative_place_on_every_resolution(OverlayAnchor anchor, int height, double ox, double oy)
    {
        var p = new OverlayPlacement { Anchor = anchor, OffsetX = ox, OffsetY = oy };
        var refArea = new PixelRect(0, 0, 1920, 1080);
        var (rx, ry) = OverlayPlacementMath.Position(p, refArea, 200, 120, 1);
        var s = height / 1080.0;
        var area = new PixelRect(0, 0, height * 16 / 9, height);
        var (x, y) = OverlayPlacementMath.Position(p, area, (int)Math.Round(200 * s), (int)Math.Round(120 * s), s);
        Assert.InRange(x / s, rx - 1.5, rx + 1.5);
        Assert.InRange(y / s, ry - 1.5, ry + 1.5);
    }

    [Fact]
    public void Position_is_clamped_inside_the_area()
    {
        var area = new PixelRect(0, 0, 1920, 1080);
        var (x, y) = OverlayPlacementMath.Position(new OverlayPlacement { OffsetX = -500, OffsetY = 5000 }, area, 200, 100, 1);
        Assert.Equal(0, x);
        Assert.Equal(980, y);
    }

    [Fact]
    public void From_position_picks_the_ninth_the_overlay_is_in()
    {
        var area = new PixelRect(0, 0, 3000, 1500);
        var cur = new OverlayPlacement { Margin = 24 };
        Assert.Equal(OverlayAnchor.TopLeft, OverlayPlacementMath.FromPosition(10, 10, 100, 50, area, 1, cur).Anchor);
        Assert.Equal(OverlayAnchor.Center, OverlayPlacementMath.FromPosition(1450, 720, 100, 50, area, 1, cur).Anchor);
        Assert.Equal(OverlayAnchor.BottomRight, OverlayPlacementMath.FromPosition(2800, 1400, 100, 50, area, 1, cur).Anchor);
        Assert.Equal(OverlayAnchor.MiddleRight, OverlayPlacementMath.FromPosition(2800, 700, 100, 50, area, 1, cur).Anchor);
        var br = OverlayPlacementMath.FromPosition(2800, 1400, 100, 50, area, 2, cur);
        Assert.Equal((3000 - 2900) / 2.0 - 24, br.OffsetX, 1);
        Assert.Equal((1500 - 1450) / 2.0 - 24, br.OffsetY, 1);
    }

    private static readonly OverlayMonitor Primary = new("\\\\?\\DISPLAY#AAA#1", "\\\\.\\DISPLAY1", "Main", new PixelRect(0, 0, 2560, 1440), new PixelRect(0, 0, 2560, 1392), true);
    private static readonly OverlayMonitor Side = new("\\\\?\\DISPLAY#BBB#2", "\\\\.\\DISPLAY2", "Side", new PixelRect(2560, 0, 4480, 1080), new PixelRect(2560, 0, 4480, 1080), false);

    [Fact]
    public void Missing_specific_monitor_falls_back_to_primary_and_returns()
    {
        var p = new OverlayPlacement { MonitorMode = OverlayMonitorMode.Specific, MonitorKey = Side.Key, Anchor = OverlayAnchor.BottomRight };
        Assert.Same(Side, OverlayPlacementMath.Resolve(p, [Primary, Side]));
        Assert.Same(Primary, OverlayPlacementMath.Resolve(p, [Primary]));
        Assert.Same(Side, OverlayPlacementMath.Resolve(p, [Side, Primary])); // back after re-plug, any order
        Assert.Equal(OverlayAnchor.BottomRight, p.Anchor);
        Assert.Null(OverlayPlacementMath.Resolve(p, []));
    }

    [Fact]
    public void Follow_game_uses_the_game_monitor_else_primary()
    {
        var p = new OverlayPlacement { MonitorMode = OverlayMonitorMode.FollowGame };
        Assert.Same(Primary, OverlayPlacementMath.Resolve(p, [Primary, Side]));
        Assert.Same(Side, OverlayPlacementMath.Resolve(p, [Primary, Side], Side.Key));
        Assert.Same(Primary, OverlayPlacementMath.Resolve(new OverlayPlacement(), [Side, Primary], Side.Key));
    }

    [Fact]
    public void Snap_to_edges_margin_and_center()
    {
        var area = new PixelRect(0, 0, 1920, 1040);
        var s = OverlayPlacementMath.Snap(5, 300, 200, 100, area, 24, 10);
        Assert.Equal(0, s.X);
        Assert.Equal(0, s.GuideX);
        Assert.Null(s.GuideY);
        Assert.Equal(300, s.Y);

        s = OverlayPlacementMath.Snap(30, 20, 200, 100, area, 24, 10);
        Assert.Equal(24, s.X);
        Assert.Equal(24, s.Y);

        s = OverlayPlacementMath.Snap(864, 474, 200, 100, area, 24, 10);
        Assert.Equal(860, s.X);
        Assert.Equal(960, s.GuideX);
        Assert.Equal(470, s.Y);
        Assert.Equal(520, s.GuideY);

        s = OverlayPlacementMath.Snap(1700, 935, 200, 100, area, 24, 10);
        Assert.Equal(1696, s.X); // margin from the right edge
        Assert.Equal(1895, s.GuideX);
        Assert.Equal(940, s.Y); // bottom edge
        Assert.Equal(1039, s.GuideY);
    }
}
