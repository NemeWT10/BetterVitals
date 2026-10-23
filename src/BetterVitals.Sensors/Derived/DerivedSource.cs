using System.Text.RegularExpressions;
using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Engine;

namespace BetterVitals.Sensors.Derived;

/// <summary>
/// Values computed from other sensors, attached to the same hardware so they appear in its card: max / average of
/// the CPU cores (temperature, clock, load) and the hottest DIMM.
/// Created only where they add something (e.g. no "CPU Core Max" when the CPU already reports one, none for 1 core).
/// Names are English like the hardware's own (never translated).
/// </summary>
public sealed partial class DerivedSource : IDependentSource
{
    private enum Op : byte { Max, Average }

    private sealed record Spec(Op Op, int[] Inputs);

    private IReadOnlyList<SensorDescriptor> _upstream = [];

    public string Name => "derived";

    public SourceStatus Status => new(SourceState.Ok);

    public event Action? InventoryChanged { add { } remove { } }

    public void SetUpstream(IReadOnlyList<SensorDescriptor> upstream) => _upstream = upstream;

    public ReadOnlySpan<int> InputsOf(SensorDescriptor output) => output.SourceTag is Spec s ? s.Inputs : [];

    public SourceInventory Discover()
    {
        var outputs = new List<SensorDescriptor>();
        foreach (var hw in _upstream.GroupBy(s => s.HardwareId))
        {
            var sensors = hw.ToList();
            var kind = sensors[0].HardwareKind;
            switch (kind)
            {
                case HardwareKind.Cpu:
                {
                    var hasOwnMax = sensors.Any(s => s.Kind == SensorKind.Temperature && s.Name.Equals("Core Max", StringComparison.OrdinalIgnoreCase));
                    var coreTemps = sensors.Where(s => s.Kind == SensorKind.Temperature && (CoreName().IsMatch(s.Name) || CcdName().IsMatch(s.Name))).ToList();
                    if (!hasOwnMax && coreTemps.Count >= 2)
                    {
                        Add(outputs, hw.Key, kind, SensorKind.Temperature, "CPU Core Max", Op.Max, coreTemps);
                        Add(outputs, hw.Key, kind, SensorKind.Temperature, "CPU Core Average", Op.Average, coreTemps);
                    }
                    var clocks = sensors.Where(s => s.Kind == SensorKind.Clock && CoreName().IsMatch(s.Name)).ToList();
                    if (clocks.Count >= 2)
                    {
                        Add(outputs, hw.Key, kind, SensorKind.Clock, "CPU Clock Max", Op.Max, clocks);
                        Add(outputs, hw.Key, kind, SensorKind.Clock, "CPU Clock Average", Op.Average, clocks);
                    }
                    var loads = sensors.Where(s => s.Kind == SensorKind.Load && CpuCoreLoadName().IsMatch(s.Name)).ToList();
                    var hasOwnLoadMax = sensors.Any(s => s.Kind == SensorKind.Load && s.Name.Equals("CPU Core Max", StringComparison.OrdinalIgnoreCase));
                    if (!hasOwnLoadMax && loads.Count >= 2) Add(outputs, hw.Key, kind, SensorKind.Load, "CPU Core Load Max", Op.Max, loads);
                    break;
                }
                case HardwareKind.Memory:
                {
                    var dimms = sensors.Where(s => s.Kind == SensorKind.Temperature && DimmName().IsMatch(s.Name)).ToList();
                    if (dimms.Count >= 2) Add(outputs, hw.Key, kind, SensorKind.Temperature, "DIMM Max", Op.Max, dimms);
                    break;
                }
            }
        }

        // no network totals: virtual switches (Hyper-V, VMware, WSL) carry the same traffic as the physical adapter,
        // so a sum counts it twice; the friendly catalog picks the primary adapter instead
        return new SourceInventory([], outputs);
    }

    private static void Add(List<SensorDescriptor> outputs, string hardwareId, HardwareKind hwKind, SensorKind kind, string name, Op op, List<SensorDescriptor> inputs)
    {
        if (inputs.Count == 0) return;
        outputs.Add(new SensorDescriptor
        {
            Id = SensorIds.Sensor(hardwareId, kind, name),
            HardwareId = hardwareId,
            HardwareKind = hwKind,
            Name = name,
            Kind = kind,
            // an output is never read faster than its slowest input allows
            MinInterval = inputs.Max(i => i.MinInterval),
            Order = -1, // computed values first in their hardware
            SourceTag = new Spec(op, inputs.Select(i => i.Index).Where(i => i >= 0).ToArray()),
        });
    }

    public void Update(ReadOnlySpan<SensorDescriptor> requested, Span<float> values)
    {
        foreach (var d in requested)
        {
            if (d.SourceTag is not Spec spec)
            {
                values[d.Index] = float.NaN;
                continue;
            }
            var acc = spec.Op == Op.Max ? float.NegativeInfinity : 0f;
            var n = 0;
            foreach (var i in spec.Inputs)
            {
                if ((uint)i >= (uint)values.Length) continue;
                var v = values[i];
                if (float.IsNaN(v)) continue;
                acc = spec.Op == Op.Max ? MathF.Max(acc, v) : acc + v;
                n++;
            }
            values[d.Index] = n == 0 ? float.NaN : spec.Op == Op.Average ? acc / n : acc; // Max: acc
        }
    }

    public void Dispose() { }

    [GeneratedRegex(@"^Core #\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CoreName();

    [GeneratedRegex(@"^CCD ?#?\d+( \(Tdie\))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CcdName();

    [GeneratedRegex(@"^CPU Core #\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CpuCoreLoadName();

    [GeneratedRegex(@"^DIMM ?#?\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DimmName();
}
