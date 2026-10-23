using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Engine;
using BetterVitals.Sensors.Fake;
using BetterVitals.Sensors.Native;
using Xunit;

namespace BetterVitals.Tests.Native;

public sealed class NativeSourceTests
{
    /// <summary>A node that counts its refreshes and can be told to fail.</summary>
    private sealed class CountingNode : NativeNode
    {
        public int Refreshes;
        public bool Throw;
        public float Value = 1;

        internal override void Refresh(long timestamp)
        {
            Refreshes++;
            if (Throw) throw new InvalidOperationException("boom");
        }

        internal override float Read(int slot) => Throw ? float.NaN : Value + slot;
    }

    private sealed class FakeModule(Action<InventoryBuilder> discover, string name = "fake") : INativeModule
    {
        public int Released;
        public int Disposed;
        public string Name => name;
        public void Discover(InventoryBuilder builder) => discover(builder);
        public void Release() => Released++;
        public void Dispose() => Disposed++;
    }

    private static FakeModule Module(CountingNode node, int sensors = 2, string hardware = "Fake CPU", string name = "fake")
        => new(b =>
        {
            var id = b.AddHardware(HardwareKind.Cpu, hardware, 0);
            for (var i = 0; i < sensors; i++) b.AddSensor(id, HardwareKind.Cpu, SensorKind.Load, $"Load {i}", node, i);
        }, name);

    private static NativeSource Source(params INativeModule[] modules) => new(modules);

    private static void Index(SourceInventory inv)
    {
        for (var i = 0; i < inv.Sensors.Count; i++) inv.Sensors[i].Index = i;
    }

    [Fact]
    public void Identity_and_trivial_members()
    {
        using var s = new NativeSource();

        Assert.Equal("native", s.Name);
        Assert.Equal(SourceState.Idle, s.Status.State);
        Assert.True(s.InputsOf(Up.S(HardwareKind.Cpu, "x", SensorKind.Load, "y")).IsEmpty);
        Assert.Empty(s.MemoryModules);
        s.Suspend();
        s.Resume();
    }

    [Fact]
    public void Discover_collects_the_modules_inventories()
    {
        using var s = Source(Module(new CountingNode(), 2, "A", "a"), Module(new CountingNode(), 3, "B", "b"));

        var inv = s.Discover();

        Assert.Equal(["cpu/a/0", "cpu/b/0"], inv.Hardware.Select(h => h.Id).ToArray());
        Assert.Equal(5, inv.Sensors.Count);
        Assert.All(inv.Hardware, h => Assert.Equal("native", h.Source));
        Assert.Equal(SourceState.Ok, s.Status.State);
    }

    [Fact]
    public void A_module_that_throws_loses_only_its_own_part_and_marks_the_source_limited()
    {
        var bad = new FakeModule(b =>
        {
            b.AddHardware(HardwareKind.Gpu, "Half", 0);
            b.AddSensor("gpu/half/0", HardwareKind.Gpu, SensorKind.Load, "x", new CountingNode(), 0);
            throw new InvalidOperationException("driver exploded");
        }, "bad");
        using var s = Source(Module(new CountingNode(), 1, "Good", "good"), bad);

        var inv = s.Discover();

        Assert.Equal(["cpu/good/0"], inv.Hardware.Select(h => h.Id).ToArray());
        Assert.Single(inv.Sensors);
        Assert.Equal(SourceState.Limited, s.Status.State);
        Assert.Equal(1, bad.Released); // its half-built state was dropped
    }

    [Fact]
    public void When_every_module_throws_the_source_is_unavailable_but_discovery_still_returns()
    {
        var bad = new FakeModule(_ => throw new InvalidOperationException("x"), "bad");
        using var s = Source(bad);

        var inv = s.Discover();

        Assert.Empty(inv.Sensors);
        Assert.Equal(SourceState.Unavailable, s.Status.State);
    }

    [Fact]
    public void Update_refreshes_a_node_once_per_call_however_many_of_its_sensors_are_due()
    {
        var node = new CountingNode { Value = 10 };
        using var s = Source(Module(node, 3));
        var inv = s.Discover();
        Index(inv);
        var values = new float[3];

        s.Update(inv.Sensors.ToArray(), values);
        Assert.Equal(1, node.Refreshes);
        Assert.Equal([10f, 11f, 12f], values);

        s.Update(inv.Sensors.Take(1).ToArray(), values);
        Assert.Equal(2, node.Refreshes);
    }

    [Fact]
    public void Update_only_touches_the_nodes_that_have_a_due_sensor()
    {
        var a = new CountingNode();
        var b = new CountingNode();
        using var s = Source(Module(a, 1, "A", "a"), Module(b, 1, "B", "b"));
        var inv = s.Discover();
        Index(inv);

        s.Update(inv.Sensors.Where(x => x.HardwareId == "cpu/b/0").ToArray(), new float[2]);

        Assert.Equal(0, a.Refreshes);
        Assert.Equal(1, b.Refreshes);
    }

    [Fact]
    public void A_node_that_throws_gives_nan_for_its_sensors_and_never_throws_out_of_update()
    {
        var bad = new CountingNode { Throw = true };
        var good = new CountingNode { Value = 5 };
        using var s = Source(Module(bad, 1, "Bad", "bad"), Module(good, 1, "Good", "good"));
        var inv = s.Discover();
        Index(inv);
        var values = new float[] { 99, 99 };

        s.Update(inv.Sensors.ToArray(), values);

        Assert.True(float.IsNaN(values[0]));
        Assert.Equal(5f, values[1]);
        s.Update(inv.Sensors.ToArray(), values); // and again: the error is logged once, not thrown
        Assert.True(float.IsNaN(values[0]));
    }

    [Fact]
    public void A_foreign_descriptor_gets_nan()
    {
        using var s = Source();
        var foreign = Up.S(HardwareKind.Cpu, "x", SensorKind.Load, "y");
        foreign.Index = 0;
        var values = new float[] { 7 };

        s.Update([foreign], values);

        Assert.True(float.IsNaN(values[0]));
    }

    [Fact]
    public void Update_does_not_allocate()
    {
        var node = new CountingNode();
        using var s = Source(Module(node, 4));
        var inv = s.Discover();
        Index(inv);
        var due = inv.Sensors.ToArray();
        var values = new float[4];
        for (var i = 0; i < 50; i++) s.Update(due, values); // warm up

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) s.Update(due, values);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Gaps_are_checked_again_at_every_discovery()
    {
        // the module asks the upstream index whether the sensor exists; first it does not, then another source has it
        var seen = new List<bool>();
        var module = new FakeModule(b =>
        {
            var covered = b.Upstream.HasHardwareOfKind(HardwareKind.Cpu);
            seen.Add(covered);
            if (!covered) b.AddSensor(b.AddHardware(HardwareKind.Cpu, "Fallback CPU", 0), HardwareKind.Cpu, SensorKind.Load, "CPU Total", new CountingNode(), 0);
        });
        using var s = Source(module);

        s.SetUpstream([]);
        Assert.Single(s.Discover().Sensors);

        s.SetUpstream([Up.S(HardwareKind.Cpu, "AMD Ryzen 7 9800X3D", SensorKind.Load, "CPU Total")]);
        Assert.Empty(s.Discover().Sensors);

        Assert.Equal([false, true], seen);
    }

    [Fact]
    public void Our_own_sensors_from_an_earlier_discovery_do_not_count_as_upstream()
    {
        var module = new FakeModule(b =>
        {
            if (!b.Upstream.HasHardwareOfKind(HardwareKind.Cpu))
                b.AddSensor(b.AddHardware(HardwareKind.Cpu, "Fallback CPU", 0), HardwareKind.Cpu, SensorKind.Load, "CPU Total", new CountingNode(), 0);
        });
        using var s = Source(module);
        s.SetUpstream([]);
        var first = s.Discover();

        // the engine hands everything registered so far — including our own sensors — to the next SetUpstream
        s.SetUpstream(first.Sensors);

        Assert.Single(s.Discover().Sensors);
    }

    [Fact]
    public void Suspend_releases_the_modules_and_dispose_disposes_them()
    {
        var m = new FakeModule(_ => { });
        var s = Source(m);

        s.Suspend();
        s.Dispose();

        Assert.Equal(1, m.Released);
        Assert.Equal(1, m.Disposed);
    }

    [Fact]
    public void Memory_modules_are_exposed_after_discovery()
    {
        using var s = new NativeSource(new NativeSourceOptions { Drives = false, Gpu = false, Cpu = false, Network = false, MemoryUsageFallback = false });

        s.Discover();

        // a PC without SMBIOS memory info (a VM) is allowed to report none; this one has DDR5 modules
        Assert.All(s.MemoryModules, m => Assert.True(m.CapacityMb > 0));
        if (s.MemoryModules.Count > 0) Assert.All(s.MemoryModules, m => Assert.False(string.IsNullOrEmpty(m.Type)));
    }

    // ───────────────────────── with the real engine ─────────────────────────

    private static readonly long T0 = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc).Ticks;

    [Fact]
    public void The_engine_schedules_and_publishes_the_native_memory_speed_on_the_fake_cards()
    {
        var clock = T0;
        var native = new NativeSource(new NativeSourceOptions { Drives = false, Gpu = false, Cpu = false, Network = false, MemoryUsageFallback = false });
        using var engine = new SensorEngine([new FakeSensorSource(FakeProfile.DesktopAmdNvidia, 1, () => clock), native], () => clock);
        engine.DiscoverNow();

        var speed = engine.Sensors.FirstOrDefault(s => s.Name == "Memory Speed" && s.SourceTag is NativeTag);
        if (native.MemoryModules.Count == 0) return; // no SMBIOS memory data on this machine
        Assert.NotNull(speed);
        Assert.Equal("memory/generic-memory/0/clock/memory-speed", speed.Id); // on the fake card, no extra hardware
        Assert.DoesNotContain(engine.Hardware, h => h.Source == "native");

        using var demand = engine.Demand("test", [speed.Id], TimeSpan.FromSeconds(1));
        for (var i = 0; i < 10; i++)
        {
            clock += TimeSpan.FromMilliseconds(250).Ticks;
            engine.ApplyDemandNow();
            engine.Tick(clock);
        }

        Assert.Equal(native.MemoryModules.Min(m => m.EffectiveSpeedMTs), (int)engine.Latest[speed.Index]);
    }

    // ───────────────────────── on this PC (read-only, in the background) ─────────────────────────

    [Fact]
    public void Against_a_fake_upstream_no_sensor_is_duplicated_and_every_card_exists()
    {
        foreach (var profile in new[] { FakeProfile.DesktopAmdNvidia, FakeProfile.Minimal, FakeProfile.LaptopIntel })
        {
            var fake = new FakeSensorSource(profile).Discover();
            using var s = new NativeSource(new NativeSourceOptions { Gpu = true, Drives = true, Cpu = true, Network = true, Memory = true });
            s.SetUpstream(fake.Sensors);

            var inv = s.Discover();

            var upstreamIds = fake.Sensors.Select(x => x.Id).ToHashSet();
            var known = fake.Hardware.Select(h => h.Id).Concat(inv.Hardware.Select(h => h.Id)).ToHashSet();
            Assert.All(inv.Sensors, n =>
            {
                Assert.DoesNotContain(n.Id, upstreamIds);
                Assert.DoesNotContain(fake.Sensors, u => u.HardwareId == n.HardwareId && u.Kind == n.Kind && u.Name == n.Name);
                Assert.Contains(n.HardwareId, known);
            });
            Assert.Equal(inv.Sensors.Count, inv.Sensors.Select(x => x.Id).Distinct().Count());
            // the CPU / network fallbacks appear exactly when upstream has no such hardware (the minimal PC has no CPU)
            Assert.Equal(!fake.Hardware.Any(h => h.Kind == HardwareKind.Cpu), inv.Sensors.Any(n => n.HardwareKind == HardwareKind.Cpu));
            Assert.Equal(!fake.Hardware.Any(h => h.Kind == HardwareKind.Network), inv.Sensors.Any(n => n.HardwareKind == HardwareKind.Network));
        }
    }

    [Fact]
    public void Discover_and_update_on_this_pc_do_not_throw_return_sane_values_and_allocate_nothing()
    {
        using var s = new NativeSource();
        s.SetUpstream([]);
        var inv = s.Discover();
        Index(inv);
        var values = new float[inv.Sensors.Count];
        var due = inv.Sensors.ToArray();
        Assert.Equal(SourceState.Ok, s.Status.State);

        // warm up: JIT, lazy PDH open, rate windows
        for (var i = 0; i < 4; i++)
        {
            s.Update(due, values);
            Thread.Sleep(300);
        }

        long allocated = 0;
        for (var i = 0; i < 5; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            s.Update(due, values);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            Thread.Sleep(300);
        }

        Assert.Equal(0, allocated);
        foreach (var d in due)
        {
            var v = values[d.Index];
            if (float.IsNaN(v)) continue;
            Assert.True(float.IsFinite(v), $"{d.Id} = {v}");
            switch (d.Kind)
            {
                case SensorKind.Temperature: Assert.InRange(v, 0f, 126f); break;
                case SensorKind.Load: Assert.InRange(v, 0f, 100.01f); break;
                case SensorKind.Clock:
                case SensorKind.Fan:
                case SensorKind.Throughput:
                case SensorKind.Data:
                case SensorKind.SmallData: Assert.True(v >= 0, $"{d.Id} = {v}"); break;
            }
        }
        // the ids are unique and every sensor sits on one of the nodes the source created
        Assert.Equal(due.Length, due.Select(x => x.Id).Distinct().Count());
        var hardwareIds = inv.Hardware.Select(h => h.Id).ToHashSet();
        Assert.All(due, d => Assert.Contains(d.HardwareId, hardwareIds));
    }

    [Fact]
    public void Suspend_then_discover_again_reopens_everything()
    {
        using var s = new NativeSource(new NativeSourceOptions { Gpu = false });
        s.SetUpstream([]);
        var first = s.Discover();

        s.Suspend();
        s.Resume();
        var second = s.Discover();

        Assert.Equal(first.Sensors.Select(x => x.Id).Order(StringComparer.Ordinal), second.Sensors.Select(x => x.Id).Order(StringComparer.Ordinal));
    }
}
