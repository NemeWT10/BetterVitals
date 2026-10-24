using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Derived;
using BetterVitals.Sensors.Engine;
using BetterVitals.Sensors.Fake;
using Xunit;

namespace BetterVitals.Tests.Engine;

public sealed class SensorEngineTests
{
    private static readonly long T0 = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc).Ticks;

    private sealed class Clock
    {
        public long Now { get; set; } = T0;
        public void Advance(TimeSpan by) => Now += by.Ticks;
    }

    private static (SensorEngine Engine, FakeSensorSource Fake, Clock Clock) Create(FakeProfile profile = FakeProfile.DesktopAmdNvidia, bool derived = false)
    {
        var clock = new Clock();
        var fake = new FakeSensorSource(profile, 1, () => clock.Now);
        ISensorSource[] sources = derived ? [new DerivedSource(), fake] : [fake];
        var engine = new SensorEngine(sources, () => clock.Now);
        engine.DiscoverNow();
        return (engine, fake, clock);
    }

    /// <summary>Runs the engine's own loop logic for <paramref name="duration"/> in 250 ms steps; returns ticks published.</summary>
    private static int Run(SensorEngine engine, Clock clock, TimeSpan duration)
    {
        var frames = 0;
        var end = clock.Now + duration.Ticks;
        while (clock.Now < end)
        {
            clock.Advance(SensorEngine.BaseTick);
            engine.ApplyDemandNow();
            if (engine.Tick(clock.Now)) frames++;
        }
        return frames;
    }

    private static string Id(SensorEngine e, string hardwareName, SensorKind kind, string name)
        => e.Sensors.Single(s => s.Name == name && s.Kind == kind && e.FindHardware(s.HardwareId)!.Name == hardwareName).Id;

    [Fact]
    public void Discovery_registers_hardware_and_sensors_with_stable_slots()
    {
        var (engine, _, _) = Create();
        Assert.Contains(engine.Hardware, h => h.Name == "NVIDIA GeForce RTX 5080" && h.Kind == HardwareKind.Gpu);
        Assert.True(engine.Sensors.Count > 60);
        for (var i = 0; i < engine.Sensors.Count; i++) Assert.Equal(i, engine.Sensors[i].Index);
        var hot = engine.Find(Id(engine, "NVIDIA GeForce RTX 5080", SensorKind.Temperature, "GPU Hot Spot"));
        Assert.NotNull(hot);
        Assert.Equal("gpu/nvidia-geforce-rtx-5080/0/temperature/gpu-hot-spot", hot!.Id);
    }

    [Fact]
    public void Nothing_is_read_without_demand()
    {
        var (engine, fake, clock) = Create();
        var frames = Run(engine, clock, TimeSpan.FromSeconds(10));
        Assert.Equal(0, frames);
        Assert.Equal(0, fake.TotalReads);
    }

    [Fact]
    public void Only_demanded_sensors_are_read_at_the_asked_interval()
    {
        var (engine, fake, clock) = Create();
        var cpu = Id(engine, "AMD Ryzen 7 9800X3D", SensorKind.Temperature, "Core (Tctl/Tdie)");
        var gpu = Id(engine, "NVIDIA GeForce RTX 5080", SensorKind.Temperature, "GPU Core");
        using var d = engine.Demand("test", [cpu, gpu], TimeSpan.FromSeconds(1));
        Run(engine, clock, TimeSpan.FromSeconds(10));

        var cpuReads = fake.Reads(engine.Find(cpu)!.Index);
        Assert.InRange(cpuReads, 10, 11); // first read at the next 250 ms slot, then every second
        Assert.Equal(cpuReads, fake.TotalReads / 2);
        Assert.False(float.IsNaN(engine.Latest[engine.Find(cpu)!.Index]));
    }

    [Fact]
    public void Shortest_interval_wins_and_disposing_releases()
    {
        var (engine, fake, clock) = Create();
        var cpu = Id(engine, "AMD Ryzen 7 9800X3D", SensorKind.Temperature, "Core (Tctl/Tdie)");
        var slot = engine.Find(cpu)!.Index;
        var slow = engine.Demand("recorder", [cpu], TimeSpan.FromSeconds(2));
        var fast = engine.Demand("overlay", [cpu], TimeSpan.FromMilliseconds(500));
        Run(engine, clock, TimeSpan.FromSeconds(10));
        Assert.InRange(fake.Reads(slot), 20, 21);

        fast.Dispose();
        var before = fake.Reads(slot);
        Run(engine, clock, TimeSpan.FromSeconds(10));
        Assert.InRange(fake.Reads(slot) - before, 5, 6);

        slow.Dispose();
        before = fake.Reads(slot);
        Run(engine, clock, TimeSpan.FromSeconds(10));
        Assert.Equal(before, fake.Reads(slot));
    }

    [Fact]
    public void Min_interval_of_expensive_sensors_is_respected()
    {
        var (engine, fake, clock) = Create();
        var nvme = Id(engine, "KINGSTON SKC3000D2048G", SensorKind.Temperature, "Composite Temperature");
        using var d = engine.Demand("overlay", [nvme], TimeSpan.FromSeconds(1));
        Run(engine, clock, TimeSpan.FromSeconds(60));
        Assert.InRange(fake.Reads(engine.Find(nvme)!.Index), 6, 7); // every 10 s
    }

    [Fact]
    public void Intervals_share_ticks_on_the_wall_clock_grid()
    {
        var (engine, _, clock) = Create();
        var a = Id(engine, "AMD Ryzen 7 9800X3D", SensorKind.Load, "CPU Total");
        var b = Id(engine, "NVIDIA GeForce RTX 5080", SensorKind.Load, "GPU Core");
        using var d1 = engine.Demand("x", [a], TimeSpan.FromSeconds(1));
        using var d2 = engine.Demand("y", [b], TimeSpan.FromSeconds(2));
        var frames = Run(engine, clock, TimeSpan.FromSeconds(20));
        Assert.InRange(frames, 20, 21); // the 2 s group always lands on a 1 s tick
        Assert.Equal(0, engine.Latest.TimestampUtcTicks % SensorEngine.BaseTick.Ticks);
    }

    [Fact]
    public void Unplugged_hardware_keeps_its_slot_and_reads_NaN_then_returns()
    {
        var (engine, fake, clock) = Create();
        var gpuHw = engine.Hardware.Single(h => h.Name == "NVIDIA GeForce RTX 5080").Id;
        var gpu = Id(engine, "NVIDIA GeForce RTX 5080", SensorKind.Temperature, "GPU Core");
        var slot = engine.Find(gpu)!.Index;
        using var d = engine.Demand("overlay", [gpu], TimeSpan.FromSeconds(1));
        Run(engine, clock, TimeSpan.FromSeconds(3));
        Assert.False(float.IsNaN(engine.Latest[slot]));

        fake.SetPresent(gpuHw, false);
        engine.DiscoverNow();
        Run(engine, clock, TimeSpan.FromSeconds(3));
        Assert.False(engine.IsPresent(slot));
        Assert.True(float.IsNaN(engine.Latest[slot]));
        Assert.Null(engine.FindHardware(gpuHw));

        fake.SetPresent(gpuHw, true);
        engine.DiscoverNow();
        Run(engine, clock, TimeSpan.FromSeconds(3));
        Assert.Equal(slot, engine.Find(gpu)!.Index);
        Assert.True(engine.IsPresent(slot));
        Assert.False(float.IsNaN(engine.Latest[slot]));
    }

    [Fact]
    public void Demand_for_an_unknown_id_starts_working_when_the_sensor_appears()
    {
        var (engine, fake, clock) = Create();
        var gpuHw = engine.Hardware.Single(h => h.Name == "NVIDIA GeForce RTX 5080").Id;
        var gpu = Id(engine, "NVIDIA GeForce RTX 5080", SensorKind.Temperature, "GPU Core");
        fake.SetPresent(gpuHw, false);
        engine.DiscoverNow();
        using var d = engine.Demand("overlay", [gpu], TimeSpan.FromSeconds(1));
        Run(engine, clock, TimeSpan.FromSeconds(2));
        fake.SetPresent(gpuHw, true);
        engine.DiscoverNow();
        Run(engine, clock, TimeSpan.FromSeconds(2));
        Assert.False(float.IsNaN(engine.Latest[engine.Find(gpu)!.Index]));
    }

    [Fact]
    public void Derived_max_is_computed_from_this_ticks_inputs()
    {
        var (engine, _, clock) = Create(derived: true);
        var max = engine.Sensors.Single(s => s.Name == "CPU Clock Max");
        var cores = engine.Sensors.Where(s => s.Kind == SensorKind.Clock && s.Name.StartsWith("Core #", StringComparison.Ordinal)).ToList();
        Assert.Equal(8, cores.Count);
        Assert.Contains(engine.Sensors, s => s.Name == "DIMM Max");
        Assert.DoesNotContain(engine.Sensors, s => s.Name == "Total Download"); // one adapter only

        using var d = engine.Demand("overlay", [max.Id], TimeSpan.FromSeconds(1));
        Run(engine, clock, TimeSpan.FromSeconds(3));
        var frame = engine.Latest;
        Assert.True(frame.IsFresh(max.Index));
        Assert.All(cores, c => Assert.True(frame.IsFresh(c.Index)));
        Assert.Equal(cores.Max(c => frame[c.Index]), frame[max.Index]);
    }

    [Fact]
    public void Laptop_cpu_with_own_core_max_gets_no_duplicate()
    {
        var (engine, _, _) = Create(FakeProfile.LaptopIntel, derived: true);
        Assert.DoesNotContain(engine.Sensors, s => s.Name == "CPU Core Max");
        Assert.Contains(engine.Sensors, s => s.Name == "CPU Clock Max");
    }

    [Fact]
    public void A_failing_source_writes_NaN_and_the_engine_keeps_going()
    {
        var (engine, fake, clock) = Create();
        var cpu = Id(engine, "AMD Ryzen 7 9800X3D", SensorKind.Load, "CPU Total");
        var slot = engine.Find(cpu)!.Index;
        using var d = engine.Demand("x", [cpu], TimeSpan.FromSeconds(1));
        fake.ThrowOnUpdate = true;
        Run(engine, clock, TimeSpan.FromSeconds(3));
        Assert.True(float.IsNaN(engine.Latest[slot]));
        fake.ThrowOnUpdate = false;
        Run(engine, clock, TimeSpan.FromSeconds(2));
        Assert.False(float.IsNaN(engine.Latest[slot]));
    }

    [Fact]
    public void Session_stats_track_min_max_average()
    {
        var (engine, _, clock) = Create();
        var cpu = Id(engine, "AMD Ryzen 7 9800X3D", SensorKind.Temperature, "Core (Tctl/Tdie)");
        var slot = engine.Find(cpu)!.Index;
        using var d = engine.Demand("x", [cpu], TimeSpan.FromSeconds(1));
        var seen = new List<float>();
        engine.FrameReady += f => { if (f.IsFresh(slot)) seen.Add(f[slot]); };
        Run(engine, clock, TimeSpan.FromSeconds(30));
        Assert.Equal(seen.Count, engine.Stats.Count(slot));
        Assert.Equal(seen.Min(), engine.Stats.Min(slot));
        Assert.Equal(seen.Max(), engine.Stats.Max(slot));
        Assert.Equal(seen.Average(), engine.Stats.Average(slot), 3);
    }

    [Fact]
    public void Frames_rotate_and_a_published_frame_stays_intact_for_two_ticks()
    {
        var (engine, _, clock) = Create();
        var cpu = Id(engine, "AMD Ryzen 7 9800X3D", SensorKind.Load, "CPU Total");
        using var d = engine.Demand("x", [cpu], TimeSpan.FromSeconds(1));
        Run(engine, clock, TimeSpan.FromSeconds(2));
        var held = engine.Latest;
        var seq = held.Sequence;
        var value = held[engine.Find(cpu)!.Index];
        Run(engine, clock, TimeSpan.FromSeconds(2)); // two more ticks
        Assert.Equal(seq, held.Sequence);
        Assert.Equal(value, held[engine.Find(cpu)!.Index]);
        Assert.NotSame(held, engine.Latest);
    }

    [Fact]
    public void Tick_does_not_allocate()
    {
        var (engine, _, clock) = Create(derived: false);
        var all = engine.Sensors.Select(s => s.Id).ToArray();
        using var d = engine.Demand("everything", all, TimeSpan.FromMilliseconds(250));
        engine.ApplyDemandNow();
        for (var i = 0; i < 200; i++) { clock.Advance(SensorEngine.BaseTick); engine.Tick(clock.Now); } // warm up (JIT, growth)

        // a real per-tick allocation shows in every round; one-off runtime noise (tiering on a loaded machine) does not
        var best = long.MaxValue;
        for (var round = 0; round < 3; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++) { clock.Advance(SensorEngine.BaseTick); engine.Tick(clock.Now); }
            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        Assert.True(best < 256, $"{best} bytes allocated in 1000 ticks of {all.Length} sensors");
    }

    [Fact]
    public void A_rediscovery_that_finds_the_same_inventory_tells_nobody_but_keeps_reading()
    {
        var (engine, fake, clock) = Create(derived: true);
        var gpuHw = engine.Hardware.Single(h => h.Name == "NVIDIA GeForce RTX 5080").Id;
        var gpu = Id(engine, "NVIDIA GeForce RTX 5080", SensorKind.Temperature, "GPU Core");
        var slot = engine.Find(gpu)!.Index;
        var hardware = engine.Hardware;
        var notified = 0;
        engine.InventoryChanged += () => notified++;
        using var d = engine.Demand("overlay", [gpu], TimeSpan.FromSeconds(1));
        Run(engine, clock, TimeSpan.FromSeconds(2));

        // a device-change storm that touched no sensor hardware: three passes, nobody is told
        var before = engine.Sensors[slot];
        for (var i = 0; i < 3; i++) engine.DiscoverNow();
        Assert.Equal(0, notified);
        Assert.Equal(3, engine.UnchangedDiscoveries);
        Assert.Same(hardware, engine.Hardware);
        Assert.NotSame(before, engine.Sensors[slot]); // the source's fresh descriptor (its new tag) is used from now on
        Assert.Same(engine.Sensors[slot], engine.Find(gpu));
        var reads = fake.Reads(slot);
        Run(engine, clock, TimeSpan.FromSeconds(3));
        Assert.InRange(fake.Reads(slot) - reads, 3, 4);
        Assert.False(float.IsNaN(engine.Latest[slot]));

        // real changes still notify: hardware gone, hardware back
        fake.SetPresent(gpuHw, false);
        engine.DiscoverNow();
        Assert.Equal(1, notified);
        Assert.Null(engine.FindHardware(gpuHw));
        fake.SetPresent(gpuHw, true);
        engine.DiscoverNow();
        Assert.Equal(2, notified);
        Assert.NotNull(engine.FindHardware(gpuHw));
        Assert.Equal(slot, engine.Find(gpu)!.Index);
    }

    [Fact]
    public void A_source_status_change_with_the_same_sensors_notifies()
    {
        var clock = new Clock();
        var source = new StatusSource(new FakeSensorSource(FakeProfile.Minimal, 1, () => clock.Now));
        var engine = new SensorEngine([source], () => clock.Now);
        engine.DiscoverNow();
        var notified = 0;
        engine.InventoryChanged += () => notified++;
        engine.DiscoverNow();
        Assert.Equal(0, notified);
        source.State = new SourceStatus(SourceState.Paused, "source.lhm.paused"); // e.g. the anti-cheat pause: same ids (ghosts)
        engine.DiscoverNow();
        Assert.Equal(1, notified);
        Assert.Equal(SourceState.Paused, engine.SourceStatuses.Single().Status.State);
    }

    private sealed class StatusSource(FakeSensorSource inner) : ISensorSource
    {
        public SourceStatus State { get; set; } = new(SourceState.Ok);
        public string Name => "status";
        public SourceStatus Status => State;
        public event Action? InventoryChanged { add { } remove { } }
        public SourceInventory Discover() => inner.Discover();
        public void Update(ReadOnlySpan<SensorDescriptor> requested, Span<float> values) => inner.Update(requested, values);
        public void Suspend() { }
        public void Resume() { }
        public void Dispose() => inner.Dispose();
    }

    [Fact]
    public void Real_thread_ticks_and_sleeps()
    {
        var fake = new FakeSensorSource(FakeProfile.Minimal);
        using var engine = new SensorEngine([fake]);
        var frames = 0;
        engine.FrameReady += _ => Interlocked.Increment(ref frames);
        engine.Start();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (engine.Sensors.Count == 0 && DateTime.UtcNow < deadline) Thread.Sleep(20);
        using var d = engine.Demand("x", [engine.Sensors[0].Id], TimeSpan.FromMilliseconds(250));
        Thread.Sleep(1600);
        Assert.InRange(Volatile.Read(ref frames), 4, 8);
    }
}
