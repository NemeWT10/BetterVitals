using System.Text.RegularExpressions;
using BetterVitals.Core.Sensors;

namespace BetterVitals.Core.Catalog;

/// <summary>A friendly parameter resolved on this PC: what the simple overlay editor, the flyout and the "Key" preset offer.</summary>
public sealed record FriendlyItem(
    string Key,
    HardwareKind Category,
    string HardwareId,
    string HardwareName,
    IReadOnlyList<string> SensorIds,
    bool IsMulti,
    bool InKeyPreset)
{
    /// <summary>Loc key of the friendly name ("friendly.cpu.temp").</summary>
    public string LocKey => "friendly." + Key;
}

/// <summary>
/// Maps friendly parameters ("CPU temperature", "Clock of every core", "GPU Hot Spot", "FPS"…) to the best sensor this
/// particular PC has — Intel reports "Core Max", AMD "Core (Tctl/Tdie)", laptops "CPU Package", and so on — so the
/// simple overlay editor and the default presets work on any machine. Items whose sensors do not exist are left out.
/// Rules are ordered alternatives; the first that matches wins. Sensor names stay verbatim; only the friendly names
/// are translated (<see cref="Texts"/>).
/// </summary>
public static partial class FriendlyCatalog
{
    private delegate bool Match(SensorDescriptor s);

    private sealed record Rule(string Key, HardwareKind Category, bool Multi, bool KeyPreset, Match[] Alternatives);

    private static Match Named(SensorKind kind, params string[] names)
        => s => s.Kind == kind && names.Any(n => s.Name.Equals(n, StringComparison.OrdinalIgnoreCase));

    private static Match Pattern(SensorKind kind, Regex regex) => s => s.Kind == kind && regex.IsMatch(s.Name);

    private static readonly Rule[] Rules =
    [
        // ── CPU ──
        new("cpu.temp", HardwareKind.Cpu, false, true, [
            Named(SensorKind.Temperature, "Core Max"), Named(SensorKind.Temperature, "Core (Tctl/Tdie)"), Named(SensorKind.Temperature, "Core (Tctl)"),
            Named(SensorKind.Temperature, "CPU Core Max"), Named(SensorKind.Temperature, "CPU Package", "Package"), Named(SensorKind.Temperature, "CPU Cores")]),
        new("cpu.ccd", HardwareKind.Cpu, true, false, [Pattern(SensorKind.Temperature, CcdTemp())]),
        new("cpu.coreTemps", HardwareKind.Cpu, true, false, [Pattern(SensorKind.Temperature, CoreN())]),
        new("cpu.clockMax", HardwareKind.Cpu, false, true, [Named(SensorKind.Clock, "CPU Clock Max")]),
        new("cpu.clocks", HardwareKind.Cpu, true, false, [Pattern(SensorKind.Clock, CoreN())]),
        new("cpu.load", HardwareKind.Cpu, false, true, [Named(SensorKind.Load, "CPU Total")]),
        new("cpu.loads", HardwareKind.Cpu, true, false, [Pattern(SensorKind.Load, CpuCoreN())]),
        new("cpu.vcore", HardwareKind.Cpu, false, false, [
            Named(SensorKind.Voltage, "Core (SVI3 TFN)", "Core (SVI2 TFN)", "CPU Core", "Core", "Vcore", "CPU VCORE")]),
        new("cpu.soc", HardwareKind.Cpu, false, false, [Named(SensorKind.Voltage, "SoC (SVI3 TFN)", "SoC (SVI2 TFN)", "SoC", "VSOC", "CPU VSOC")]),
        new("cpu.power", HardwareKind.Cpu, false, true, [Named(SensorKind.Power, "Package", "CPU Package")]),

        // ── GPU (the main discrete GPU) ──
        new("gpu.temp", HardwareKind.Gpu, false, true, [Named(SensorKind.Temperature, "GPU Core")]),
        new("gpu.hotspot", HardwareKind.Gpu, false, true, [Named(SensorKind.Temperature, "GPU Hot Spot")]),
        new("gpu.memJunction", HardwareKind.Gpu, false, true, [Named(SensorKind.Temperature, "GPU Memory Junction")]),
        new("gpu.load", HardwareKind.Gpu, false, true, [Named(SensorKind.Load, "GPU Core"), Named(SensorKind.Load, "D3D 3D")]),
        new("gpu.clock", HardwareKind.Gpu, false, true, [Named(SensorKind.Clock, "GPU Core")]),
        new("gpu.memClock", HardwareKind.Gpu, false, true, [Named(SensorKind.Clock, "GPU Memory")]),
        new("gpu.vram", HardwareKind.Gpu, false, true, [
            Named(SensorKind.SmallData, "GPU Memory Used"), Named(SensorKind.SmallData, "D3D Dedicated Memory Used"), Named(SensorKind.Data, "GPU Memory Used")]),
        new("gpu.power", HardwareKind.Gpu, false, true, [Named(SensorKind.Power, "GPU Package", "GPU Power", "GPU Board Power", "GPU Total")]),
        new("gpu.voltage", HardwareKind.Gpu, false, false, [Named(SensorKind.Voltage, "GPU Core", "GPU Core Voltage")]),
        new("gpu.fan", HardwareKind.Gpu, false, true, [Named(SensorKind.Fan, "GPU Fan", "GPU Fan 1", "GPU Fan #1")]),

        // ── memory ──
        new("ram.used", HardwareKind.Memory, false, true, [Named(SensorKind.Data, "Memory Used")]),
        new("ram.load", HardwareKind.Memory, false, false, [Named(SensorKind.Load, "Memory")]),
        new("ram.speed", HardwareKind.Memory, false, false, [Named(SensorKind.Clock, "Memory Speed", "Speed")]),
        new("ram.dimm", HardwareKind.Memory, false, true, [Named(SensorKind.Temperature, "DIMM Max"), Pattern(SensorKind.Temperature, DimmN())]),

        // ── motherboard ──
        new("mb.chipset", HardwareKind.Motherboard, false, false, [Named(SensorKind.Temperature, "Chipset", "PCH")]),
        new("mb.vrm", HardwareKind.Motherboard, false, true, [Named(SensorKind.Temperature, "VRM MOS", "VRM", "MOS", "VRM Temperature")]),
        new("mb.cpuFan", HardwareKind.Motherboard, false, true, [Named(SensorKind.Fan, "CPU Fan", "CPU", "CPU_FAN", "CPU Fan #1")]),
        new("mb.pump", HardwareKind.Motherboard, false, false, [Named(SensorKind.Fan, "Pump", "AIO Pump", "CPU Pump", "W_PUMP", "Water Pump")]),
        new("mb.v12", HardwareKind.Motherboard, false, false, [Named(SensorKind.Voltage, "+12V", "12V")]),

        // ── drives (every drive, one value each) ──
        new("disk.temp", HardwareKind.Storage, true, true, [Named(SensorKind.Temperature, "Composite Temperature", "Temperature", "Temperature 1", "Disk Temperature")]),
        new("disk.read", HardwareKind.Storage, false, false, [Named(SensorKind.Throughput, "Read Rate")]),
        new("disk.write", HardwareKind.Storage, false, false, [Named(SensorKind.Throughput, "Write Rate")]),

        // ── network ──
        new("net.down", HardwareKind.Network, false, true, [Named(SensorKind.Throughput, "Download Speed")]),
        new("net.up", HardwareKind.Network, false, true, [Named(SensorKind.Throughput, "Upload Speed")]),

        // ── the foreground game ──
        new("fps.fps", HardwareKind.Game, false, true, [Named(SensorKind.Fps, "Framerate")]),
        new("fps.frametime", HardwareKind.Game, false, false, [Named(SensorKind.FrameTime, "Frametime")]),
        new("fps.low1", HardwareKind.Game, false, true, [Named(SensorKind.Fps, "1% Low")]),
        new("fps.low01", HardwareKind.Game, false, false, [Named(SensorKind.Fps, "0.1% Low")]),
    ];

    /// <summary>Default overlay of the simple mode (user example: FPS, CPU and GPU temperature, top-left).</summary>
    public static IReadOnlyList<string> DefaultOverlayKeys { get; } = ["fps.fps", "cpu.temp", "gpu.temp"];

    /// <summary>
    /// Resolves every friendly item that exists on this PC, in catalog order. <paramref name="primaryNetwork"/> = name of
    /// the adapter that carries the internet traffic (see Platform NetworkPrimary); virtual adapters come last.
    /// </summary>
    public static IReadOnlyList<FriendlyItem> Resolve(IReadOnlyList<HardwareDescriptor> hardware, IReadOnlyList<SensorDescriptor> sensors, string? primaryNetwork = null)
    {
        var result = new List<FriendlyItem>();
        var byHw = sensors.GroupBy(s => s.HardwareId).ToDictionary(g => g.Key, g => g.OrderBy(s => s.Order).ToList(), StringComparer.Ordinal);
        var names = hardware.ToDictionary(h => h.Id, h => h.Name, StringComparer.Ordinal);
        var mainGpu = PickMainGpu(hardware, byHw);

        foreach (var rule in Rules)
        {
            // which hardware nodes a rule looks at
            var candidates = rule.Category switch
            {
                HardwareKind.Gpu => mainGpu is null ? [] : [mainGpu],
                // the board's sensors live on its SuperIO / EC children, CPU voltages may too
                HardwareKind.Motherboard => hardware.Where(h => h.Kind == HardwareKind.Motherboard).Select(h => h.Id).ToList(),
                // physical memory before the page file ("Total Memory" before "Virtual Memory")
                HardwareKind.Memory => hardware.Where(h => h.Kind == HardwareKind.Memory).OrderBy(h => h.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ? 1 : 0).Select(h => h.Id).ToList(),
                HardwareKind.Network => hardware.Where(h => h.Kind == HardwareKind.Network)
                    .OrderBy(h => primaryNetwork is not null && h.Name.Equals(primaryNetwork, StringComparison.OrdinalIgnoreCase) ? 0 : VirtualAdapter().IsMatch(h.Name) ? 2 : 1)
                    .Select(h => h.Id).ToList(),
                _ => hardware.Where(h => h.Kind == rule.Category).Select(h => h.Id).ToList(),
            };
            if (rule.Key is "cpu.vcore" or "cpu.soc")
                candidates = [.. candidates, .. hardware.Where(h => h.Kind == HardwareKind.Motherboard).Select(h => h.Id)];

            if (rule.Category == HardwareKind.Storage && rule.Multi)
            {
                // one value per drive
                var ids = new List<string>();
                foreach (var hw in candidates)
                    if (byHw.TryGetValue(hw, out var list) && First(list, rule) is { } s) ids.Add(s.Id);
                if (ids.Count > 0) result.Add(new FriendlyItem(rule.Key, rule.Category, candidates[0], names.GetValueOrDefault(candidates[0], ""), ids, true, rule.KeyPreset));
                continue;
            }

            foreach (var hw in candidates)
            {
                if (!byHw.TryGetValue(hw, out var list)) continue;
                if (rule.Multi)
                {
                    var all = list.Where(s => rule.Alternatives.Any(m => m(s))).Select(s => s.Id).ToList();
                    if (all.Count < 2) continue; // a multi item needs several values
                    result.Add(new FriendlyItem(rule.Key, rule.Category, hw, names.GetValueOrDefault(hw, ""), all, true, rule.KeyPreset));
                }
                else if (First(list, rule) is { } s)
                {
                    result.Add(new FriendlyItem(rule.Key, rule.Category, hw, names.GetValueOrDefault(hw, ""), [s.Id], false, rule.KeyPreset));
                }
                else
                {
                    continue;
                }
                break; // first hardware that has it (first CPU, first board chip…)
            }
        }
        return result;
    }

    /// <summary>Sensor ids of the "Key" preset (default recording): ~25–40 sensors on a typical desktop.</summary>
    public static HashSet<string> KeyPresetSensorIds(IReadOnlyList<FriendlyItem> items)
        => items.Where(i => i.InKeyPreset).SelectMany(i => i.SensorIds).ToHashSet(StringComparer.Ordinal);

    private static SensorDescriptor? First(List<SensorDescriptor> list, Rule rule)
    {
        foreach (var alt in rule.Alternatives)
            foreach (var s in list)
                if (alt(s)) return s;
        return null;
    }

    /// <summary>The GPU a gamer means by "GPU": a discrete card over the integrated one; then the one with the most sensors.</summary>
    internal static string? PickMainGpu(IReadOnlyList<HardwareDescriptor> hardware, IReadOnlyDictionary<string, List<SensorDescriptor>> byHw)
    {
        var gpus = hardware.Where(h => h.Kind == HardwareKind.Gpu).ToList();
        if (gpus.Count == 0) return null;
        return gpus
            .OrderByDescending(h => IntegratedGpu().IsMatch(h.Name) ? 0 : 1)
            .ThenByDescending(h => byHw.TryGetValue(h.Id, out var l) ? l.Count : 0)
            .First().Id;
    }

    [GeneratedRegex(@"^CCD ?#?\d+( \(Tdie\))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CcdTemp();

    /// <summary>Hyper-V / VMware / VirtualBox / WSL switches, Bluetooth PAN, Wi-Fi Direct and other miniport pseudo adapters.</summary>
    [GeneratedRegex(@"vEthernet|vSwitch|VMware|VMnet|VirtualBox|Hyper-V|WSL|Loopback|Bluetooth|\*\d|Teredo|isatap|TAP-|WireGuard|Tailscale|ZeroTier", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VirtualAdapter();

    [GeneratedRegex(@"^Core #\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CoreN();

    [GeneratedRegex(@"^CPU Core #\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CpuCoreN();

    [GeneratedRegex(@"^DIMM ?#?\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DimmN();

    [GeneratedRegex(@"Radeon\(TM\) Graphics|Radeon Graphics|Intel\(R\) (UHD|Iris|HD) Graphics|Intel\(R\) Graphics|Intel\(R\) Arc\(TM\) Graphics|Vega \d+ Graphics", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntegratedGpu();

    /// <summary>Friendly names (pl, en) — the app registers them into its localization table.</summary>
    public static IReadOnlyDictionary<string, (string Pl, string En)> Texts { get; } = new Dictionary<string, (string, string)>
    {
        ["friendly.cpu.temp"] = ("Temperatura CPU", "CPU temperature"),
        ["friendly.cpu.ccd"] = ("Temperatura każdego CCD", "Temperature of every CCD"),
        ["friendly.cpu.coreTemps"] = ("Temperatura każdego rdzenia", "Temperature of every core"),
        ["friendly.cpu.clockMax"] = ("Taktowanie CPU (maks. rdzeń)", "CPU clock (fastest core)"),
        ["friendly.cpu.clocks"] = ("Taktowanie każdego rdzenia", "Clock of every core"),
        ["friendly.cpu.load"] = ("Obciążenie CPU", "CPU load"),
        ["friendly.cpu.loads"] = ("Obciążenie każdego rdzenia", "Load of every core"),
        ["friendly.cpu.vcore"] = ("Napięcie rdzeni (Vcore)", "Core voltage (Vcore)"),
        ["friendly.cpu.soc"] = ("Napięcie SoC", "SoC voltage"),
        ["friendly.cpu.power"] = ("Moc CPU", "CPU power"),
        ["friendly.gpu.temp"] = ("Temperatura GPU", "GPU temperature"),
        ["friendly.gpu.hotspot"] = ("GPU Hot Spot", "GPU Hot Spot"),
        ["friendly.gpu.memJunction"] = ("Temperatura pamięci GPU", "GPU memory temperature"),
        ["friendly.gpu.load"] = ("Obciążenie GPU", "GPU load"),
        ["friendly.gpu.clock"] = ("Taktowanie GPU", "GPU clock"),
        ["friendly.gpu.memClock"] = ("Taktowanie pamięci GPU", "GPU memory clock"),
        ["friendly.gpu.vram"] = ("Zajęta VRAM", "VRAM used"),
        ["friendly.gpu.power"] = ("Moc GPU", "GPU power"),
        ["friendly.gpu.voltage"] = ("Napięcie GPU", "GPU voltage"),
        ["friendly.gpu.fan"] = ("Wentylator GPU", "GPU fan"),
        ["friendly.ram.used"] = ("Zajęta pamięć RAM", "RAM used"),
        ["friendly.ram.load"] = ("Obciążenie RAM", "RAM load"),
        ["friendly.ram.speed"] = ("Prędkość RAM", "RAM speed"),
        ["friendly.ram.dimm"] = ("Temperatura RAM", "RAM temperature"),
        ["friendly.mb.chipset"] = ("Temperatura chipsetu", "Chipset temperature"),
        ["friendly.mb.vrm"] = ("Temperatura sekcji zasilania (VRM)", "Power stage temperature (VRM)"),
        ["friendly.mb.cpuFan"] = ("Wentylator CPU", "CPU fan"),
        ["friendly.mb.pump"] = ("Pompa", "Pump"),
        ["friendly.mb.v12"] = ("Napięcie +12V", "+12V voltage"),
        ["friendly.disk.temp"] = ("Temperatura dysków", "Drive temperatures"),
        ["friendly.disk.read"] = ("Odczyt z dysku", "Drive read"),
        ["friendly.disk.write"] = ("Zapis na dysk", "Drive write"),
        ["friendly.net.down"] = ("Pobieranie", "Download"),
        ["friendly.net.up"] = ("Wysyłanie", "Upload"),
        ["friendly.fps.fps"] = ("FPS", "FPS"),
        ["friendly.fps.frametime"] = ("Frametime", "Frametime"),
        ["friendly.fps.low1"] = ("1% low", "1% low"),
        ["friendly.fps.low01"] = ("0.1% low", "0.1% low"),
    };
}
