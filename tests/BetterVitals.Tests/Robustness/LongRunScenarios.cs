using System.Diagnostics;
using BetterVitals.Core.Catalog;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using Xunit;
using Xunit.Abstractions;

namespace BetterVitals.Tests.Robustness;

/// <summary>PROJEKT §6 / §11: the app runs for days. A simulated day through the real engine and the real history.</summary>
[Collection(HeavyCollection.Name)]
public sealed class LongRunScenarios(ITestOutputHelper output)
{
    [Fact]
    public async Task A_simulated_day_keeps_memory_flat_and_disk_use_close_to_the_estimate()
    {
        using var app = new SimApp("long-day", intervalSeconds: 2,
            sensors: pc => pc.Engine.Sensors.Where(s => s.HardwareKind != HardwareKind.Game).Select(s => s.Id));
        var pc = app.Pc;
        var cpu = pc.Sensor("AMD Ryzen 7 9800X3D", SensorKind.Temperature, "Core (Tctl/Tdie)");
        var gpuHw = pc.HardwareId("NVIDIA GeForce RTX 5080");
        var recorded = app.Recording.SensorIds.Count;
        var t0 = pc.Clock.Utc;

        // what the open windows ask for, now and then
        var overlayIds = pc.Engine.Sensors.Where(s => s.HardwareKind is HardwareKind.Cpu or HardwareKind.Gpu && s.Kind == SensorKind.Temperature).Take(4).Select(s => s.Id).ToArray();
        using var overlay = pc.Engine.Demand("overlay", overlayIds, TimeSpan.FromSeconds(1));
        IDisposable? watch = null;
        IDemand? panel = null;

        var memory = new List<(int Hour, long Bytes)>();
        var ticks = 0L;
        pc.Engine.FrameReady += _ => ticks++;
        var sw = Stopwatch.StartNew();

        for (var hour = 0; hour < 24; hour++)
        {
            if (hour == 3)
            {
                app.Sleep(); // the PC sleeps for two hours
                pc.Clock.Advance(TimeSpan.FromHours(2));
                app.Wake();
            }
            if (hour % 4 == 2) pc.Fake.SetPresent(gpuHw, false); // the GPU "vanishes" for an hour, every 4 h
            if (hour % 4 == 3) pc.Fake.SetPresent(gpuHw, true);
            if (hour == 7) watch = app.History.Watch(overlayIds); // the History page is open for a while
            if (hour == 10)
            {
                watch?.Dispose();
                watch = null;
            }
            if (hour % 6 == 5) panel = pc.Engine.Demand("overview", pc.Engine.Sensors.Take(60).Select(s => s.Id), TimeSpan.FromSeconds(1)); // the panel
            if (hour % 6 == 0 && panel is not null)
            {
                panel.Dispose();
                panel = null;
            }

            app.Run(TimeSpan.FromHours(1));

            if (hour is 0 or 3 or 7 or 11 or 15 or 19 or 23)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                memory.Add((hour, GC.GetTotalMemory(true)));
            }
        }
        panel?.Dispose();
        app.History.Suspend();
        var usage = app.History.GetUsage();
        var files = Directory.GetFiles(app.HistoryDir).Select(f => new FileInfo(f)).ToArray();
        var diskBytes = files.Sum(f => f.Length);
        var simulated = pc.Clock.Utc - t0;
        output.WriteLine($"simulated {simulated.TotalHours:F1} h ({ticks} frames) in {sw.Elapsed.TotalSeconds:F1} s");
        output.WriteLine("memory (KB): " + string.Join(", ", memory.Select(m => $"h{m.Hour}={m.Bytes >> 10}")));
        output.WriteLine($"{recorded} sensors recorded; on disk {diskBytes / 1048576.0:F2} MB in {files.Length} files; estimate per day {usage.EstimatedBytesPerDay / 1048576.0:F2} MB; UI estimate {RecordingPolicy.EstimateBytesPerDay(recorded, 2) / 1048576.0:F2} MB");
        foreach (var f in files.OrderBy(f => f.Name)) output.WriteLine($"  {f.Name} {f.Length >> 10} KB");

        // memory: flat after the first hours (a day of 2 s samples, sleeps, unplugging and windows opening must not leave anything behind)
        var warm = memory.First(m => m.Hour == 3).Bytes;
        var end = memory[^1].Bytes;
        Assert.True(end - warm < 3 << 20, $"managed heap grew from {warm >> 10} KB to {end >> 10} KB");
        Assert.True(memory.Max(m => m.Bytes) - warm < 6 << 20);
        Assert.Equal(0, app.History.DroppedSamples);
        Assert.Empty(app.Warnings);
        Assert.True(app.History.LiveBufferBytes < 2 << 20);

        // disk: the estimate the UI shows is about what a day really costs (24 h were recorded; the PC also slept for 2 more)
        Assert.InRange(diskBytes / (double)usage.EstimatedBytesPerDay, 0.6, 1.6);
        Assert.InRange(diskBytes / 1048576.0, 0.3, 40);
        // the figure the Sensors page shows (a fixed 0.4 B per sample) is a guess: for the jittery fake sensors a day costs about 3x that, never 10x
        Assert.InRange(diskBytes / (double)RecordingPolicy.EstimateBytesPerDay(recorded, 2), 0.5, 6);
        Assert.Equal(diskBytes, usage.BytesOnDisk);

        // the day is all there: two raw files (the UTC day changed), minute and hour rollups, a catalog
        Assert.Equal(2, files.Count(f => f.Name.StartsWith("raw-", StringComparison.Ordinal)));
        Assert.Contains(files, f => f.Name.StartsWith("min-", StringComparison.Ordinal));
        Assert.Contains(files, f => f.Name.StartsWith("hour-", StringComparison.Ordinal));
        var points = await app.Series(cpu.Id, t0, pc.Clock.Utc, 100_000);
        Assert.InRange(points.Count, 24 * 1800 - 250, 24 * 1800 + 50);
        var day = await app.Series(cpu.Id, t0, pc.Clock.Utc, 200);
        Assert.InRange(day.Count, 60, 200);
        var stats = await app.History.GetStatsAsync(cpu.Id, t0, pc.Clock.Utc);
        Assert.NotNull(stats);
        Assert.InRange(stats.Value.Count, 24 * 1800 - 250, 24 * 1800 + 50);
    }
}
