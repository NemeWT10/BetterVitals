using BetterVitals.Core.Sensors;

namespace BetterVitals.Sensors.Engine;

/// <summary>
/// A source whose sensors are computed from other sensors (max of cores, sum of throughputs…). The engine tells it
/// what the other sources found before calling <see cref="ISensorSource.Discover"/>, updates it after all the others
/// in the same tick (so it reads this tick's inputs from the frame values), and reads inputs whenever an output is due.
/// </summary>
public interface IDependentSource : ISensorSource
{
    /// <summary>Everything the other sources registered (with <see cref="SensorDescriptor.Index"/> set).</summary>
    void SetUpstream(IReadOnlyList<SensorDescriptor> upstream);

    /// <summary>Slots an output reads; the engine schedules them together with the output.</summary>
    ReadOnlySpan<int> InputsOf(SensorDescriptor output);
}
