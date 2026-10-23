using BetterVitals.Fps;
using Xunit;

namespace BetterVitals.Tests.Fps;

public sealed class FrameStatsTests
{
    private const long Freq = 10_000_000; // QPC at 10 MHz, like most PCs
    private const long T0 = 1_000_000_000_000;

    private static long Ms(double ms) => (long)Math.Round(ms * Freq / 1000.0);

    /// <summary>Timestamps from frame times (ms), starting at <see cref="T0"/>.</summary>
    private static long[] FromDeltas(IEnumerable<double> deltasMs)
    {
        var list = new List<long> { T0 };
        var t = (double)T0;
        foreach (var d in deltasMs)
        {
            t += d * Freq / 1000.0;
            list.Add((long)Math.Round(t));
        }
        return list.ToArray();
    }

    private static FrameStats Compute(long[] ts, uint[]? keys = null, long? now = null)
    {
        keys ??= Enumerable.Repeat(1u, ts.Length).ToArray();
        var calc = new FrameStatsCalculator(Math.Max(16, ts.Length));
        return calc.Compute(ts.ToArray(), keys.ToArray(), now ?? (ts.Length > 0 ? ts[^1] : T0), Freq);
    }

    [Fact]
    public void Empty_is_NaN()
    {
        var s = Compute([]);
        Assert.False(s.HasValue);
        Assert.True(float.IsNaN(s.Low1));
        Assert.True(float.IsNaN(s.FrameTimeMaxMs));
    }

    [Fact]
    public void Steady_60_fps()
    {
        var s = Compute(FromDeltas(Enumerable.Repeat(1000.0 / 60, 720)));
        Assert.Equal(60f, s.Framerate, 0.01f);
        Assert.Equal(16.667f, s.FrameTimeMs, 0.01f);
        Assert.Equal(60f, s.Low1, 0.01f);
        Assert.Equal(60f, s.Low01, 0.01f);
        Assert.Equal(16.667f, s.FrameTimeMaxMs, 0.01f);
    }

    [Fact]
    public void Lows_use_nearest_rank_percentiles()
    {
        // 1000 frames in ~11 s would leave the window: keep it inside 10 s → 985 × 8 ms + 14 × 40 ms + 1 × 100 ms = 8.54 s
        var deltas = Enumerable.Repeat(8.0, 985).Concat(Enumerable.Repeat(40.0, 14)).Concat([100.0]).ToArray();
        var rng = new Random(3);
        rng.Shuffle(deltas);
        var s = Compute(FromDeltas(deltas));
        // n = 1000: p99 = 990th smallest = 40 ms (985 × 8 ms below it); p99.9 = 999th smallest = 40 ms; max = 100 ms
        Assert.Equal(25f, s.Low1, 0.01f);
        Assert.Equal(25f, s.Low01, 0.01f);
        Assert.Equal(100f, s.FrameTimeMaxMs, 0.01f);

        // 986 fast → the 990th smallest is still slow, 984 slow-ish moves it: check the boundary exactly
        deltas = Enumerable.Repeat(8.0, 990).Concat(Enumerable.Repeat(40.0, 9)).Concat([100.0]).ToArray();
        s = Compute(FromDeltas(deltas));
        Assert.Equal(125f, s.Low1, 0.01f); // 990th smallest = 8 ms
        Assert.Equal(25f, s.Low01, 0.01f); // 999th smallest = 40 ms
    }

    [Fact]
    public void Lows_match_a_sorted_reference_on_random_frame_times()
    {
        var rng = new Random(11);
        for (var run = 0; run < 50; run++)
        {
            var n = rng.Next(1, 1200);
            var deltas = Enumerable.Range(0, n).Select(_ => 2 + rng.NextDouble() * 6 + (rng.NextDouble() < 0.02 ? rng.NextDouble() * 60 : 0)).ToArray();
            var ts = FromDeltas(deltas);
            var s = Compute(ts);

            // reference: frame times from the rounded timestamps, sorted, nearest rank, over the last 10 s
            var newest = ts[^1];
            var kept = ts.Where(t => t >= newest - 10 * Freq).ToArray();
            var ft = kept.Zip(kept.Skip(1), (a, b) => (b - a) * 1000.0 / Freq).Order().ToArray();
            if (ft.Length == 0) continue;
            double P(double p) => ft[Math.Clamp((int)Math.Ceiling(p * ft.Length - 1e-9) - 1, 0, ft.Length - 1)];
            Assert.Equal((float)(1000 / P(0.99)), s.Low1, 0.001f);
            Assert.Equal((float)(1000 / P(0.999)), s.Low01, 0.001f);
            Assert.Equal((float)ft[^1], s.FrameTimeMaxMs, 0.001f);
        }
    }

    [Fact]
    public void Select_matches_sort()
    {
        var rng = new Random(5);
        for (var run = 0; run < 200; run++)
        {
            var a = Enumerable.Range(0, rng.Next(1, 300)).Select(_ => (float)rng.Next(0, 20)).ToArray(); // many duplicates
            var sorted = a.Order().ToArray();
            var k = rng.Next(0, a.Length);
            var v = FrameStatsCalculator.Select(a, k);
            Assert.Equal(sorted[k], v);
            Assert.All(a.Take(k), x => Assert.True(x <= v));
            Assert.All(a.Skip(k + 1), x => Assert.True(x >= v));
        }
    }

    [Theory]
    [InlineData(0.99, 1, 0)]
    [InlineData(0.99, 100, 98)]
    [InlineData(0.99, 101, 99)]
    [InlineData(0.999, 1000, 998)]
    [InlineData(0.999, 999, 998)]
    public void Rank_is_nearest_rank(double p, int n, int expected) => Assert.Equal(expected, FrameStatsCalculator.Rank(p, n));

    [Fact]
    public void Pause_longer_than_gap_is_not_a_frame()
    {
        var deltas = Enumerable.Repeat(1000.0 / 60, 180).Concat([5000.0]).Concat(Enumerable.Repeat(1000.0 / 60, 120));
        var s = Compute(FromDeltas(deltas));
        Assert.Equal(60f, s.Framerate, 0.01f);
        Assert.Equal(16.667f, s.FrameTimeMaxMs, 0.01f);
        Assert.Equal(60f, s.Low01, 0.01f);
    }

    [Fact]
    public void Hitch_below_gap_counts()
    {
        var deltas = Enumerable.Repeat(10.0, 300).Concat([1500.0]).Concat(Enumerable.Repeat(10.0, 300));
        var s = Compute(FromDeltas(deltas));
        Assert.Equal(100f, s.Framerate, 0.01f);
        Assert.Equal(1500f, s.FrameTimeMaxMs, 0.01f);
        Assert.Equal(1000f / 1500f, s.Low01, 0.001f);
    }

    [Fact]
    public void Old_frames_leave_the_10_s_window()
    {
        // a stutter 12 s ago no longer counts
        var deltas = new[] { 500.0 }.Concat(Enumerable.Repeat(10.0, 1200));
        var s = Compute(FromDeltas(deltas));
        Assert.Equal(10f, s.FrameTimeMaxMs, 0.01f);
    }

    [Fact]
    public void Stale_presents_are_NaN()
    {
        var ts = FromDeltas(Enumerable.Repeat(10.0, 100));
        Assert.True(Compute(ts, now: ts[^1] + Ms(2900)).HasValue); // ETW delivers ~1 s late: still presenting
        Assert.False(Compute(ts, now: ts[^1] + Ms(3100)).HasValue);
    }

    [Fact]
    public void Slow_app_uses_last_frame_time()
    {
        var s = Compute(FromDeltas([800.0, 800.0]));
        Assert.Equal(1.25f, s.Framerate, 0.001f);
        Assert.Equal(800f, s.FrameTimeMs, 0.01f);
    }

    [Fact]
    public void Single_present_is_NaN()
    {
        Assert.False(Compute([T0]).HasValue);
    }

    [Fact]
    public void Busiest_swap_chain_wins()
    {
        // game swap chain (key 2) at 100 FPS, launcher / UI swap chain (key 3) at 10 FPS in the same process
        var ts = new List<long>();
        var keys = new List<uint>();
        for (var i = 0; i < 300; i++)
        {
            ts.Add(T0 + Ms(i * 10.0));
            keys.Add(2);
            if (i % 10 == 5)
            {
                ts.Add(T0 + Ms(i * 10.0 + 3));
                keys.Add(3);
            }
        }
        var s = Compute(ts.ToArray(), keys.ToArray());
        Assert.Equal(100f, s.Framerate, 0.01f);
        Assert.Equal(10f, s.FrameTimeMaxMs, 0.01f);
    }

    [Fact]
    public void Out_of_order_delivery_is_sorted()
    {
        var ts = FromDeltas(Enumerable.Repeat(1000.0 / 144, 1000));
        var swapped = ts.ToArray();
        for (var i = 1; i + 1 < swapped.Length; i += 7) (swapped[i], swapped[i + 1]) = (swapped[i + 1], swapped[i]);
        var a = Compute(ts);
        var b = Compute(swapped);
        Assert.Equal(a, b);
        Assert.Equal(144f, b.Framerate, 0.02f);
    }

    [Fact]
    public void Compute_does_not_allocate()
    {
        var ts = FromDeltas(Enumerable.Repeat(7.0, 1400));
        var keys = Enumerable.Repeat(1u, ts.Length).ToArray();
        var calc = new FrameStatsCalculator(ts.Length);
        calc.Compute(ts, keys, ts[^1], Freq);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10; i++) calc.Compute(ts, keys, ts[^1], Freq);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
