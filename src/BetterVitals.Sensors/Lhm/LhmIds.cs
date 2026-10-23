using BetterVitals.Core.Sensors;

namespace BetterVitals.Sensors.Lhm;

/// <summary>One hardware node as seen by the id builder.</summary>
/// <param name="Parent">Index of the parent in the same list, -1 for top level.</param>
/// <param name="Kind">Component family.</param>
/// <param name="Name">Verbatim LHM name.</param>
/// <param name="StableKey">Serial number / PCI instance id / LHM identifier — anything that does not change when LHM re-orders devices.</param>
/// <param name="Order">LHM enumeration order (last tie-breaker only).</param>
internal readonly record struct HardwareKey(int Parent, HardwareKind Kind, string Name, string StableKey, int Order);

/// <summary>One sensor of a hardware node as seen by the id builder.</summary>
internal readonly record struct SensorKey(SensorKind Kind, string Name, int Index);

/// <summary>
/// Stable id assignment. Hardware: ordinal among siblings with the same kind and name slug, ordered by the stable key
/// (so LHM's adapter order never matters). Sensors: "#k" for the k-th sensor with the same kind and name slug, ordered
/// by LHM index.
/// </summary>
internal static class LhmIds
{
    /// <summary>Hardware ids for <paramref name="items"/> (parents must precede their children).</summary>
    public static string[] HardwareIds(IReadOnlyList<HardwareKey> items)
    {
        var ordinals = Ordinals(items);
        var ids = new string[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.Parent >= i) throw new ArgumentException("A parent must precede its children.", nameof(items));
            ids[i] = SensorIds.Hardware(it.Kind, it.Name, ordinals[i], it.Parent < 0 ? null : ids[it.Parent]);
        }
        return ids;
    }

    /// <summary>Ordinal of each item among siblings of the same kind and name slug.</summary>
    public static int[] Ordinals(IReadOnlyList<HardwareKey> items)
    {
        var result = new int[items.Count];
        var groups = new Dictionary<(int, HardwareKind, string), List<int>>();
        for (var i = 0; i < items.Count; i++)
        {
            var key = (items[i].Parent, items[i].Kind, SensorIds.Slug(items[i].Name));
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add(i);
        }
        foreach (var list in groups.Values)
        {
            list.Sort((a, b) =>
            {
                var c = string.CompareOrdinal(items[a].StableKey, items[b].StableKey);
                return c != 0 ? c : items[a].Order.CompareTo(items[b].Order);
            });
            for (var k = 0; k < list.Count; k++) result[list[k]] = k;
        }
        return result;
    }

    /// <summary>Sensor ids for one hardware node; repeats of a (kind, name slug) pair get "#2", "#3"… by LHM index.</summary>
    public static string[] SensorIdsFor(string hardwareId, IReadOnlyList<SensorKey> sensors)
    {
        var order = new int[sensors.Count];
        for (var i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            var c = sensors[a].Kind.CompareTo(sensors[b].Kind);
            if (c != 0) return c;
            c = sensors[a].Index.CompareTo(sensors[b].Index);
            return c != 0 ? c : a.CompareTo(b);
        });
        var seen = new Dictionary<(SensorKind, string), int>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var ids = new string[sensors.Count];
        foreach (var i in order)
        {
            var key = (sensors[i].Kind, SensorIds.Slug(sensors[i].Name));
            seen.TryGetValue(key, out var n);
            string id;
            do
            {
                n++;
                id = SensorIds.Sensor(hardwareId, sensors[i].Kind, sensors[i].Name, n);
            }
            while (!used.Add(id));
            seen[key] = n;
            ids[i] = id;
        }
        return ids;
    }
}
