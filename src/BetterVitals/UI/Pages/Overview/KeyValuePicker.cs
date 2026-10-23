using BetterVitals.Core.Catalog;
using BetterVitals.Core.Sensors;
using BetterVitals.UI.Pages.Shared;

namespace BetterVitals.UI.Pages.Overview;

/// <summary>A big value on a collapsed card: the sensor and how to title it.</summary>
/// <param name="FriendlyKey">Loc key of the friendly parameter ("friendly.cpu.temp"); null = titled by its kind.</param>
public sealed record KeyValue(SensorDescriptor Sensor, string? FriendlyKey);

/// <summary>
/// Picks the 2–4 big values of a hardware card: the friendly parameters FriendlyCatalog resolved for this PC in a
/// per-family order (CPU: temperature, load, clock, power; GPU: temperature, hot spot, load, VRAM…), topped up from the
/// card's own sensors by kind when the catalog has fewer than two (an iGPU, a second drive, a battery).
/// </summary>
public static class KeyValuePicker
{
    public const int Max = 4;

    private static readonly Dictionary<HardwareKind, string[]> Preferred = new()
    {
        [HardwareKind.Cpu] = ["cpu.temp", "cpu.load", "cpu.clockMax", "cpu.power"],
        [HardwareKind.Gpu] = ["gpu.temp", "gpu.hotspot", "gpu.load", "gpu.vram", "gpu.power", "gpu.clock"],
        [HardwareKind.Memory] = ["ram.used", "ram.load", "ram.dimm"],
        [HardwareKind.Motherboard] = ["mb.vrm", "mb.chipset", "mb.cpuFan", "mb.pump"],
        [HardwareKind.Storage] = ["disk.temp", "disk.read", "disk.write"],
        [HardwareKind.Network] = ["net.down", "net.up"],
        [HardwareKind.Game] = ["fps.fps", "fps.low1", "fps.frametime"],
    };

    /// <summary>Kinds tried, in order, when the catalog leaves a card with fewer than two values.</summary>
    private static readonly SensorKind[] FallbackKinds =
    [
        SensorKind.Temperature, SensorKind.Load, SensorKind.Level, SensorKind.Throughput, SensorKind.Power, SensorKind.Clock,
        SensorKind.SmallData, SensorKind.Data, SensorKind.Fan, SensorKind.Fps, SensorKind.Voltage,
    ];

    /// <summary>Friendly keys whose catalog name speaks of several devices ("Drive temperatures"): titled by kind on a single card.</summary>
    private static readonly HashSet<string> TitledByKind = new(StringComparer.Ordinal) { "disk.temp" };

    public static IReadOnlyList<KeyValue> Pick(HardwareGroup group, IReadOnlyList<FriendlyItem> items)
    {
        var mine = new HashSet<string>(group.Sensors.Select(s => s.Id), StringComparer.Ordinal);
        var byId = group.Sensors.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var result = new List<KeyValue>(Max);

        if (Preferred.TryGetValue(group.Kind, out var keys))
        {
            foreach (var key in keys)
            {
                if (result.Count >= Max) break;
                foreach (var item in items)
                {
                    if (item.Key != key) continue;
                    // multi items (every core) are not one big value — except per-drive values, one per card
                    if (item.IsMulti && item.Category != HardwareKind.Storage) continue;
                    var id = item.SensorIds.FirstOrDefault(mine.Contains);
                    if (id is null || result.Any(r => r.Sensor.Id == id)) continue;
                    result.Add(new KeyValue(byId[id], TitledByKind.Contains(key) ? null : item.LocKey));
                    break;
                }
            }
        }

        if (result.Count < 2)
        {
            foreach (var kind in FallbackKinds)
            {
                if (result.Count >= 3) break;
                var pick = group.Sensors.Where(s => s.Kind == kind && result.All(r => r.Sensor.Id != s.Id))
                    .OrderBy(s => IsSecondary(s.Name) ? 1 : 0)
                    .FirstOrDefault();
                if (pick is not null) result.Add(new KeyValue(pick, null));
            }
        }
        return result;
    }

    /// <summary>"GPU Memory Free", "Memory Available", "… Total" are context, not the value people watch.</summary>
    private static bool IsSecondary(string name)
        => name.Contains("Free", StringComparison.OrdinalIgnoreCase) || name.Contains("Total", StringComparison.OrdinalIgnoreCase) && !name.Contains("Total Activity", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Available", StringComparison.OrdinalIgnoreCase);
}
