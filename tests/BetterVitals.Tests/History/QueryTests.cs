using BetterVitals.Core.Sensors;
using BetterVitals.History;
using BetterVitals.History.Storage;
using Xunit;

namespace BetterVitals.Tests.History;

public class QueryTests
{
    private static readonly DateTime T0 = new(2026, 10, 18, 21, 37, 0, DateTimeKind.Utc);

    /// <summary>Random walk with spikes and NaN gaps; deterministic.</summary>
    private static float Walk(int sensor, long ts)
    {
        var s = ts / TimeSpan.TicksPerSecond;
        var h = Signals.Hash(sensor, s);
        if (sensor == 1 && s % 5000 < 300) return float.NaN; // regular gaps
        var baseLine = 50 + 20 * Math.Sin(s / 3000.0 + sensor) + 5 * Math.Sin(s / 97.0);
        if (h % 4000 == 0) baseLine += 40; // rare spikes
        if (h % 4001 == 0) baseLine -= 45;
        return (float)(baseLine + (h % 100) * 0.01);
    }

    private static (long Count, long Min, long Max, double Sum, long MinT, long MaxT) Brute(List<(long T, long Q)> truth, long from, long to)
    {
        long count = 0, min = 0, max = 0, minT = 0, maxT = 0;
        double sum = 0;
        foreach (var (t, q) in truth)
        {
            if (t < from || t >= to) continue;
            if (count == 0 || q < min || (q == min && t < minT)) (min, minT) = (q, t);
            if (count == 0 || q > max || (q == max && t < maxT)) (max, maxT) = (q, t);
            sum += q;
            count++;
        }
        return (count, min, max, sum, minT, maxT);
    }

    [Fact]
    public async Task Stats_match_brute_force_on_random_ranges()
    {
        using var rig = new Rig("stats", T0);
        var sensors = new[]
        {
            rig.Hub.Add("Temp", SensorKind.Temperature),
            rig.Hub.Add("Gappy", SensorKind.Load),
            rig.Hub.Add("Power", SensorKind.Power),
        };
        var svc = rig.Open();
        svc.Configure(Rig.Settings(true, 2, sensors));
        // ~2.2 days, a 3-hour hole (PC asleep), then more data left partly unflushed
        rig.Feed(T0, 100_000, 2, Walk, interval: 2);
        var t2 = T0.AddSeconds(100_000 + 3 * 3600);
        var end = rig.Feed(t2, 90_000, 2, Walk, interval: 2);
        Assert.True(svc.Recorder.EarliestPendingStart() < end, "some data must still be in memory");

        var rnd = new Random(42);
        var from0 = T0.AddMinutes(-30).Ticks;
        var span = end - from0 + TimeSpan.TicksPerHour;
        for (var k = 0; k < 120; k++)
        {
            var len = k switch
            {
                < 20 => rnd.NextInt64(TimeSpan.TicksPerSecond, TimeSpan.TicksPerHour),
                < 40 => rnd.NextInt64(TimeSpan.TicksPerHour, TimeSpan.TicksPerDay),
                _ => rnd.NextInt64(TimeSpan.TicksPerSecond, span),
            };
            var from = from0 + rnd.NextInt64(0, span - len);
            if (k % 3 == 0) from = TimeGrid.Floor(from, k % 2 == 0 ? TimeGrid.Hour : TimeGrid.Minute);
            var to = from + len;
            var i = k % sensors.Length;
            var q = Quant.Of(sensors[i].Kind);
            var b = Brute(rig.Truth[i], from, to);
            var s = await svc.GetStatsAsync(sensors[i].Id, new DateTime(from, DateTimeKind.Utc), new DateTime(to, DateTimeKind.Utc));
            if (b.Count == 0)
            {
                Assert.Null(s);
                continue;
            }
            Assert.NotNull(s);
            var msg = $"range #{k} {new DateTime(from):O} +{TimeSpan.FromTicks(len)} sensor {i}";
            Assert.True(b.Count == s.Value.Count, $"{msg}: count {s.Value.Count} != {b.Count}");
            Assert.True(Quant.ToFloat(b.Min, q) == s.Value.Min, $"{msg}: min");
            Assert.True(Quant.ToFloat(b.Max, q) == s.Value.Max, $"{msg}: max");
            Assert.True(Math.Abs(b.Sum * q / b.Count - s.Value.Avg) < 1e-4, $"{msg}: avg {s.Value.Avg} vs {b.Sum * q / b.Count}");
            Assert.True(b.MinT == s.Value.MinTimeUtcTicks, $"{msg}: min time {new DateTime(s.Value.MinTimeUtcTicks):O} vs {new DateTime(b.MinT):O}");
            Assert.True(b.MaxT == s.Value.MaxTimeUtcTicks, $"{msg}: max time");
        }

        // the same after everything is flushed and the service restarted (rollups rebuilt from disk)
        svc.Suspend();
        svc.Dispose();
        var svc2 = rig.Open();
        svc2.Configure(Rig.Settings(true, 2, sensors));
        for (var i = 0; i < sensors.Length; i++)
        {
            var b = Brute(rig.Truth[i], from0, end);
            var s = (await svc2.GetStatsAsync(sensors[i].Id, new DateTime(from0, DateTimeKind.Utc), new DateTime(end, DateTimeKind.Utc)))!.Value;
            Assert.Equal(b.Count, s.Count);
            Assert.Equal(b.MinT, s.MinTimeUtcTicks);
            Assert.Equal(b.MaxT, s.MaxTimeUtcTicks);
        }
        Assert.Empty(rig.Warnings);
    }

    [Fact]
    public async Task Series_buckets_are_exact_and_keep_peaks()
    {
        using var rig = new Rig("series", T0);
        var a = rig.Hub.Add("A", SensorKind.Temperature);
        var svc = rig.Open();
        svc.Configure(Rig.Settings(true, 1, [a]));
        var spike = T0.AddHours(30).AddSeconds(15).Ticks;
        var end = rig.Feed(T0, 3 * 86400, 5, (_, t) => t == spike ? 99f : 40f + (t / TimeSpan.TicksPerSecond % 7) * 0.05f, interval: 5);
        var truth = rig.Truth[0];

        foreach (var maxPoints in new[] { 50, 200, 1000, 5000, 60_000 })
        {
            var from = T0.AddMinutes(-7).Ticks;
            var pts = await svc.GetSeriesAsync(a.Id, new DateTime(from, DateTimeKind.Utc), new DateTime(end, DateTimeKind.Utc), maxPoints);
            Assert.InRange(pts.Count, 1, maxPoints);
            Assert.Equal(99f, pts.Max(p => p.Max)); // the 1-sample spike never vanishes
            Assert.Equal(40f, pts.Min(p => p.Min));
            // each bucket equals a brute-force scan of its samples
            var w = HistoryStore.ChooseBucket(from, end, maxPoints, 1 * TimeSpan.TicksPerSecond);
            foreach (var p in pts.Where((_, i) => i % 17 == 0))
            {
                var b = Brute(truth, p.TimeUtcTicks, p.TimeUtcTicks + w);
                Assert.Equal(Quant.ToFloat(b.Min, 0.05), p.Min);
                Assert.Equal(Quant.ToFloat(b.Max, 0.05), p.Max);
                Assert.Equal(b.Sum * 0.05 / b.Count, p.Avg, 4);
            }
        }
    }

    [Theory]
    [InlineData(15 * 60, 1000, 2, 2, 0)]
    [InlineData(3600, 1000, 1, 5, 0)]
    [InlineData(86400, 1000, 1, 120, 1)]
    [InlineData(86400, 100_000, 1, 1, 0)]
    [InlineData(7 * 86400, 2000, 2, 600, 1)]
    [InlineData(30 * 86400, 1000, 2, 3600, 2)]
    [InlineData(365 * 86400, 500, 2, 86400, 2)]
    public void Resolution_choice(int seconds, int maxPoints, int interval, int expectSeconds, int expectLevel)
    {
        var from = T0.Ticks;
        var to = from + seconds * TimeSpan.TicksPerSecond;
        var w = HistoryStore.ChooseBucket(from, to, maxPoints, interval * TimeSpan.TicksPerSecond);
        Assert.Equal(expectSeconds * TimeSpan.TicksPerSecond, w);
        Assert.Equal((Level)expectLevel, HistoryStore.LevelFor(w));
        Assert.True(HistoryStore.BucketCount(from, to, w) <= maxPoints);
    }

    [Fact]
    public void Bucket_count_never_exceeds_max_points()
    {
        var rnd = new Random(7);
        for (var k = 0; k < 2000; k++)
        {
            var from = T0.Ticks + rnd.NextInt64(0, TimeSpan.TicksPerDay);
            var to = from + rnd.NextInt64(1, 800 * TimeSpan.TicksPerDay);
            var max = rnd.Next(1, 5000);
            var w = HistoryStore.ChooseBucket(from, to, max, TimeSpan.TicksPerSecond);
            Assert.True(HistoryStore.BucketCount(from, to, w) <= max, $"{from} {to} {max} → {w}");
        }
    }
}
