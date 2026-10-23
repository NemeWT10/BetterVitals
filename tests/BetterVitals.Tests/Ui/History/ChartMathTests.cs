using System.Globalization;
using System.Windows;
using BetterVitals.Core.History;
using BetterVitals.UI.Charts;
using Xunit;

namespace BetterVitals.Tests.Ui.History;

public sealed class ChartMathTests
{
    private const long S = TimeSpan.TicksPerSecond;
    private const long M = TimeSpan.TicksPerMinute;
    private const long H = TimeSpan.TicksPerHour;
    private static readonly long T0 = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc).Ticks;

    private static HistoryPoint P(long t, float v) => new(t, v, v, v);

    // ───────────── scales ─────────────

    [Theory]
    [InlineData(0.7, 1)]
    [InlineData(1.0, 1)]
    [InlineData(1.3, 2)]
    [InlineData(2.2, 2.5)]
    [InlineData(3.1, 5)]
    [InlineData(7, 10)]
    [InlineData(0.031, 0.05)]
    [InlineData(1300, 2000)]
    public void Nice_steps(double rough, double expected) => Assert.Equal(expected, ChartMath.NiceStep(rough), 9);

    [Theory]
    [InlineData(5, 0)]
    [InlineData(2.5, 1)]
    [InlineData(0.25, 2)]
    [InlineData(0.001, 3)]
    public void Label_decimals_follow_the_step(double step, int decimals) => Assert.Equal(decimals, ChartMath.DecimalsFor(step));

    [Fact]
    public void Temperatures_get_a_padded_nice_scale()
    {
        var s = ChartMath.Nice(41.3, 78.9, 300, "°C");
        Assert.True(s.Min <= 41.3 && s.Max >= 78.9);
        Assert.True(s.Min > 0, "temperatures do not start at zero");
        Assert.Equal(0, s.Min % s.Step, 6);
        Assert.True(s.Step is 5 or 10, $"step {s.Step}");
        Assert.All(s.Ticks(), t => Assert.True(Math.Abs(t / s.Step - Math.Round(t / s.Step)) < 1e-6, $"tick {t} is off the step"));
    }

    [Fact]
    public void Percent_is_always_zero_to_hundred_and_extends_above()
    {
        var s = ChartMath.Nice(12, 40, 300, "%");
        Assert.Equal(0, s.Min);
        Assert.Equal(100, s.Max);
        Assert.True(ChartMath.Nice(5, 130, 300, "%").Max >= 130);
    }

    [Fact]
    public void Flat_lines_and_near_zero_data()
    {
        var flat = ChartMath.Nice(15000, 15000, 300, "MHz");
        Assert.True(flat.Min < 15000 && flat.Max > 15000);
        var watts = ChartMath.Nice(18, 350, 300, "W");
        Assert.Equal(0, watts.Min); // non-negative data that starts near zero starts at zero
        var nan = ChartMath.Nice(double.NaN, double.NaN, 300, "V");
        Assert.True(nan.Max > nan.Min);
    }

    [Fact]
    public void Two_axes_share_one_grid()
    {
        var (left, right) = ChartMath.Paired(66, 81, "°C", 20, 90, "%", 260);
        var nl = (int)Math.Round(left.Span / left.Step);
        var nr = (int)Math.Round(right.Span / right.Step);
        Assert.Equal(nl, nr);
        Assert.Equal(0, right.Min);
        Assert.Equal(100, right.Max); // percent never stretches to 120 to line up
        Assert.True(left.Min <= 66 && left.Max >= 81);
        Assert.True(left.Span <= 30, $"left scale wastes height: {left}");

        var (w, t) = ChartMath.Paired(20, 340, "W", 35, 84, "°C", 300);
        Assert.Equal(Math.Round(w.Span / w.Step), Math.Round(t.Span / t.Step));
        Assert.True(w.Min <= 20 && w.Max >= 340 && t.Min <= 35 && t.Max >= 84);
    }

    // ───────────── axes ─────────────

    [Fact]
    public void Units_go_left_then_right_then_own_scales()
    {
        var (sides, left, right, own) = ChartMath.AssignAxes(["°C", "%", "°C", "MHz", "W", "MHz"]);
        Assert.Equal("°C", left);
        Assert.Equal("%", right);
        Assert.Equal([AxisSide.Left, AxisSide.Right, AxisSide.Left, AxisSide.Own, AxisSide.Own, AxisSide.Own], sides);
        Assert.Equal(["MHz", "W"], own);
        var single = ChartMath.AssignAxes(["°C", "°C"]);
        Assert.Null(single.Right);
        Assert.Empty(single.Own);
    }

    // ───────────── gaps ─────────────

    [Fact]
    public void Gaps_break_the_line_one_missing_bucket_does_not()
    {
        var pts = new List<HistoryPoint>();
        for (var i = 0; i < 10; i++) pts.Add(P(T0 + i * 2 * S, i));
        pts.RemoveAt(4); // one missing sample: tolerated
        for (var i = 0; i < 10; i++) pts.Add(P(T0 + H + i * 2 * S, i)); // app was off for an hour
        var step = ChartMath.TypicalStep(pts.ToArray());
        Assert.Equal(2 * S, step);
        var segs = new List<(int, int)>();
        ChartMath.Segments(pts.ToArray(), ChartMath.GapThreshold(step), segs);
        Assert.Equal([(0, 9), (9, 19)], segs);
        ChartMath.Segments([], 10, segs);
        Assert.Empty(segs);
    }

    // ───────────── hit testing ─────────────

    [Fact]
    public void Nearest_point_and_nothing_over_a_gap()
    {
        HistoryPoint[] pts = [P(T0, 1), P(T0 + M, 2), P(T0 + 2 * M, 3), P(T0 + 10 * M, 4)];
        Assert.Equal(1, ChartMath.Nearest(pts, T0 + M + M / 2 + 5 * S, M, M));
        Assert.Equal(0, ChartMath.Nearest(pts, T0 - 10 * S, M, M));
        Assert.Equal(-1, ChartMath.Nearest(pts, T0 + 6 * M, M, M)); // in the gap
        Assert.Equal(3, ChartMath.Nearest(pts, T0 + 10 * M + 20 * S, M, M));
        Assert.Equal(-1, ChartMath.Nearest([], T0, M, M));
        Assert.Equal(2, ChartMath.LowerBound(pts, T0 + 90 * S));
    }

    [Fact]
    public void Time_and_x_map_both_ways()
    {
        var v = new TimeView(T0, T0 + H);
        Assert.Equal(T0 + H / 2, ChartMath.TimeAt(v, 50, 400, 250));
        Assert.Equal(250, ChartMath.XOf(v, 50, 400, T0 + H / 2), 6);
    }

    // ───────────── zoom, pan, selection ─────────────

    [Fact]
    public void Wheel_zoom_keeps_the_time_under_the_cursor()
    {
        var v = new TimeView(T0, T0 + 4 * H);
        var at = T0 + H; // a quarter across
        var z = ChartMath.ZoomAt(v, 0.25, 0.5, long.MaxValue);
        Assert.Equal(2 * H, z.Span);
        Assert.Equal(at, z.From + z.Span / 4);
        var tiny = ChartMath.ZoomAt(new TimeView(T0, T0 + 2 * M), 0.5, 0.01, long.MaxValue);
        Assert.Equal(ChartMath.MinSpan, tiny.Span);
    }

    [Fact]
    public void The_window_never_reaches_past_now()
    {
        var now = T0 + 4 * H;
        var v = new TimeView(T0, now);
        var zoomOut = ChartMath.ZoomAt(v, 0.9, 2, now);
        Assert.Equal(now, zoomOut.To);
        Assert.Equal(8 * H, zoomOut.Span);
        var pan = ChartMath.Pan(v, 30 * M, now);
        Assert.Equal(v, pan);
        var back = ChartMath.Pan(v, -30 * M, now);
        Assert.Equal(now - 30 * M, back.To);
    }

    [Fact]
    public void A_drag_becomes_a_range_and_a_click_does_not()
    {
        var v = new TimeView(T0, T0 + H);
        Assert.Null(ChartMath.SelectionRange(v, 50, 600, 200, 202));
        var r = ChartMath.SelectionRange(v, 50, 600, 650, 350)!.Value;
        Assert.Equal(T0 + H / 2, r.From);
        Assert.Equal(T0 + H, r.To); // clamped to the plot
        Assert.True(r.From < r.To);
    }

    // ───────────── live append ─────────────

    [Fact]
    public void Live_samples_merge_into_the_last_bucket_then_open_new_ones()
    {
        var pts = new List<HistoryPoint> { new(T0, 40, 42, 44) }; // a 1-minute bucket from the query
        var state = default(LiveBucket);
        Assert.True(ChartMath.AppendLive(pts, ref state, T0 + 30 * S, 50, M));
        Assert.Single(pts);
        Assert.Equal(50, pts[0].Max);
        Assert.Equal(40, pts[0].Min);
        Assert.InRange(pts[0].Avg, 42, 50);
        Assert.False(ChartMath.AppendLive(pts, ref state, T0 + 30 * S, 99, M)); // same sample again
        Assert.False(ChartMath.AppendLive(pts, ref state, T0 + 40 * S, float.NaN, M));
        Assert.True(ChartMath.AppendLive(pts, ref state, T0 + 2 * M + 5 * S, 30, M));
        Assert.Equal(2, pts.Count);
        Assert.Equal(T0 + 2 * M, pts[1].TimeUtcTicks); // on the same grid
        Assert.True(ChartMath.AppendLive(pts, ref state, T0 + 2 * M + 15 * S, 34, M));
        Assert.Equal(32, pts[1].Avg, 3);
        Assert.False(ChartMath.AppendLive(pts, ref state, T0, 1, M)); // older than the data

        var empty = new List<HistoryPoint>();
        var s2 = default(LiveBucket);
        Assert.True(ChartMath.AppendLive(empty, ref s2, T0 + 7 * S + 300, 5, 2 * S));
        Assert.Equal(T0 + 6 * S, empty[0].TimeUtcTicks);
    }

    [Fact]
    public void Old_points_are_trimmed_but_one_stays_to_enter_from_the_left()
    {
        var pts = Enumerable.Range(0, 100).Select(i => P(T0 + i * S, i)).ToList();
        ChartMath.TrimBefore(pts, T0 + 50 * S);
        Assert.Equal(T0 + 49 * S, pts[0].TimeUtcTicks);
    }

    // ───────────── decimation ─────────────

    [Fact]
    public void Dense_points_merge_per_pixel_column_and_keep_the_peaks()
    {
        var v = new TimeView(T0, T0 + 1000 * S);
        var pts = Enumerable.Range(0, 1000).Select(i => new HistoryPoint(T0 + i * S, i % 50 == 7 ? -5 : 1, 2, i == 333 ? 99 : 3)).ToArray();
        var output = new List<HistoryPoint>();
        ChartMath.Decimate(pts, 0, pts.Length, v, 0, 100, 1.25, 0, output);
        Assert.InRange(output.Count, 100, 130);
        Assert.Equal(99, output.Max(p => p.Max));
        Assert.Equal(-5, output.Min(p => p.Min));
        Assert.All(output, p => Assert.Equal(2, p.Avg, 4));
        var (lo, hi) = ChartMath.Extent(pts, T0 + 300 * S, T0 + 400 * S);
        Assert.Equal(-5, lo);
        Assert.Equal(99, hi);
        Assert.True(double.IsNaN(ChartMath.Extent(pts, T0 + 5000 * S, T0 + 6000 * S).Lo));
    }
}

public sealed class TimeTicksTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
    private static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    private static long Utc(int y, int mo, int d, int h, int mi) => new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Utc).Ticks;

    [Fact]
    public void One_hour_on_800_px_gets_ten_minute_ticks_on_the_local_clock()
    {
        var from = Utc(2026, 10, 23, 17, 33);
        var ticks = TimeTicks.Build(from, from + TimeSpan.TicksPerHour, 800, Warsaw, Pl, 84);
        Assert.Equal(["19:40", "19:50", "20:00", "20:10", "20:20", "20:30"], ticks.Select(t => t.Label));
        var en = TimeTicks.Build(from, from + TimeSpan.TicksPerHour, 800, Warsaw, En, 110);
        Assert.Contains("8:00 PM", en.Select(t => t.Label));
    }

    [Fact]
    public void A_day_shows_hours_and_the_date_at_midnight()
    {
        var to = Utc(2026, 10, 23, 18, 40);
        var ticks = TimeTicks.Build(to - TimeSpan.TicksPerDay, to, 900, Warsaw, Pl, 84);
        var midnight = Assert.Single(ticks, t => t.Major);
        Assert.Equal("23 paź", midnight.Label);
        Assert.Equal(Utc(2026, 10, 22, 22, 0), midnight.Utc);
        Assert.All(ticks.Where(t => !t.Major), t => Assert.Matches(@"^\d\d:00$", t.Label));
        var en = TimeTicks.Build(to - TimeSpan.TicksPerDay, to, 900, Warsaw, En, 110);
        Assert.Equal("Oct 23", en.Single(t => t.Major).Label);
    }

    [Fact]
    public void Week_and_month_ranges_label_days()
    {
        var to = Utc(2026, 10, 23, 18, 40);
        var week = TimeTicks.Build(to - 7 * TimeSpan.TicksPerDay, to, 900, Warsaw, Pl, 84);
        Assert.Equal(7, week.Count);
        Assert.All(week, t => Assert.Matches(@"^\d{1,2} \p{L}+$", t.Label));
        var month = TimeTicks.Build(to - 30 * TimeSpan.TicksPerDay, to, 900, Warsaw, Pl, 84);
        Assert.InRange(month.Count, 4, 16);
        var year = TimeTicks.Build(to - 400 * TimeSpan.TicksPerDay, to, 900, Warsaw, Pl, 84);
        Assert.Contains(year, t => t.Label == "2026");
    }

    [Fact]
    public void Daylight_saving_change_keeps_ticks_increasing_and_unique()
    {
        // Warsaw falls back from 03:00 CEST to 02:00 CET on 25 Oct 2026
        var from = Utc(2026, 10, 24, 21, 30);
        var ticks = TimeTicks.Build(from, from + 8 * TimeSpan.TicksPerHour, 1200, Warsaw, Pl, 60);
        for (var i = 1; i < ticks.Count; i++) Assert.True(ticks[i].Utc > ticks[i - 1].Utc);
        Assert.Equal(ticks.Count, ticks.Select(t => t.Label).Distinct().Count());
        Assert.Contains(ticks, t => t.Label == "25 paź");
        // spring forward (28 Mar 2027): the missing hour has no tick
        var spring = TimeTicks.Build(Utc(2027, 3, 27, 23, 30), Utc(2027, 3, 28, 4, 0), 1200, Warsaw, Pl, 60);
        Assert.DoesNotContain(spring, t => t.Label == "02:00");
        for (var i = 1; i < spring.Count; i++) Assert.True(spring[i].Utc > spring[i - 1].Utc);
    }

    [Fact]
    public void Short_ranges_get_seconds()
    {
        var from = Utc(2026, 10, 23, 18, 39);
        var ticks = TimeTicks.Build(from, from + TimeSpan.TicksPerMinute, 800, Warsaw, Pl, 84);
        Assert.All(ticks, t => Assert.Matches(@"^\d\d:\d\d:\d\d$", t.Label));
        Assert.True(TimeTicks.StepTicks(TimeTicks.PickStep(TimeSpan.TicksPerMinute, 800, 84)) <= 10 * TimeSpan.TicksPerSecond);
    }
}

[Collection(UiCollection.Name)]
public sealed class TimeChartTests
{
    private static readonly long Now = HistoryRig.NowUtc.Ticks;

    private static (TimeChart Chart, System.Windows.Controls.Border Host) Make(params ChartSeries[] series)
    {
        var chart = new TimeChart { Clock = () => Now, Zone = HistoryRig.Warsaw, Animations = false };
        var host = new System.Windows.Controls.Border { Width = 900, Height = 360, Child = chart, Resources = HistoryRig.Resources("aurora") };
        host.Measure(new Size(900, 360));
        host.Arrange(new Rect(0, 0, 900, 360));
        chart.SetSeries(series);
        chart.SetView(new TimeView(Now - TimeSpan.TicksPerHour, Now), animate: false);
        chart.RenderNow();
        return (chart, host);
    }

    private static ChartSeries Series(string unit, int color, long from, long to, long step, Func<long, float> f)
    {
        var pts = new List<HistoryPoint>();
        for (var t = from; t < to; t += step) pts.Add(new HistoryPoint(t, f(t) - 1, f(t), f(t) + 1));
        return new ChartSeries { Id = unit + color, Name = "S" + color, Unit = unit, ColorIndex = color, Points = pts, Bucket = step };
    }

    [Fact]
    public void Drag_selects_click_clears_and_esc_like_reset() => Sta.Run(() =>
    {
        var (chart, _) = Make(Series("°C", 0, Now - TimeSpan.TicksPerHour, Now, 10 * TimeSpan.TicksPerSecond, t => 50));
        var plot = chart.PlotRect;
        (long, long)? got = null;
        var events = 0;
        chart.SelectionChanged += s =>
        {
            got = s;
            events++;
        };
        chart.PointerDown(new Point(plot.Left + plot.Width * 0.25, plot.Top + 20), pan: false);
        chart.PointerMove(new Point(plot.Left + plot.Width * 0.5, plot.Top + 20));
        chart.PointerUp(new Point(plot.Left + plot.Width * 0.5, plot.Top + 20));
        Assert.NotNull(got);
        Assert.Equal(Now - TimeSpan.TicksPerHour * 3 / 4, got!.Value.Item1, TimeSpan.TicksPerSecond * 5);
        Assert.Equal(Now - TimeSpan.TicksPerHour / 2, got.Value.Item2, TimeSpan.TicksPerSecond * 5);
        chart.PointerDown(new Point(plot.Left + 100, plot.Top + 20), pan: false);
        chart.PointerUp(new Point(plot.Left + 101, plot.Top + 20));
        Assert.Null(chart.Selection);
        Assert.Equal(2, events);
    });

    [Fact]
    public void Pan_moves_back_in_time_and_stops_at_now() => Sta.Run(() =>
    {
        var (chart, _) = Make(Series("°C", 0, Now - 2 * TimeSpan.TicksPerHour, Now, 10 * TimeSpan.TicksPerSecond, t => 50));
        var plot = chart.PlotRect;
        var user = 0;
        chart.ViewChanged += (_, r) => user += r == ViewChangeReason.User ? 1 : 0;
        chart.PointerDown(new Point(plot.Left + 200, 100), pan: true);
        chart.PointerMove(new Point(plot.Left + 200 + plot.Width / 4, 100)); // drag right = earlier
        chart.PointerUp(new Point(plot.Left + 200 + plot.Width / 4, 100));
        Assert.Equal(Now - TimeSpan.TicksPerHour / 4, chart.View.To, TimeSpan.TicksPerSecond * 10);
        chart.PointerDown(new Point(plot.Left + 200, 100), pan: true);
        chart.PointerMove(new Point(plot.Left - 600, 100)); // far into the future: clamped
        chart.PointerUp(new Point(plot.Left - 600, 100));
        Assert.Equal(Now, chart.View.To);
        Assert.Equal(TimeSpan.TicksPerHour, chart.View.Span);
        Assert.True(user >= 2);

        chart.Wheel(new Point(plot.Left + plot.Width / 2, 100), 120);
        Assert.True(chart.View.Span < TimeSpan.TicksPerHour);
    });

    [Fact]
    public void Crosshair_reports_every_series_and_gaps_have_no_value() => Sta.Run(() =>
    {
        var step = 30 * TimeSpan.TicksPerSecond;
        var a = Series("°C", 0, Now - TimeSpan.TicksPerHour, Now, step, _ => 60);
        // the second series has a 20-minute hole in the middle
        var b = Series("%", 1, Now - TimeSpan.TicksPerHour, Now, step, _ => 30);
        b.Points.RemoveAll(p => p.TimeUtcTicks > Now - 40 * TimeSpan.TicksPerMinute && p.TimeUtcTicks < Now - 20 * TimeSpan.TicksPerMinute);
        var (chart, _) = Make(a, b);
        var hits = chart.HitTest(Now - 30 * TimeSpan.TicksPerMinute);
        Assert.True(hits[0].Index >= 0);
        Assert.Equal(-1, hits[1].Index);
        var both = chart.HitTest(Now - 10 * TimeSpan.TicksPerMinute);
        Assert.All(both, h => Assert.True(h.Index >= 0));
        Assert.NotNull(chart.ScaleOf("°C"));
        Assert.Equal(100, chart.ScaleOf("%")!.Value.Max);

        chart.ShowMarker(Now - 10 * TimeSpan.TicksPerMinute);
        Assert.True(chart.CrosshairVisible);
        chart.PointerMove(new Point(2, 2)); // outside the plot: the pinned marker gives way
        Assert.False(chart.CrosshairVisible);
    });

    [Fact]
    public void Bubble_time_shows_the_bucket() => Sta.Run(() =>
    {
        var (chart, _) = Make();
        chart.Culture = CultureInfo.GetCultureInfo("pl-PL");
        var t = new DateTime(2026, 10, 23, 18, 5, 0, DateTimeKind.Utc).Ticks;
        Assert.Equal("23 paź, 20:05:00", chart.BubbleTime(t + TimeSpan.TicksPerSecond, 2 * TimeSpan.TicksPerSecond));
        Assert.Equal("23 paź, 20:05 – 20:06", chart.BubbleTime(t + 30 * TimeSpan.TicksPerSecond, TimeSpan.TicksPerMinute));
    });
}
