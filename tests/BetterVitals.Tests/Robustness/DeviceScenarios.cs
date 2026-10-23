using System.Globalization;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Overlay;
using BetterVitals.Overlay.Rendering;
using BetterVitals.Sensors.Engine;
using BetterVitals.Sensors.Fake;
using BetterVitals.Services;
using BetterVitals.UI.Flyout;
using Xunit;
using Xunit.Abstractions;

namespace BetterVitals.Tests.Robustness;

/// <summary>PROJEKT §12: devices that vanish, reappear, fall asleep or misbehave (USB, eGPU, laptop dGPU, a broken driver).</summary>
public sealed class DeviceScenarios(ITestOutputHelper output)
{
    private const string Cpu = "AMD Ryzen 7 9800X3D";
    private const string Gpu = "NVIDIA GeForce RTX 5080";

    private sealed class EngineLookup(ISensorHub hub) : ISensorLookup
    {
        public SensorDescriptor? Find(string sensorId) => hub.Find(sensorId);

        public int Slot(SensorDescriptor sensor) => sensor.Index;
    }

    private static Scene SceneFor(ISensorHub hub, params SensorDescriptor[] sensors)
    {
        var layout = new OverlayLayout { Enabled = true, Items = [.. sensors.Select(s => new OverlayItem { SensorIds = [s.Id] })] };
        return new Scene(new SceneInput(layout, OverlayTheme.Default, new EngineLookup(hub), CultureInfo.InvariantCulture, TemperatureUnit.Celsius, null, 1));
    }

    private static string[] Shown(Scene scene, SensorEngine engine)
    {
        var f = engine.Latest;
        scene.Update(f.Values.AsSpan(0, Math.Min(f.Count, f.Values.Length)));
        return scene.Items.Select(i => new string(i.Main.Text)).ToArray();
    }

    // ───────────────────────── vanish and return ─────────────────────────

    [Fact]
    public async Task A_vanished_device_keeps_its_slots_leaves_a_gap_and_comes_back_while_the_others_carry_on()
    {
        using var app = new SimApp("dev-vanish");
        var pc = app.Pc;
        var cpuTemp = pc.Sensor(Cpu, SensorKind.Temperature, "Core (Tctl/Tdie)");
        var gpuTemp = pc.Sensor(Gpu, SensorKind.Temperature, "GPU Core");
        var gpuHot = pc.Sensor(Gpu, SensorKind.Temperature, "GPU Hot Spot");
        Assert.Contains(gpuTemp.Id, app.Recording.SensorIds);
        var slots = pc.Slots();
        var gpuHw = pc.HardwareId(Gpu);

        app.Run(TimeSpan.FromMinutes(2));
        var tUnplug = pc.Clock.Utc;
        pc.Fake.SetPresent(gpuHw, false);
        app.Run(TimeSpan.FromMinutes(3));
        var tReplug = pc.Clock.Utc;

        Assert.False(pc.Engine.IsPresent(gpuTemp.Index));
        Assert.True(float.IsNaN(pc.Engine.Latest[gpuTemp.Index]));
        Assert.True(float.IsNaN(pc.Engine.Latest[gpuHot.Index]));
        Assert.Null(pc.Engine.FindHardware(gpuHw));
        Assert.NotNull(pc.Engine.Find(gpuTemp.Id)); // the id still resolves: overlay items and history keep their slot
        Assert.False(float.IsNaN(pc.Engine.Latest[cpuTemp.Index]));
        Assert.All(slots, kv => Assert.Equal(kv.Value, pc.Engine.Find(kv.Key)!.Index));

        pc.Fake.SetPresent(gpuHw, true);
        app.Run(TimeSpan.FromMinutes(2));
        var tEnd = pc.Clock.Utc;
        Assert.True(pc.Engine.IsPresent(gpuTemp.Index));
        Assert.False(float.IsNaN(pc.Engine.Latest[gpuTemp.Index]));
        Assert.Equal(gpuTemp.Index, pc.Engine.Find(gpuTemp.Id)!.Index);
        Assert.NotNull(pc.Engine.FindHardware(gpuHw));
        Assert.All(slots, kv => Assert.Equal(kv.Value, pc.Engine.Find(kv.Key)!.Index));

        app.History.Suspend();
        var gpuSeries = await app.Series(gpuTemp.Id, pc.Clock.Utc.AddMinutes(-8), tEnd.AddMinutes(1));
        var cpuSeries = await app.Series(cpuTemp.Id, pc.Clock.Utc.AddMinutes(-8), tEnd.AddMinutes(1));
        var gap = gpuSeries.Where(p => p.TimeUtcTicks > tUnplug.AddSeconds(5).Ticks && p.TimeUtcTicks < tReplug.Ticks).ToList();
        output.WriteLine($"gpu points {gpuSeries.Count}, cpu points {cpuSeries.Count}, points inside the gap {gap.Count}");
        Assert.Empty(gap); // recorded as a gap, not as zeros or frozen values
        Assert.Contains(gpuSeries, p => p.TimeUtcTicks < tUnplug.Ticks);
        Assert.Contains(gpuSeries, p => p.TimeUtcTicks > tReplug.AddSeconds(3).Ticks);
        Assert.InRange(cpuSeries.Count, 7 * 60 / 2 - 6, 7 * 60 / 2 + 2); // the CPU never stopped (2 s samples over 7 minutes)
        Assert.Empty(app.Warnings);
    }

    [Fact]
    public void Overlay_values_of_a_vanished_sensor_turn_into_a_dash_and_recover_the_others_keep_their_numbers()
    {
        using var pc = new SimPc();
        var cpu = pc.Sensor(Cpu, SensorKind.Temperature, "Core (Tctl/Tdie)");
        var gpu = pc.Sensor(Gpu, SensorKind.Temperature, "GPU Core");
        var ram = pc.Sensor("Generic Memory", SensorKind.Load, "Memory");
        using var scene = SceneFor(pc.Engine, cpu, gpu, ram);
        using var demand = pc.Engine.Demand("overlay", [cpu.Id, gpu.Id, ram.Id], TimeSpan.FromSeconds(1));
        var gpuHw = pc.HardwareId(Gpu);

        pc.Run(TimeSpan.FromSeconds(5));
        var normal = Shown(scene, pc.Engine);
        Assert.DoesNotContain("—", normal);
        Assert.False(scene.HasUnresolved);
        var width = scene.Width;

        pc.Fake.SetPresent(gpuHw, false);
        pc.Run(TimeSpan.FromSeconds(6)); // debounce, rediscovery, next tick
        var gone = Shown(scene, pc.Engine);
        Assert.Equal("—", gone[1]);
        Assert.NotEqual("—", gone[0]);
        Assert.NotEqual("—", gone[2]);
        Assert.Equal(width, scene.Width); // the panel does not jump around

        pc.Fake.SetPresent(gpuHw, true);
        pc.Run(TimeSpan.FromSeconds(6));
        Assert.DoesNotContain("—", Shown(scene, pc.Engine));

        // a USB gadget that flaps ten times
        for (var i = 0; i < 10; i++)
        {
            pc.Fake.SetPresent(gpuHw, i % 2 == 1);
            pc.Run(TimeSpan.FromSeconds(3));
        }
        pc.Fake.SetPresent(gpuHw, true);
        pc.Run(TimeSpan.FromSeconds(6));
        Assert.DoesNotContain("—", Shown(scene, pc.Engine));
    }

    [Fact]
    public void A_sensor_that_does_not_exist_at_start_is_picked_up_when_the_device_appears()
    {
        var gpuHw = SensorIds.Hardware(HardwareKind.Gpu, Gpu, 0);
        var gpuId = SensorIds.Sensor(gpuHw, SensorKind.Temperature, "GPU Core");
        var cpuId = SensorIds.Sensor(SensorIds.Hardware(HardwareKind.Cpu, Cpu, 0), SensorKind.Temperature, "Core (Tctl/Tdie)");

        // a fresh engine where the card is not plugged in yet (hot-plug dock, driver still loading)
        using var pc = new SimPc(prepare: fake => fake.SetPresent(gpuHw, false));
        Assert.Null(pc.Engine.FindHardware(gpuHw));
        Assert.Null(pc.Engine.Find(gpuId));
        var layout = new OverlayLayout { Enabled = true, Items = [new OverlayItem { SensorIds = [cpuId] }, new OverlayItem { SensorIds = [gpuId] }] };
        using var host = new OverlayHost(new OverlayHostOptions { NeverShow = true });
        host.Start();
        host.Apply(layout, OverlayTheme.Default, new OverlayContext { Hub = pc.Engine, Animations = false });
        Assert.True(host.Diagnostics().HasDemand); // applied (the overlay thread has asked the engine for its sensors)
        pc.Run(TimeSpan.FromSeconds(5));
        host.RefreshNow();
        var values = host.ShownValues();
        Assert.NotEqual("—", values[0]);
        Assert.Equal("—", values[1]);

        pc.Fake.SetPresent(gpuHw, true);
        pc.Run(TimeSpan.FromSeconds(4)); // quiet period, rediscovery → InventoryChanged → the host rebuilds its scene and asks for the card
        Assert.True(SpinUntil(() => host.ShownValues()[1] != "—" && host.RefreshNow() | true, pc), "the card shows up on the overlay by itself");
        Assert.NotEqual("—", host.ShownValues()[0]);
    }

    private static bool SpinUntil(Func<bool> condition, SimPc pc)
    {
        for (var i = 0; i < 60; i++)
        {
            if (condition()) return true;
            pc.Run(TimeSpan.FromSeconds(1));
            Thread.Sleep(10);
        }
        return condition();
    }

    [Fact]
    public void The_live_overlay_window_keeps_showing_the_others_while_a_device_comes_and_goes()
    {
        using var pc = new SimPc();
        var cpu = pc.Sensor(Cpu, SensorKind.Temperature, "Core (Tctl/Tdie)");
        var gpu = pc.Sensor(Gpu, SensorKind.Temperature, "GPU Core");
        var ram = pc.Sensor("Generic Memory", SensorKind.Load, "Memory");
        var layout = new OverlayLayout { Enabled = true, Items = [.. new[] { cpu, gpu, ram }.Select(s => new OverlayItem { SensorIds = [s.Id] })] };
        using var host = new OverlayHost(new OverlayHostOptions { NeverShow = true });
        host.Start();
        host.Apply(layout, OverlayTheme.Default, new OverlayContext { Hub = pc.Engine, Animations = false });
        var gpuHw = pc.HardwareId(Gpu);

        Assert.True(host.Diagnostics().HasDemand); // applied
        pc.Run(TimeSpan.FromSeconds(4));
        Assert.Contains(pc.Engine.Demands, d => d.Consumer == "overlay" && d.Sensors == 3);
        host.RefreshNow();
        Assert.DoesNotContain("—", host.ShownValues());
        var renders = host.Diagnostics().Renders;

        for (var round = 0; round < 5; round++)
        {
            pc.Fake.SetPresent(gpuHw, false);
            pc.Run(TimeSpan.FromSeconds(5));
            host.RefreshNow();
            var gone = host.ShownValues();
            Assert.Equal("—", gone[1]);
            Assert.NotEqual("—", gone[0]);
            Assert.NotEqual("—", gone[2]);
            Assert.True(host.Diagnostics().SurfaceReady);

            pc.Fake.SetPresent(gpuHw, true);
            pc.Run(TimeSpan.FromSeconds(5));
            host.RefreshNow();
            Assert.DoesNotContain("—", host.ShownValues());
        }
        Assert.True(host.Diagnostics().Renders > renders); // it kept drawing
    }

    [Fact]
    public void A_device_that_flaps_does_not_make_the_engine_rediscover_in_a_storm()
    {
        using var pc = new SimPc(FakeProfile.Minimal, derived: false);
        var hw = pc.HardwareId("Ethernet");
        var before = pc.Source.Discovers;
        pc.Run(TimeSpan.FromSeconds(1));
        for (var i = 0; i < 100; i++)
        {
            pc.Fake.SetPresent(hw, i % 2 == 1);
            pc.Run(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        }
        Assert.True(pc.Source.Discovers - before <= 1, "no rediscovery while the events keep coming");
        pc.Fake.SetPresent(hw, true);
        pc.Run(TimeSpan.FromSeconds(4));
        Assert.Equal(before + 1, pc.Source.Discovers); // one, after the quiet period
        Assert.NotNull(pc.Engine.FindHardware(hw));
    }

    // ───────────────────────── asleep ─────────────────────────

    [Fact]
    public async Task A_sleeping_dGPU_reads_NaN_the_flyout_shows_a_dash_history_a_gap_and_it_wakes_up_again()
    {
        using var app = new SimApp("dev-asleep", FakeProfile.LaptopIntel);
        var pc = app.Pc;
        var gpuTemp = pc.Sensor("NVIDIA GeForce RTX 4060 Laptop GPU", SensorKind.Temperature, "GPU Core");
        var cpuTemp = pc.Sensor("Intel Core Ultra 7 155H", SensorKind.Temperature, "Core Max");
        var gpuHw = pc.HardwareId("NVIDIA GeForce RTX 4060 Laptop GPU");
        Assert.Contains(gpuTemp.Id, app.Recording.SensorIds);
        // the flyout's four tiles, updated the way FlyoutViewModel does it on every frame
        var tiles = SensorPresentation.KeySensors(pc.Engine, []).Select(s => new MetricTile(s)).ToList();
        using var flyoutDemand = pc.Engine.Demand("flyout", tiles.Select(t => t.Sensor.Id), TimeSpan.FromSeconds(1)); // what the open flyout asks for
        var tile = tiles.Single(t => t.Sensor.Id == gpuTemp.Id);
        var cpuTile = tiles.First(t => t.Sensor.HardwareKind == HardwareKind.Cpu && t.Sensor.Kind == SensorKind.Temperature);

        void RefreshTiles() => tiles.ForEach(t => t.Update(pc.Engine.Latest[t.Sensor.Index], TemperatureUnit.Celsius));

        app.Run(TimeSpan.FromMinutes(2));
        RefreshTiles();
        Assert.NotEqual("—", tile.Value);
        var tSleep = pc.Clock.Utc;
        var countBefore = pc.Engine.Stats.Count(gpuTemp.Index);

        pc.Fake.SetAsleep(gpuHw, true);
        app.Run(TimeSpan.FromMinutes(3));
        var tWake = pc.Clock.Utc;
        RefreshTiles();
        Assert.True(float.IsNaN(pc.Engine.Latest[gpuTemp.Index]));
        Assert.Equal("—", tile.Value); // the flyout's tile shows a dash, not a stale or zero value
        Assert.NotEqual("—", cpuTile.Value);
        Assert.True(pc.Engine.IsPresent(gpuTemp.Index), "asleep is not gone: the slot stays present");
        Assert.Equal(countBefore, pc.Engine.Stats.Count(gpuTemp.Index)); // the session min / max / average ignore the sleep
        Assert.InRange(pc.Engine.Stats.Max(gpuTemp.Index), 20, 120);

        pc.Fake.SetAsleep(gpuHw, false);
        app.Run(TimeSpan.FromMinutes(2));
        RefreshTiles();
        Assert.NotEqual("—", tile.Value);

        app.History.Suspend();
        var series = await app.Series(gpuTemp.Id, tSleep.AddMinutes(-2), pc.Clock.Utc.AddMinutes(1));
        Assert.DoesNotContain(series, p => p.TimeUtcTicks > tSleep.AddSeconds(8).Ticks && p.TimeUtcTicks < tWake.Ticks); // a gap while asleep
        Assert.Contains(series, p => p.TimeUtcTicks > tWake.AddSeconds(3).Ticks); // data again once awake
        Assert.NotEmpty(await app.Series(cpuTemp.Id, tSleep, tWake));
        Assert.Empty(app.Warnings);
    }

    // ───────────────────────── a source that throws ─────────────────────────

    [Fact]
    public void A_source_that_keeps_throwing_is_logged_once_a_minute_and_the_rest_of_the_app_keeps_running()
    {
        using var log = new LogCapture();
        var id = Guid.NewGuid().ToString("N")[..8];
        var healthy = new ScriptedSource("healthy-" + id, "Healthy", 3) { Value = i => 50 + i };
        var broken = new ScriptedSource("broken-" + id, "Broken", 3) { FailUpdate = new InvalidOperationException("driver went away") };
        using var pc = SimPc.With(healthy, broken);
        using var demand = pc.Engine.Demand("test", Enumerable.Range(0, 3).SelectMany(i => new[] { healthy.SensorId(i), broken.SensorId(i) }), TimeSpan.FromSeconds(1));
        var okSlot = pc.Engine.Find(healthy.SensorId(0))!.Index;
        var badSlot = pc.Engine.Find(broken.SensorId(0))!.Index;
        var frameFailures = 0;
        pc.Engine.FrameReady += f =>
        {
            if (float.IsNaN(f[okSlot])) frameFailures++; // the healthy source must never be hurt
        };

        pc.Run(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(1));

        var logged = log.MineWith("ERROR", $"engine: broken-{id} update failed");
        output.WriteLine($"frames {pc.Frames}, broken updates {broken.Updates}, logged {logged}");
        Assert.InRange(logged, 9, 11);
        Assert.True(broken.Updates > 500, "it keeps being asked (the driver may come back)");
        Assert.Equal(0, frameFailures);
        Assert.True(pc.Frames >= 590);
        Assert.Equal(50f, pc.Engine.Latest[okSlot]);
        Assert.True(float.IsNaN(pc.Engine.Latest[badSlot]));

        broken.FailUpdate = null; // the driver is back
        pc.Run(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1));
        Assert.Equal(40f, pc.Engine.Latest[badSlot]);
        Assert.Equal(logged, log.MineWith("ERROR", $"engine: broken-{id} update failed"));
    }

    [Fact]
    public void A_throwing_FrameReady_handler_does_not_stop_the_engine_nor_flood_the_log()
    {
        using var log = new LogCapture();
        var id = Guid.NewGuid().ToString("N")[..8];
        var source = new ScriptedSource("ok-" + id, "Ok", 2);
        using var pc = SimPc.With(source);
        using var demand = pc.Engine.Demand("test", [source.SensorId(0)], TimeSpan.FromSeconds(1));
        var calls = 0;
        pc.Engine.FrameReady += _ =>
        {
            calls++;
            throw new InvalidOperationException("consumer bug " + id);
        };
        var good = 0;
        pc.Engine.FrameReady += _ => good++;

        pc.Run(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(1));

        Assert.True(calls >= 590);
        Assert.Equal(calls, good); // the other handlers still run, every tick
        var lines = log.Mine.Count(l => l.Level == "ERROR" && l.Message.Contains("consumer bug " + id, StringComparison.Ordinal));
        output.WriteLine($"{calls} failing calls -> {lines} log entries");
        Assert.InRange(lines, 1, 15); // about once a minute, not once per tick
    }

    [Fact]
    public void A_source_whose_discovery_fails_comes_back_by_itself()
    {
        using var log = new LogCapture();
        var id = Guid.NewGuid().ToString("N")[..8];
        var healthy = new ScriptedSource("healthy-" + id, "Healthy", 2);
        var flaky = new ScriptedSource("flaky-" + id, "Flaky", 2) { FailDiscoveries = 1 }; // a driver that is not ready yet
        using var pc = SimPc.With(healthy, flaky);
        using var demand = pc.Engine.Demand("test", [healthy.SensorId(0), flaky.SensorId(0)], TimeSpan.FromSeconds(1));
        Assert.NotNull(pc.Engine.Find(healthy.SensorId(0)));
        Assert.Equal(1, log.MineWith("ERROR", $"engine: flaky-{id} discovery failed"));

        pc.Run(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(1));
        var slot = pc.Engine.Find(flaky.SensorId(0))?.Index ?? -1;
        Assert.True(slot >= 0, "the engine tried again and found the sensors");
        Assert.False(float.IsNaN(pc.Engine.Latest[slot]));
        Assert.False(float.IsNaN(pc.Engine.Latest[pc.Engine.Find(healthy.SensorId(0))!.Index]));
    }

    [Fact]
    public void A_source_whose_discovery_fails_later_does_not_make_its_known_sensors_vanish()
    {
        using var log = new LogCapture();
        var id = Guid.NewGuid().ToString("N")[..8];
        var healthy = new ScriptedSource("healthy-" + id, "Healthy", 2);
        var flaky = new ScriptedSource("flaky-" + id, "Flaky", 2);
        using var pc = SimPc.With(healthy, flaky);
        using var demand = pc.Engine.Demand("test", [healthy.SensorId(0), flaky.SensorId(0)], TimeSpan.FromSeconds(1));
        pc.Run(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
        var slot = pc.Engine.Find(flaky.SensorId(0))!.Index;
        Assert.False(float.IsNaN(pc.Engine.Latest[slot]));

        flaky.FailDiscoveries = 1; // one bad discovery (a transient error) while some other device changed
        healthy.RaiseInventoryChanged();
        pc.Run(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(1));

        Assert.True(flaky.Discovers >= 2);
        Assert.True(pc.Engine.IsPresent(slot), "a hiccup in discovery must not wipe the source's sensors");
        Assert.False(float.IsNaN(pc.Engine.Latest[slot]));
    }
}
