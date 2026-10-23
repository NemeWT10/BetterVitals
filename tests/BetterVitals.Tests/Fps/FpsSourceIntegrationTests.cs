using System.Diagnostics;
using BetterVitals.Core.Sensors;
using BetterVitals.Fps;
using Xunit;
using Xunit.Abstractions;

namespace BetterVitals.Tests.Fps;

/// <summary>
/// Real ETW: FakeGame (hidden desktop) measured through <see cref="FpsSource"/> with <c>TargetPidOverride</c>.
/// Needs administrator or "Performance Log Users"; without it the tests only check the Unavailable status.
/// One class = sequential (a single test session name).
/// </summary>
[Collection(EtwCollection.Name)]
public sealed class FpsSourceIntegrationTests(ITestOutputHelper output)
{
    private const string SessionName = "BetterVitals-Present-Test";

    private static (FpsSource Source, SensorDescriptor[] Sensors) Create()
    {
        var source = new FpsSource(SessionName);
        var sensors = source.Discover().Sensors.ToArray();
        for (var i = 0; i < sensors.Length; i++) sensors[i].Index = i;
        return (source, sensors);
    }

    private bool EtwAvailable(FpsSource source)
    {
        if (source.Status.State == SourceState.Ok) return true;
        output.WriteLine($"ETW unavailable on this machine: {source.Status.MessageKey} — integration checks skipped.");
        Assert.Equal(SourceState.Unavailable, source.Status.State);
        Assert.Contains(source.Status.MessageKey, new[] { FpsSource.StatusNoAccess, FpsSource.StatusBlocked });
        return false;
    }

    /// <summary>Calls Update like the engine (every <paramref name="tick"/>) and returns the last values.</summary>
    private static float[] Pump(FpsSource source, SensorDescriptor[] sensors, TimeSpan duration, TimeSpan tick, List<float[]>? samples = null)
    {
        var values = new float[sensors.Length];
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < duration)
        {
            source.Update(sensors, values);
            samples?.Add((float[])values.Clone());
            Thread.Sleep(tick);
        }
        source.Update(sensors, values);
        return values;
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void Measures_FakeGame_within_2_percent(int fps)
    {
        var (source, sensors) = Create();
        using var _ = source;
        if (!EtwAvailable(source)) return;

        using var game = FakeGameProcess.Start($"--fps {fps} --seconds 14 --vsync off");
        source.TargetPidOverride = game.Pid;
        // warm-up: providers on, ~1 s ETW flush latency, fill part of the 10 s window
        Pump(source, sensors, TimeSpan.FromSeconds(4), TimeSpan.FromMilliseconds(500));
        var samples = new List<float[]>();
        var v = Pump(source, sensors, TimeSpan.FromSeconds(6), TimeSpan.FromMilliseconds(500), samples);

        output.WriteLine($"target {fps}: framerate {v[0]:F2}, frametime {v[1]:F3} ms, 1% {v[2]:F2}, 0.1% {v[3]:F2}, max {v[4]:F3} ms");
        output.WriteLine($"process '{source.ForegroundProcessName}' pid {source.ForegroundProcessId}; ring stored {source.Ring!.EventsStored}, seen {source.Ring.EventsSeen}");
        foreach (var s in samples)
        {
            Assert.InRange(s[0], fps * 0.98f, fps * 1.02f);
            Assert.InRange(s[1], 1000f / fps * 0.98f, 1000f / fps * 1.02f);
        }
        Assert.Equal("FakeGame.exe", source.ForegroundProcessName, ignoreCase: true);
        Assert.Equal(game.Pid, source.ForegroundProcessId);
        // steady pacing: lows close to the rate, max frame time bounded
        Assert.InRange(v[2], fps * 0.75f, fps * 1.02f);
        Assert.InRange(v[3], fps * 0.5f, fps * 1.02f);
        Assert.InRange(v[4], 1000f / fps * 0.98f, 1000f / fps * 2.5f + 4f);
    }

    [Fact]
    public void Jitter_keeps_average_and_lowers_lows()
    {
        var (source, sensors) = Create();
        using var _ = source;
        if (!EtwAvailable(source)) return;

        using var game = FakeGameProcess.Start("--fps 100 --seconds 13 --vsync off --jitter --seed 7");
        source.TargetPidOverride = game.Pid;
        Pump(source, sensors, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(500));
        var v = Pump(source, sensors, TimeSpan.FromSeconds(7), TimeSpan.FromMilliseconds(500));
        output.WriteLine($"jitter 100: framerate {v[0]:F2}, 1% {v[2]:F2}, 0.1% {v[3]:F2}, max {v[4]:F3} ms");
        // frame times are uniform in [0.75, 1.25] × 10 ms around a fixed grid → deltas in ~[5, 15] ms
        Assert.InRange(v[0], 95f, 105f);
        Assert.InRange(v[2], 66f, 85f);
        // the slowest frame also carries the OS scheduler's hiccups on a busy machine (parallel builds): generous top
        Assert.InRange(v[4], 12f, 25f);
    }

    [Fact]
    public void Values_are_NaN_without_presents_and_reset_on_target_change()
    {
        var (source, sensors) = Create();
        using var _ = source;
        if (!EtwAvailable(source)) return;

        using var game = FakeGameProcess.Start("--fps 60 --seconds 12 --vsync off");
        // a process that never presents: this test host
        source.TargetPidOverride = Environment.ProcessId;
        var v = Pump(source, sensors, TimeSpan.FromSeconds(2.5), TimeSpan.FromMilliseconds(500));
        Assert.All(v, x => Assert.True(float.IsNaN(x)));

        source.TargetPidOverride = game.Pid;
        v = Pump(source, sensors, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(500));
        Assert.InRange(v[0], 58.8f, 61.2f);

        // switch away: reset at once, nothing carried over
        source.TargetPidOverride = Environment.ProcessId;
        source.Update(sensors, v);
        Assert.All(v, x => Assert.True(float.IsNaN(x)));
    }

    [Fact]
    public void Kernel_fallback_counts_presents_without_runtime_events()
    {
        var (source, sensors) = Create();
        using var _ = source;
        if (!EtwAvailable(source)) return;
        source.Session!.EnableRuntimeProviders = false; // as for a Vulkan / OpenGL app: only DxgKrnl PresentHistory

        using var game = FakeGameProcess.Start("--fps 60 --seconds 10 --vsync off");
        source.TargetPidOverride = game.Pid;
        var v = Pump(source, sensors, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(500));
        unsafe
        {
            output.WriteLine($"kernel only: framerate {v[0]:F2}; stored {source.Ring!.EventsStored}, seen {source.Ring.EventsSeen}, model {source.Ring.Shared->LastKernelModel}");
        }
        Assert.InRange(v[0], 58.8f, 61.2f);
    }

    [Fact]
    public void Providers_follow_demand_and_turn_off_when_idle()
    {
        var (source, sensors) = Create();
        using var _ = source;
        if (!EtwAvailable(source)) return;

        Assert.False(source.ProvidersEnabled); // session only, no providers at start
        var values = new float[sensors.Length];
        source.Update(sensors, values);
        Assert.True(source.ProvidersEnabled);
        source.Update([], values); // empty request is not demand
        var sw = Stopwatch.StartNew();
        while (source.ProvidersEnabled && sw.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(100);
        output.WriteLine($"providers off after {sw.Elapsed.TotalSeconds:F2} s");
        Assert.False(source.ProvidersEnabled);
        Assert.InRange(sw.Elapsed.TotalSeconds, 4, 7);

        source.Update(sensors, values); // demand again → back on
        Assert.True(source.ProvidersEnabled);
    }

    [Fact]
    public void Overhead_at_144_fps_and_idle()
    {
        var (source, sensors) = Create();
        using var _ = source;
        if (!EtwAvailable(source)) return;

        var values = new float[sensors.Length];
        using var game = FakeGameProcess.Start("--fps 144 --seconds 16 --vsync off");
        source.TargetPidOverride = game.Pid;
        Pump(source, sensors, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(500));

        var cpu0 = ConsumerCpu(source);
        var seen0 = source.Ring!.EventsSeen;
        var sw = Stopwatch.StartNew();
        long updateTicks = 0, updateMax = 0, alloc = 0;
        var calls = 0;
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            var a0 = GC.GetAllocatedBytesForCurrentThread();
            var t0 = Stopwatch.GetTimestamp();
            source.Update(sensors, values);
            var dt = Stopwatch.GetTimestamp() - t0;
            alloc += GC.GetAllocatedBytesForCurrentThread() - a0;
            updateTicks += dt;
            updateMax = Math.Max(updateMax, dt);
            calls++;
            Thread.Sleep(250);
        }
        var activeWall = sw.Elapsed;
        var activeCpu = ConsumerCpu(source) - cpu0;
        var events = source.Ring.EventsSeen - seen0;
        var q = source.Session!.Query();
        double us(long ticks) => ticks * 1e6 / Stopwatch.Frequency;
        output.WriteLine($"active 144 FPS: consumer CPU {activeCpu.TotalMilliseconds:F2} ms over {activeWall.TotalSeconds:F1} s = {100 * activeCpu / activeWall:F4} % of one core; {events} events ({events / activeWall.TotalSeconds:F0}/s, all processes), {activeCpu.TotalMicroseconds / Math.Max(1, events):F2} us/event");
        output.WriteLine($"Update: {calls} calls, avg {us(updateTicks) / calls:F1} us, max {us(updateMax):F1} us, allocated {alloc} B");
        output.WriteLine($"session buffers {q?.Buffers} x {q?.BufferSizeKb} KB, events lost {q?.EventsLost}, rt buffers lost {q?.RealTimeBuffersLost}; ring {source.Ring.NativeBytes / 1024} KB native");
        Assert.InRange(values[0], 141f, 147f);
        Assert.Equal(0, alloc);

        // no demand → providers off → no events, no CPU
        sw.Restart();
        while (source.ProvidersEnabled && sw.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(100);
        Assert.False(source.ProvidersEnabled);
        Thread.Sleep(1500); // events already in flight
        cpu0 = ConsumerCpu(source);
        seen0 = source.Ring.EventsSeen;
        Thread.Sleep(5000);
        var idleCpu = ConsumerCpu(source) - cpu0;
        var idleEvents = source.Ring.EventsSeen - seen0;
        output.WriteLine($"providers off: consumer CPU {idleCpu.TotalMilliseconds:F3} ms over 5 s, {idleEvents} events; {GC.GetTotalMemory(false) / 1024} KB managed heap (whole test host)");
        Assert.Equal(0, idleEvents);
        Assert.True(idleCpu < TimeSpan.FromMilliseconds(1));
        Assert.True(activeCpu < TimeSpan.FromMilliseconds(100)); // < 1 % of one core at 144 FPS
    }

    /// <summary>
    /// CPU time of the consumer thread from its cycle counter (thread times tick at 15.6 ms, far too coarse), converted
    /// with this core's cycles per second measured by spinning.
    /// </summary>
    private static TimeSpan ConsumerCpu(FpsSource source)
    {
        var h = OpenThread(0x0800 /* THREAD_QUERY_LIMITED_INFORMATION */, false, (uint)source.Session!.ConsumerOsThreadId);
        Assert.NotEqual(0, h);
        try
        {
            Assert.True(QueryThreadCycleTime(h, out var cycles));
            return TimeSpan.FromSeconds(cycles / CyclesPerSecond.Value);
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private static readonly Lazy<double> CyclesPerSecond = new(() =>
    {
        QueryThreadCycleTime(-2 /* current thread */, out var c0);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 200) { }
        QueryThreadCycleTime(-2, out var c1);
        return (c1 - c0) / sw.Elapsed.TotalSeconds;
    });

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern nint OpenThread(uint access, bool inherit, uint threadId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool QueryThreadCycleTime(nint thread, out ulong cycles);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EtwCollection
{
    public const string Name = "ETW";
}
