using System.Runtime.InteropServices;
using BetterVitals.Core.Sensors;
using BetterVitals.Fps;
using BetterVitals.Fps.Interop;
using Xunit;

namespace BetterVitals.Tests.Fps;

public sealed unsafe class PresentRingTests
{
    private const long Sec = 10_000_000;

    [Fact]
    public void Native_layouts_match_Windows_x64()
    {
        Assert.Equal(120, sizeof(EventTraceProperties));
        Assert.Equal(448, sizeof(EventTraceLogfile));
        Assert.Equal(112, sizeof(EventRecord));
        Assert.Equal(48, sizeof(EnableTraceParameters));
        Assert.Equal(16, sizeof(EventFilterDescriptor));
        Assert.Equal(568, sizeof(ProcessEntry32));
        Assert.Equal(16, Marshal.OffsetOf<EventRecord>(nameof(EventRecord.TimeStamp)).ToInt32());
        Assert.Equal(96, Marshal.OffsetOf<EventRecord>(nameof(EventRecord.UserData)).ToInt32());
    }

    [Fact]
    public void Keeps_only_the_target_process()
    {
        using var ring = new PresentRing(16, Sec);
        PresentRing.OnPresent(ring.Shared, 0, 100, PresentClass.Dxgi, 1); // no target yet: pid 0 never matches
        ring.TargetPid = 42;
        PresentRing.OnPresent(ring.Shared, 7, 200, PresentClass.Dxgi, 1);
        PresentRing.OnPresent(ring.Shared, 42, 300, PresentClass.Dxgi, 1);
        Assert.Equal(1, ring.WriteIndex);
        Assert.Equal(300, Read(ring, 0)[0]);
    }

    private static long[] Read(PresentRing ring, long from)
    {
        var ts = new long[ring.Capacity];
        var n = ring.Snapshot(from, ts, new uint[ring.Capacity], out _);
        return ts[..n];
    }

    [Fact]
    public void Kernel_present_is_dropped_while_a_runtime_presents()
    {
        using var ring = new PresentRing(64, Sec);
        ring.TargetPid = 5;
        // DXGI present + its DxgKrnl token: counted once
        for (var i = 0; i < 10; i++)
        {
            PresentRing.OnPresent(ring.Shared, 5, 1000 + i * 100_000, PresentClass.Dxgi, 9);
            PresentRing.OnPresent(ring.Shared, 5, 1010 + i * 100_000, PresentClass.Kernel, 0);
        }
        Assert.Equal(10, ring.WriteIndex);
        // D3D9 also loses to DXGI (nested runtimes), but wins over the kernel
        PresentRing.OnPresent(ring.Shared, 5, 2_000_000, PresentClass.D3D9, 3);
        Assert.Equal(10, ring.WriteIndex);
        // runtime silent for over a second: the kernel fallback counts (Vulkan / OpenGL)
        PresentRing.OnPresent(ring.Shared, 5, 2_000_000 + 2 * Sec, PresentClass.Kernel, 0);
        Assert.Equal(11, ring.WriteIndex);
    }

    [Fact]
    public void Ring_wraps_and_snapshot_keeps_order()
    {
        using var ring = new PresentRing(16, Sec);
        ring.TargetPid = 1;
        for (var i = 0; i < 40; i++) PresentRing.OnPresent(ring.Shared, 1, 1000 + i, PresentClass.Dxgi, 1);
        var ts = new long[16];
        var keys = new uint[16];
        var n = ring.Snapshot(0, ts, keys, out var end);
        Assert.Equal(40, end);
        Assert.Equal(16, n);
        Assert.Equal(Enumerable.Range(24, 16).Select(i => 1000L + i), ts);

        // from a marker inside the ring (PID switch / provider restart): only newer entries
        n = ring.Snapshot(35, ts, keys, out _);
        Assert.Equal(5, n);
        Assert.Equal(1035, ts[0]);
    }

    [Fact]
    public void Pid_switch_starts_from_the_marker()
    {
        using var ring = new PresentRing(32, Sec);
        ring.TargetPid = 10;
        for (var i = 0; i < 8; i++) PresentRing.OnPresent(ring.Shared, 10, 100 + i, PresentClass.Dxgi, 1);
        ring.TargetPid = 20;
        var marker = ring.WriteIndex;
        PresentRing.OnPresent(ring.Shared, 10, 200, PresentClass.Dxgi, 1); // old target, late: dropped
        PresentRing.OnPresent(ring.Shared, 20, 201, PresentClass.Dxgi, 1);
        PresentRing.OnPresent(ring.Shared, 20, 202, PresentClass.Dxgi, 1);
        var ts = new long[32];
        var n = ring.Snapshot(marker, ts, new uint[32], out _);
        Assert.Equal([201L, 202L], ts[..n]);
    }

    [Fact]
    public void Event_callback_parses_providers_and_payloads()
    {
        using var ring = new PresentRing(64, Sec);
        ring.TargetPid = 77;
        var payload = stackalloc byte[64];
        new Span<byte>(payload, 64).Clear();

        void Fire(Guid provider, ushort id, int len, ushort flags = 0, uint pid = 77, long ts = 1000)
        {
            EventRecord r = default;
            r.ProviderId = provider;
            r.Id = id;
            r.ProcessId = pid;
            r.TimeStamp = ts;
            r.Flags = flags;
            r.UserData = payload;
            r.UserDataLength = (ushort)len;
            r.UserContext = ring.Shared;
            var cb = (delegate* unmanaged<EventRecord*, void>)&PresentRing.OnEvent;
            cb(&r);
        }

        // DXGI Present_Start: pIDXGISwapChain, Flags, SyncInterval
        *(ulong*)payload = 0x0000_0123_4567_8000;
        *(uint*)(payload + 8) = 0;
        Fire(PresentProviders.Dxgi, 42, 16);
        Assert.Equal(1, ring.WriteIndex);
        *(uint*)(payload + 8) = PresentProviders.DxgiPresentTest; // DXGI_PRESENT_TEST presents nothing
        Fire(PresentProviders.Dxgi, 42, 16, ts: 2000);
        Assert.Equal(1, ring.WriteIndex);
        Fire(PresentProviders.Dxgi, 43, 16, ts: 3000); // Present_Stop: not a frame
        Fire(PresentProviders.Dxgi, 42, 16, pid: 78, ts: 4000); // other process
        Fire(Guid.NewGuid(), 42, 16, ts: 5000); // other provider
        Assert.Equal(1, ring.WriteIndex);

        // 32-bit game: pointer is 4 bytes, Flags at offset 4
        *(uint*)payload = 0x1234_5678;
        *(uint*)(payload + 4) = 0;
        *(uint*)(payload + 8) = PresentProviders.DxgiPresentTest; // would be "test" if read at the 64-bit offset
        Fire(PresentProviders.Dxgi, 55, 12, flags: Etw.EventHeaderFlag32BitHeader, ts: 6000);
        Assert.Equal(2, ring.WriteIndex);

        // D3D9 Present_Start (suppressed by the recent DXGI present of the same process)
        Fire(PresentProviders.D3D9, 1, 12, ts: 7000);
        Assert.Equal(2, ring.WriteIndex);

        // DxgKrnl PresentHistory(Detailed)_Start, far later (runtime silent): composition / GDI tokens are not frames
        *(ulong*)(payload + 16) = PresentProviders.ModelRedirectedComposition;
        Fire(PresentProviders.DxgKrnl, 215, 40, ts: 10 * Sec);
        *(uint*)(payload + 16) = PresentProviders.ModelRedirectedGdi;
        Fire(PresentProviders.DxgKrnl, 171, 40, ts: 10 * Sec + 1);
        Assert.Equal(2, ring.WriteIndex);
        *(uint*)(payload + 16) = 2; // D3DKMT_PM_REDIRECTED_FLIP
        Fire(PresentProviders.DxgKrnl, 215, 40, ts: 10 * Sec + 2);
        Fire(PresentProviders.DxgKrnl, 171, 40, ts: 10 * Sec + 3);
        Fire(PresentProviders.DxgKrnl, 184, 40, ts: 10 * Sec + 4); // Present_Info: not counted
        Assert.Equal(4, ring.WriteIndex);
        Assert.Equal(2u, ring.Shared->LastKernelModel);

        var keys = new uint[64];
        var n = ring.Snapshot(0, new long[64], keys, out _);
        Assert.Equal(4, n);
        Assert.NotEqual(0u, keys[0]);
        Assert.NotEqual(keys[0], keys[1]); // different swap chains
        Assert.Equal(0u, keys[2]); // kernel
        Assert.Equal(12, ring.EventsSeen);
    }

    [Fact]
    public void Inventory_is_fixed_and_named_as_specified()
    {
        using var source = new FpsSource("BetterVitals-Present-UnitTest-NeverStarted");
        Assert.Equal("fps", source.Name);
        Assert.Equal(SourceState.Idle, source.Status.State);
        var hw = Assert.Single(FpsInventory(source).Hardware);
        Assert.Equal("game/foreground/0", hw.Id);
        Assert.Equal("Foreground app", hw.Name);
        Assert.Equal(HardwareKind.Game, hw.Kind);
        Assert.Equal(
            ["game/foreground/0/fps/framerate", "game/foreground/0/frametime/frametime", "game/foreground/0/fps/1-low",
             "game/foreground/0/fps/0-1-low", "game/foreground/0/frametime/frametime-max"],
            FpsInventory(source).Sensors.Select(s => s.Id));
        Assert.Equal([SensorKind.Fps, SensorKind.FrameTime, SensorKind.Fps, SensorKind.Fps, SensorKind.FrameTime],
            FpsInventory(source).Sensors.Select(s => s.Kind));
    }

    // Discover() would start an ETW session; the inventory is built in the constructor
    private static SourceInventory FpsInventory(FpsSource s) => s.Inventory;

    [Fact]
    public void Update_without_session_writes_NaN_and_never_throws()
    {
        using var source = new FpsSource("BetterVitals-Present-UnitTest-NeverStarted");
        var sensors = FpsInventory(source).Sensors.ToArray();
        for (var i = 0; i < sensors.Length; i++) sensors[i].Index = i;
        var values = new float[sensors.Length];
        values.AsSpan().Fill(1);
        source.Update(sensors, values); // not started: no session, providers cannot be enabled
        Assert.All(values, v => Assert.True(float.IsNaN(v)));
        source.Update(sensors, new float[1]); // indexes out of range are skipped
    }

    [Fact]
    public void Measure_resets_on_target_switch()
    {
        using var source = new FpsSource("BetterVitals-Present-UnitTest-NeverStarted");
        using var ring = new PresentRing(1024, Sec);
        source.UseRingForTests(ring);
        var f = System.Diagnostics.Stopwatch.Frequency;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();

        source.TargetPidOverride = 100;
        Assert.False(source.Measure(now).HasValue);
        Assert.Equal(100u, ring.TargetPid);
        for (var i = 1; i <= 120; i++) PresentRing.OnPresent(ring.Shared, 100, now + i * f / 60, PresentClass.Dxgi, 1);
        var end = now + 2 * f;
        Assert.Equal(60f, source.Measure(end).Framerate, 0.05f);
        Assert.Equal(60f, source.Measure(end).Framerate, 0.05f); // cached path

        source.TargetPidOverride = 200;
        Assert.False(source.Measure(end).HasValue); // reset at once
        PresentRing.OnPresent(ring.Shared, 100, end + 1, PresentClass.Dxgi, 1); // old target: ignored
        for (var i = 1; i <= 60; i++) PresentRing.OnPresent(ring.Shared, 200, end + i * f / 30, PresentClass.Dxgi, 1);
        Assert.Equal(30f, source.Measure(end + 2 * f).Framerate, 0.05f);
        Assert.False(source.Measure(end + 6 * f).HasValue); // stopped presenting
    }
}
