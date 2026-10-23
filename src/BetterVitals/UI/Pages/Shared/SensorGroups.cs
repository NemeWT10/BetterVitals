using System.Text.RegularExpressions;
using BetterVitals.Core.Catalog;
using BetterVitals.Core.Sensors;

namespace BetterVitals.UI.Pages.Shared;

/// <summary>What a group of sensors measures: the sections of an expanded card and the Sensors page's type chips.</summary>
public enum KindGroup : byte
{
    Temperature,
    Load,
    Clock,
    Voltage,
    Power,
    Fan,
    Memory,
    Throughput,
    Level,
    Fps,
    Other,
}

public static class SensorGroups
{
    /// <summary>Display order of the groups.</summary>
    public static IReadOnlyList<KindGroup> Order { get; } = Enum.GetValues<KindGroup>();

    public static KindGroup Of(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => KindGroup.Temperature,
        SensorKind.Load => KindGroup.Load,
        SensorKind.Clock or SensorKind.Frequency => KindGroup.Clock,
        SensorKind.Voltage or SensorKind.Current => KindGroup.Voltage,
        SensorKind.Power or SensorKind.Energy => KindGroup.Power,
        // Control = fan / pump duty in %
        SensorKind.Fan or SensorKind.Control or SensorKind.Flow => KindGroup.Fan,
        SensorKind.Data or SensorKind.SmallData => KindGroup.Memory,
        SensorKind.Throughput => KindGroup.Throughput,
        SensorKind.Level => KindGroup.Level,
        SensorKind.Fps or SensorKind.FrameTime => KindGroup.Fps,
        _ => KindGroup.Other,
    };

    /// <summary>Plural section label ("Temperatury").</summary>
    public static string LabelKey(KindGroup g) => "sensors.group." + g;

    public static string IconKey(KindGroup g) => g switch
    {
        KindGroup.Temperature => "Icon.Thermometer",
        KindGroup.Load => "Icon.Gauge",
        KindGroup.Clock => "Icon.Clock",
        KindGroup.Voltage => "Icon.Wave",
        KindGroup.Power => "Icon.Bolt",
        KindGroup.Fan => "Icon.Fan",
        KindGroup.Memory => "Icon.Memory",
        KindGroup.Throughput => "Icon.Network",
        KindGroup.Level => "Icon.Battery",
        KindGroup.Fps => "Icon.Game",
        _ => "Icon.Pulse",
    };
}

/// <summary>
/// A top-level hardware node with everything under it (the motherboard with its SuperIO / EC chips): one Overview card,
/// one Sensors page section.
/// </summary>
public sealed record HardwareGroup(HardwareDescriptor Root, IReadOnlyList<HardwareDescriptor> Members, IReadOnlyList<SensorDescriptor> Sensors)
{
    public string Id => Root.Id;

    public HardwareKind Kind => Root.Kind;

    public string Name => Root.Name;
}

/// <summary>Groups the hub's inventory into <see cref="HardwareGroup"/>s in the order people look for them.</summary>
public static partial class HardwareTree
{
    /// <summary>
    /// CPU, GPUs (the discrete card first, the integrated one last), memory (physical before virtual), motherboard,
    /// drives, network (virtual adapters last), battery, everything else, the game / FPS. Children are merged into
    /// their top-level parent. Hardware without a present sensor is left out.
    /// </summary>
    public static IReadOnlyList<HardwareGroup> Build(ISensorHub hub)
    {
        var hardware = hub.Hardware;
        var byId = new Dictionary<string, HardwareDescriptor>(StringComparer.Ordinal);
        foreach (var h in hardware) byId[h.Id] = h;

        string RootOf(HardwareDescriptor h)
        {
            var cur = h;
            for (var guard = 0; guard < 8 && cur.ParentId is { } p && byId.TryGetValue(p, out var parent); guard++) cur = parent;
            return cur.Id;
        }

        var members = new Dictionary<string, List<HardwareDescriptor>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var h in hardware)
        {
            var root = RootOf(h);
            if (!members.TryGetValue(root, out var list))
            {
                members[root] = list = [];
                order.Add(root);
            }
            list.Add(h);
        }

        var memberRank = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, list) in members)
            for (var i = 0; i < list.Count; i++) memberRank[list[i].Id] = i;

        var sensorsOf = new Dictionary<string, List<SensorDescriptor>>(StringComparer.Ordinal);
        var all = hub.Sensors;
        for (var i = 0; i < all.Count; i++)
        {
            var s = all[i];
            if (!byId.TryGetValue(s.HardwareId, out var hw) || !hub.IsPresent(s.Index)) continue;
            var root = RootOf(hw);
            if (!sensorsOf.TryGetValue(root, out var list)) sensorsOf[root] = list = [];
            list.Add(s);
        }

        var byHwForGpu = all.GroupBy(s => s.HardwareId).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var mainGpu = FriendlyCatalog.PickMainGpu(hardware, byHwForGpu);

        var groups = new List<(HardwareGroup Group, int Rank, int Sub, int Index)>();
        for (var i = 0; i < order.Count; i++)
        {
            var id = order[i];
            if (!sensorsOf.TryGetValue(id, out var sensors) || sensors.Count == 0) continue;
            var root = byId[id];
            // discovery order per member (the hardware's natural order; derived values come after the source's own)
            var ordered = sensors.OrderBy(s => memberRank.GetValueOrDefault(s.HardwareId)).ThenBy(s => s.Index).ToList();
            var (rank, sub) = Rank(root, mainGpu, sensors.Count);
            groups.Add((new HardwareGroup(root, members[id], ordered), rank, sub, i));
        }
        return groups.OrderBy(g => g.Rank).ThenBy(g => g.Sub).ThenBy(g => g.Index).Select(g => g.Group).ToList();
    }

    private static (int Rank, int Sub) Rank(HardwareDescriptor h, string? mainGpu, int sensorCount) => h.Kind switch
    {
        HardwareKind.Cpu => (0, 0),
        HardwareKind.Gpu => (1, h.Id == mainGpu ? 0 : IntegratedGpu().IsMatch(h.Name) ? 100_000 : 1000 - Math.Min(999, sensorCount)),
        HardwareKind.Memory => (2, h.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ? 1 : 0),
        HardwareKind.Motherboard => (3, 0),
        HardwareKind.Storage => (4, 0),
        HardwareKind.Network => (5, VirtualAdapter().IsMatch(h.Name) ? 1 : 0),
        HardwareKind.Battery => (6, 0),
        HardwareKind.Game => (9, 0),
        _ => (7, (int)h.Kind),
    };

    [GeneratedRegex(@"Radeon\(TM\) Graphics|Radeon Graphics|Intel\(R\) (UHD|Iris|HD) Graphics|Intel\(R\) Graphics|Intel\(R\) Arc\(TM\) Graphics|Vega \d+ Graphics", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntegratedGpu();

    [GeneratedRegex(@"vEthernet|vSwitch|VMware|VMnet|VirtualBox|Hyper-V|WSL|Loopback|Bluetooth|\*\d|Teredo|isatap|TAP-|WireGuard|Tailscale|ZeroTier", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VirtualAdapter();
}
