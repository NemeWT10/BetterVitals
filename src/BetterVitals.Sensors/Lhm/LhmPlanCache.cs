using BetterVitals.Core.Sensors;

namespace BetterVitals.Sensors.Lhm;

/// <summary>
/// Small cache of per-request plans keyed by the CONTENT of the requested span (the sensor slot indices, in order).
/// The engine asks for a handful of distinct sets (one per demand interval), so a lookup is an O(n) compare of ints
/// against a few cached keys — no hashing of strings, no allocation.
/// </summary>
internal sealed class LhmPlanCache<TPlan> where TPlan : class
{
    private readonly Entry[] _entries;
    private int _next;

    public LhmPlanCache(int capacity = 8) => _entries = new Entry[capacity];

    /// <summary>Plans built so far (diagnostics; a growing number under a steady load means churn).</summary>
    public int Builds { get; private set; }

    public static int Hash(ReadOnlySpan<SensorDescriptor> requested)
    {
        var h = unchecked((int)2166136261);
        foreach (var d in requested) h = unchecked((h ^ d.Index) * 16777619);
        return h ^ requested.Length;
    }

    public TPlan? Find(ReadOnlySpan<SensorDescriptor> requested, int generation)
    {
        var hash = Hash(requested);
        foreach (var e in _entries)
        {
            if (e.Plan is null || e.Hash != hash || e.Generation != generation || e.Key.Length != requested.Length) continue;
            if (Matches(e.Key, requested)) return e.Plan;
        }
        return null;
    }

    public void Add(ReadOnlySpan<SensorDescriptor> requested, int generation, TPlan plan)
    {
        var key = new int[requested.Length];
        for (var i = 0; i < key.Length; i++) key[i] = requested[i].Index;
        _entries[_next] = new Entry(key, Hash(requested), generation, plan);
        _next = (_next + 1) % _entries.Length;
        Builds++;
    }

    public void Clear()
    {
        Array.Clear(_entries);
        _next = 0;
    }

    private static bool Matches(int[] key, ReadOnlySpan<SensorDescriptor> requested)
    {
        for (var i = 0; i < key.Length; i++)
            if (key[i] != requested[i].Index) return false;
        return true;
    }

    private readonly record struct Entry(int[] Key, int Hash, int Generation, TPlan? Plan);
}
