using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Fake;
using BetterVitals.Sensors.Native;
using BetterVitals.Sensors.Native.Cpu;
using BetterVitals.Sensors.Native.Drives;
using BetterVitals.Sensors.Native.Gpu;
using BetterVitals.Sensors.Native.Interop;
using BetterVitals.Sensors.Native.Memory;
using BetterVitals.Sensors.Native.Network;
using Xunit;
using static BetterVitals.Tests.Native.SmbiosSamples;

namespace BetterVitals.Tests.Native;

/// <summary>
/// "Add only what upstream does not have": every module is fed fake probe results and a fake upstream inventory, no hardware
/// is touched. Includes the situations seen with LibreHardwareMonitor on the reference PC (unelevated).
/// </summary>
public sealed class GapDetectionTests
{
    private static readonly TimeSpan Ten = TimeSpan.FromSeconds(10);

    private static void Same(string[] expected, IEnumerable<string> actual) => Assert.Equal(expected, actual.ToArray());

    // ───────────────────────── drives ─────────────────────────

    private static ProbedDrive Drive(int index, string model, bool rotating = false, TemperatureMethod method = TemperatureMethod.DeviceProperty)
        => new() { PhysicalIndex = index, Model = model, Rotating = rotating, Method = method, Temperature = 40 };

    private static InventoryBuilder RunDrives(IEnumerable<ProbedDrive> drives, params SensorDescriptor[] upstream)
    {
        var list = drives.ToList();
        var b = new InventoryBuilder(Up.Index(upstream));
        new DriveTemperatureModule(() => list).Discover(b);
        return b;
    }

    [Fact]
    public void A_drive_nobody_knows_gets_its_own_storage_node_and_a_temperature()
    {
        var b = RunDrives([Drive(4, "KINGSTON SKC3000D2048G")]);

        var hw = Assert.Single(b.Hardware);
        Assert.Equal("storage/kingston-skc3000d2048g/0", hw.Id);
        Assert.Equal("KINGSTON SKC3000D2048G", hw.Name);
        Assert.Equal(HardwareKind.Storage, hw.Kind);
        Assert.Equal("native", hw.Source);
        var s = Assert.Single(b.Sensors);
        Assert.Equal("storage/kingston-skc3000d2048g/0/temperature/temperature", s.Id);
        Assert.Equal("Temperature", s.Name);
        Assert.Equal(SensorKind.Temperature, s.Kind);
        Assert.Equal(HardwareKind.Storage, s.HardwareKind);
        Assert.Equal(Ten, s.MinInterval);
        Assert.IsType<NativeTag>(s.SourceTag);
    }

    [Fact]
    public void A_rotating_drive_is_read_less_often()
    {
        var b = RunDrives([Drive(0, "ST4000DM004-2CV104", rotating: true), Drive(2, "Samsung SSD 860 EVO 250GB")]);

        Assert.Equal(TimeSpan.FromSeconds(30), b.Sensors.Single(s => s.HardwareId.Contains("st4000")).MinInterval);
        Assert.Equal(Ten, b.Sensors.Single(s => s.HardwareId.Contains("samsung")).MinInterval);
    }

    [Fact]
    public void Upstream_temperature_covers_the_drive()
    {
        var b = RunDrives([Drive(4, "KINGSTON SKC3000D2048G")],
            Up.S(HardwareKind.Storage, "KINGSTON SKC3000D2048G", SensorKind.Temperature, "Composite Temperature"));

        Assert.Empty(b.Sensors);
        Assert.Empty(b.Hardware);
    }

    [Fact]
    public void Librehardwaremonitor_without_driver_registers_only_limits_so_the_gap_is_filled_on_its_card()
    {
        // seen on the reference PC: Kingston NVMe cards carry 'Warning Temperature' / 'Critical Temperature' (constants) but no reading
        var up = new[]
        {
            Up.S(HardwareKind.Storage, "KINGSTON SKC3000D2048G", SensorKind.Temperature, "Warning Temperature"),
            Up.S(HardwareKind.Storage, "KINGSTON SKC3000D2048G", SensorKind.Temperature, "Critical Temperature"),
            Up.S(HardwareKind.Storage, "KINGSTON SKC3000D2048G", SensorKind.Load, "Used Space"),
        };

        var b = RunDrives([Drive(4, "KINGSTON SKC3000D2048G", method: TemperatureMethod.NvmeHealthLog)], up);

        Assert.Empty(b.Hardware); // no duplicate card: the reading goes on LHM's
        var s = Assert.Single(b.Sensors);
        Assert.Equal("storage/kingston-skc3000d2048g/0", s.HardwareId);
        Assert.Equal("storage/kingston-skc3000d2048g/0/temperature/temperature", s.Id);
        Assert.True(s.Order >= 1000); // after the card's own sensors
    }

    [Fact]
    public void Of_two_identical_drives_upstream_covers_the_first_and_the_second_gets_a_sensor()
    {
        var up = new[] { Up.S(HardwareKind.Storage, "Samsung SSD 860 EVO 250GB", SensorKind.Temperature, "Temperature", ordinal: 0) };

        var b = RunDrives([Drive(2, "Samsung SSD 860 EVO 250GB"), Drive(3, "Samsung SSD 860 EVO 250GB")], up);

        var s = Assert.Single(b.Sensors);
        Assert.Equal("storage/samsung-ssd-860-evo-250gb/1/temperature/temperature", s.Id);
        Assert.Equal("storage/samsung-ssd-860-evo-250gb/1", Assert.Single(b.Hardware).Id);
    }

    [Fact]
    public void Two_identical_drives_nobody_knows_get_ordinals_0_and_1()
    {
        var b = RunDrives([Drive(2, "Samsung SSD 860 EVO 250GB"), Drive(3, "Samsung SSD 860 EVO 250GB")]);

        Same(["storage/samsung-ssd-860-evo-250gb/0", "storage/samsung-ssd-860-evo-250gb/1"], b.Hardware.Select(h => h.Id));
    }

    [Fact]
    public void Drives_that_do_not_answer_get_no_sensor_and_different_models_do_not_cover_each_other()
    {
        var up = new[] { Up.S(HardwareKind.Storage, "ST4000DM004-2CV104", SensorKind.Temperature, "Temperature") };

        var b = RunDrives([
            Drive(0, "ST4000DM004-2CV104", true),                                               // covered
            Drive(1, "ST4000DM004-2U9104", true),                                               // a different Seagate: not covered
            Drive(2, "Samsung SSD 850 EVO 500GB", method: TemperatureMethod.None),              // firmware does not answer
        ], up);

        Assert.Equal("storage/st4000dm004-2u9104/0/temperature/temperature", Assert.Single(b.Sensors).Id);
    }

    [Fact]
    public void A_sleeping_rotating_drive_found_at_discovery_gets_no_sensor_until_a_later_discovery()
    {
        var asleep = new ProbedDrive { PhysicalIndex = 0, Model = "ST4000DM004-2CV104", Rotating = true, Asleep = true, Method = TemperatureMethod.None };

        Assert.Empty(RunDrives([asleep]).Sensors);
    }

    [Fact]
    public void Against_the_fake_desktop_upstream_only_the_uncovered_drives_are_added()
    {
        var fake = new FakeSensorSource(FakeProfile.DesktopAmdNvidia).Discover().Sensors;
        var drives = new[]
        {
            Drive(0, "ST4000DM004-2CV104", true),   // fake has it with 'Temperature'
            Drive(1, "ST4000DM004-2U9104", true),
            Drive(2, "Samsung SSD 850 EVO 500GB"),
            Drive(3, "Samsung SSD 860 EVO 250GB"),
            Drive(4, "KINGSTON SKC3000D2048G"),     // fake has it with 'Composite Temperature'
            Drive(5, "KINGSTON SKC3000S1024G"),
        };

        var b = RunDrives(drives, [.. fake]);

        Same(
            ["KINGSTON SKC3000S1024G", "ST4000DM004-2U9104", "Samsung SSD 850 EVO 500GB", "Samsung SSD 860 EVO 250GB"],
            b.Hardware.Select(h => h.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Discovering_again_gives_the_same_ids()
    {
        var drives = new List<ProbedDrive> { Drive(4, "KINGSTON SKC3000D2048G"), Drive(2, "Samsung SSD 860 EVO 250GB") };
        var module = new DriveTemperatureModule(() => drives);
        var first = new InventoryBuilder(UpstreamIndex.Empty);
        var second = new InventoryBuilder(UpstreamIndex.Empty);

        module.Discover(first);
        module.Discover(second);

        Assert.Equal(first.Sensors.Select(s => s.Id), second.Sensors.Select(s => s.Id));
    }

    // ───────────────────────── memory ─────────────────────────

    private static MemoryModule Ram(bool usage = true) => new(() => Ddr5Pc(), usage);

    private static float ValueOf(SensorDescriptor s)
    {
        var tag = (NativeTag)s.SourceTag!;
        tag.Node.Refresh(0);
        return tag.Node.Read(tag.Slot);
    }

    [Fact]
    public void Memory_speed_goes_on_the_physical_memory_card_not_on_virtual_memory()
    {
        // LibreHardwareMonitor registers 'Virtual Memory' first, then 'Total Memory', both with the same sensor names
        var up = new[]
        {
            Up.S(HardwareKind.Memory, "Virtual Memory", SensorKind.Data, "Memory Used"),
            Up.S(HardwareKind.Memory, "Virtual Memory", SensorKind.Load, "Memory"),
            Up.S(HardwareKind.Memory, "Total Memory", SensorKind.Data, "Memory Used"),
            Up.S(HardwareKind.Memory, "Total Memory", SensorKind.Data, "Memory Available"),
            Up.S(HardwareKind.Memory, "Total Memory", SensorKind.Load, "Memory"),
        };
        var b = new InventoryBuilder(Up.Index(up));

        var module = Ram();
        module.Discover(b);

        Assert.Empty(b.Hardware);
        var speed = Assert.Single(b.Sensors); // and no usage fallback, upstream has usage
        Assert.Equal("memory/total-memory/0/clock/memory-speed", speed.Id);
        Assert.Equal("Memory Speed", speed.Name);
        Assert.Equal(SensorKind.Clock, speed.Kind);
        Assert.Equal("MT/s", speed.Unit);
        Assert.Equal(TimeSpan.FromSeconds(60), speed.MinInterval);
        Assert.Equal(6000f, ValueOf(speed));
        Assert.Equal(2, module.Modules.Count);
    }

    [Fact]
    public void Without_any_upstream_memory_the_native_source_adds_a_total_memory_node_with_speed_and_usage()
    {
        var b = new InventoryBuilder(UpstreamIndex.Empty);
        Ram().Discover(b);

        var hw = Assert.Single(b.Hardware);
        Assert.Equal("Total Memory", hw.Name); // the name LibreHardwareMonitor uses
        Assert.Equal("memory/total-memory/0", hw.Id);
        Assert.Equal(HardwareKind.Memory, hw.Kind);
        Same(["Memory Speed", "Memory Used", "Memory Available", "Memory"], b.Sensors.Select(s => s.Name));

        var used = ValueOf(b.Sensors.Single(s => s.Name == "Memory Used"));
        var load = ValueOf(b.Sensors.Single(s => s.Name == "Memory"));
        Assert.InRange(used, 0.1f, 4096f);
        Assert.InRange(load, 0.1f, 100f);
    }

    [Fact]
    public void Dimm_only_upstream_memory_cards_are_not_used_for_the_speed()
    {
        var up = new[] { Up.S(HardwareKind.Memory, "Kingston DIMM #1", SensorKind.Temperature, "Temperature") };
        var b = new InventoryBuilder(Up.Index(up));

        Ram().Discover(b);

        Assert.Equal("memory/total-memory/0", Assert.Single(b.Hardware).Id);
    }

    [Fact]
    public void An_upstream_memory_speed_is_not_duplicated_and_partial_usage_disables_the_fallback()
    {
        var up = new[]
        {
            Up.S(HardwareKind.Memory, "Generic Memory", SensorKind.Clock, "Memory Speed"),
            Up.S(HardwareKind.Memory, "Generic Memory", SensorKind.Load, "Memory"),
        };
        var b = new InventoryBuilder(Up.Index(up));

        var module = Ram();
        module.Discover(b);

        Assert.Empty(b.Sensors);
        Assert.Equal(2, module.Modules.Count); // the module list is exposed all the same
    }

    [Fact]
    public void Fake_desktop_and_minimal_pcs_get_the_speed_on_their_generic_memory_card()
    {
        foreach (var profile in new[] { FakeProfile.DesktopAmdNvidia, FakeProfile.Minimal, FakeProfile.LaptopIntel })
        {
            var up = new FakeSensorSource(profile).Discover().Sensors;
            var b = new InventoryBuilder(Up.Index([.. up]));
            Ram().Discover(b);

            var speed = Assert.Single(b.Sensors);
            Assert.Equal("memory/generic-memory/0/clock/memory-speed", speed.Id);
            Assert.Empty(b.Hardware);
        }
    }

    [Fact]
    public void No_smbios_means_no_speed_and_an_empty_module_list_but_the_usage_fallback_still_works()
    {
        var module = new MemoryModule(() => null, usageFallback: true);
        var b = new InventoryBuilder(UpstreamIndex.Empty);

        module.Discover(b);

        Assert.Empty(module.Modules);
        Assert.DoesNotContain(b.Sensors, s => s.Name == "Memory Speed");
        Assert.Contains(b.Sensors, s => s.Name == "Memory Used");
    }

    [Fact]
    public void Usage_fallback_can_be_switched_off()
    {
        var b = new InventoryBuilder(UpstreamIndex.Empty);

        Ram(usage: false).Discover(b);

        Assert.Equal("Memory Speed", Assert.Single(b.Sensors).Name);
    }

    [Fact]
    public void Speed_is_the_slowest_configured_speed_else_the_slowest_rated_one_else_nothing()
    {
        MemoryModuleInfo M(int rated, int configured) => new("A", "B", "DDR5", "DIMM", 16384, rated, configured, "", "");

        Assert.Equal(5600, MemoryModule.SpeedOf([M(6000, 6000), M(6000, 5600)]));
        Assert.Equal(4800, MemoryModule.SpeedOf([M(4800, 0), M(6000, 0)]));
        Assert.Equal(6000, MemoryModule.SpeedOf([M(6000, 6000), M(50, 50)]));  // 50 MT/s is not plausible
        Assert.Equal(0, MemoryModule.SpeedOf([M(0, 0)]));
        Assert.Equal(0, MemoryModule.SpeedOf([]));
    }

    // ───────────────────────── GPU ─────────────────────────

    private static readonly GpuPerfProbe Everything = new(true, Temperature: true, Fan: true, MemoryClock: true, Power: true, CoreClock: true, CoreVoltage: true, NodeOrdinal: 0);

    private static string[] Names(IEnumerable<GpuModule.Planned> planned) => planned.Select(p => $"{p.Kind}/{p.Name}").ToArray();

    [Fact]
    public void An_adapter_nobody_covers_gets_everything_the_driver_and_the_counters_report()
    {
        var plan = GpuModule.PlanSensors("Intel(R) UHD Graphics 770", 0, Everything, pdh: true, engineCounter: true, UpstreamIndex.Empty, out var hw);

        Assert.Null(hw);
        Same(
            ["Temperature/GPU Core", "Fan/GPU Fan", "Clock/GPU Memory", "Clock/GPU Core", "Voltage/GPU Core", "Load/GPU Power", "Load/D3D 3D",
             "SmallData/D3D Dedicated Memory Used", "SmallData/D3D Shared Memory Used"],
            Names(plan));
    }

    [Fact]
    public void Librehardwaremonitor_without_driver_already_covers_the_reference_nvidia_card()
    {
        // what LHM registers for the RTX 5080 on the reference PC, unelevated (kinds and names verbatim)
        var up = Up.Index(
            Up.S(HardwareKind.Gpu, "NVIDIA GeForce RTX 5080", SensorKind.Temperature, "GPU Core"),
            Up.S(HardwareKind.Gpu, "NVIDIA GeForce RTX 5080", SensorKind.Temperature, "GPU Memory Junction"),
            Up.S(HardwareKind.Gpu, "NVIDIA GeForce RTX 5080", SensorKind.Clock, "GPU Core"),
            Up.S(HardwareKind.Gpu, "NVIDIA GeForce RTX 5080", SensorKind.Clock, "GPU Memory"),
            Up.S(HardwareKind.Gpu, "NVIDIA GeForce RTX 5080", SensorKind.Fan, "GPU Fan 1"),
            Up.S(HardwareKind.Gpu, "NVIDIA GeForce RTX 5080", SensorKind.Load, "GPU Core"),
            Up.S(HardwareKind.Gpu, "NVIDIA GeForce RTX 5080", SensorKind.Load, "GPU Power"),
            Up.S(HardwareKind.Gpu, "NVIDIA GeForce RTX 5080", SensorKind.Load, "D3D 3D"),
            Up.S(HardwareKind.Gpu, "NVIDIA GeForce RTX 5080", SensorKind.Voltage, "GPU Core Voltage"),
            Up.S(HardwareKind.Gpu, "NVIDIA GeForce RTX 5080", SensorKind.Power, "GPU Package"),
            Up.S(HardwareKind.Gpu, "NVIDIA GeForce RTX 5080", SensorKind.Data, "D3D Dedicated Memory Used"),   // LHM: Data, not SmallData
            Up.S(HardwareKind.Gpu, "NVIDIA GeForce RTX 5080", SensorKind.Data, "D3D Shared Memory Used"));

        var plan = GpuModule.PlanSensors("NVIDIA GeForce RTX 5080", 0, Everything, pdh: true, engineCounter: true, up, out var hw);

        Assert.Empty(plan);
        Assert.Equal("gpu/nvidia-geforce-rtx-5080/0", hw!.Id);
    }

    [Fact]
    public void Only_the_missing_pieces_are_added_to_the_upstream_card()
    {
        var up = Up.Index(
            Up.S(HardwareKind.Gpu, "NVIDIA GeForce RTX 5080", SensorKind.Load, "GPU Core"),
            Up.S(HardwareKind.Gpu, "NVIDIA GeForce RTX 5080", SensorKind.Data, "GPU Memory Used"));

        var plan = GpuModule.PlanSensors("NVIDIA GeForce RTX 5080", 0, Everything, pdh: true, engineCounter: true, up, out var hw);

        Assert.NotNull(hw);
        Same(
            ["Temperature/GPU Core", "Fan/GPU Fan", "Clock/GPU Memory", "Clock/GPU Core", "Voltage/GPU Core", "Load/GPU Power", "SmallData/D3D Shared Memory Used"],
            Names(plan));
    }

    [Fact]
    public void Limits_do_not_count_as_a_gpu_temperature_and_a_watt_reading_replaces_the_percentage()
    {
        var up = Up.Index(
            Up.S(HardwareKind.Gpu, "AMD Radeon(TM) Graphics", SensorKind.Temperature, "Critical Temperature"),
            Up.S(HardwareKind.Gpu, "AMD Radeon(TM) Graphics", SensorKind.Power, "GPU Core"));

        var plan = GpuModule.PlanSensors("AMD Radeon(TM) Graphics", 0, Everything, pdh: false, engineCounter: false, up, out _);

        Assert.Contains("Temperature/GPU Core", Names(plan));
        Assert.DoesNotContain("Load/GPU Power", Names(plan));
    }

    [Fact]
    public void Without_counters_or_driver_data_nothing_is_planned_for_those_parts()
    {
        var onlyTemp = new GpuPerfProbe(true, Temperature: true, Fan: false, MemoryClock: false, Power: false, CoreClock: false, CoreVoltage: false, NodeOrdinal: 0);

        Same(["Temperature/GPU Core"], Names(GpuModule.PlanSensors("X Graphics", 0, onlyTemp, pdh: false, engineCounter: false, UpstreamIndex.Empty, out _)));
        Assert.Empty(GpuModule.PlanSensors("X Graphics", 0, GpuPerfProbe.None, pdh: false, engineCounter: false, UpstreamIndex.Empty, out _));
        Same(["SmallData/D3D Dedicated Memory Used", "SmallData/D3D Shared Memory Used"], // the load needs the engine counter
            Names(GpuModule.PlanSensors("X Graphics", 0, GpuPerfProbe.None, pdh: true, engineCounter: false, UpstreamIndex.Empty, out _)));
    }

    private const uint FakeHandle = 0x7EDCBA98;

    private static GpuAdapterInfo Adapter(string name, uint flags, uint handle = FakeHandle, bool address = true, int luid = 1)
        => new() { Name = name, TypeFlags = flags, Handle = handle, HasAddress = address, Address = new AdapterAddress { BusNumber = 1 }, Luid = new Luid { Low = (uint)luid } };

    private const uint RenderDisplay = AdapterFlags.Render | AdapterFlags.Display;

    private static class AdapterFlags
    {
        public const uint Render = GpuAdapterInfo.TypeRenderSupported;
        public const uint Display = GpuAdapterInfo.TypeDisplaySupported;
        public const uint HybridDiscrete = GpuAdapterInfo.TypeHybridDiscrete;
    }

    private sealed class TestGate(bool awake) : IDevicePowerGate
    {
        public bool Awake { get; set; } = awake;

        public int Calls { get; private set; }

        public bool IsAwake(string deviceInstanceId)
        {
            Calls++;
            return Awake;
        }
    }

    private static InventoryBuilder RunGpu(GpuModule module, params SensorDescriptor[] upstream)
    {
        var b = new InventoryBuilder(Up.Index(upstream));
        module.Discover(b);
        return b;
    }

    [Fact]
    public void Adapter_hardware_filter_drops_software_and_indirect_display_adapters()
    {
        Assert.True(GpuAdapterInfo.IsHardware(0x2323));                                         // the Radeon iGPU of the reference PC
        Assert.True(GpuAdapterInfo.IsHardware(0x231B));                                         // the RTX 5080
        Assert.False(GpuAdapterInfo.IsHardware(RenderDisplay | GpuAdapterInfo.TypeSoftwareDevice)); // Microsoft Basic Render Driver
        Assert.False(GpuAdapterInfo.IsHardware(RenderDisplay | GpuAdapterInfo.TypeIndirectDisplay));
        Assert.False(GpuAdapterInfo.IsHardware(0));
    }

    [Fact]
    public void Only_adapters_that_can_power_down_need_the_power_gate()
    {
        Assert.True(Adapter("dGPU", RenderDisplay | AdapterFlags.HybridDiscrete).NeedsPowerGate);
        Assert.True(Adapter("eGPU", RenderDisplay | GpuAdapterInfo.TypeDetachable).NeedsPowerGate);
        Assert.False(Adapter("iGPU", RenderDisplay | GpuAdapterInfo.TypeHybridIntegrated).NeedsPowerGate);
        Assert.False(Adapter("desktop", RenderDisplay).NeedsPowerGate);
    }

    [Fact]
    public void Module_uses_an_unknown_adapter_name_for_its_own_node_and_numbers_equal_names()
    {
        var module = new GpuModule(new TestGate(true), null,
            enumerate: () => [Adapter("Fake GPU", RenderDisplay, luid: 1), Adapter("Fake GPU", RenderDisplay, luid: 2)],
            probe: _ => new GpuPerfProbe(true, true, false, false, false, false, false, 0),
            findInstanceId: _ => null, usePdh: false);

        var b = RunGpu(module);

        Same(["gpu/fake-gpu/0", "gpu/fake-gpu/1"], b.Hardware.Select(h => h.Id));
        Assert.All(b.Hardware, h => Assert.Equal(HardwareKind.Gpu, h.Kind));
        module.Release();
    }

    [Fact]
    public void Module_attaches_to_the_upstream_card_without_adding_hardware()
    {
        var module = new GpuModule(new TestGate(true), null,
            enumerate: () => [Adapter("NVIDIA GeForce RTX 5080", RenderDisplay)],
            probe: _ => Everything, findInstanceId: _ => null, usePdh: false);

        var b = RunGpu(module, Up.S(HardwareKind.Gpu, "NVIDIA GeForce RTX 5080", SensorKind.Load, "GPU Core"));

        Assert.Empty(b.Hardware);
        Assert.All(b.Sensors, s => Assert.Equal("gpu/nvidia-geforce-rtx-5080/0", s.HardwareId));
        Assert.All(b.Sensors, s => Assert.Equal(TimeSpan.FromSeconds(1), s.MinInterval));
        module.Release();
    }

    [Fact]
    public void A_sleeping_adapter_gives_nan_and_the_gate_is_asked_at_most_every_ten_seconds()
    {
        var gate = new TestGate(true);
        var module = new GpuModule(gate, null,
            enumerate: () => [Adapter("Laptop dGPU", RenderDisplay | AdapterFlags.HybridDiscrete)],
            probe: _ => new GpuPerfProbe(true, true, true, false, false, false, false, 0),
            findInstanceId: _ => @"PCI\FAKE\1", usePdh: false);
        var b = RunGpu(module);
        var temp = b.Sensors.Single(s => s.Kind == SensorKind.Temperature);
        var node = ((NativeTag)temp.SourceTag!).Node;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var second = System.Diagnostics.Stopwatch.Frequency;

        var atDiscovery = gate.Calls;
        gate.Awake = false; // the card goes to sleep after discovery
        for (var i = 0; i < 6; i++) node.Refresh(t0 + i * second); // six reads over five seconds
        Assert.True(float.IsNaN(node.Read(0)));
        Assert.Equal(atDiscovery + 1, gate.Calls);

        node.Refresh(t0 + 11 * second);                            // the window is over: one more look
        Assert.Equal(atDiscovery + 2, gate.Calls);
        Assert.True(float.IsNaN(node.Read(0)));
        module.Release();
    }

    [Fact]
    public void An_adapter_asleep_at_discovery_is_probed_once_when_it_wakes_and_a_rediscovery_is_requested()
    {
        var gate = new TestGate(false);
        var rediscover = 0;
        var probes = 0;
        var module = new GpuModule(gate, () => rediscover++,
            enumerate: () => [Adapter("Laptop dGPU", RenderDisplay | AdapterFlags.HybridDiscrete)],
            probe: _ => { probes++; return Everything; },
            findInstanceId: _ => @"PCI\FAKE\1", usePdh: true);
        var b = RunGpu(module);

        // asleep: the driver was not asked; only the performance-counter sensors exist
        Assert.Equal(0, probes);
        Assert.DoesNotContain(b.Sensors, s => s.Kind == SensorKind.Temperature);
        Assert.Contains(b.Sensors, s => s.Name == "D3D 3D");

        var node = ((NativeTag)b.Sensors[0].SourceTag!).Node;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        node.Refresh(now);
        Assert.Equal(0, probes);
        Assert.Equal(0, rediscover);

        gate.Awake = true;
        node.Refresh(now + 20 * System.Diagnostics.Stopwatch.Frequency); // past the recheck window
        Assert.Equal(1, probes);
        Assert.Equal(1, rediscover);

        node.Refresh(now + 21 * System.Diagnostics.Stopwatch.Frequency);
        Assert.Equal(1, probes); // only once
        module.Release();
    }

    // ───────────────────────── CPU and network ─────────────────────────

    [Fact]
    public void Cpu_load_fallback_exists_only_without_any_upstream_cpu()
    {
        var empty = new InventoryBuilder(UpstreamIndex.Empty);
        new CpuLoadModule(() => "AMD Ryzen 7 9800X3D 8-Core Processor           ").Discover(empty);

        var hw = Assert.Single(empty.Hardware);
        Assert.Equal("AMD Ryzen 7 9800X3D", hw.Name);
        Assert.Equal("cpu/amd-ryzen-7-9800x3d/0", hw.Id);
        Assert.Equal("CPU Total", empty.Sensors[0].Name);
        Assert.True(empty.Sensors.Count >= 2);
        Assert.Equal("CPU Core #1", empty.Sensors[1].Name);
        Assert.All(empty.Sensors, s => Assert.Equal(SensorKind.Load, s.Kind));
        Assert.Equal(empty.Sensors.Count, empty.Sensors.Select(s => s.Id).Distinct().Count());

        var covered = new InventoryBuilder(Up.Index(Up.S(HardwareKind.Cpu, "AMD Ryzen 7 9800X3D", SensorKind.Load, "CPU Total")));
        new CpuLoadModule(() => "AMD Ryzen 7 9800X3D").Discover(covered);
        Assert.Empty(covered.Sensors);
        Assert.Empty(covered.Hardware);
    }

    [Fact]
    public void Cpu_load_is_a_percentage_once_a_window_has_passed()
    {
        var b = new InventoryBuilder(UpstreamIndex.Empty);
        var module = new CpuLoadModule(() => "Test CPU");
        module.Discover(b);
        var node = ((NativeTag)b.Sensors[0].SourceTag!).Node;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 150) { /* burn some CPU so there is something to measure */ }
        node.Refresh(System.Diagnostics.Stopwatch.GetTimestamp());

        var total = node.Read(0);
        Assert.InRange(total, 0f, 100f);
        for (var core = 1; core < b.Sensors.Count; core++) Assert.InRange(node.Read(core), 0f, 100f);
        module.Release();
    }

    [Fact]
    public void A_cpu_window_older_than_thirty_seconds_only_rebases()
    {
        var b = new InventoryBuilder(UpstreamIndex.Empty);
        var module = new CpuLoadModule(() => "Test CPU");
        module.Discover(b);
        var node = ((NativeTag)b.Sensors[0].SourceTag!).Node;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();

        node.Refresh(now + 31 * System.Diagnostics.Stopwatch.Frequency);

        Assert.True(float.IsNaN(node.Read(0)));
        module.Release();
    }

    [Theory]
    [InlineData(6u, (byte)1, "Intel(R) Ethernet Controller (3) I225-V", true)]
    [InlineData(71u, (byte)1, "Intel(R) Wi-Fi 6 AX201 160MHz", true)]
    [InlineData(243u, (byte)1, "Mobile broadband", true)]
    [InlineData(6u, (byte)0, "Hyper-V Virtual Ethernet Adapter", false)]                 // not a hardware interface
    [InlineData(6u, (byte)3, "Realtek PCIe 2.5GbE Family Controller-WFP Native MAC Layer LightWeight Filter-0000", false)] // filter interface
    [InlineData(24u, (byte)1, "Loopback Pseudo-Interface 1", false)]
    [InlineData(131u, (byte)1, "Teredo Tunneling Pseudo-Interface", false)]
    [InlineData(6u, (byte)1, "Bluetooth Device (Personal Area Network)", false)]
    [InlineData(71u, (byte)1, "Microsoft Wi-Fi Direct Virtual Adapter #2", false)]
    [InlineData(6u, (byte)1, "VMware Virtual Ethernet Adapter for VMnet8", false)]
    public void Physical_adapter_filter(uint type, byte flags, string description, bool physical)
        => Assert.Equal(physical, NetworkModule.IsPhysical(type, flags, description));

    [Fact]
    public void Network_fallback_exists_only_without_any_upstream_network_and_names_adapters_after_their_connection()
    {
        var module = new NetworkModule();
        var found = NetworkModule.Enumerate();
        var b = new InventoryBuilder(UpstreamIndex.Empty);

        module.Discover(b);

        Assert.True(b.Hardware.Count <= found.Count);
        Assert.All(b.Hardware, h => Assert.Equal(HardwareKind.Network, h.Kind));
        Assert.All(b.Hardware, h => Assert.Contains(found, a => a.Name == h.Name));
        Assert.Equal(b.Hardware.Count * 2, b.Sensors.Count);
        Assert.All(b.Sensors, s => Assert.Equal(SensorKind.Throughput, s.Kind));
        Assert.All(b.Sensors, s => Assert.Contains(s.Name, new[] { "Download Speed", "Upload Speed" }));

        var covered = new InventoryBuilder(Up.Index(Up.S(HardwareKind.Network, "Ethernet", SensorKind.Throughput, "Download Speed")));
        module.Discover(covered);
        Assert.Empty(covered.Sensors);
        Assert.Empty(covered.Hardware);
        module.Release();
    }

    [Fact]
    public void An_adapter_that_cannot_be_read_gets_no_sensor()
    {
        var module = new NetworkModule(() => [new NetworkAdapter(uint.MaxValue - 5, "Ghost", "Ghost adapter")]);
        var b = new InventoryBuilder(UpstreamIndex.Empty);

        module.Discover(b);

        Assert.Empty(b.Sensors);
        Assert.Empty(b.Hardware);
    }
}
