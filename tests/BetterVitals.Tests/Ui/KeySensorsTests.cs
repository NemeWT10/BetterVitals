using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Engine;
using BetterVitals.Services;
using Xunit;

namespace BetterVitals.Tests.Ui;

public sealed class KeySensorsTests
{
    [Fact]
    public void Gpu_values_come_from_the_discrete_card_even_when_the_integrated_one_is_listed_first()
    {
        using var engine = new SensorEngine([new TwoGpus()]);
        engine.DiscoverNow();
        var picked = SensorPresentation.KeySensors(engine, [], 4);
        var gpu = picked.Where(s => s.HardwareKind == HardwareKind.Gpu).ToList();
        Assert.Equal(2, gpu.Count); // temperature and load
        Assert.All(gpu, s => Assert.Equal("gpu/rtx/0", s.HardwareId));
    }

    /// <summary>A Ryzen with Radeon graphics in the CPU, enumerated before the RTX card (as LHM does elevated).</summary>
    private sealed class TwoGpus : ISensorSource
    {
        public string Name => "fake";

        public SourceStatus Status => new(SourceState.Ok);

        public event Action? InventoryChanged
        {
            add { }
            remove { }
        }

        public SourceInventory Discover()
        {
            static HardwareDescriptor Hw(string id, string name) => new() { Id = id, Name = name, Kind = HardwareKind.Gpu, Source = "fake" };
            static SensorDescriptor S(string hw, SensorKind kind, string name)
                => new() { Id = $"{hw}/{kind}/{name}", HardwareId = hw, Name = name, Kind = kind, HardwareKind = HardwareKind.Gpu };
            return new SourceInventory(
                [Hw("gpu/igpu/0", "AMD Radeon(TM) Graphics"), Hw("gpu/rtx/0", "NVIDIA GeForce RTX 5080")],
                [
                    S("gpu/igpu/0", SensorKind.Temperature, "GPU Core"),
                    S("gpu/igpu/0", SensorKind.Load, "GPU Core"),
                    S("gpu/rtx/0", SensorKind.Temperature, "GPU Core"),
                    S("gpu/rtx/0", SensorKind.Temperature, "GPU Hot Spot"),
                    S("gpu/rtx/0", SensorKind.Load, "GPU Core"),
                ]);
        }

        public void Update(ReadOnlySpan<SensorDescriptor> requested, Span<float> values)
        {
            foreach (var d in requested) values[d.Index] = 50;
        }

        public void Dispose()
        {
        }
    }
}
