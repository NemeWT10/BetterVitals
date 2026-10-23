using System.Runtime.InteropServices;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Platform;
using BetterVitals.Platform.Updates;
using Xunit;
using Xunit.Abstractions;

namespace BetterVitals.Tests.Robustness;

/// <summary>PROJEKT §10 / §12: update timing, the anti-cheat guard and the Windows notifications, on simulated inputs.</summary>
public sealed class PlatformScenarios(ITestOutputHelper output)
{
    // ───────────────────────── updates ─────────────────────────

    [Fact]
    public void The_update_decision_holds_its_promises_for_any_input()
    {
        var rnd = new Random(2026);
        var origin = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime Any() => origin.AddSeconds(rnd.NextInt64(-86_400L * 400, 86_400L * 400));
        for (var i = 0; i < 50_000; i++)
        {
            var s = new UpdateSettings
            {
                Mode = (UpdateMode)rnd.Next(0, 4), // the last one is not a defined mode (a hand-edited 3)
                LastCheckUtc = rnd.Next(4) == 0 ? null : Any(),
            };
            var now = Any();
            var start = now.AddSeconds(-rnd.Next(0, 3 * 86_400));
            bool game = rnd.Next(3) == 0, metered = rnd.Next(3) == 0, manual = rnd.Next(3) == 0;
            var may = UpdateScheduler.MayCheck(s, now, start, game, metered, manual);

            if (game) Assert.False(may, "never while a game is in front, not even for 'check now'");
            if (manual && !game) Assert.True(may);
            if (!manual && s.Mode == UpdateMode.Off) Assert.False(may);
            if (!manual && metered) Assert.False(may);
            if (!manual && !game && !metered && s.Mode != UpdateMode.Off && now - start < UpdateScheduler.FirstCheckDelay) Assert.False(may, "not right after start");
            if (may && !manual && s.LastCheckUtc is { } last && last <= now) Assert.True(now - last >= UpdateScheduler.Interval, "at most once a day");

            var delay = UpdateScheduler.NextDelay(s, now, start, rnd.Next(2) == 0);
            if (s.Mode == UpdateMode.Off) Assert.Equal(Timeout.InfiniteTimeSpan, delay);
            else Assert.InRange(delay, TimeSpan.FromSeconds(1), UpdateScheduler.Interval + UpdateScheduler.FirstCheckDelay + TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public void A_month_of_ticking_the_update_timer_checks_about_once_a_day_never_in_a_game_and_survives_clock_jumps()
    {
        var rnd = new Random(8);
        var start = new DateTime(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc);
        var now = start;
        var settings = new UpdateSettings { Mode = UpdateMode.Ask };
        var checks = new List<DateTime>();
        var skipped = false;
        var steps = 0;
        while (now < start.AddDays(30) && steps++ < 100_000)
        {
            var delay = UpdateScheduler.NextDelay(settings, now, start, skipped);
            Assert.NotEqual(Timeout.InfiniteTimeSpan, delay);
            now += delay;
            if (rnd.Next(40) == 0) now = now.AddHours(rnd.Next(-5, 5)); // the clock moves now and then
            var game = rnd.Next(4) == 0;
            var metered = rnd.Next(6) == 0;
            if (UpdateScheduler.MayCheck(settings, now, start, game, metered, manual: false))
            {
                Assert.False(game);
                checks.Add(now);
                settings.LastCheckUtc = now;
                skipped = false;
            }
            else
            {
                skipped = true;
            }
        }
        output.WriteLine($"{checks.Count} checks in 30 days after {steps} timer ticks");
        Assert.InRange(checks.Count, 25, 40);
        Assert.True(steps < 20_000, "the timer does not spin");
        Assert.True(checks[0] - start >= UpdateScheduler.FirstCheckDelay);
        for (var i = 1; i < checks.Count; i++)
            if (checks[i] > checks[i - 1]) Assert.True(checks[i] - checks[i - 1] >= TimeSpan.FromHours(23), $"check {i}: {checks[i] - checks[i - 1]} after the previous one");
    }

    // ───────────────────────── anti-cheat guard ─────────────────────────

    [Fact]
    public void The_anti_cheat_guard_follows_foreground_changes_exactly_and_announces_each_flip_once()
    {
        var rnd = new Random(3);
        string? running = null;
        var guard = new AntiCheatGuard(() => running);
        var flips = new List<bool>();
        guard.PausedChanged += flips.Add;
        var expected = false;
        var expectedFlips = 0;
        for (var i = 0; i < 20_000; i++)
        {
            var setting = rnd.Next(4) != 0;
            var game = rnd.Next(2) == 0;
            running = rnd.Next(3) == 0 ? null : (rnd.Next(2) == 0 ? "vgc.exe" : "EasyAntiCheat.exe");
            guard.Evaluate(setting, game);
            var want = setting && game && running is not null;
            if (want != expected) expectedFlips++;
            expected = want;
            Assert.Equal(want, guard.Paused);
            Assert.Equal(want ? running : null, guard.Reason);
        }
        Assert.Equal(expectedFlips, flips.Count);
        for (var i = 1; i < flips.Count; i++) Assert.NotEqual(flips[i - 1], flips[i]); // strictly alternating
    }

    // ───────────────────────── Windows notifications ─────────────────────────

    private const uint WmPowerBroadcast = 0x0218, WmDeviceChange = 0x0219, WmDisplayChange = 0x007E, WmSessionChange = 0x02B1, WmEndSession = 0x0016;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowExW(nint parent, nint after, string? cls, string? title);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    /// <summary>Sends a message to this process's own SystemEvents windows only (the user's running copy has one too).</summary>
    private static void Send(uint msg, nint wParam, nint lParam = 0)
    {
        nint h = 0;
        var sent = 0;
        while ((h = FindWindowExW(0, h, "BetterVitals.SystemEvents", null)) != 0)
        {
            GetWindowThreadProcessId(h, out var pid);
            if (pid != (uint)Environment.ProcessId) continue;
            SendMessageW(h, msg, wParam, lParam);
            sent++;
        }
        Assert.True(sent > 0, "the system events window exists");
    }

    [Fact]
    public async Task Windows_power_and_device_notifications_reach_the_engine_and_the_history_once_each()
    {
        using var app = new SimApp("sysevents");
        var pc = app.Pc;
        using var events = new SystemEvents();
        var suspends = 0;
        var resumes = 0;
        var devices = 0;
        var displays = 0;
        var locks = new List<bool>();
        var ending = 0;
        // the wiring of AppHost: sleep -> engine + history, resume -> engine + history, device change -> re-discover
        events.Suspending += () =>
        {
            suspends++;
            app.Sleep();
        };
        events.Resumed += () =>
        {
            resumes++;
            app.Wake();
        };
        events.DevicesChanged += () =>
        {
            devices++;
            pc.Engine.RequestRediscover();
        };
        events.DisplayChanged += () => displays++;
        events.SessionLockChanged += locks.Add;
        events.SessionEnding += () => ending++;
        events.DevicesChanged += () => throw new InvalidOperationException("a handler that fails must not stop the next notification");

        app.Run(TimeSpan.FromMinutes(2));
        var id = pc.Sensor("AMD Ryzen 7 9800X3D", SensorKind.Temperature, "Core (Tctl/Tdie)").Id;
        var reads = pc.Fake.TotalReads;

        // sleep (Windows can repeat PBT_APMSUSPEND)
        Send(WmPowerBroadcast, 0x4);
        Send(WmPowerBroadcast, 0x4);
        pc.Run(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1));
        Assert.Equal(1, suspends);
        Assert.Equal(reads, pc.Fake.TotalReads); // the engine really stood still
        Assert.Equal(1, pc.Source.Suspends);

        // resume: PBT_APMRESUMESUSPEND and PBT_APMRESUMEAUTOMATIC both arrive
        pc.Clock.Advance(TimeSpan.FromHours(6));
        Send(WmPowerBroadcast, 0x7);
        Send(WmPowerBroadcast, 0x12);
        Send(WmPowerBroadcast, 0x12);
        Assert.Equal(1, resumes);
        app.Run(TimeSpan.FromMinutes(2));
        Assert.True(pc.Fake.TotalReads > reads);
        Assert.False(float.IsNaN(pc.Engine.Latest[pc.Engine.Find(id)!.Index]));

        // a resume with no sleep before it, and a power message nobody cares about
        Send(WmPowerBroadcast, 0x7);
        Send(WmPowerBroadcast, 0xA);
        Assert.Equal(1, resumes);

        for (var i = 0; i < 50; i++) Send(WmDeviceChange, 0x7); // DBT_DEVNODES_CHANGED storm (a USB hub being plugged in)
        var header = Marshal.AllocHGlobal(32); // DEV_BROADCAST_HDR: the system insists on a real structure for the 0x8000 messages
        try
        {
            Marshal.WriteInt32(header, 0, 12);
            Marshal.WriteInt32(header, 4, 0);
            Marshal.WriteInt32(header, 8, 0);
            Send(WmDeviceChange, 0x8000, header); // DBT_DEVICEARRIVAL
            Send(WmDeviceChange, 0x8004, header); // DBT_DEVICEREMOVECOMPLETE
            Send(WmDeviceChange, 0x0018, header); // DBT_DEVICEQUERYREMOVEFAILED: not ours
        }
        finally
        {
            Marshal.FreeHGlobal(header);
        }
        Assert.Equal(52, devices);
        var discovers = pc.Source.Discovers;
        app.Run(TimeSpan.FromSeconds(10));
        Assert.Equal(discovers + 1, pc.Source.Discovers); // 52 notifications, one re-discovery (the engine debounces)

        Send(WmDisplayChange, 0);
        Send(WmDisplayChange, 0);
        Assert.Equal(2, displays);
        Send(WmSessionChange, 0x7);
        Send(WmSessionChange, 0x8);
        Send(WmSessionChange, 0x5); // some other session event
        Assert.Equal([true, false], locks);
        Send(WmEndSession, 0); // the shutdown was cancelled
        Assert.Equal(0, ending);
        Send(WmEndSession, 1);
        Assert.Equal(1, ending);

        app.History.Suspend();
        Assert.NotEmpty(await app.Series(id, pc.Clock.Utc.AddMinutes(-3), pc.Clock.Utc.AddMinutes(1)));
        Assert.Empty(app.Warnings);
    }

    [Fact]
    public void System_events_can_be_created_and_disposed_again_and_again()
    {
        // the window class lives as long as the process: a later window must never end up calling a dead delegate
        for (var i = 0; i < 5; i++)
        {
            var count = 0;
            using (var events = new SystemEvents())
            {
                events.DisplayChanged += () => count++;
                Send(WmDisplayChange, 0);
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Assert.True(count >= 1);
        }
    }
}
