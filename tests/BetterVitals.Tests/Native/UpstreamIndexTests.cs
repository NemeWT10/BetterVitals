using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Native;
using BetterVitals.Sensors.Native.Cpu;
using Xunit;

namespace BetterVitals.Tests.Native;

/// <summary>Shared helpers to build upstream sensors the way another source would register them.</summary>
internal static class Up
{
    public static SensorDescriptor S(HardwareKind hw, string hardwareName, SensorKind kind, string name, int ordinal = 0, object? tag = null)
    {
        var hardwareId = SensorIds.Hardware(hw, hardwareName, ordinal);
        return new SensorDescriptor
        {
            Id = SensorIds.Sensor(hardwareId, kind, name),
            HardwareId = hardwareId,
            Name = name,
            Kind = kind,
            HardwareKind = hw,
            SourceTag = tag,
        };
    }

    public static UpstreamIndex Index(params SensorDescriptor[] sensors) => new(sensors);
}

public sealed class UpstreamIndexTests
{
    [Theory]
    [InlineData("storage/kingston-skc3000d2048g/0", "kingston-skc3000d2048g", 0)]
    [InlineData("gpu/nvidia-geforce-rtx-5080/1", "nvidia-geforce-rtx-5080", 1)]
    [InlineData("motherboard/gigabyte-x870e/0/motherboard/ite-it8696e/0", "ite-it8696e", 0)] // a parent prefix may precede kind/slug/ordinal
    [InlineData("weird", "", 0)]
    [InlineData("a/b", "", 0)]
    public void Hardware_ids_are_split_into_slug_and_ordinal(string id, string slug, int ordinal)
    {
        UpstreamIndex.ParseHardwareId(id, out var s, out var o);
        Assert.Equal(slug, s);
        Assert.Equal(ordinal, o);
    }

    [Theory]
    [InlineData("kingston-skc3000d2048g", "KINGSTON SKC3000D2048G", true)]
    [InlineData("samsung-ssd-860-evo-250gb", "Samsung SSD 860 EVO 250GB", true)]
    [InlineData("amd-radeon-tm-graphics", "AMD Radeon(TM) Graphics", true)]
    [InlineData("kingston-skc3000d2048g", "KINGSTON SKC3000D", true)]            // a driver may cut the model
    [InlineData("nvidia-geforce-rtx-5080", "GeForce RTX 5080", true)]            // a vendor prefix more or less
    [InlineData("st4000dm004-2cv104", "ST4000DM004-2U9104", false)]              // two different Seagates
    [InlineData("samsung-ssd-860-evo-250gb", "Samsung SSD 850 EVO 500GB", false)]
    [InlineData("kingston-skc3000d2048g", "KINGSTON SKC3000S1024G", false)]
    [InlineData("ssd", "Samsung SSD", false)]                                    // too short to be specific
    [InlineData("", "anything", false)]
    [InlineData("anything", "", false)]
    public void Names_match_loosely_but_not_between_different_models(string slug, string name, bool same)
        => Assert.Equal(same, UpstreamIndex.NameMatches(slug, name));

    [Fact]
    public void Match_prefers_the_same_ordinal_then_the_first_of_the_same_name()
    {
        var idx = Up.Index(
            Up.S(HardwareKind.Storage, "Samsung SSD 860 EVO 250GB", SensorKind.Load, "Used Space", 0),
            Up.S(HardwareKind.Storage, "Samsung SSD 860 EVO 250GB", SensorKind.Load, "Used Space", 1),
            Up.S(HardwareKind.Gpu, "Samsung SSD 860 EVO 250GB", SensorKind.Load, "x"));

        Assert.Equal("storage/samsung-ssd-860-evo-250gb/1", idx.Match(HardwareKind.Storage, "Samsung SSD 860 EVO 250GB", 1)!.Id);
        Assert.Equal("storage/samsung-ssd-860-evo-250gb/0", idx.Match(HardwareKind.Storage, "Samsung SSD 860 EVO 250GB", 0)!.Id);
        Assert.Equal("storage/samsung-ssd-860-evo-250gb/0", idx.Match(HardwareKind.Storage, "Samsung SSD 860 EVO 250GB", 7)!.Id); // no such ordinal: first
        Assert.Null(idx.Match(HardwareKind.Network, "Samsung SSD 860 EVO 250GB"));                                                   // other kind
        Assert.Equal(2, idx.MatchAll(HardwareKind.Storage, "Samsung SSD 860 EVO 250GB").Count);
    }

    [Fact]
    public void The_native_sources_own_earlier_sensors_are_not_upstream()
    {
        var own = Up.S(HardwareKind.Storage, "KINGSTON SKC3000D2048G", SensorKind.Temperature, "Temperature", tag: new NativeTag(new ConstantNode(1), 0));
        var idx = Up.Index(own, Up.S(HardwareKind.Cpu, "CPU", SensorKind.Load, "CPU Total"));

        Assert.False(idx.HasHardwareOfKind(HardwareKind.Storage));
        Assert.True(idx.HasHardwareOfKind(HardwareKind.Cpu));
        Assert.False(idx.ContainsSensorId(own.Id));
    }

    [Fact]
    public void Warning_and_critical_temperatures_are_limits_not_a_measurement()
    {
        var limitsOnly = Up.Index(
            Up.S(HardwareKind.Storage, "KINGSTON SKC3000D2048G", SensorKind.Temperature, "Warning Temperature"),
            Up.S(HardwareKind.Storage, "KINGSTON SKC3000D2048G", SensorKind.Temperature, "Critical Temperature"),
            Up.S(HardwareKind.Storage, "KINGSTON SKC3000D2048G", SensorKind.Load, "Used Space"));
        Assert.False(limitsOnly.Hardware[0].HasMeasuredTemperature());
        Assert.True(limitsOnly.Hardware[0].HasKind(SensorKind.Temperature));

        var measured = Up.Index(
            Up.S(HardwareKind.Storage, "KINGSTON SKC3000D2048G", SensorKind.Temperature, "Warning Temperature"),
            Up.S(HardwareKind.Storage, "KINGSTON SKC3000D2048G", SensorKind.Temperature, "Temperature"));
        Assert.True(measured.Hardware[0].HasMeasuredTemperature());
    }

    [Fact]
    public void Sensor_lookup_is_case_insensitive_by_name_and_exact_by_kind()
    {
        var hw = Up.Index(Up.S(HardwareKind.Gpu, "NVIDIA GeForce RTX 5080", SensorKind.Load, "D3D 3D")).Hardware[0];

        Assert.True(hw.Has(SensorKind.Load, "d3d 3d"));
        Assert.False(hw.Has(SensorKind.SmallData, "D3D 3D"));
        Assert.True(hw.HasNameContaining(SensorKind.Load, "3D"));
    }
}

public sealed class CoreMapTests
{
    /// <summary>One SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX record of RelationProcessorCore (48 bytes) with a single group mask.</summary>
    private static byte[] Core(ulong mask, ushort group, int relationship = 0)
    {
        var b = new byte[48];
        BitConverter.GetBytes(relationship).CopyTo(b, 0);
        BitConverter.GetBytes(48).CopyTo(b, 4);
        b[8] = 1; // Flags: LTP_PC_SMT
        BitConverter.GetBytes((ushort)1).CopyTo(b, 30); // GroupCount
        BitConverter.GetBytes(mask).CopyTo(b, 32);
        BitConverter.GetBytes(group).CopyTo(b, 40);
        return b;
    }

    private static byte[] Join(params byte[][] records) => records.SelectMany(r => r).ToArray();

    [Fact]
    public void Four_smt_cores_map_their_two_threads_each()
    {
        var buffer = Join(Core(0x03, 0), Core(0x0C, 0), Core(0x30, 0), Core(0xC0, 0));

        var (map, cores) = CoreMap.Parse(buffer, [0], 8);

        Assert.Equal(4, cores);
        Assert.Equal(new[] { 0, 0, 1, 1, 2, 2, 3, 3 }, map);
    }

    [Fact]
    public void Hybrid_cpu_has_smt_p_cores_and_single_thread_e_cores()
    {
        var buffer = Join(Core(0x03, 0), Core(0x0C, 0), Core(0x10, 0), Core(0x20, 0), Core(0x40, 0));

        var (map, cores) = CoreMap.Parse(buffer, [0], 7);

        Assert.Equal(5, cores);
        Assert.Equal(new[] { 0, 0, 1, 1, 2, 3, 4 }, map);
    }

    [Fact]
    public void Cores_are_numbered_by_their_first_thread_whatever_the_record_order()
    {
        var buffer = Join(Core(0x0C, 0), Core(0x03, 0));

        var (map, cores) = CoreMap.Parse(buffer, [0], 4);

        Assert.Equal(2, cores);
        Assert.Equal(new[] { 0, 0, 1, 1 }, map);
    }

    [Fact]
    public void Second_processor_group_starts_after_the_first_groups_threads()
    {
        // group 0 has 64 logical processors, group 1 starts at index 64
        var buffer = Join(Core(0x01, 0), Core(0x03, 1));

        var (map, cores) = CoreMap.Parse(buffer, [0, 64], 66);

        Assert.Equal(2, cores);
        Assert.Equal(0, map[0]);
        Assert.Equal(1, map[64]);
        Assert.Equal(1, map[65]);
        Assert.Equal(-1, map[1]);
    }

    [Fact]
    public void Other_relationships_are_skipped_and_an_empty_or_cut_buffer_falls_back_to_one_core_per_thread()
    {
        var cache = Core(0xFF, 0, relationship: 2);
        var (map, cores) = CoreMap.Parse(Join(cache, Core(0x03, 0)), [0], 2);
        Assert.Equal(1, cores);
        Assert.Equal(new[] { 0, 0 }, map);

        (map, cores) = CoreMap.Parse([], [0], 4);
        Assert.Equal(4, cores);
        Assert.Equal(new[] { 0, 1, 2, 3 }, map);

        (_, cores) = CoreMap.Parse(Core(0x03, 0).AsSpan(0, 40), [0], 2); // record cut off: nothing usable
        Assert.Equal(2, cores);
    }

    [Fact]
    public void A_core_mentioning_a_thread_beyond_the_count_does_not_overflow()
    {
        var (map, cores) = CoreMap.Parse(Core(0xFF00, 0), [0], 4);

        Assert.Equal(1, cores);
        Assert.Equal(new[] { -1, -1, -1, -1 }, map);
    }

    [Fact]
    public void The_os_answer_for_this_pc_is_consistent()
    {
        var logical = Environment.ProcessorCount;

        var (map, cores) = CoreMap.Read(logical);

        Assert.Equal(logical, map.Length);
        Assert.InRange(cores, 1, logical);
        Assert.All(map, c => Assert.InRange(c, -1, cores - 1));
    }

    [Theory]
    [InlineData("AMD Ryzen 7 9800X3D 8-Core Processor           ", "AMD Ryzen 7 9800X3D")]
    [InlineData("Intel(R) Core(TM) i7-12700K", "Intel Core i7-12700K")]
    [InlineData("13th Gen Intel(R) Core(TM) i9-13900K", "13th Gen Intel Core i9-13900K")]
    [InlineData("AMD Ryzen 5 3600 6-Core Processor", "AMD Ryzen 5 3600")]
    [InlineData("AMD Ryzen 9 5950X 16-Core Processor", "AMD Ryzen 9 5950X")]
    [InlineData("", "CPU")]
    public void Cpu_names_are_cleaned_like_librehardwaremonitor_does(string brand, string expected)
        => Assert.Equal(expected, LhmNames.Cpu(brand));
}
