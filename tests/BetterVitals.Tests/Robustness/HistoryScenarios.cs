using BetterVitals.Core.History;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.History;
using Xunit;
using Xunit.Abstractions;

namespace BetterVitals.Tests.Robustness;

/// <summary>PROJEKT §12: damaged history files, an unusable history folder, a disk that fills up.</summary>
public sealed class HistoryScenarios(ITestOutputHelper output)
{
    private static HistoryOptions Options(SimClock clock, List<string> warnings) => new()
    {
        Time = new SimTime(clock),
        AutoFlush = false,
        BackgroundStart = false,
        FreeSpace = _ => null,
        Warn = m =>
        {
            lock (warnings) warnings.Add(m);
        },
    };

    private static void CopyDirectory(string from, string to)
    {
        if (Directory.Exists(to)) Directory.Delete(to, recursive: true);
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
    }

    private static void Damage(string dir, Random rnd, List<string> what)
    {
        var files = Directory.GetFiles(dir).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        var file = files[rnd.Next(files.Length)];
        var name = Path.GetFileName(file);
        var bytes = File.ReadAllBytes(file);
        switch (rnd.Next(12))
        {
            case 0:
                File.WriteAllBytes(file, bytes.AsSpan(0, rnd.Next(bytes.Length + 1)).ToArray());
                what.Add($"truncate {name}");
                break;
            case 1:
                for (var i = rnd.Next(1, 200); i > 0; i--) bytes[rnd.Next(bytes.Length)] ^= (byte)rnd.Next(1, 256);
                File.WriteAllBytes(file, bytes);
                what.Add($"bit rot {name}");
                break;
            case 2:
            {
                var a = rnd.Next(bytes.Length);
                var n = Math.Min(rnd.Next(1, 4096), bytes.Length - a);
                rnd.NextBytes(bytes.AsSpan(a, n));
                File.WriteAllBytes(file, bytes);
                what.Add($"random range {name}");
                break;
            }
            case 3:
            {
                var a = rnd.Next(bytes.Length);
                Array.Clear(bytes, a, Math.Min(rnd.Next(1, 8192), bytes.Length - a));
                File.WriteAllBytes(file, bytes);
                what.Add($"zero range {name}");
                break;
            }
            case 4:
            {
                var junk = new byte[rnd.Next(0, 5000)];
                rnd.NextBytes(junk);
                File.WriteAllBytes(file, junk);
                what.Add($"random content {name}");
                break;
            }
            case 5:
                File.Delete(file);
                what.Add($"delete {name}");
                break;
            case 6:
                File.WriteAllBytes(file, []);
                what.Add($"empty {name}");
                break;
            case 7:
            {
                var junk = new byte[rnd.Next(1, 3000)];
                rnd.NextBytes(junk);
                using var fs = new FileStream(file, FileMode.Append);
                fs.Write(junk);
                what.Add($"garbage appended to {name}");
                break;
            }
            case 8:
            {
                // a good file under another history name (a restored backup, a copy)
                File.WriteAllBytes(Path.Combine(dir, rnd.Next(2) == 0 ? "raw-20240101.bvr" : "min-202401.bvm"), bytes);
                what.Add($"misplaced copy of {name}");
                break;
            }
            case 9:
            {
                // a block header repeated over the middle of a file
                if (bytes.Length > 200)
                {
                    var a = rnd.Next(bytes.Length - 100);
                    bytes.AsSpan(0, 64).CopyTo(bytes.AsSpan(a, 64));
                    File.WriteAllBytes(file, bytes);
                }
                what.Add($"header pasted into {name}");
                break;
            }
            case 10:
                // the file name of today's block is taken by a folder
                Directory.CreateDirectory(Path.Combine(dir, "raw-20261024.bvr"));
                what.Add("folder named like a raw file");
                break;
            default:
                File.WriteAllBytes(Path.Combine(dir, "catalog.bvc"), bytes.Length > 50 ? bytes.AsSpan(0, bytes.Length / 2).ToArray() : []);
                what.Add("catalog cut");
                break;
        }
    }

    private static async Task Query(HistoryService svc, string[] ids, DateTime t0, DateTime t1)
    {
        foreach (var id in ids)
        {
            foreach (var max in new[] { 3, 120, 100_000 })
            {
                var series = await svc.GetSeriesAsync(id, t0.AddDays(-2), t1.AddDays(1), max);
                long last = long.MinValue;
                foreach (var p in series)
                {
                    Assert.True(p.TimeUtcTicks > last, "points in time order");
                    last = p.TimeUtcTicks;
                    Assert.False(float.IsNaN(p.Min) || float.IsNaN(p.Avg) || float.IsNaN(p.Max));
                    Assert.True(p.Min <= p.Avg + 1e-3 && p.Avg <= p.Max + 1e-3, $"min {p.Min} avg {p.Avg} max {p.Max}");
                }
            }
            var stats = await svc.GetStatsAsync(id, t0, t1);
            if (stats is { } st) Assert.True(st.Min <= st.Max && st.Count > 0);
            _ = await svc.GetStatsAsync(id, t0.AddMinutes(37), t0.AddMinutes(38));
            _ = svc.GetCoverage(id);
        }
        _ = svc.GetUsage();
        foreach (var res in Enum.GetValues<ExportResolution>())
        {
            var request = new ExportRequest(ids, t0.AddDays(-1), t1.AddDays(1), res, Zip: res == ExportResolution.Hour);
            _ = svc.EstimateExportBytes(request);
            await svc.ExportAsync(request, new MemoryStream());
        }
    }

    [Fact]
    public async Task Damaged_history_files_never_make_a_query_throw_and_recording_carries_on()
    {
        using var app = new SimApp("hist-fuzz");
        var pc = app.Pc;
        var ids = app.Recording.SensorIds.Order().Take(6).ToArray();
        Assert.NotEmpty(ids);
        var t0 = pc.Clock.Utc;
        app.Run(TimeSpan.FromHours(2));
        app.History.Suspend();
        var t1 = pc.Clock.Utc;
        var pristine = app.HistoryDir;
        var baseline = await app.Series(ids[0], t0, t1);
        Assert.True(baseline.Count > 3000);
        app.History.Configure(new RecordingSettings { Enabled = false }); // the original service stops; copies are tested below
        var rnd = new Random(20261023);
        var iterations = 0;
        var recovered = 0;

        for (; iterations < 40; iterations++)
        {
            var dir = Path.Combine(app.Dir.Path, "fuzz");
            CopyDirectory(pristine, dir);
            var what = new List<string>();
            for (var i = rnd.Next(1, 4); i > 0; i--) Damage(dir, rnd, what);
            var label = string.Join("; ", what);
            var warnings = new List<string>();
            try
            {
                using var svc = new HistoryService(pc.Engine, dir, Options(pc.Clock, warnings));
                await Query(svc, ids, t0, t1);

                // the app starts recording again over the damaged folder
                var rec = new RecordingSettings { Enabled = true, IntervalSeconds = 2, SensorIds = [.. app.Recording.SensorIds] };
                svc.Configure(rec);
                var tNew = pc.Clock.Utc;
                var step = TimeSpan.FromSeconds(1);
                var end = pc.Clock.Now + TimeSpan.FromMinutes(4).Ticks;
                var nextFlush = (pc.Clock.Now / TimeSpan.TicksPerMinute + 1) * TimeSpan.TicksPerMinute + 2 * TimeSpan.TicksPerSecond;
                while (pc.Clock.Now < end)
                {
                    pc.Clock.Advance(step);
                    pc.Engine.Step();
                    if (pc.Clock.Now >= nextFlush)
                    {
                        svc.Flush();
                        nextFlush += TimeSpan.TicksPerMinute;
                    }
                }
                svc.Suspend();
                await Query(svc, ids, t0, pc.Clock.Utc);
                var fresh = await svc.GetSeriesAsync(ids[0], tNew, pc.Clock.Utc, 100_000);
                var blockedByFolder = what.Any(w => w.StartsWith("folder named", StringComparison.Ordinal));
                if (fresh.Count >= 100) recovered++;
                else Assert.True(blockedByFolder || warnings.Count > 0, $"new samples vanished without a word: {fresh.Count} points");

                await svc.ClearAsync();
                Assert.Empty(await svc.GetSeriesAsync(ids[0], t0, pc.Clock.Utc, 1000));
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                throw new Xunit.Sdk.XunitException($"iteration {iterations} [{label}] threw {ex}");
            }
            catch (Xunit.Sdk.XunitException ex)
            {
                throw new Xunit.Sdk.XunitException($"iteration {iterations} [{label}]: {ex.Message}", ex);
            }
        }
        output.WriteLine($"{iterations} damaged copies survived, {recovered} of them recorded new data that could be read back");
        Assert.True(recovered > iterations * 0.6, $"only {recovered} of {iterations}");
    }

    [Fact]
    public async Task An_untouched_copy_answers_exactly_like_the_original()
    {
        using var app = new SimApp("hist-copy");
        var pc = app.Pc;
        var id = app.Recording.SensorIds.Order().First();
        var t0 = pc.Clock.Utc;
        app.Run(TimeSpan.FromMinutes(90));
        app.History.Suspend();
        var t1 = pc.Clock.Utc;
        var copy = Path.Combine(app.Dir.Path, "copy");
        CopyDirectory(app.HistoryDir, copy);
        using var other = new HistoryService(pc.Engine, copy, Options(pc.Clock, []));
        var a = await app.Series(id, t0, t1);
        var b = await other.GetSeriesAsync(id, t0, t1, 100_000);
        Assert.Equal(a, b);
        Assert.Equal(await app.History.GetStatsAsync(id, t0, t1), await other.GetStatsAsync(id, t0, t1));
    }

    [Fact]
    public async Task An_unusable_history_folder_for_an_hour_keeps_memory_bounded_and_recording_recovers()
    {
        using var pc = new SimPc();
        using var dir = new QaDir("hist-blocked");
        var path = dir.File_("history");
        File.WriteAllText(path, "not a folder"); // something else sits where the history folder should be
        var warnings = new List<string>();
        using var svc = new HistoryService(pc.Engine, path, Options(pc.Clock, warnings));
        var ids = SimApp.KeySensors(pc).ToArray();
        svc.Configure(new RecordingSettings { Enabled = true, IntervalSeconds = 2, SensorIds = [.. ids] });
        var t0 = pc.Clock.Utc;
        var nextFlush = (pc.Clock.Now / TimeSpan.TicksPerMinute + 1) * TimeSpan.TicksPerMinute + 2 * TimeSpan.TicksPerSecond;

        void Run(TimeSpan d)
        {
            var end = pc.Clock.Now + d.Ticks;
            while (pc.Clock.Now < end)
            {
                pc.Clock.Advance(TimeSpan.FromSeconds(1));
                pc.Engine.Step();
                if (pc.Clock.Now < nextFlush) continue;
                svc.Flush();
                nextFlush += TimeSpan.TicksPerMinute;
            }
        }

        Run(TimeSpan.FromMinutes(5));
        var before = GC.GetTotalMemory(true);
        Run(TimeSpan.FromMinutes(60));
        var after = GC.GetTotalMemory(true);
        output.WriteLine($"dropped samples {svc.DroppedSamples}, memory {before >> 10} KB -> {after >> 10} KB, warnings {warnings.Count}");
        Assert.True(svc.DroppedSamples > 0, "the oldest minutes are dropped once the buffers are full");
        Assert.True(after - before < 2 << 20, "memory stays flat while nothing can be written");
        Assert.InRange(warnings.Count, 1, 10); // said, but not flooded

        File.Delete(path); // the obstacle is gone
        Run(TimeSpan.FromMinutes(3));
        svc.Flush();
        var series = await svc.GetSeriesAsync(ids[0], pc.Clock.Utc.AddMinutes(-20), pc.Clock.Utc, 100_000);
        // the recorder keeps a few minutes in memory while it cannot write (a pool of 4 minute buffers): those and the new ones arrive
        output.WriteLine($"{series.Count} of the last 20 minutes are on disk (30 points per minute)");
        Assert.True(series.Count >= 3 * 30 + 60, $"{series.Count} points of the last 20 minutes");
        Assert.True(Directory.Exists(path));
        _ = t0;
    }
}
