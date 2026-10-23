using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Engine;
using Xunit;

namespace BetterVitals.Tests.Robustness;

/// <summary>PROJEKT §4 / §5: the engine is the only place that touches hardware, so it must shrug off whatever a source or a late caller does.</summary>
public sealed class EngineScenarios
{
    [Fact]
    public void Calls_that_arrive_after_the_engine_was_disposed_do_nothing_and_throw_nothing()
    {
        // a window or the overlay thread can release its demand a moment after the app began to shut down
        var source = new ScriptedSource("late-" + Guid.NewGuid().ToString("N")[..8], "Late", 4);
        var engine = new SensorEngine([source]);
        engine.Start();
        Assert.True(SpinWait.SpinUntil(() => engine.Sensors.Count == 4, 5000));
        var demand = engine.Demand("x", [source.SensorId(0)], TimeSpan.FromSeconds(1));
        var other = engine.Demand("y", [source.SensorId(1)], TimeSpan.FromSeconds(1));
        engine.Dispose();

        demand.Update([source.SensorId(2)], TimeSpan.FromSeconds(2));
        demand.Dispose();
        demand.Dispose();
        using var later = engine.Demand("z", [source.SensorId(3)], TimeSpan.FromSeconds(1));
        later.Update([], TimeSpan.FromSeconds(1));
        engine.NotifySuspend();
        engine.NotifyResume();
        engine.RequestRediscover();
        engine.ResetStats();
        engine.FrameReady += _ => { };
        engine.InventoryChanged += () => { };
        _ = engine.Latest[0];
        _ = engine.Find(source.SensorId(0));
        _ = engine.Sensors.Count;
        _ = engine.SourceStatuses;
        other.Dispose();
        engine.Dispose(); // twice
    }

    [Fact]
    public void Values_that_are_not_numbers_never_poison_the_statistics_or_the_recording()
    {
        var source = new ScriptedSource("odd-" + Guid.NewGuid().ToString("N")[..8], "Odd", 4, SensorKind.Temperature);
        using var pc = SimPc.With(source);
        using var demand = pc.Engine.Demand("test", Enumerable.Range(0, 4).Select(source.SensorId), TimeSpan.FromSeconds(1));
        float[] feed = [50, float.PositiveInfinity, 51, float.NegativeInfinity, 52, float.NaN, 53, float.MaxValue, -273.15f, 54];
        var i = 0;
        source.Value = ordinal => ordinal == 0 ? feed[i++ % feed.Length] : 40;
        pc.Run(TimeSpan.FromSeconds(60));

        var slot = pc.Engine.Find(source.SensorId(0))!.Index;
        var stats = pc.Engine.Stats;
        Assert.True(float.IsFinite(stats.Average(slot)), "one Infinity must not make the average infinite for the rest of the session");
        Assert.True(float.IsFinite(stats.Min(slot)));
        Assert.True(float.IsFinite(stats.Max(slot)));
        Assert.Equal(40f, stats.Average(pc.Engine.Find(source.SensorId(1))!.Index));
    }

    [Fact]
    public void A_source_that_misbehaves_in_its_inventory_or_writes_where_it_should_not_is_contained()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var healthy = new ScriptedSource("healthy-" + id, "Healthy", 3);
        using var pc = SimPc.With(healthy, new Misbehaving("odd-" + id), new ScriptedSource("twin-" + id, "Healthy", 3)); // a second source with the very same ids
        using var demand = pc.Engine.Demand("test", pc.Engine.Sensors.Select(s => s.Id), TimeSpan.FromSeconds(1));
        pc.Run(TimeSpan.FromSeconds(30));
        Assert.True(pc.Frames > 20);
        Assert.False(float.IsNaN(pc.Engine.Latest[pc.Engine.Find(healthy.SensorId(0))!.Index]));
    }

    /// <summary>Reports the same sensor twice, one with an index-hungry write, and a descriptor whose name is empty.</summary>
    private sealed class Misbehaving(string name) : ISensorSource
    {
        public string Name { get; } = name;

        public SourceStatus Status => new(SourceState.Ok);

        public event Action? InventoryChanged
        {
            add { }
            remove { }
        }

        public SourceInventory Discover()
        {
            var hw = new HardwareDescriptor { Id = "other/odd/0", Name = "", Kind = HardwareKind.Other, Source = Name };
            SensorDescriptor Make(string n) => new() { Id = SensorIds.Sensor(hw.Id, SensorKind.Load, n), HardwareId = hw.Id, Name = n, Kind = SensorKind.Load, HardwareKind = HardwareKind.Other };
            return new SourceInventory([hw, hw], [Make("a"), Make("a"), Make(""), Make("  ")]);
        }

        public void Update(ReadOnlySpan<SensorDescriptor> requested, Span<float> values)
        {
            foreach (var d in requested) values[d.Index + 100_000] = 1; // an off-by-a-lot bug: the span check throws
        }

        public void Dispose()
        {
        }
    }
}
