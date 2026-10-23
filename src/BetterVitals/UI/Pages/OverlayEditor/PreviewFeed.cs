using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Overlay;

namespace BetterVitals.UI.Pages.OverlayEditor;

/// <summary>
/// Live values for the editor (tiles, picker, preview): sampled from the hub's latest frame once per editor tick for the
/// sensors the page shows, with a short history per sensor for the preview's sparklines and Max / Average aggregation.
/// Game sensors without a game get a fixed example value, so the preview of the "game scene" reads like a game (the
/// page says so). UI thread only.
/// </summary>
internal sealed class PreviewFeed(ISensorHub hub)
{
    public const int HistoryLength = 40;

    private sealed class Ring
    {
        public readonly float[] Values = new float[HistoryLength];
        public int Count;
        public int Start;

        public void Push(float v)
        {
            if (Count < Values.Length) Values[(Start + Count++) % Values.Length] = v;
            else
            {
                Values[Start] = v;
                Start = (Start + 1) % Values.Length;
            }
        }

        public float[] ToArray()
        {
            var a = new float[Count];
            for (var i = 0; i < Count; i++) a[i] = Values[(Start + i) % Values.Length];
            return a;
        }
    }

    private readonly Dictionary<string, Ring> _rings = new(StringComparer.Ordinal);

    public ISensorHub Hub { get; } = hub;

    /// <summary>The latest value (NaN when unknown).</summary>
    public float Value(string sensorId)
    {
        var s = Hub.Find(sensorId);
        return s is null || s.Index < 0 ? float.NaN : Hub.Latest[s.Index];
    }

    /// <summary>One sample of each sensor into its history (the editor's 1 Hz tick).</summary>
    public void Sample(IEnumerable<string> sensorIds)
    {
        var frame = Hub.Latest;
        foreach (var id in sensorIds)
        {
            var s = Hub.Find(id);
            if (s is null || s.Index < 0) continue;
            if (!_rings.TryGetValue(id, out var ring)) _rings[id] = ring = new Ring();
            ring.Push(frame[s.Index]);
        }
    }

    /// <summary>Older values (oldest first), e.g. from a preview hub, so sparklines start filled.</summary>
    public void Prefill(string sensorId, IReadOnlyList<float> values)
    {
        if (!_rings.TryGetValue(sensorId, out var ring)) _rings[sensorId] = ring = new Ring();
        foreach (var v in values) ring.Push(v);
    }

    public bool HasHistory(string sensorId) => _rings.TryGetValue(sensorId, out var r) && r.Count > 0;

    /// <summary>Forget sensors nobody shows any more (keeps the memory flat over a long session).</summary>
    public void Trim(IReadOnlySet<string> keep)
    {
        foreach (var id in _rings.Keys.Where(k => !keep.Contains(k)).ToList()) _rings.Remove(id);
    }

    /// <summary>Preview values for every sensor of the layout's items.</summary>
    public List<PreviewSensor> Build(OverlayLayout layout)
    {
        var result = new List<PreviewSensor>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in layout.Items)
        {
            foreach (var id in item.SensorIds)
            {
                if (!seen.Add(id)) continue;
                var descriptor = Hub.Find(id) ?? Describe(id);
                if (descriptor is null) continue;
                var value = descriptor.Index >= 0 ? Hub.Latest[descriptor.Index] : float.NaN;
                var history = _rings.TryGetValue(id, out var ring) && ring.Count > 0 ? ring.ToArray() : null;
                if (float.IsNaN(value) && Example(descriptor) is { } example)
                {
                    value = example;
                    history = ExampleHistory(example, id);
                }
                result.Add(new PreviewSensor(descriptor, value, history));
            }
        }
        return result;
    }

    /// <summary>True when the preview shows example game values (no game in the foreground right now).</summary>
    public bool UsesExampleValues(OverlayLayout layout)
        => layout.Items.SelectMany(i => i.SensorIds).Any(id =>
        {
            var d = Hub.Find(id) ?? Describe(id);
            return d is not null && Example(d) is not null && (d.Index < 0 || float.IsNaN(Hub.Latest[d.Index]));
        });

    /// <summary>A descriptor for a configured sensor the hub does not know (yet), from its stable id.</summary>
    internal static SensorDescriptor? Describe(string id)
    {
        if (!DefaultLabels.TryParseId(id, out var hw, out var kind, out var name)) return null;
        var slash = id.LastIndexOf('/');
        var sensorKindSlash = slash > 0 ? id.LastIndexOf('/', slash - 1) : -1;
        return new SensorDescriptor
        {
            Id = id,
            HardwareId = sensorKindSlash > 0 ? id[..sensorKindSlash] : id,
            Name = name,
            Kind = kind,
            HardwareKind = hw,
        };
    }

    private static float? Example(SensorDescriptor s)
    {
        if (s.HardwareKind != HardwareKind.Game && s.Kind is not (SensorKind.Fps or SensorKind.FrameTime)) return null;
        var n = s.Name.ToLowerInvariant();
        if (s.Kind == SensorKind.FrameTime) return n.Contains("max") ? 11.8f : 6.9f;
        if (s.Kind != SensorKind.Fps) return null;
        if (n.Contains("0.1%") || n.Contains("0,1%")) return 96;
        if (n.Contains("1%")) return 118;
        return 144;
    }

    private static float[] ExampleHistory(float center, string seed)
    {
        var a = new float[HistoryLength];
        var phase = (seed.Length % 7) * 0.4;
        for (var i = 0; i < a.Length; i++)
            a[i] = center * (float)(1 + 0.05 * Math.Sin(i * 0.45 + phase) + 0.025 * Math.Sin(i * 1.3 + phase * 2));
        return a;
    }
}
