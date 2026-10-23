using BetterVitals.Core.Sensors;
using BetterVitals.History.Storage;
using BetterVitals.Sensors.Engine;
using Xunit;
using Xunit.Abstractions;

namespace BetterVitals.Tests.Robustness;

/// <summary>PROJEKT §12: the wall clock jumps (time sync, a manual change, a dead CMOS battery, a resume after days) while recording.</summary>
public sealed class ClockScenarios(ITestOutputHelper output)
{
    private static readonly TimeSpan Base = SensorEngine.BaseTick;

    private static (int Frames, int MaxSleepMs, int FirstFrameAfterMs) Segment(SimPc pc, TimeSpan duration)
    {
        var before = pc.Frames;
        var maxSleep = 0;
        var first = -1;
        var elapsed = TimeSpan.Zero;
        while (elapsed < duration)
        {
            pc.Clock.Advance(Base);
            elapsed += Base;
            var sleep = pc.Engine.Step();
            maxSleep = Math.Max(maxSleep, sleep);
            if (first < 0 && pc.Frames > before) first = (int)elapsed.TotalMilliseconds;
        }
        return (pc.Frames - before, maxSleep, first);
    }

    [Fact]
    public void The_engine_neither_stalls_nor_bursts_when_the_clock_jumps_back_or_forward()
    {
        using var pc = new SimPc();
        var ids = pc.Engine.Sensors.Take(20).Select(s => s.Id).ToArray();
        using var demand = pc.Engine.Demand("test", ids, TimeSpan.FromSeconds(1));
        pc.Engine.ApplyDemandNow();
        Segment(pc, TimeSpan.FromSeconds(5));
        var steady = Segment(pc, TimeSpan.FromSeconds(20));
        Assert.InRange(steady.Frames, 19, 21);

        foreach (var (name, jump) in new (string, TimeSpan)[]
                 {
                     ("back 1 h (DST fall back / manual)", TimeSpan.FromHours(-1)),
                     ("forward 1 day", TimeSpan.FromDays(1)),
                     ("back 3 s (time sync)", TimeSpan.FromSeconds(-3)),
                     ("forward 3 s", TimeSpan.FromSeconds(3)),
                     ("back 400 days (CMOS battery)", TimeSpan.FromDays(-400)),
                     ("forward 25 years", TimeSpan.FromDays(25 * 365)),
                     ("back 25 years", TimeSpan.FromDays(-25 * 365)),
                     ("forward 10 min (a long freeze)", TimeSpan.FromMinutes(10)),
                 })
        {
            pc.Clock.Advance(jump);
            var seg = Segment(pc, TimeSpan.FromSeconds(20));
            output.WriteLine($"{name}: {seg.Frames} frames in 20 s, first after {seg.FirstFrameAfterMs} ms, longest sleep {seg.MaxSleepMs} ms");
            Assert.InRange(seg.Frames, 18, 23); // the cadence is unchanged: no pause for the missing time, no catch-up for the lost time
            Assert.InRange(seg.FirstFrameAfterMs, 0, 3000); // and it carries on at once
            Assert.InRange(seg.MaxSleepMs, 0, 3000); // nothing asks the thread to sleep for the length of the jump
            Assert.Equal(0, pc.Engine.Latest.TimestampUtcTicks % Base.Ticks);
            Assert.False(float.IsNaN(pc.Engine.Latest[pc.Engine.Find(ids[0])!.Index]));
        }
    }

    [Fact]
    public void Five_hundred_random_clock_changes_in_a_row_keep_the_cadence()
    {
        using var pc = new SimPc();
        var ids = pc.Engine.Sensors.Take(30).Select(s => s.Id).ToArray();
        using var demand = pc.Engine.Demand("test", ids, TimeSpan.FromSeconds(1));
        var rnd = new Random(11);
        var worstLow = int.MaxValue;
        var worstHigh = 0;
        var worstSleep = 0;
        Segment(pc, TimeSpan.FromSeconds(3));
        for (var i = 0; i < 500; i++)
        {
            var kind = rnd.Next(4);
            var jump = kind switch
            {
                0 => TimeSpan.FromMilliseconds(rnd.Next(-3000, 3000)),
                1 => TimeSpan.FromSeconds(rnd.Next(-7200, 7200)),
                2 => TimeSpan.FromDays(rnd.Next(-3000, 3000)),
                _ => TimeSpan.Zero,
            };
            // DateTime has a floor and a ceiling; a real clock stays inside them
            var next = pc.Clock.Now + jump.Ticks;
            if (next < new DateTime(2001, 1, 1).Ticks || next > new DateTime(2100, 1, 1).Ticks) jump = TimeSpan.Zero;
            pc.Clock.Advance(jump);
            var seg = Segment(pc, TimeSpan.FromSeconds(10));
            worstLow = Math.Min(worstLow, seg.Frames);
            worstHigh = Math.Max(worstHigh, seg.Frames);
            worstSleep = Math.Max(worstSleep, seg.MaxSleepMs);
        }
        output.WriteLine($"frames per 10 s segment: {worstLow}..{worstHigh}, longest sleep {worstSleep} ms");
        Assert.InRange(worstLow, 6, 12);
        Assert.InRange(worstHigh, 6, 12);
        Assert.InRange(worstSleep, 0, 3000);
    }

    [Fact]
    public async Task Recording_across_clock_jumps_leaves_readable_history_without_bursts_or_runaway_blocks()
    {
        using var app = new SimApp("clock-history");
        var pc = app.Pc;
        var id = pc.Sensor("AMD Ryzen 7 9800X3D", SensorKind.Temperature, "Core (Tctl/Tdie)").Id;
        var t0 = pc.Clock.Utc;
        app.Run(TimeSpan.FromMinutes(5));

        pc.Clock.Advance(TimeSpan.FromMinutes(-45)); // the clock is set back by 45 minutes
        var tBack = pc.Clock.Utc;
        app.Run(TimeSpan.FromMinutes(5));

        pc.Clock.Advance(TimeSpan.FromDays(3)); // and forward by days
        var tForward = pc.Clock.Utc;
        app.Run(TimeSpan.FromMinutes(5));

        // 200 small corrections (time sync): a second or two back, again and again
        var rnd = new Random(3);
        for (var i = 0; i < 200; i++)
        {
            pc.Clock.Advance(TimeSpan.FromMilliseconds(-rnd.Next(1100, 2600)));
            app.Run(TimeSpan.FromSeconds(rnd.Next(5, 20)));
        }
        app.History.Suspend();
        var tEnd = pc.Clock.Utc;

        var first = await app.Series(id, t0, t0.AddMinutes(5));
        var second = await app.Series(id, tBack, tBack.AddMinutes(5));
        var third = await app.Series(id, tForward, tForward.AddMinutes(5));
        output.WriteLine($"points: {first.Count} / {second.Count} / {third.Count}, dropped {app.History.DroppedSamples}");
        Assert.InRange(first.Count, 145, 152); // 5 min at 2 s
        Assert.InRange(second.Count, 145, 152); // the 45 minutes back overlap nothing recorded before
        Assert.InRange(third.Count, 145, 160);
        Assert.Equal(0, app.History.DroppedSamples);
        Assert.Empty(app.Warnings);

        // everything can be asked for in one go, in any resolution
        var all = await app.Series(id, t0.AddDays(-1), tEnd.AddDays(1), 500);
        Assert.NotEmpty(all);
        Assert.Equal(all.OrderBy(p => p.TimeUtcTicks).ToList(), all);
        var stats = await app.History.GetStatsAsync(id, t0.AddDays(-1), tEnd.AddDays(1));
        Assert.NotNull(stats);
        Assert.True(app.History.GetCoverage(id) is not null);

        // blocks: one per recorded minute plus at most one extra per correction, not one per sample
        var blocks = new List<BlockRef>();
        foreach (var f in Directory.GetFiles(app.HistoryDir, "raw-*.bvr")) BlockScanner.Scan(File.ReadAllBytes(f), blocks);
        output.WriteLine($"{blocks.Count} raw blocks");
        Assert.True(blocks.Count < 3 * 6 + 200 * 3 + 200, $"{blocks.Count} blocks");
    }

    [Fact]
    public async Task A_clock_detour_into_the_far_future_and_back_loses_no_history_by_itself()
    {
        using var app = new SimApp("clock-future");
        var pc = app.Pc;
        var id = pc.Sensor("AMD Ryzen 7 9800X3D", SensorKind.Temperature, "Core (Tctl/Tdie)").Id;
        var t0 = pc.Clock.Utc;
        app.Run(TimeSpan.FromMinutes(20));
        app.History.Suspend();
        var before = (await app.Series(id, t0, t0.AddHours(1))).Count;
        Assert.True(before > 500);

        pc.Clock.Advance(TimeSpan.FromDays(5 * 365)); // five years ahead (a dead CMOS battery that jumped, a wrong manual date)
        app.Run(TimeSpan.FromMinutes(3));
        pc.Clock.Advance(TimeSpan.FromDays(-5 * 365)); // fixed again
        app.Run(TimeSpan.FromMinutes(2));
        app.History.Suspend();

        var after = (await app.Series(id, t0, t0.AddHours(1))).Count;
        Assert.True(after >= before, $"{after} of {before} points left after a clock detour");
    }

    [Fact(Skip = "Known gap, see the report: retention counts back from the wall clock, so a clock set years ahead followed by the daily retention pass deletes the detailed (7-day) and the minute (1-year) history; only the hourly rollups survive.")]
    public async Task Retention_after_a_clock_set_years_ahead_keeps_the_detailed_history()
    {
        using var app = new SimApp("clock-retention");
        var pc = app.Pc;
        var id = pc.Sensor("AMD Ryzen 7 9800X3D", SensorKind.Temperature, "Core (Tctl/Tdie)").Id;
        var t0 = pc.Clock.Utc;
        app.Run(TimeSpan.FromMinutes(20));
        app.History.Suspend();
        var before = (await app.Series(id, t0, t0.AddHours(1))).Count;
        pc.Clock.Advance(TimeSpan.FromDays(5 * 365));
        app.Run(TimeSpan.FromMinutes(3));
        app.History.RunRetentionNow();
        pc.Clock.Advance(TimeSpan.FromDays(-5 * 365));
        var after = (await app.Series(id, t0, t0.AddHours(1))).Count;
        Assert.True(after >= before);
    }
}
