using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Lhm;
using LibreHardwareMonitor.Hardware;
using Xunit;

namespace BetterVitals.Tests.Lhm;

public class LhmClassifierTests
{
    [Theory]
    [InlineData(HardwareType.Cpu, HardwareKind.Cpu)]
    [InlineData(HardwareType.GpuNvidia, HardwareKind.Gpu)]
    [InlineData(HardwareType.GpuAmd, HardwareKind.Gpu)]
    [InlineData(HardwareType.GpuIntel, HardwareKind.Gpu)]
    [InlineData(HardwareType.Motherboard, HardwareKind.Motherboard)]
    [InlineData(HardwareType.SuperIO, HardwareKind.Motherboard)]
    [InlineData(HardwareType.EmbeddedController, HardwareKind.Motherboard)]
    [InlineData(HardwareType.Memory, HardwareKind.Memory)]
    [InlineData(HardwareType.Storage, HardwareKind.Storage)]
    [InlineData(HardwareType.Network, HardwareKind.Network)]
    [InlineData(HardwareType.Battery, HardwareKind.Battery)]
    [InlineData(HardwareType.Psu, HardwareKind.Psu)]
    [InlineData(HardwareType.Cooler, HardwareKind.Cooler)]
    [InlineData(HardwareType.PowerMonitor, HardwareKind.Controller)]
    public void Maps_hardware_types(HardwareType type, HardwareKind expected)
        => Assert.Equal(expected, LhmClassifier.MapHardware(type));

    [Fact]
    public void Every_lhm_hardware_type_has_a_specific_kind()
    {
        foreach (var t in Enum.GetValues<HardwareType>())
            Assert.NotEqual(HardwareKind.Other, LhmClassifier.MapHardware(t));
        Assert.Equal(HardwareKind.Other, LhmClassifier.MapHardware((HardwareType)200));
    }

    [Fact]
    public void Every_lhm_sensor_type_maps_by_name()
    {
        foreach (var t in Enum.GetValues<SensorType>())
        {
            var kind = LhmClassifier.MapSensor(t);
            Assert.NotNull(kind);
            Assert.Equal(t.ToString(), kind.Value.ToString());
        }
        Assert.Null(LhmClassifier.MapSensor((SensorType)250));
    }

    [Fact]
    public void Data_is_converted_from_bytes_to_gb()
    {
        Assert.Equal(1f, LhmClassifier.Scale(SensorType.Data) * 1024f * 1024f * 1024f, 3);
        Assert.Equal(1f, LhmClassifier.Scale(SensorType.Temperature));
        Assert.Equal(16f, LhmClassifier.Sanitize(16f * 1024 * 1024 * 1024, LhmClassifier.Scale(SensorType.Data), hotSpotLike: false), 3);
    }

    [Theory]
    [InlineData("GPU Hot Spot", true)]
    [InlineData("GPU Hot Spot #1", true)]
    [InlineData("GPU Hot Spot #6", true)]
    [InlineData("GPU Memory #1", true)]
    [InlineData("GPU Memory #48", true)]
    [InlineData("GPU Memory Junction", true)]
    [InlineData("GPU Memory", false)]
    [InlineData("GPU Core", false)]
    [InlineData("GPU Hot Spot #", false)]
    [InlineData("GPU Hot Spot #x", false)]
    public void Hot_spot_like_names(string name, bool expected)
    {
        var flags = LhmClassifier.Classify(HardwareType.GpuNvidia, "NVIDIA GeForce RTX 4090", SensorType.Temperature, name);
        Assert.Equal(expected, (flags & LhmSensorFlags.HotSpotLike) != 0);
    }

    [Fact]
    public void Hot_spot_names_on_a_cpu_or_as_a_load_are_ignored()
    {
        Assert.Equal(LhmSensorFlags.None, LhmClassifier.Classify(HardwareType.Cpu, "AMD Ryzen", SensorType.Temperature, "GPU Hot Spot"));
        Assert.Equal(LhmSensorFlags.None, LhmClassifier.Classify(HardwareType.GpuNvidia, "NVIDIA GeForce RTX 5080", SensorType.Load, "GPU Memory #1"));
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5080", "GPU Hot Spot", true)]
    [InlineData("NVIDIA GeForce RTX 5090", "GPU Hot Spot #3", true)]
    [InlineData("NVIDIA GeForce RTX 5070 Ti", "GPU Memory #12", true)]
    [InlineData("NVIDIA GeForce RTX 5080", "GPU Memory Junction", false)] // NVAPI, no driver
    [InlineData("NVIDIA GeForce RTX 4090", "GPU Hot Spot", false)] // NVAPI on RTX 40
    [InlineData("NVIDIA GeForce RTX 4090", "GPU Hot Spot #1", true)]
    [InlineData("NVIDIA RTX PRO 6000 Blackwell", "GPU Hot Spot", false)]
    public void Driver_backed_nvidia_sensors(string gpu, string sensor, bool expected)
    {
        var flags = LhmClassifier.Classify(HardwareType.GpuNvidia, gpu, SensorType.Temperature, sensor);
        Assert.Equal(expected, (flags & LhmSensorFlags.DriverBacked) != 0);
    }

    [Fact]
    public void Amd_gpu_hot_spot_is_not_driver_backed()
    {
        var flags = LhmClassifier.Classify(HardwareType.GpuAmd, "AMD Radeon RX 7900 XTX", SensorType.Temperature, "GPU Hot Spot");
        Assert.Equal(LhmSensorFlags.HotSpotLike, flags);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5080", "GPU Hot Spot", "sensor.note.unofficialHotspot")]
    [InlineData("NVIDIA GeForce RTX 5080", "GPU Hot Spot #2", "sensor.note.unofficialHotspot")]
    [InlineData("NVIDIA GeForce RTX 5080", "GPU Memory Junction", null)]
    [InlineData("NVIDIA GeForce RTX 5080", "GPU Core", null)]
    [InlineData("NVIDIA GeForce RTX 4080", "GPU Hot Spot", null)]
    [InlineData("NVIDIA RTX PRO 6000 Blackwell", "GPU Hot Spot", null)]
    public void Note_only_on_rtx50_hot_spot(string gpu, string sensor, string? expected)
        => Assert.Equal(expected, LhmClassifier.NoteKey(HardwareType.GpuNvidia, gpu, SensorType.Temperature, sensor));

    [Theory]
    [InlineData(0f)]
    [InlineData(255f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Hot_spot_invalid_readings_are_nan(float raw)
        => Assert.True(float.IsNaN(LhmClassifier.Sanitize(raw, 1f, hotSpotLike: true)));

    [Fact]
    public void Sanitize_keeps_valid_values_and_maps_null()
    {
        Assert.Equal(71.5f, LhmClassifier.Sanitize(71.5f, 1f, hotSpotLike: true));
        Assert.Equal(0f, LhmClassifier.Sanitize(0f, 1f, hotSpotLike: false)); // 0 % load is a real value
        Assert.True(float.IsNaN(LhmClassifier.Sanitize(null, 1f, hotSpotLike: false)));
    }

    [Theory]
    [InlineData("Temperature", SensorType.Temperature, 10)]
    [InlineData("Temperature 2", SensorType.Temperature, 10)]
    [InlineData("Life", SensorType.Level, 60)]
    [InlineData("Data Read", SensorType.Data, 60)]
    [InlineData("Data Written", SensorType.Data, 60)]
    [InlineData("Power On Hours", SensorType.Factor, 60)]
    [InlineData("Available Spare", SensorType.Level, 60)]
    [InlineData("Read Rate", SensorType.Throughput, 10)]
    [InlineData("Total Activity", SensorType.Load, 10)]
    [InlineData("Used Space", SensorType.Load, 10)]
    public void Storage_min_intervals(string name, SensorType type, int seconds)
    {
        var flags = LhmClassifier.Classify(HardwareType.Storage, "Samsung SSD 850 EVO 500GB", type, name);
        Assert.Equal(TimeSpan.FromSeconds(seconds), LhmClassifier.MinInterval(HardwareType.Storage, type, flags));
    }

    [Fact]
    public void Smart_need_only_for_smart_backed_storage_sensors()
    {
        Assert.True((LhmClassifier.Classify(HardwareType.Storage, "x", SensorType.Temperature, "Temperature") & LhmSensorFlags.NeedsSmart) != 0);
        Assert.True((LhmClassifier.Classify(HardwareType.Storage, "x", SensorType.Data, "Data Read") & LhmSensorFlags.NeedsSmart) != 0);
        Assert.Equal(LhmSensorFlags.None, LhmClassifier.Classify(HardwareType.Storage, "x", SensorType.Throughput, "Write Rate"));
        Assert.Equal(LhmSensorFlags.None, LhmClassifier.Classify(HardwareType.Storage, "x", SensorType.Data, "Free Space"));
    }

    [Fact]
    public void Smart_counters_are_marked_but_temperatures_are_not()
    {
        Assert.True((LhmClassifier.Classify(HardwareType.Storage, "x", SensorType.Level, "Available Spare") & LhmSensorFlags.SmartCounter) != 0);
        Assert.True((LhmClassifier.Classify(HardwareType.Storage, "x", SensorType.Data, "Data Written") & LhmSensorFlags.SmartCounter) != 0);
        Assert.Equal(LhmSensorFlags.NeedsSmart, LhmClassifier.Classify(HardwareType.Storage, "x", SensorType.Temperature, "Temperature"));
    }

    [Fact]
    public void Other_min_intervals()
    {
        Assert.Equal(TimeSpan.Zero, LhmClassifier.MinInterval(HardwareType.Cpu, SensorType.Temperature, LhmSensorFlags.None));
        Assert.Equal(TimeSpan.Zero, LhmClassifier.MinInterval(HardwareType.Network, SensorType.Data, LhmSensorFlags.None));
        var hot = LhmClassifier.Classify(HardwareType.GpuNvidia, "NVIDIA GeForce RTX 5080", SensorType.Temperature, "GPU Hot Spot");
        Assert.Equal(TimeSpan.FromSeconds(1), LhmClassifier.MinInterval(HardwareType.GpuNvidia, SensorType.Temperature, hot));
        // NVML PCIe throughput blocks the engine thread ~20 ms per counter: every 2 s at most; other GPU sensors stay free
        var pcie = LhmClassifier.Classify(HardwareType.GpuNvidia, "NVIDIA GeForce RTX 5080", SensorType.Throughput, "GPU PCIe Tx");
        Assert.Equal(TimeSpan.FromSeconds(2), LhmClassifier.MinInterval(HardwareType.GpuNvidia, SensorType.Throughput, pcie));
        var load = LhmClassifier.Classify(HardwareType.GpuNvidia, "NVIDIA GeForce RTX 5080", SensorType.Load, "GPU Core");
        Assert.Equal(TimeSpan.Zero, LhmClassifier.MinInterval(HardwareType.GpuNvidia, SensorType.Load, load));
    }

    [Fact]
    public void Gpu_cost_flags()
    {
        Assert.True((LhmClassifier.Classify(HardwareType.GpuNvidia, "x", SensorType.Throughput, "GPU PCIe Rx") & LhmSensorFlags.NeedsPcie) != 0);
        Assert.True((LhmClassifier.Classify(HardwareType.GpuAmd, "x", SensorType.Load, "D3D 3D") & LhmSensorFlags.NeedsD3d) != 0);
        Assert.True((LhmClassifier.Classify(HardwareType.GpuNvidia, "x", SensorType.Data, "D3D Dedicated Memory Used") & LhmSensorFlags.NeedsD3d) != 0);
        Assert.Equal(LhmSensorFlags.None, LhmClassifier.Classify(HardwareType.GpuNvidia, "x", SensorType.Load, "GPU Core"));
        Assert.Equal(LhmSensorFlags.None, LhmClassifier.Classify(HardwareType.GpuAmd, "x", SensorType.Throughput, "GPU PCIe Rx"));
    }

    [Theory]
    [InlineData(HardwareType.Cpu, "/amdcpu/0", true)]
    [InlineData(HardwareType.Motherboard, "/motherboard", true)]
    [InlineData(HardwareType.SuperIO, "/lpc/nct6701d/0", true)]
    [InlineData(HardwareType.EmbeddedController, "/lpc/ec/0", true)]
    [InlineData(HardwareType.Memory, "/memory/dimm/2", true)]
    [InlineData(HardwareType.Memory, "/ram", false)]
    [InlineData(HardwareType.GpuIntel, "/gpu-intel-integrated/0", true)]
    [InlineData(HardwareType.GpuIntel, "/gpu-intel/0x56A0", false)]
    [InlineData(HardwareType.GpuNvidia, "/gpu-nvidia/0", false)]
    [InlineData(HardwareType.Storage, "/nvme/0", false)]
    public void Driver_backed_hardware(HardwareType type, string identifier, bool expected)
        => Assert.Equal(expected, LhmClassifier.IsDriverBackedHardware(type, identifier));

    [Theory]
    [InlineData(HardwareType.Cpu, "/amdcpu/0", SensorType.Temperature, true)]
    [InlineData(HardwareType.Cpu, "/amdcpu/0", SensorType.Voltage, true)]
    [InlineData(HardwareType.Cpu, "/amdcpu/0", SensorType.Clock, true)]
    [InlineData(HardwareType.Cpu, "/amdcpu/0", SensorType.Load, false)]
    [InlineData(HardwareType.SuperIO, "/lpc/nct6701d/0", SensorType.Fan, true)]
    [InlineData(HardwareType.Memory, "/memory/dimm/0", SensorType.Temperature, true)]
    [InlineData(HardwareType.Memory, "/ram", SensorType.Data, false)]
    [InlineData(HardwareType.GpuIntel, "/gpu-intel-integrated/0", SensorType.Power, true)]
    [InlineData(HardwareType.GpuIntel, "/gpu-intel-integrated/0", SensorType.Load, false)]
    [InlineData(HardwareType.GpuNvidia, "/gpu-nvidia/0", SensorType.Temperature, false)]
    public void Driver_only_sensors(HardwareType type, string identifier, SensorType sensorType, bool expected)
        => Assert.Equal(expected, LhmClassifier.NeedsDriver(type, identifier, sensorType));

    [Theory]
    [InlineData("Ethernet 2-QoS Packet Scheduler-0000", true)]
    [InlineData("Ethernet 2-WFP Native MAC Layer LightWeight Filter-0000", true)]
    [InlineData("vSwitch (Default Switch)-Hyper-V Virtual Switch Extension Filter-0000", true)]
    [InlineData("Ethernet 2", false)]
    [InlineData("Wi-Fi", false)]
    [InlineData("Ethernet-0001", false)]
    [InlineData("Filter", false)]
    public void Network_filter_pseudo_adapters(string name, bool expected)
        => Assert.Equal(expected, LhmClassifier.IsNetworkFilterPseudoAdapter(name));
}
