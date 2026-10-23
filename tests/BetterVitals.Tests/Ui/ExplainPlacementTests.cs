using Xunit;
using BetterVitals.UI.Explain;
using R = BetterVitals.UI.Explain.ExplainPlacement.PxRect;

namespace BetterVitals.Tests.Ui;

public sealed class ExplainPlacementTests
{
    private static readonly R Work = new(0, 0, 1920, 1040);

    [Fact]
    public void Goes_below_the_anchor_when_there_is_room()
    {
        var (x, y, above) = ExplainPlacement.Compute(new R(100, 200, 120, 20), 300, 90, Work, 6);
        Assert.False(above);
        Assert.Equal(100, x);
        Assert.Equal(226, y);
    }

    [Fact]
    public void Flips_above_near_the_bottom_edge()
    {
        var (_, y, above) = ExplainPlacement.Compute(new R(100, 1000, 120, 20), 300, 90, Work, 6);
        Assert.True(above);
        Assert.Equal(1000 - 6 - 90, y);
    }

    [Fact]
    public void Shifts_left_at_the_right_edge()
    {
        var (x, _, _) = ExplainPlacement.Compute(new R(1850, 200, 60, 20), 320, 90, Work, 6);
        Assert.Equal(1920 - 320, x);
    }

    [Fact]
    public void Stays_on_a_secondary_monitor_left_of_the_primary()
    {
        var work = new R(-1920, 0, 1920, 1080);
        var (x, y, _) = ExplainPlacement.Compute(new R(-1940, 500, 30, 20), 320, 90, work, 6);
        Assert.Equal(-1920, x);
        Assert.Equal(526, y);
    }

    [Fact]
    public void A_bubble_taller_than_the_room_on_both_sides_is_clamped_inside()
    {
        var (_, y, _) = ExplainPlacement.Compute(new R(10, 500, 50, 20), 300, 900, Work, 6);
        Assert.InRange(y, Work.Top, Work.Bottom - 900);
    }

    [Fact]
    public void Never_leaves_the_work_area()
    {
        var rng = new Random(7);
        for (var i = 0; i < 2000; i++)
        {
            var work = new R(rng.Next(-3000, 3000), rng.Next(-500, 500), rng.Next(800, 4000), rng.Next(600, 2200));
            var anchor = new R(work.Left + rng.Next(-50, (int)work.Width), work.Top + rng.Next(-50, (int)work.Height), rng.Next(1, 400), rng.Next(1, 60));
            var w = rng.Next(80, 400);
            var h = rng.Next(30, 500);
            var (x, y, _) = ExplainPlacement.Compute(anchor, w, h, work, 6);
            Assert.InRange(x, work.Left, work.Right - w);
            Assert.InRange(y, work.Top, work.Bottom - h);
        }
    }
}
