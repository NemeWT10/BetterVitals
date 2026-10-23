using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;

namespace BetterVitals.Overlay;

/// <summary>Warning / danger limits in base units (°C for temperatures).</summary>
public readonly record struct ThresholdPair(float Warning, float Danger);

/// <summary>
/// Default value thresholds per sensor (user decision, PROJEKT §7): CPU temperature 80 / 90 °C, GPU core 80 / 88,
/// GPU Hot Spot 90 / 100, GPU memory junction / memory chips 90 / 100, storage 60 / 70. The value turns the theme's
/// Warning color at the first and Danger at the second. Items can override them with their own list.
/// </summary>
public static class DefaultThresholds
{
    public static readonly ThresholdPair CpuTemperature = new(80, 90);
    public static readonly ThresholdPair GpuTemperature = new(80, 88);
    public static readonly ThresholdPair GpuHotSpot = new(90, 100);
    public static readonly ThresholdPair GpuMemoryJunction = new(90, 100);
    public static readonly ThresholdPair StorageTemperature = new(60, 70);

    /// <summary>Default thresholds for a sensor, or null when it has none.</summary>
    public static ThresholdPair? For(SensorDescriptor sensor) => For(sensor.HardwareKind, sensor.Kind, sensor.Name);

    public static ThresholdPair? For(HardwareKind hw, SensorKind kind, string name)
    {
        if (kind != SensorKind.Temperature) return null;
        switch (hw)
        {
            case HardwareKind.Cpu:
                return CpuTemperature;
            case HardwareKind.Gpu:
                var n = name.ToLowerInvariant();
                if (n.Contains("hot spot") || n.Contains("hotspot") || n.Contains("hot-spot")) return GpuHotSpot;
                if (n.Contains("junction") || n.StartsWith("gpu memory", StringComparison.Ordinal)) return GpuMemoryJunction;
                return GpuTemperature;
            case HardwareKind.Storage:
                return StorageTemperature;
            default:
                return null;
        }
    }

    /// <summary>The same defaults as editable <see cref="OverlayThreshold"/>s with the theme's colors.</summary>
    public static List<OverlayThreshold> AsList(ThresholdPair pair, OverlayTheme theme) =>
    [
        new() { Value = pair.Warning, Color = theme.Warning.ToString() },
        new() { Value = pair.Danger, Color = theme.Danger.ToString() },
    ];
}
