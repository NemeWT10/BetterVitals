using System.Diagnostics;
using BetterVitals.Core.Sensors;
using BetterVitals.History.Storage;
using Xunit;
using Xunit.Abstractions;

namespace BetterVitals.Tests.History;

/// <summary>
/// Measurements (not micro-benchmarks with a harness, but stable enough): append cost and allocations on the engine
/// thread, flush cost, bytes on disk per sample, query latency. Results go to the test output and to
/// tests/output/history/bench-*.txt. Budgets are asserted loosely so a slow CI box does not fail them.
/// </summary>
[Trait("Category", "Bench")]
public class BenchTests(ITestOutputHelper output)
{
    private static readonly DateTime T0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private void Report(string name, IEnumerable<string> lines)
    {
        var text = string.Join(Environment.NewLine, lines);
        output.WriteLine(text);
        Directory.CreateDirectory(TestDir.Root);
        File.WriteAllText(Path.Combine(TestDir.Root, $"bench-{name}.txt"), text + Environment.NewLine);
    }

    private static double Median(List<double> xs)
    {
        xs.Sort();
        return xs[xs.Count / 2];
    }

    private static (double First, double Median) Time(Func<Task> f, int runs = 7)
    {
        var all = new List<double>();
        for (var i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            f().GetAwaiter().GetResult();
            all.Add(sw.Elapsed.TotalMilliseconds);
        }
        var first = all[0];
        return (first, Median(all.Skip(1).ToList()));
    }

    [Fact]
    public void Append_is_allocation_free_and_cheap()
    {
        using var rig = new Rig("bench-append", T0);
        var kinds = Signals.TypicalMix(100);
        var sensors = kinds.Select((k, i) => rig.Hub.Add($"S{i}", k)).ToList();
        var svc = rig.Open();
        svc.Configure(Rig.Settings(true, 1, sensors));
        using var watch = svc.Watch(sensors.Take(20).Select(s => s.Id));

        var frame = new SensorFrame(128) { Count = 100 };
        long allocated = 0, ticks = 0, flushTicks = 0;
        var flushes = 0;
        const int n = 6 * 3600; // 6 h at 1 s
        var ts = T0.Ticks;
        for (var k = -600; k < n; k++, ts += TimeSpan.TicksPerSecond)
        {
            frame.Sequence = k + 601;
            frame.TimestampUtcTicks = ts;
            for (var i = 0; i < 100; i++)
            {
                frame.Values[i] = Signals.Slow(kinds[i], i, ts);
                frame.UpdatedAt[i] = frame.Sequence;
            }
            rig.Time.Ticks = ts;
            var a0 = GC.GetAllocatedBytesForCurrentThread();
            var t0 = Stopwatch.GetTimestamp();
            rig.Hub.Raise(frame);
            var t1 = Stopwatch.GetTimestamp();
            var a1 = GC.GetAllocatedBytesForCurrentThread();
            if (k >= 0)
            {
                ticks += t1 - t0;
                allocated += a1 - a0;
            }
            if (ts % TimeSpan.TicksPerMinute == 0)
            {
                var f0 = Stopwatch.GetTimestamp();
                svc.Flush();
                if (k >= 0)
                {
                    flushTicks += Stopwatch.GetTimestamp() - f0;
                    flushes++;
                }
            }
        }
        var perTickUs = ticks * 1e6 / Stopwatch.Frequency / n;
        var perFlushMs = flushTicks * 1e3 / Stopwatch.Frequency / flushes;
        Report("append",
        [
            $"append: 100 sensors recorded at 1 s (+20 watched in the live ring), {n} ticks",
            $"  engine-thread cost: {perTickUs:F2} µs/tick, allocated {allocated} B over {n} ticks",
            $"  flush (one 60 s x 100-sensor block + due rollups): {perFlushMs:F3} ms avg over {flushes} flushes",
            $"  live ring memory: {svc.LiveBufferBytes / 1024} KB",
        ]);
        Assert.Equal(0, allocated);
        Assert.True(perTickUs < 50, $"{perTickUs} µs/tick");
    }

    [Fact]
    public async Task Size_per_sample_and_day_queries()
    {
        using var rig = new Rig("bench-size", T0);
        var kinds = Signals.TypicalMix(60);
        var sensors = kinds.Select((k, i) => rig.Hub.Add($"S{i}", k)).ToList();
        var svc = rig.Open();
        svc.Configure(Rig.Settings(true, 1, sensors));
        var sw = Stopwatch.StartNew();
        var end = rig.Feed(T0, 86400, 1, (i, t) => Signals.Slow(kinds[i], i, t));
        svc.Suspend();
        var feedMs = sw.Elapsed.TotalMilliseconds;

        long Size(string prefix) => Directory.GetFiles(rig.Dir.Path, prefix + "*").Sum(f => new FileInfo(f).Length);
        var raw = Size("raw-");
        var min = Size("min-");
        var hour = Size("hour-");
        var cat = Size("catalog");
        var values = 86400.0 * 60;

        // noisy (busy PC) for one hour, extrapolated
        using var noisy = new Rig("bench-noisy", T0);
        var ns = kinds.Select((k, i) => noisy.Hub.Add($"S{i}", k)).ToList();
        var nsvc = noisy.Open();
        nsvc.Configure(Rig.Settings(true, 1, ns));
        noisy.Feed(T0, 3600, 1, (i, t) => Signals.Noisy(kinds[i], i, t));
        nsvc.Suspend();
        var noisyRaw = Directory.GetFiles(noisy.Dir.Path, "raw-*").Sum(f => new FileInfo(f).Length);

        // queries over the day (files are in the OS cache, like on a real PC)
        var id = sensors[0].Id;
        var from = T0;
        var to = new DateTime(end, DateTimeKind.Utc);
        var rawSeries = Time(() => svc.GetSeriesAsync(id, from, to, 100_000));
        var rawCount = (await svc.GetSeriesAsync(id, from, to, 100_000)).Count;
        var chart = Time(() => svc.GetSeriesAsync(id, from, to, 1500));
        var stats = Time(() => svc.GetStatsAsync(id, from.AddSeconds(17), to.AddSeconds(-23)));
        var last15 = Time(() => svc.GetSeriesAsync(id, to.AddMinutes(-15), to, 1000));
        var cov = Time(() => Task.FromResult(svc.GetCoverage(id)));
        var usage = Time(() => Task.FromResult(svc.GetUsage()));
        var exportMs = Time(() => svc.ExportAsync(new([.. sensors.Select(s => s.Id)], from, from.AddHours(1), Core.History.ExportResolution.Raw, true),
            new MemoryStream()), 3);

        Report("size",
        [
            $"size: 60 sensors (typical mix), 1 s, 24 h of slowly varying data (feed+flush took {feedMs:F0} ms)",
            $"  raw {raw / 1024.0:F0} KB, minute {min / 1024.0:F0} KB, hour {hour / 1024.0:F1} KB, catalog {cat} B",
            $"  raw bytes/value {raw / values:F3}, all levels {(raw + min + hour) / values:F3} B/value",
            $"  => {(raw + min + hour) / 1048576.0:F2} MB/day at 1 s, ~{(raw / 2.0 + min + hour) / 1048576.0:F2} MB/day at 2 s (default)",
            $"  noisy (every sample random, busy PC): raw {noisyRaw / (3600.0 * 60):F3} B/value => {noisyRaw * 24 / 1048576.0:F1} MB/day at 1 s",
            $"queries (24 h of 1 s data, one sensor; first / median of warm runs, ms):",
            $"  series raw 1 s ({rawCount} points): {rawSeries.First:F1} / {rawSeries.Median:F1}",
            $"  series chart 1500 points (minute rollups): {chart.First:F1} / {chart.Median:F1}",
            $"  stats 24 h (hour+minute rollups, raw edges, time refine): {stats.First:F1} / {stats.Median:F1}",
            $"  series last 15 min: {last15.First:F1} / {last15.Median:F1}",
            $"  coverage: {cov.First:F1} / {cov.Median:F1}; usage: {usage.First:F1} / {usage.Median:F1}",
            $"  export 1 h x 60 sensors raw zip: {exportMs.First:F0} / {exportMs.Median:F0}",
        ]);
        Assert.Equal(86400, rawCount);
        Assert.True(rawSeries.Median < 200, $"{rawSeries.Median} ms");
    }

    [Fact]
    public async Task Thirty_days_via_minute_rollups()
    {
        using var rig = new Rig("bench-30d", T0);
        var kinds = Signals.TypicalMix(60);
        var sensors = kinds.Select((k, i) => rig.Hub.Add($"S{i}", k)).ToList();
        var svc = rig.Open();
        svc.Configure(Rig.Settings(true, 10, sensors));
        var sw = Stopwatch.StartNew();
        var end = rig.Feed(T0, 30 * 86400, 10, (i, t) => Signals.Slow(kinds[i], i, t));
        svc.Suspend();
        var feedMs = sw.Elapsed.TotalMilliseconds;
        var id = sensors[5].Id;
        var to = new DateTime(end, DateTimeKind.Utc);
        var minuteSeries = Time(() => svc.GetSeriesAsync(id, T0, to, 50_000));
        var minuteCount = (await svc.GetSeriesAsync(id, T0, to, 50_000)).Count;
        var hourSeries = Time(() => svc.GetSeriesAsync(id, T0, to, 1000));
        var stats = Time(() => svc.GetStatsAsync(id, T0.AddSeconds(5), to.AddMinutes(-7)));
        var week = Time(() => svc.GetSeriesAsync(id, to.AddDays(-7), to, 2000));
        long Size(string prefix) => Directory.GetFiles(rig.Dir.Path, prefix + "*").Sum(f => new FileInfo(f).Length);
        Report("30d",
        [
            $"30 days, 60 sensors at 10 s (feed+flush {feedMs / 1000:F1} s); raw {Size("raw-") / 1048576.0:F1} MB, minute {Size("min-") / 1048576.0:F2} MB, hour {Size("hour-") / 1024.0:F0} KB",
            $"  series 30 d via minute rollups ({minuteCount} points): {minuteSeries.First:F1} / {minuteSeries.Median:F1} ms",
            $"  series 30 d via hour rollups (1000 points): {hourSeries.First:F1} / {hourSeries.Median:F1} ms",
            $"  series 7 d, 2000 points: {week.First:F1} / {week.Median:F1} ms",
            $"  stats 30 d: {stats.First:F1} / {stats.Median:F1} ms",
        ]);
        Assert.Equal(30 * 1440, minuteCount);
        Assert.True(minuteSeries.Median < 200, $"{minuteSeries.Median} ms");
    }
}
