using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Lhm;
using Xunit;

namespace BetterVitals.Tests.Lhm;

public class LhmIdsTests
{
    [Fact]
    public void Ids_follow_the_core_format()
    {
        var ids = LhmIds.HardwareIds(
        [
            new HardwareKey(-1, HardwareKind.Gpu, "NVIDIA GeForce RTX 5080", "PCI\\VEN_10DE&DEV_2C02\\A", 0),
            new HardwareKey(-1, HardwareKind.Motherboard, "Gigabyte X870E AORUS ELITE WIFI7", "/motherboard", 1),
            new HardwareKey(1, HardwareKind.Motherboard, "Nuvoton NCT6701D", "/lpc/nct6701d/0", 2),
        ]);
        Assert.Equal("gpu/nvidia-geforce-rtx-5080/0", ids[0]);
        Assert.Equal("motherboard/gigabyte-x870e-aorus-elite-wifi7/0", ids[1]);
        Assert.Equal("motherboard/gigabyte-x870e-aorus-elite-wifi7/0/motherboard/nuvoton-nct6701d/0", ids[2]);
    }

    [Fact]
    public void Identical_devices_keep_their_ids_when_lhm_reorders_them()
    {
        HardwareKey Drive(string serial, int order) => new(-1, HardwareKind.Storage, "KINGSTON SKC3000D2048G", serial, order);
        var first = LhmIds.HardwareIds([Drive("SERIAL-B", 0), Drive("SERIAL-A", 1)]);
        var second = LhmIds.HardwareIds([Drive("SERIAL-A", 0), Drive("SERIAL-B", 1)]);
        Assert.Equal("storage/kingston-skc3000d2048g/1", first[0]);  // B
        Assert.Equal("storage/kingston-skc3000d2048g/0", first[1]);  // A
        Assert.Equal("storage/kingston-skc3000d2048g/0", second[0]); // A
        Assert.Equal("storage/kingston-skc3000d2048g/1", second[1]); // B
    }

    [Fact]
    public void Two_gpus_ordered_by_pci_instance_not_by_adapter_index()
    {
        HardwareKey Gpu(string instance, int order) => new(-1, HardwareKind.Gpu, "NVIDIA GeForce RTX 5080", instance, order);
        var a = LhmIds.HardwareIds([Gpu(@"PCI\VEN_10DE&DEV_2C02\4&2&0&0008", 0), Gpu(@"PCI\VEN_10DE&DEV_2C02\4&1&0&0010", 1)]);
        var b = LhmIds.HardwareIds([Gpu(@"PCI\VEN_10DE&DEV_2C02\4&1&0&0010", 0), Gpu(@"PCI\VEN_10DE&DEV_2C02\4&2&0&0008", 1)]);
        Assert.Equal(a[0], b[1]);
        Assert.Equal(a[1], b[0]);
    }

    [Fact]
    public void Ordinals_are_per_parent_kind_and_name()
    {
        var ords = LhmIds.Ordinals(
        [
            new HardwareKey(-1, HardwareKind.Memory, "Total Memory", "/ram", 0),
            new HardwareKey(-1, HardwareKind.Memory, "Virtual Memory", "/vram", 1),
            new HardwareKey(-1, HardwareKind.Storage, "Total Memory", "x", 2),
            new HardwareKey(-1, HardwareKind.Memory, "Total Memory", "/ram2", 3),
        ]);
        Assert.Equal([0, 0, 0, 1], ords);
    }

    [Fact]
    public void Ties_fall_back_to_lhm_order()
    {
        var ords = LhmIds.Ordinals(
        [
            new HardwareKey(-1, HardwareKind.Network, "Ethernet", "same", 5),
            new HardwareKey(-1, HardwareKind.Network, "Ethernet", "same", 2),
        ]);
        Assert.Equal([1, 0], ords);
    }

    [Fact]
    public void Parent_after_child_is_rejected()
        => Assert.Throws<ArgumentException>(() => LhmIds.HardwareIds([new HardwareKey(0, HardwareKind.Cpu, "x", "x", 0)]));

    [Fact]
    public void Repeated_sensor_names_get_a_repeat_suffix_by_index()
    {
        const string hw = "gpu/nvidia-geforce-rtx-5080/0";
        var ids = LhmIds.SensorIdsFor(hw,
        [
            new SensorKey(SensorKind.Load, "D3D Copy", 12),
            new SensorKey(SensorKind.Load, "D3D Copy", 10),
            new SensorKey(SensorKind.Temperature, "GPU Hot Spot", 3),
            new SensorKey(SensorKind.Load, "D3D Copy", 11),
            new SensorKey(SensorKind.Load, "GPU Core", 0),
        ]);
        Assert.Equal($"{hw}/load/d3d-copy#3", ids[0]);
        Assert.Equal($"{hw}/load/d3d-copy", ids[1]);
        Assert.Equal($"{hw}/temperature/gpu-hot-spot", ids[2]);
        Assert.Equal($"{hw}/load/d3d-copy#2", ids[3]);
        Assert.Equal($"{hw}/load/gpu-core", ids[4]);
    }

    [Fact]
    public void Same_name_different_kind_is_not_a_repeat()
    {
        var ids = LhmIds.SensorIdsFor("gpu/amd/0",
        [
            new SensorKey(SensorKind.Temperature, "GPU Memory", 1),
            new SensorKey(SensorKind.Clock, "GPU Memory", 2),
            new SensorKey(SensorKind.Voltage, "GPU Memory", 1),
        ]);
        Assert.Equal(["gpu/amd/0/temperature/gpu-memory", "gpu/amd/0/clock/gpu-memory", "gpu/amd/0/voltage/gpu-memory"], ids);
    }

    [Fact]
    public void Slug_collisions_still_give_unique_ids()
    {
        var ids = LhmIds.SensorIdsFor("cpu/x/0",
        [
            new SensorKey(SensorKind.Voltage, "+3.3V", 0),
            new SensorKey(SensorKind.Voltage, "+3,3V", 1),
        ]);
        Assert.Equal(2, ids.Distinct().Count());
        Assert.Equal("cpu/x/0/voltage/plus3-3v", ids[0]);
        Assert.Equal("cpu/x/0/voltage/plus3-3v#2", ids[1]);
    }

    [Fact]
    public void Ids_are_deterministic_regardless_of_input_order()
    {
        SensorKey[] a = [new(SensorKind.Fan, "Fan #1", 0), new(SensorKind.Fan, "Fan #1", 1)];
        SensorKey[] b = [a[1], a[0]];
        var ia = LhmIds.SensorIdsFor("m/0", a);
        var ib = LhmIds.SensorIdsFor("m/0", b);
        Assert.Equal(ia[0], ib[1]);
        Assert.Equal(ia[1], ib[0]);
    }
}
