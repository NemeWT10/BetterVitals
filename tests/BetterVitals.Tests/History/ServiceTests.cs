using BetterVitals.Core.Sensors;
using BetterVitals.History.Storage;
using Xunit;

namespace BetterVitals.Tests.History;

public class ServiceTests
{
    private static readonly DateTime T0 = new(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Recording_off_writes_no_files_and_serves_live_ring()
    {
        using var rig = new Rig("off", T0);
        var temp = rig.Hub.Add("CPU Package", SensorKind.Temperature);
        var load = rig.Hub.Add("CPU Total", SensorKind.Load);
        var svc = rig.Open();
        svc.Configure(Rig.Settings(false, 2, [temp, load]));
        using var watch = svc.Watch([temp.Id]);
        Assert.Contains(rig.Hub.Demands, d => d.Consumer == "history-live" && d.Ids.Contains(temp.Id));
        Assert.DoesNotContain(rig.Hub.Demands, d => d.Consumer == "history");

        rig.Feed(T0, 600, 1, (i, t) => i == 0 ? 40 + (t / TimeSpan.TicksPerSecond % 60) * 0.1f : 5);
        svc.Flush();
        svc.Suspend();

        var series = await svc.GetSeriesAsync(temp.Id, T0, T0.AddMinutes(10), 1000);
        Assert.Equal(600, series.Count); // 1 s live rows
        var stats = await svc.GetStatsAsync(temp.Id, T0, T0.AddMinutes(10));
        Assert.NotNull(stats);
        Assert.Equal(40f, stats.Value.Min, 3);
        Assert.Equal(45.9f, stats.Value.Max, 3);
        Assert.Equal(600, stats.Value.Count);
        Assert.NotNull(svc.GetCoverage(temp.Id));
        Assert.Equal(0, svc.GetUsage().BytesOnDisk);
        await svc.ExportAsync(new([temp.Id], T0, T0.AddMinutes(10), Core.History.ExportResolution.Raw, false), new MemoryStream());
        svc.Dispose();

        Assert.False(Directory.Exists(rig.Dir.Path), "recording off must not create anything on disk");
    }

    [Fact]
    public async Task Records_flushes_and_reads_back_exactly()
    {
        using var rig = new Rig("basic", T0);
        var sensors = Enumerable.Range(0, 5).Select(i => rig.Hub.Add($"S{i}", (SensorKind)(i * 3 % 20))).ToList();
        var svc = rig.Open();
        svc.Configure(Rig.Settings(true, 2, sensors));
        Assert.Contains(rig.Hub.Demands, d => d.Consumer == "history" && d.Interval == TimeSpan.FromSeconds(2) && d.Ids.Length == 5);

        rig.Feed(T0, 3 * 3600, 1, (i, t) => Signals.Slow(sensors[i].Kind, i, t), interval: 2);
        Assert.Contains(rig.Dir.Files(), f => f == "raw-20261020.bvr");
        Assert.Contains(rig.Dir.Files(), f => f == "min-202610.bvm");
        Assert.Contains(rig.Dir.Files(), f => f == "catalog.bvc");

        for (var i = 0; i < sensors.Count; i++)
        {
            var truth = rig.Truth[i];
            var series = await svc.GetSeriesAsync(sensors[i].Id, T0, T0.AddHours(3), 100_000);
            Assert.Equal(truth.Count, series.Count);
            var q = Quant.Of(sensors[i].Kind);
            for (var k = 0; k < truth.Count; k++)
            {
                Assert.Equal(truth[k].T, series[k].TimeUtcTicks);
                Assert.Equal(Quant.ToFloat(truth[k].Q, q), series[k].Max);
            }
        }
        Assert.Empty(rig.Warnings);
    }

    [Fact]
    public async Task Gaps_and_absent_sensors_are_stored_as_gaps()
    {
        using var rig = new Rig("gaps", T0);
        var a = rig.Hub.Add("A", SensorKind.Temperature);
        var b = rig.Hub.Add("B", SensorKind.Temperature);
        var svc = rig.Open();
        var settings = Rig.Settings(true, 1, [a, b]);
        settings.SensorIds.Add("fake/never/0/temperature/missing");
        svc.Configure(settings);
        // B has no value in the 2nd minute
        rig.Feed(T0, 180, 1, (i, t) => i == 1 && t >= T0.AddMinutes(1).Ticks && t < T0.AddMinutes(2).Ticks ? float.NaN : 50, interval: 1);
        svc.Suspend();

        var sb = await svc.GetSeriesAsync(b.Id, T0, T0.AddMinutes(3), 1000);
        Assert.Equal(120, sb.Count);
        Assert.DoesNotContain(sb, p => p.TimeUtcTicks >= T0.AddMinutes(1).Ticks && p.TimeUtcTicks < T0.AddMinutes(2).Ticks);
        Assert.Equal(180, (await svc.GetSeriesAsync(a.Id, T0, T0.AddMinutes(3), 1000)).Count);
        Assert.Empty(await svc.GetSeriesAsync("fake/never/0/temperature/missing", T0, T0.AddMinutes(3), 1000));
        Assert.Null(svc.GetCoverage("fake/never/0/temperature/missing"));

        // engine skipped ticks inside a minute → missing rows, not shifted ones
        var t1 = T0.AddMinutes(10);
        rig.Feed(t1, 60, 5, (_, _) => 60, interval: 0);
        svc.Suspend();
        var sparse = await svc.GetSeriesAsync(a.Id, t1, t1.AddMinutes(1), 1000);
        Assert.Equal(12, sparse.Count);
        Assert.All(sparse, p => Assert.Equal(0, (p.TimeUtcTicks - t1.Ticks) % (5 * TimeSpan.TicksPerSecond)));
    }

    [Fact]
    public async Task Stale_values_of_unread_sensors_become_gaps()
    {
        using var rig = new Rig("stale", T0);
        var a = rig.Hub.Add("A", SensorKind.Temperature);
        var svc = rig.Open();
        svc.Configure(Rig.Settings(true, 1, [a]));
        var ts = T0.Ticks;
        for (var s = 0; s < 60; s++, ts += TimeSpan.TicksPerSecond)
        {
            rig.Time.Ticks = ts;
            rig.Hub.Tick(ts, [55f], [s < 10]); // read only during the first 10 s
        }
        rig.Time.Ticks = ts;
        svc.Suspend();
        var series = await svc.GetSeriesAsync(a.Id, T0, T0.AddMinutes(1), 1000);
        // limit = 2 × 1 s + 5 s after the last refresh
        Assert.InRange(series.Count, 10, 18);
    }

    [Fact]
    public async Task Torn_last_block_is_skipped_and_writing_continues()
    {
        using var rig = new Rig("torn", T0);
        var a = rig.Hub.Add("A", SensorKind.Load);
        var svc = rig.Open();
        svc.Configure(Rig.Settings(true, 1, [a]));
        rig.Feed(T0, 300, 1, (_, t) => t / TimeSpan.TicksPerSecond % 100, interval: 1);
        svc.Suspend();
        svc.Dispose();

        // simulate a power loss in the middle of the last append: cut the file inside its last block
        var raw = Path.Combine(rig.Dir.Path, "raw-20261020.bvr");
        var bytes = File.ReadAllBytes(raw);
        var blocks = new List<BlockRef>();
        BlockScanner.Scan(bytes, blocks);
        var last = blocks[^1];
        File.WriteAllBytes(raw, bytes.AsSpan(0, last.PayloadOffset + last.Header.PayloadLength / 2).ToArray());

        var svc2 = rig.Open();
        svc2.Configure(Rig.Settings(true, 1, [a]));
        var series = await svc2.GetSeriesAsync(a.Id, T0, T0.AddMinutes(5), 1000);
        Assert.Equal(240, series.Count); // the torn minute is gone, the rest is intact
        Assert.Contains(rig.Warnings, w => w.Contains("corrupted"));

        // new data appended after the garbage is readable
        var t1 = T0.AddMinutes(6);
        rig.Feed(t1, 120, 1, (_, _) => 7, interval: 1);
        svc2.Suspend();
        Assert.Equal(120, (await svc2.GetSeriesAsync(a.Id, t1, t1.AddMinutes(2), 1000)).Count);
        Assert.Equal(360, (await svc2.GetSeriesAsync(a.Id, T0, t1.AddMinutes(2), 1000)).Count);
        // whole minutes come from the minute rollups, which were written intact before the raw block got torn
        var stats = await svc2.GetStatsAsync(a.Id, T0, t1.AddMinutes(2));
        Assert.Equal(420, stats!.Value.Count);
    }

    [Fact]
    public async Task Corrupted_file_never_throws()
    {
        using var rig = new Rig("corrupt", T0);
        var a = rig.Hub.Add("A", SensorKind.Load);
        var svc = rig.Open();
        svc.Configure(Rig.Settings(true, 1, [a]));
        rig.Feed(T0, 7200, 1, (_, t) => t / TimeSpan.TicksPerSecond % 100, interval: 1);
        svc.Suspend();
        svc.Dispose();
        foreach (var f in Directory.GetFiles(rig.Dir.Path))
        {
            var bytes = File.ReadAllBytes(f);
            var rnd = new Random(f.Length);
            for (var i = 0; i < bytes.Length / 50; i++) bytes[rnd.Next(bytes.Length)] ^= (byte)rnd.Next(1, 255);
            File.WriteAllBytes(f, bytes);
        }
        File.WriteAllBytes(Path.Combine(rig.Dir.Path, "raw-20261019.bvr"), [1, 2, 3]);

        var svc2 = rig.Open();
        svc2.Configure(Rig.Settings(true, 1, [a]));
        _ = await svc2.GetSeriesAsync(a.Id, T0.AddDays(-1), T0.AddDays(1), 500);
        _ = await svc2.GetSeriesAsync(a.Id, T0, T0.AddHours(2), 100_000);
        _ = await svc2.GetStatsAsync(a.Id, T0.AddMinutes(-3), T0.AddHours(3));
        _ = svc2.GetCoverage(a.Id);
        _ = svc2.GetUsage();
        await svc2.ExportAsync(new([a.Id], T0, T0.AddHours(2), Core.History.ExportResolution.Minute, true), new MemoryStream());
    }

    [Fact]
    public async Task Crash_recovery_rebuilds_pending_rollups()
    {
        using var rig = new Rig("crash", T0);
        var a = rig.Hub.Add("A", SensorKind.Temperature);
        var svc = rig.Open();
        svc.Configure(Rig.Settings(true, 1, [a]));
        // 2.5 h: the last hour of minutes and the day of hours are only in memory when we "crash"
        rig.Feed(T0, 9000, 1, (_, t) => (float)(40 + 10 * Math.Sin(t / 1e10)), interval: 1);
        // crash: no Suspend / Dispose; the last minute (not flushed) is lost
        var flushedUntil = T0.AddSeconds(9000 - 60).Ticks;
        var expected = rig.Truth[0].Where(x => x.T < flushedUntil).ToList();

        var svc2 = rig.Open();
        svc2.Configure(Rig.Settings(true, 1, [a]));
        var stats = await svc2.GetStatsAsync(a.Id, T0, T0.AddHours(3));
        Assert.Equal(expected.Count, stats!.Value.Count);
        Assert.Equal(Quant.ToFloat(expected.Min(x => x.Q), 0.05), stats.Value.Min);
        var hourly = await svc2.GetSeriesAsync(a.Id, T0, T0.AddDays(2), 60); // 1 h buckets from hour rollups
        Assert.Equal(3, hourly.Count);
        Assert.Equal(stats.Value.Min, hourly.Min(p => p.Min));
        Assert.Equal(stats.Value.Max, hourly.Max(p => p.Max));
    }

    [Fact]
    public async Task Retention_deletes_whole_old_files_only()
    {
        using var rig = new Rig("retention", T0);
        var a = rig.Hub.Add("A", SensorKind.Load);
        var svc = rig.Open();
        var settings = Rig.Settings(true, 10, [a]);
        settings.RawRetentionDays = 2;
        settings.MinuteRetentionDays = 30;
        settings.HourRetentionDays = 0;
        svc.Configure(settings);
        var start = new DateTime(2026, 8, 30, 22, 0, 0, DateTimeKind.Utc);
        // ~2 hours on several old days
        foreach (var day in new[] { 0, 1, 20, 40, 52, 53 })
            rig.Feed(start.AddDays(day), 7200, 10, (_, _) => 42, interval: 10);
        svc.Suspend();
        var before = rig.Dir.Files();
        Assert.Contains("min-202608.bvm", before);
        Assert.Contains("raw-20260830.bvr", before);

        rig.Time.Ticks = start.AddDays(54).Ticks; // 2026-10-23 22:00
        var deleted = svc.RunRetentionNow();
        var after = rig.Dir.Files();
        Assert.True(deleted > 0);
        Assert.DoesNotContain(after, f => f.StartsWith("raw-") && string.CompareOrdinal(f, "raw-20261021.bvr") < 0);
        Assert.Contains("raw-20261021.bvr", after); // ends after the cutoff: whole files only
        Assert.Contains("raw-20261022.bvr", after);
        Assert.DoesNotContain("min-202608.bvm", after);
        Assert.Contains("min-202609.bvm", after); // ends 2026-10-01, newer than 30 days ago
        Assert.Contains("hour-2026.bvh", after); // forever
        Assert.Contains("catalog.bvc", after);

        // data past raw retention is still answered from rollups
        var stats = await svc.GetStatsAsync(a.Id, start.AddDays(20), start.AddDays(20).AddHours(2));
        Assert.Equal(42f, stats!.Value.Max);
        Assert.Equal(720, stats.Value.Count);
    }

    [Fact]
    public async Task Low_disk_stops_writing_and_resumes()
    {
        using var rig = new Rig("lowdisk", T0);
        var a = rig.Hub.Add("A", SensorKind.Load);
        rig.FreeBytes = 10 << 20; // 10 MB < 256 MB
        var svc = rig.Open();
        svc.Configure(Rig.Settings(true, 1, [a]));
        rig.Feed(T0, 180, 1, (_, _) => 3, interval: 1);
        Assert.True(svc.IsDiskLow);
        Assert.Empty(rig.Dir.Files());
        // still answered from memory
        Assert.NotEmpty(await svc.GetSeriesAsync(a.Id, T0, T0.AddMinutes(3), 1000));

        rig.FreeBytes = null;
        svc.Flush();
        Assert.False(svc.IsDiskLow);
        Assert.Contains("raw-20261020.bvr", rig.Dir.Files());
        svc.Suspend();
        Assert.Equal(180, (await svc.GetSeriesAsync(a.Id, T0, T0.AddMinutes(3), 1000)).Count);
    }

    [Fact]
    public async Task Clock_jumps_back_and_forward()
    {
        using var rig = new Rig("clock", T0);
        var a = rig.Hub.Add("A", SensorKind.Load);
        var svc = rig.Open();
        svc.Configure(Rig.Settings(true, 1, [a]));
        rig.Feed(T0, 120, 1, (_, _) => 1, interval: 1);
        rig.Feed(T0.AddSeconds(-30), 60, 1, (_, _) => 2, interval: 1); // clock set back 2.5 min
        rig.Feed(T0.AddDays(2), 60, 1, (_, _) => 3, interval: 1); // sleep / clock forward
        svc.Suspend();
        var all = await svc.GetSeriesAsync(a.Id, T0.AddMinutes(-5), T0.AddDays(3), 100_000);
        Assert.Contains(all, p => p.Max == 2);
        Assert.Contains(all, p => p.Max == 3);
        var stats = await svc.GetStatsAsync(a.Id, T0.AddMinutes(-5), T0.AddDays(3));
        Assert.Equal(240, stats!.Value.Count);
        Assert.Empty(rig.Warnings);
    }

    [Fact]
    public async Task Turning_off_flushes_and_ring_continues()
    {
        using var rig = new Rig("toggle", T0);
        var a = rig.Hub.Add("A", SensorKind.Load);
        var svc = rig.Open();
        svc.Configure(Rig.Settings(true, 1, [a]));
        using var w = svc.Watch([a.Id]);
        rig.Feed(T0.AddSeconds(30), 60, 1, (_, _) => 5, interval: 1);
        svc.Configure(Rig.Settings(false, 1, [a]));
        Assert.DoesNotContain(rig.Hub.Demands, d => d.Consumer == "history");
        var size = new FileInfo(Path.Combine(rig.Dir.Path, "raw-20261020.bvr")).Length;
        rig.Feed(T0.AddSeconds(90), 60, 1, (_, _) => 6);
        Assert.Equal(size, new FileInfo(Path.Combine(rig.Dir.Path, "raw-20261020.bvr")).Length);
        // disk part + ring part, no double counting
        var stats = await svc.GetStatsAsync(a.Id, T0, T0.AddMinutes(3));
        Assert.Equal(120, stats!.Value.Count);
        Assert.Equal(5.5f, stats.Value.Avg, 3);
        var cov = svc.GetCoverage(a.Id)!.Value;
        Assert.Equal(T0.AddSeconds(30), cov.FromUtc);
        Assert.Equal(T0.AddSeconds(149), cov.ToUtc);
    }

    [Fact]
    public async Task Timer_flushes_once_a_minute_and_runs_retention_daily()
    {
        using var rig = new Rig("timer", T0.AddSeconds(10));
        var a = rig.Hub.Add("A", SensorKind.Load);
        var svc = rig.Open(autoFlush: true);
        var settings = Rig.Settings(true, 1, [a]);
        settings.RawRetentionDays = 1;
        svc.Configure(settings);
        var timer = Assert.Single(rig.Time.Timers);
        Assert.Equal(TimeSpan.FromSeconds(52), timer.Due); // next minute boundary + 2 s

        rig.Feed(T0.AddDays(-3), 120, 1, (_, _) => 1, flush: false); // an old day, later removed by retention
        rig.Time.Ticks = T0.AddDays(-3).AddSeconds(122).Ticks;
        timer.Fire();
        Assert.Contains("raw-20261017.bvr", rig.Dir.Files());

        rig.Feed(T0, 150, 1, (_, _) => 2, flush: false);
        Assert.DoesNotContain("raw-20261020.bvr", rig.Dir.Files());
        rig.Time.Ticks = T0.AddSeconds(152).Ticks;
        timer.Fire(); // writes the two finished minutes, new day → retention
        Assert.Contains("raw-20261020.bvr", rig.Dir.Files());
        Assert.DoesNotContain("raw-20261017.bvr", rig.Dir.Files());
        Assert.True(timer.Changes >= 2);
        Assert.Equal(TimeSpan.FromSeconds(30), timer.Due);
        Assert.Equal(120, (await svc.GetSeriesAsync(a.Id, T0, T0.AddMinutes(2), 1000)).Count(p => p.Max == 2));

        svc.Configure(Rig.Settings(false, 1, [a]));
        Assert.True(timer.Disposed);
        Assert.Equal(150, (await svc.GetSeriesAsync(a.Id, T0, T0.AddMinutes(3), 1000)).Count);
    }

    [Fact]
    public async Task Clear_deletes_everything()
    {
        using var rig = new Rig("clear", T0);
        var a = rig.Hub.Add("A", SensorKind.Load);
        var svc = rig.Open();
        svc.Configure(Rig.Settings(true, 1, [a]));
        rig.Feed(T0, 600, 1, (_, _) => 5, interval: 1);
        var usage = svc.GetUsage();
        Assert.True(usage.BytesOnDisk > 0);
        Assert.Equal(T0, usage.OldestUtc);
        Assert.True(usage.EstimatedBytesPerDay > 0);
        await svc.ClearAsync();
        Assert.Empty(rig.Dir.Files());
        Assert.Empty(await svc.GetSeriesAsync(a.Id, T0, T0.AddHours(1), 100));
        // recording goes on (catalog written again)
        rig.Feed(T0.AddHours(1), 120, 1, (_, _) => 9, interval: 1);
        svc.Suspend();
        Assert.Contains("catalog.bvc", rig.Dir.Files());
        Assert.Equal(120, (await svc.GetSeriesAsync(a.Id, T0, T0.AddHours(2), 10_000)).Count);
    }

    [Fact]
    public async Task Inventory_change_adds_late_sensor()
    {
        using var rig = new Rig("late", T0);
        var a = rig.Hub.Add("A", SensorKind.Load);
        var svc = rig.Open();
        var settings = Rig.Settings(true, 1, [a]);
        settings.SensorIds.Add("fake/test/0/fps/fps");
        svc.Configure(settings);
        rig.Feed(T0, 60, 1, (_, _) => 1);
        var fps = rig.Hub.Add("FPS", SensorKind.Fps);
        Assert.Equal("fake/test/0/fps/fps", fps.Id);
        rig.Hub.RaiseInventoryChanged();
        for (var i = 0; i < 100 && svc.Recorder.Plan!.Columns < 2; i++) await Task.Delay(10);
        rig.Feed(T0.AddMinutes(1), 120, 1, (i, _) => i == 1 ? 144 : 1);
        svc.Suspend();
        Assert.Equal(120, (await svc.GetSeriesAsync(fps.Id, T0, T0.AddMinutes(5), 1000)).Count);
        Assert.Equal(180, (await svc.GetSeriesAsync(a.Id, T0, T0.AddMinutes(5), 1000)).Count);
    }
}
