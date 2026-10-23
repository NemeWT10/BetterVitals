using BetterVitals.Core.Sensors;
using BetterVitals.History;
using BetterVitals.History.Storage;
using BetterVitals.Sensors.Engine;
using Xunit;
using Xunit.Abstractions;

namespace BetterVitals.Tests.Robustness;

/// <summary>PROJEKT §12: sleep / hibernate / resume. The real engine and history on a simulated clock, wired like AppHost wires them.</summary>
public sealed class PowerScenarios(ITestOutputHelper output)
{
    private const string Cpu = "AMD Ryzen 7 9800X3D";
    private const string Gpu = "NVIDIA GeForce RTX 5080";

    [Fact]
    public async Task Sleep_pauses_the_engine_resume_restarts_it_history_gets_a_gap_and_a_new_block()
    {
        using var log = new LogCapture();
        using var app = new SimApp("power-sleep");
        var pc = app.Pc;
        var cpuTemp = pc.Sensor(Cpu, SensorKind.Temperature, "Core (Tctl/Tdie)");
        var gpuTemp = pc.Sensor(Gpu, SensorKind.Temperature, "GPU Core");
        Assert.Contains(cpuTemp.Id, app.Recording.SensorIds);
        Assert.Contains(gpuTemp.Id, app.Recording.SensorIds);

        var t0 = pc.Clock.Utc;
        app.Run(TimeSpan.FromSeconds(185)); // 3 minutes and a bit: the 4th minute is open
        var tSleep = pc.Clock.Utc;
        var reads = pc.Fake.TotalReads;
        var frames = pc.Frames;
        Assert.True(frames > 80);

        // ── sleep: flush everything, pause. The engine thread would sit in its wait; the clock keeps running underneath. ──
        app.Sleep();
        pc.Run(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(1));
        Assert.Equal(reads, pc.Fake.TotalReads); // not a single read while suspended
        Assert.Equal(frames, pc.Frames);
        Assert.Equal(1, pc.Source.Suspends);
        Assert.Equal(0, pc.Source.Resumes);

        // the open minute reached the disk at once (sleep may turn into a power cut: hibernate, battery)
        var copy = Path.Combine(app.Dir.Path, "copy-at-sleep");
        CopyDirectory(app.HistoryDir, copy);
        Assert.NotEmpty(Directory.GetFiles(copy, "raw-*.bvr"));

        // ── resume 8 hours later ──
        pc.Clock.Advance(TimeSpan.FromHours(8));
        var tWake = pc.Clock.Utc;
        app.Wake();
        var framesBefore = pc.Frames;
        app.Run(TimeSpan.FromSeconds(125));

        Assert.Equal(1, pc.Source.Resumes);
        Assert.True(pc.Source.Discovers >= 2, "the sources re-discover after resume");
        var afterFrames = pc.Frames - framesBefore;
        // two minutes of a 2 s schedule: ~62 frames; a catch-up for the 8 lost hours would be thousands
        Assert.InRange(afterFrames, 55, 70);
        Assert.False(float.IsNaN(pc.Engine.Latest[cpuTemp.Index]));
        Assert.False(float.IsNaN(pc.Engine.Latest[gpuTemp.Index]));
        Assert.True(pc.Engine.Latest.TimestampUtc >= tWake);

        // ── history: data before sleep, nothing during, data after ──
        app.History.Suspend();
        var series = await app.Series(cpuTemp.Id, t0, tWake.AddMinutes(5));
        var before = series.Where(p => p.TimeUtcTicks < tSleep.Ticks).ToList();
        var after = series.Where(p => p.TimeUtcTicks >= tWake.Ticks).ToList();
        Assert.Equal(series.Count, before.Count + after.Count); // nothing in the sleep gap
        Assert.InRange(before.Count, 85, 95); // 185 s at 2 s
        Assert.InRange(after.Count, 55, 65);
        Assert.InRange(after[0].TimeUtcTicks - tWake.Ticks, 0, 3 * TimeSpan.TicksPerSecond); // recording is back within a tick or two
        var sleepAt = tSleep.Ticks;
        Assert.True(before[^1].TimeUtcTicks > sleepAt - 10 * TimeSpan.TicksPerSecond, "the partial minute before sleep was written, not lost");

        // the sleep gap has no block spanning it; the first block after resume starts after the wake-up
        var raw = Directory.GetFiles(app.HistoryDir, "raw-*.bvr").Order().Select(File.ReadAllBytes);
        var blocks = new List<BlockRef>();
        foreach (var bytes in raw) BlockScanner.Scan(bytes, blocks);
        output.WriteLine($"before sleep {before.Count} points, after resume {after.Count}, frames after wake {afterFrames}, blocks {blocks.Count}");
        Assert.DoesNotContain(blocks, b => b.Header.Start < sleepAt && b.Header.End > tWake.Ticks);
        Assert.Contains(blocks, b => b.Header.Start >= tWake.Ticks - TimeSpan.TicksPerSecond);
        Assert.Contains(blocks, b => b.Header.End <= tSleep.AddSeconds(2).Ticks && b.Header.Start >= tSleep.AddMinutes(-1).Ticks);

        // nothing complained
        Assert.Empty(app.Warnings);
        Assert.DoesNotContain(log.Mine, l => l.Level == "ERROR");

        // the copy made at sleep time holds every sample up to then
        var asleep = await QueryCopy(copy, pc, cpuTemp.Id, t0, tSleep.AddSeconds(1));
        Assert.Equal(before.Count, asleep.Count(p => p.TimeUtcTicks < tSleep.Ticks));
    }

    [Fact]
    public async Task Hardware_changed_during_sleep_is_seen_after_resume()
    {
        using var app = new SimApp("power-hw");
        var pc = app.Pc;
        var gpuTemp = pc.Sensor(Gpu, SensorKind.Temperature, "GPU Core");
        var cpuTemp = pc.Sensor(Cpu, SensorKind.Temperature, "Core (Tctl/Tdie)");
        var slots = pc.Slots();
        var gpuHw = pc.HardwareId(Gpu);

        app.Run(TimeSpan.FromMinutes(2));
        app.Sleep();
        pc.Fake.SetPresent(gpuHw, false); // the eGPU was unplugged while the lid was closed
        pc.Run(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1));
        Assert.True(pc.Engine.IsPresent(gpuTemp.Index), "nothing is looked at while suspended");

        pc.Clock.Advance(TimeSpan.FromHours(1));
        var tWake = pc.Clock.Utc;
        app.Wake();
        app.Run(TimeSpan.FromSeconds(70));

        Assert.False(pc.Engine.IsPresent(gpuTemp.Index));
        Assert.True(float.IsNaN(pc.Engine.Latest[gpuTemp.Index]));
        Assert.True(pc.Engine.IsPresent(cpuTemp.Index));
        Assert.False(float.IsNaN(pc.Engine.Latest[cpuTemp.Index]));
        Assert.Equal(slots, pc.Slots().Where(kv => slots.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value)); // slots unchanged
        app.History.Suspend();
        Assert.Empty(await app.Series(gpuTemp.Id, tWake, tWake.AddMinutes(2)));
        Assert.NotEmpty(await app.Series(cpuTemp.Id, tWake, tWake.AddMinutes(2)));

        // and it comes back: plugged in during the next sleep
        app.Sleep();
        pc.Fake.SetPresent(gpuHw, true);
        pc.Clock.Advance(TimeSpan.FromMinutes(30));
        var tBack = pc.Clock.Utc;
        app.Wake();
        app.Run(TimeSpan.FromSeconds(70));
        Assert.True(pc.Engine.IsPresent(gpuTemp.Index));
        Assert.False(float.IsNaN(pc.Engine.Latest[gpuTemp.Index]));
        app.History.Suspend();
        Assert.NotEmpty(await app.Series(gpuTemp.Id, tBack, tBack.AddMinutes(2)));
        Assert.Empty(app.Warnings);
    }

    [Fact]
    public void Duplicate_and_unmatched_power_notifications_are_harmless()
    {
        using var app = new SimApp("power-dup");
        var pc = app.Pc;
        app.Run(TimeSpan.FromSeconds(30));

        // Windows can report resume twice (RESUMEAUTOMATIC + RESUMESUSPEND), a resume with no suspend, a suspend twice
        pc.Engine.NotifyResume();
        app.Run(TimeSpan.FromSeconds(10));
        Assert.Equal(0, pc.Source.Resumes);
        Assert.Equal(0, pc.Source.Suspends);

        pc.Engine.NotifySuspend();
        pc.Engine.NotifySuspend();
        pc.Run(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
        Assert.Equal(1, pc.Source.Suspends);
        var reads = pc.Fake.TotalReads;
        pc.Run(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1));
        Assert.Equal(reads, pc.Fake.TotalReads);

        pc.Engine.NotifyResume();
        pc.Engine.NotifyResume();
        app.Run(TimeSpan.FromSeconds(10));
        Assert.Equal(1, pc.Source.Resumes);
        Assert.True(pc.Fake.TotalReads > reads);

        // sleep right after resume, many times (a laptop that wakes for a second and goes back to sleep)
        for (var i = 0; i < 20; i++)
        {
            app.Sleep();
            app.Wake();
            app.Run(TimeSpan.FromSeconds(2));
        }
        Assert.Equal(21, pc.Source.Suspends);
        Assert.Equal(21, pc.Source.Resumes);
        Assert.Empty(app.Warnings);
    }

    [Fact]
    public async Task Recording_switched_off_before_sleep_writes_nothing_after_resume()
    {
        using var app = new SimApp("power-off", record: false);
        app.Run(TimeSpan.FromMinutes(2));
        app.Sleep();
        app.Pc.Clock.Advance(TimeSpan.FromHours(2));
        app.Wake();
        app.Run(TimeSpan.FromMinutes(2));
        Assert.False(Directory.Exists(app.HistoryDir), "live-only mode never writes history");
        Assert.False(app.History.IsRecording);
        var id = app.Pc.Sensor(Cpu, SensorKind.Temperature, "Core (Tctl/Tdie)").Id;
        using var watch = app.History.Watch([id]);
        app.Run(TimeSpan.FromSeconds(30));
        Assert.NotEmpty(await app.Series(id, app.Pc.Clock.Utc.AddMinutes(-1), app.Pc.Clock.Utc.AddMinutes(1)));
    }

    private static async Task<IReadOnlyList<Core.History.HistoryPoint>> QueryCopy(string dir, SimPc pc, string id, DateTime from, DateTime to)
    {
        using var reader = new HistoryService(pc.Engine, dir, new HistoryOptions { Time = new SimTime(pc.Clock), AutoFlush = false, BackgroundStart = false });
        return await reader.GetSeriesAsync(id, from, to, 100_000);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
    }
}
