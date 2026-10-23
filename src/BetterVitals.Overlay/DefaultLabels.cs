using System.Text.RegularExpressions;
using BetterVitals.Core.Sensors;

namespace BetterVitals.Overlay;

/// <summary>
/// Short default overlay labels ("CPU", "GPU", "GPU HS", "VRAM Tj", "VRAM", "RAM", "FPS", "1% low", "NVMe"…)
/// derived from hardware kind + sensor kind + verbatim sensor name. Pure and allocation-light; shared with the WPF
/// editor. Sensor names themselves are never translated — these are just compact forms of them.
/// </summary>
public static partial class DefaultLabels
{
    /// <summary>Label for one sensor.</summary>
    public static string For(SensorDescriptor sensor, HardwareDescriptor? hardware = null)
        => For(sensor.HardwareKind, sensor.Kind, sensor.Name, hardware?.Name);

    /// <summary>Label from the parts (also used for sensors not discovered yet, see <see cref="ForId"/>).</summary>
    public static string For(HardwareKind hw, SensorKind kind, string name, string? hardwareName = null)
    {
        var n = name.Trim();
        var lower = n.ToLowerInvariant();

        if (kind == SensorKind.Fps || hw == HardwareKind.Game)
        {
            if (lower.Contains("0.1%") || lower.Contains("0,1%") || lower.Contains("0-1-low")) return "0.1% low";
            if (lower.Contains("1%") || lower.Contains("1-low")) return "1% low";
            if (kind == SensorKind.FrameTime || lower.Contains("frame time") || lower.Contains("frametime"))
                return lower.Contains("max") ? "Max frametime" : "Frametime";
            if (kind == SensorKind.Fps) return "FPS";
        }
        if (kind == SensorKind.FrameTime) return "Frametime";

        switch (hw)
        {
            case HardwareKind.Gpu:
                return Gpu(kind, n, lower);
            case HardwareKind.Cpu:
                return Cpu(kind, n, lower);
            case HardwareKind.Memory:
                if (kind is SensorKind.Data or SensorKind.SmallData or SensorKind.Load)
                    return lower.Contains("virtual") ? "Virtual" : "RAM";
                if (kind == SensorKind.Temperature) return Numbered(n, "DIMM") ?? "RAM";
                return n;
            case HardwareKind.Storage:
                if (kind == SensorKind.Temperature) return StorageKind(hardwareName, lower);
                if (kind == SensorKind.Throughput)
                    return lower.Contains("read") ? "Read" : lower.Contains("write") ? "Write" : n;
                if (kind == SensorKind.Load) return "Disk";
                return n;
            case HardwareKind.Network:
                if (lower.Contains("upload")) return "Up";
                if (lower.Contains("download")) return "Down";
                return n;
            case HardwareKind.Battery:
                return kind == SensorKind.Level ? "Battery" : n;
        }

        if (kind == SensorKind.Fan) return Numbered(n, "Fan") ?? n;
        return n;
    }

    /// <summary>Label for a multi-value item (several sensors drawn as bars / grid / rows).</summary>
    public static string ForGroup(IReadOnlyList<SensorDescriptor> sensors)
    {
        if (sensors.Count == 0) return "—";
        if (sensors.Count == 1) return For(sensors[0]);
        var hw = sensors[0].HardwareKind;
        var kind = sensors[0].Kind;
        if (sensors.All(s => s.HardwareKind == hw && s.Kind == kind))
        {
            if (hw == HardwareKind.Cpu && sensors.All(s => CoreNumber().IsMatch(s.Name))) return "Cores";
            if (hw == HardwareKind.Gpu && kind == SensorKind.Temperature && sensors.All(s => s.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase)))
                return "VRAM";
            return Hardware(hw);
        }
        return For(sensors[0]);
    }

    /// <summary>Short sub-label of one sensor inside a multi-value item ("Core #3" → "3", "CCD1" → "CCD1").</summary>
    public static string Sub(SensorDescriptor sensor, int ordinal)
    {
        var m = Number().Match(sensor.Name);
        return m.Success ? m.Groups[1].Value : (ordinal + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Label from a stable sensor id alone ("game/…/fps/1-low" → "1% low") for sensors that are configured but not
    /// present yet (FPS before a game starts).
    /// </summary>
    public static string ForId(string sensorId)
    {
        if (!TryParseId(sensorId, out var hw, out var kind, out var name)) return "—";
        return For(hw, kind, name);
    }

    /// <summary>Hardware kind + sensor kind + readable name from "&lt;kind&gt;/&lt;hw&gt;/&lt;n&gt;[/sub…]/&lt;sensor kind&gt;/&lt;name slug&gt;".</summary>
    public static bool TryParseId(string sensorId, out HardwareKind hw, out SensorKind kind, out string name)
    {
        hw = HardwareKind.Other;
        kind = SensorKind.Factor;
        name = "";
        if (string.IsNullOrEmpty(sensorId)) return false;
        var parts = sensorId.Split('/');
        if (parts.Length < 3) return false;
        if (!Enum.TryParse(parts[0], true, out hw)) hw = HardwareKind.Other;
        if (!Enum.TryParse(parts[^2], true, out kind) || int.TryParse(parts[^2], out _)) return false;
        var slug = parts[^1];
        var hash = slug.IndexOf('#');
        if (hash >= 0) slug = slug[..hash];
        // "1-low" → "1% low" is handled by For(); everything else: dashes back to spaces.
        name = slug.Replace('-', ' ');
        if (kind == SensorKind.Fps && slug.EndsWith("-low", StringComparison.Ordinal))
            name = slug[..^4].Replace('-', '.') + "% low";
        return true;
    }

    /// <summary>
    /// Automatic label of a row from its items' labels and hardware kinds: a row of one item is labelled like the item, a
    /// row of one component by the component ("CPU", "GPU", "FPS"…), a mixed row like its first item. Items whose label
    /// equals the row label are drawn without it ("GPU  66 °C · GPU HS 76 °C").
    /// </summary>
    public static string ForRow(IReadOnlyList<string> itemLabels, IReadOnlyList<HardwareKind> kinds)
    {
        if (itemLabels.Count == 0) return "";
        if (itemLabels.Count == 1) return itemLabels[0];
        var kind = kinds.Count > 0 ? kinds[0] : HardwareKind.Other;
        if (kind != HardwareKind.Other && kinds.All(k => k == kind) && Component(kind) is { Length: > 0 } name) return name;
        return itemLabels[0];
    }

    /// <summary>Row name of a component: "FPS" for the game's values, else <see cref="Hardware"/>.</summary>
    public static string Component(HardwareKind hw) => hw == HardwareKind.Game ? "FPS" : Hardware(hw);

    /// <summary>"CPU", "GPU", "RAM"… for a component family.</summary>
    public static string Hardware(HardwareKind hw) => hw switch
    {
        HardwareKind.Cpu => "CPU",
        HardwareKind.Gpu => "GPU",
        HardwareKind.Memory => "RAM",
        HardwareKind.Motherboard => "Board",
        HardwareKind.Storage => "Disk",
        HardwareKind.Network => "Net",
        HardwareKind.Battery => "Battery",
        HardwareKind.Psu => "PSU",
        HardwareKind.Cooler => "Cooler",
        HardwareKind.Game => "Game",
        _ => "",
    };

    private static string Gpu(SensorKind kind, string n, string lower)
    {
        switch (kind)
        {
            case SensorKind.Temperature:
                if (lower.Contains("hot spot") || lower.Contains("hotspot") || lower.Contains("hot-spot"))
                    return Numbered(n, "GPU HS") ?? "GPU HS";
                if (lower.Contains("junction")) return "VRAM Tj";
                if (lower.StartsWith("gpu memory", StringComparison.Ordinal)) return Numbered(n, "VRAM") ?? "VRAM";
                if (lower is "gpu core" or "gpu" or "core" or "temperature" or "gpu temperature") return "GPU";
                return Strip(n, "GPU ");
            case SensorKind.Data:
            case SensorKind.SmallData:
                if (lower.Contains("memory") && (lower.Contains("used") || lower.Contains("dedicated"))) return "VRAM";
                if (lower.Contains("shared")) return "Shared";
                return Strip(n, "GPU ");
            case SensorKind.Clock:
                if (lower.Contains("memory")) return "VRAM";
                if (lower.Contains("core") || lower == "gpu") return "GPU";
                return Strip(n, "GPU ");
            case SensorKind.Load:
                if (lower is "gpu core" or "gpu" or "d3d 3d" or "3d") return "GPU";
                if (lower.Contains("memory controller")) return "GPU MC";
                if (lower.Contains("memory")) return "VRAM";
                if (lower.Contains("video")) return "Video";
                return Strip(n, "GPU ");
            case SensorKind.Power:
                return lower.Contains("package") || lower.Contains("board") || lower is "gpu power" or "gpu" or "power" ? "GPU" : Strip(n, "GPU ");
            case SensorKind.Fan:
            case SensorKind.Control:
                return Numbered(n, "GPU Fan") ?? "GPU Fan";
            case SensorKind.Voltage:
                return lower.Contains("core") ? "GPU V" : Strip(n, "GPU ");
            default:
                return Strip(n, "GPU ");
        }
    }

    private static string Cpu(SensorKind kind, string n, string lower)
    {
        switch (kind)
        {
            case SensorKind.Temperature:
                if (lower.Contains("tctl") || lower.Contains("package") || lower.Contains("core max") || lower.Contains("cores (max)")
                    || lower is "cpu" or "cpu cores" or "core (average)" or "cores (average)" or "cpu temperature")
                    return "CPU";
                if (CcdName().Match(n) is { Success: true } ccd) return ccd.Value.ToUpperInvariant();
                return Numbered(n, "Core") ?? Strip(n, "CPU ");
            case SensorKind.Load:
                if (lower.Contains("total") || lower is "cpu" or "cpu load") return "CPU";
                if (lower.Contains("max")) return "CPU max";
                return Numbered(n, "Core") ?? Strip(n, "CPU ");
            case SensorKind.Clock:
                if (lower.Contains("max") || lower.Contains("average") || lower is "cpu" or "cpu clock") return "CPU";
                if (lower.Contains("bus")) return "Bus";
                return Numbered(n, "Core") ?? Strip(n, "CPU ");
            case SensorKind.Power:
                if (lower.Contains("package") || lower is "cpu" or "cpu power" or "ppt") return "CPU";
                return Strip(n, "CPU ");
            case SensorKind.Voltage:
                if (lower.Contains("vid") && CoreNumber().IsMatch(n)) return n;
                if (lower.Contains("core") || lower.Contains("vcore") || lower.Contains("svi")) return "Vcore";
                return Strip(n, "CPU ");
            default:
                return Strip(n, "CPU ");
        }
    }

    private static string StorageKind(string? hardwareName, string lower)
    {
        var hn = hardwareName?.ToUpperInvariant() ?? "";
        if (hn.Contains("NVME") || lower.Contains("composite")) return "NVMe";
        if (hn.Contains("SSD")) return "SSD";
        if (hn.Length > 0 && (hn.StartsWith("ST", StringComparison.Ordinal) || hn.StartsWith("WDC WD", StringComparison.Ordinal)
                              || hn.Contains("HDD") || hn.StartsWith("TOSHIBA", StringComparison.Ordinal) || hn.StartsWith("HGST", StringComparison.Ordinal)))
            return "HDD";
        return "Disk";
    }

    /// <summary>"Core #3" → "Core 3"; null when the name has no number.</summary>
    private static string? Numbered(string name, string prefix)
    {
        var m = Number().Match(name);
        return m.Success ? $"{prefix} {m.Groups[1].Value}" : null;
    }

    private static string Strip(string name, string prefix)
    {
        var s = name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? name[prefix.Length..] : name;
        return s.Length == 0 ? name : s;
    }

    [GeneratedRegex(@"#\s*(\d+)")]
    private static partial Regex Number();

    [GeneratedRegex(@"Core\s*#\s*\d+", RegexOptions.IgnoreCase)]
    private static partial Regex CoreNumber();

    [GeneratedRegex(@"CCD\s*\d+", RegexOptions.IgnoreCase)]
    private static partial Regex CcdName();
}
