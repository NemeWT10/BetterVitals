using BetterVitals.Core.Catalog;
using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Derived;
using BetterVitals.Sensors.Engine;
using BetterVitals.Sensors.Fake;
using Xunit;

namespace BetterVitals.Tests.Engine;

public sealed class FriendlyCatalogTests
{
    private static (SensorEngine Engine, IReadOnlyList<FriendlyItem> Items) Resolve(FakeProfile profile)
    {
        var engine = new SensorEngine([new DerivedSource(), new FakeSensorSource(profile)]);
        engine.DiscoverNow();
        return (engine, FriendlyCatalog.Resolve(engine.Hardware, engine.Sensors));
    }

    private static string NameOf(SensorEngine e, FriendlyItem item) => e.Find(item.SensorIds[0])!.Name;

    [Fact]
    public void Desktop_amd_nvidia_maps_to_the_right_sensors()
    {
        var (e, items) = Resolve(FakeProfile.DesktopAmdNvidia);
        var byKey = items.ToDictionary(i => i.Key);

        Assert.Equal("Core (Tctl/Tdie)", NameOf(e, byKey["cpu.temp"]));
        Assert.Equal("NVIDIA GeForce RTX 5080", byKey["gpu.temp"].HardwareName); // the discrete card, not the Radeon iGPU
        Assert.Equal("GPU Hot Spot", NameOf(e, byKey["gpu.hotspot"]));
        Assert.Equal(8, byKey["cpu.clocks"].SensorIds.Count);
        Assert.True(byKey["cpu.clocks"].IsMulti);
        Assert.Equal(8, byKey["cpu.loads"].SensorIds.Count);
        Assert.Equal("CPU Clock Max", NameOf(e, byKey["cpu.clockMax"]));
        Assert.Equal("Core (SVI3 TFN)", NameOf(e, byKey["cpu.vcore"]));
        Assert.Equal("VRM MOS", NameOf(e, byKey["mb.vrm"]));
        Assert.Equal("DIMM Max", NameOf(e, byKey["ram.dimm"]));
        Assert.Equal(2, byKey["disk.temp"].SensorIds.Count); // NVMe + HDD
        Assert.Equal("Framerate", NameOf(e, byKey["fps.fps"]));

        var preset = FriendlyCatalog.KeyPresetSensorIds(items);
        Assert.InRange(preset.Count, 18, 45);
        Assert.All(FriendlyCatalog.DefaultOverlayKeys, k => Assert.Contains(k, byKey.Keys));
    }

    [Fact]
    public void Intel_laptop_uses_core_max_and_battery_does_not_break_anything()
    {
        var (e, items) = Resolve(FakeProfile.LaptopIntel);
        var byKey = items.ToDictionary(i => i.Key);
        Assert.Equal("Core Max", NameOf(e, byKey["cpu.temp"]));
        Assert.Equal(6, byKey["cpu.coreTemps"].SensorIds.Count);
        Assert.Equal("NVIDIA GeForce RTX 4060 Laptop GPU", byKey["gpu.temp"].HardwareName);
        Assert.Equal("CPU Package", NameOf(e, byKey["cpu.power"]));
    }

    [Fact]
    public void Basic_mode_offers_only_what_exists()
    {
        var (_, items) = Resolve(FakeProfile.Minimal);
        Assert.DoesNotContain(items, i => i.Category == HardwareKind.Cpu);
        Assert.Contains(items, i => i.Key == "gpu.temp");
        Assert.Contains(items, i => i.Key == "gpu.memJunction");
        Assert.DoesNotContain(items, i => i.Key == "gpu.hotspot");
    }

    [Fact]
    public void Every_item_has_both_translations()
    {
        var (_, items) = Resolve(FakeProfile.DesktopAmdNvidia);
        foreach (var item in items)
        {
            Assert.True(FriendlyCatalog.Texts.TryGetValue(item.LocKey, out var t), item.LocKey);
            Assert.False(string.IsNullOrWhiteSpace(t.Pl));
            Assert.False(string.IsNullOrWhiteSpace(t.En));
        }
    }
}
