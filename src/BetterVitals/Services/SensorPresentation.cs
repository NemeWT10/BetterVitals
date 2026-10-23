using BetterVitals.Core.Catalog;
using BetterVitals.Core.Sensors;
using BetterVitals.Localization;

namespace BetterVitals.Services;

/// <summary>Heat scale of a value: normal / warm / hot = Brush.Heat.Normal / Warm / Hot (Success / Warning / Danger).</summary>
public enum HeatLevel : byte
{
    Normal,
    Warm,
    Hot,
}

/// <summary>
/// Shared presentation rules for sensors (flyout, tray tooltip, wave-2 pages): icon and label per family / kind, default
/// heat thresholds (PROJEKT §7: CPU 80 / 90 °C, GPU 80 / 88 °C, GPU Hot Spot 90 / 100 °C, NVMe 60 / 70 °C) and the
/// automatic "key" sensors of the flyout.
/// </summary>
public static class SensorPresentation
{
    /// <summary>Icon resource key for a component family ("Icon.Cpu"…).</summary>
    public static string IconKey(HardwareKind kind) => kind switch
    {
        HardwareKind.Cpu => "Icon.Cpu",
        HardwareKind.Gpu => "Icon.Gpu",
        HardwareKind.Motherboard => "Icon.Motherboard",
        HardwareKind.Memory => "Icon.Ram",
        HardwareKind.Storage => "Icon.Disk",
        HardwareKind.Network => "Icon.Network",
        HardwareKind.Battery => "Icon.Battery",
        HardwareKind.Psu => "Icon.Psu",
        HardwareKind.Cooler => "Icon.Cooler",
        HardwareKind.Controller => "Icon.Controller",
        HardwareKind.Game => "Icon.Game",
        _ => "Icon.Layers",
    };

    /// <summary>Icon resource key for a measurement ("Icon.Thermometer"…).</summary>
    public static string IconKey(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => "Icon.Thermometer",
        SensorKind.Load or SensorKind.Control or SensorKind.Level => "Icon.Gauge",
        SensorKind.Clock or SensorKind.Frequency => "Icon.Clock",
        SensorKind.Voltage or SensorKind.Current => "Icon.Wave",
        SensorKind.Power or SensorKind.Energy => "Icon.Bolt",
        SensorKind.Fan => "Icon.Fan",
        SensorKind.Data or SensorKind.SmallData => "Icon.Memory",
        SensorKind.Throughput => "Icon.Network",
        SensorKind.Fps or SensorKind.FrameTime => "Icon.Game",
        _ => "Icon.Pulse",
    };

    /// <summary>"CPU", "Płyta główna"… (Loc).</summary>
    public static string FamilyLabel(HardwareKind kind) => Loc.T("hw." + kind);

    /// <summary>"Temperatura", "Load"… (Loc).</summary>
    public static string KindLabel(SensorKind kind) => Loc.T("kind." + kind);

    /// <summary>Default warm / hot thresholds for a sensor, or null when its values are not judged (loads, clocks…).</summary>
    public static (float Warm, float Hot)? DefaultThresholds(SensorDescriptor s)
    {
        if (s.Kind != SensorKind.Temperature) return null;
        var name = s.Name;
        if (name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) || name.Contains("Junction", StringComparison.OrdinalIgnoreCase))
            return (90, 100);
        return s.HardwareKind switch
        {
            HardwareKind.Cpu => (80, 90),
            HardwareKind.Gpu => (80, 88),
            HardwareKind.Storage => (60, 70),
            HardwareKind.Memory => (60, 75),
            HardwareKind.Battery => (45, 55),
            _ => (75, 90),
        };
    }

    public static HeatLevel Heat(SensorDescriptor s, float value)
    {
        if (float.IsNaN(value) || DefaultThresholds(s) is not { } t) return HeatLevel.Normal;
        return value >= t.Hot ? HeatLevel.Hot : value >= t.Warm ? HeatLevel.Warm : HeatLevel.Normal;
    }

    public static string HeatBrushKey(HeatLevel level) => level switch
    {
        HeatLevel.Hot => "Brush.Heat.Hot",
        HeatLevel.Warm => "Brush.Heat.Warm",
        _ => "Brush.Text",
    };

    /// <summary>
    /// The flyout's sensors: the pinned ones that exist, filled up with the automatic picks in this order —
    /// CPU temperature, GPU temperature, CPU load, GPU load, RAM load, FPS.
    /// </summary>
    public static IReadOnlyList<SensorDescriptor> KeySensors(ISensorHub hub, IReadOnlyList<string> pinned, int count = 4)
    {
        var result = new List<SensorDescriptor>(count);
        foreach (var id in pinned)
        {
            if (result.Count >= count) break;
            if (hub.Find(id) is { } s && !result.Contains(s)) result.Add(s);
        }
        var sensors = hub.Sensors;
        // "GPU" is the discrete card, not the Radeon / Intel graphics inside the CPU that may be listed first
        var mainGpu = FriendlyCatalog.MainGpuId(hub.Hardware, sensors);
        foreach (var pick in Picks)
        {
            if (result.Count >= count) break;
            var s = (pick.Hw == HardwareKind.Gpu && mainGpu is not null ? Best(sensors, pick.Hw, pick.Kind, pick.Names, mainGpu) : null)
                    ?? Best(sensors, pick.Hw, pick.Kind, pick.Names);
            if (s is not null && !result.Contains(s)) result.Add(s);
        }
        return result;
    }

    private static readonly (HardwareKind Hw, SensorKind Kind, string[] Names)[] Picks =
    [
        (HardwareKind.Cpu, SensorKind.Temperature, ["Core (Tctl/Tdie)", "CPU Package", "Core Max", "Package", "Tctl/Tdie", "Tdie", "Tctl", "Core Average", "CPU"]),
        (HardwareKind.Gpu, SensorKind.Temperature, ["GPU Core", "GPU Temperature", "GPU"]),
        (HardwareKind.Cpu, SensorKind.Load, ["CPU Total", "Total"]),
        (HardwareKind.Gpu, SensorKind.Load, ["GPU Core", "D3D 3D", "GPU"]),
        (HardwareKind.Memory, SensorKind.Load, ["Memory", "Physical Memory"]),
        (HardwareKind.Game, SensorKind.Fps, ["FPS"]),
    ];

    private static SensorDescriptor? Best(IReadOnlyList<SensorDescriptor> sensors, HardwareKind hw, SensorKind kind, string[] names, string? hardwareId = null)
    {
        SensorDescriptor? first = null;
        var bestRank = int.MaxValue;
        SensorDescriptor? best = null;
        foreach (var s in sensors)
        {
            if (s.HardwareKind != hw || s.Kind != kind) continue;
            if (hardwareId is not null && s.HardwareId != hardwareId) continue;
            first ??= s;
            var rank = Array.FindIndex(names, n => n.Equals(s.Name, StringComparison.OrdinalIgnoreCase));
            if (rank >= 0 && rank < bestRank)
            {
                bestRank = rank;
                best = s;
            }
        }
        return best ?? first;
    }
}
