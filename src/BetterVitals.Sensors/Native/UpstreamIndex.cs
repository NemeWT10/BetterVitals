using BetterVitals.Core.Sensors;

namespace BetterVitals.Sensors.Native;

/// <summary>
/// What the other sources already registered, indexed for gap detection. Built from the sensor list the engine hands to
/// <see cref="NativeSource.SetUpstream"/> (hardware names are not part of it, so hardware is recognised by the slug inside
/// its id: "…/storage/kingston-skc3000d2048g/0"). Sensors owned by the native source itself (previous discovery) are ignored.
/// </summary>
internal sealed class UpstreamIndex
{
    /// <summary>One upstream hardware node.</summary>
    public sealed class Hw
    {
        public required string Id { get; init; }

        public required HardwareKind Kind { get; init; }

        /// <summary>Name part of the id ("kingston-skc3000d2048g"); empty when the id has an unexpected shape.</summary>
        public required string Slug { get; init; }

        public required int Ordinal { get; init; }

        public List<(SensorKind Kind, string Name)> Sensors { get; } = [];

        public bool Has(SensorKind kind, string name)
        {
            foreach (var s in Sensors)
                if (s.Kind == kind && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public bool HasKind(SensorKind kind)
        {
            foreach (var s in Sensors)
                if (s.Kind == kind) return true;
            return false;
        }

        /// <summary>
        /// A real temperature reading. LibreHardwareMonitor also registers the drive's limits ("Warning Temperature",
        /// "Critical Temperature") as temperature sensors; those are constants, not a measurement.
        /// </summary>
        public bool HasMeasuredTemperature()
        {
            foreach (var s in Sensors)
                if (s.Kind == SensorKind.Temperature && !IsThreshold(s.Name)) return true;
            return false;
        }

        /// <summary>Any sensor of <paramref name="kind"/> whose name contains <paramref name="part"/>.</summary>
        public bool HasNameContaining(SensorKind kind, string part)
        {
            foreach (var s in Sensors)
                if (s.Kind == kind && s.Name.Contains(part, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    public static UpstreamIndex Empty { get; } = new([]);

    private readonly List<Hw> _hardware = [];
    private readonly HashSet<string> _sensorIds = new(StringComparer.Ordinal);

    public UpstreamIndex(IReadOnlyList<SensorDescriptor> upstream)
    {
        var byId = new Dictionary<string, Hw>(StringComparer.Ordinal);
        foreach (var s in upstream)
        {
            if (s.SourceTag is NativeTag) continue; // our own previous discovery
            _sensorIds.Add(s.Id);
            if (!byId.TryGetValue(s.HardwareId, out var hw))
            {
                ParseHardwareId(s.HardwareId, out var slug, out var ordinal);
                hw = new Hw { Id = s.HardwareId, Kind = s.HardwareKind, Slug = slug, Ordinal = ordinal };
                byId[s.HardwareId] = hw;
                _hardware.Add(hw);
            }
            hw.Sensors.Add((s.Kind, s.Name));
        }
    }

    public IReadOnlyList<Hw> Hardware => _hardware;

    public bool ContainsSensorId(string id) => _sensorIds.Contains(id);

    public bool HasHardwareOfKind(HardwareKind kind)
    {
        foreach (var h in _hardware)
            if (h.Kind == kind) return true;
        return false;
    }

    /// <summary>
    /// Upstream hardware of <paramref name="kind"/> that is the same device as <paramref name="name"/> (loose name match);
    /// with several matches the one with the same ordinal wins, else the first. Null when upstream does not know the device.
    /// </summary>
    public Hw? Match(HardwareKind kind, string name, int ordinal = 0)
    {
        Hw? first = null;
        foreach (var h in _hardware)
        {
            if (h.Kind != kind || !NameMatches(h.Slug, name)) continue;
            if (h.Ordinal == ordinal) return h;
            first ??= h;
        }
        return first;
    }

    /// <summary>All upstream hardware of <paramref name="kind"/> matching <paramref name="name"/>.</summary>
    public List<Hw> MatchAll(HardwareKind kind, string name)
    {
        var list = new List<Hw>();
        foreach (var h in _hardware)
            if (h.Kind == kind && NameMatches(h.Slug, name)) list.Add(h);
        return list;
    }

    /// <summary>"Warning Temperature", "Critical Temperature", "… Threshold": limits rather than readings.</summary>
    internal static bool IsThreshold(string sensorName)
        => sensorName.Contains("Warning", StringComparison.OrdinalIgnoreCase)
           || sensorName.Contains("Critical", StringComparison.OrdinalIgnoreCase)
           || sensorName.Contains("Threshold", StringComparison.OrdinalIgnoreCase);

    /// <summary>"kind/slug/ordinal" are the last three segments of a hardware id (a parent prefix may precede them).</summary>
    internal static void ParseHardwareId(string id, out string slug, out int ordinal)
    {
        slug = "";
        ordinal = 0;
        var parts = id.Split('/');
        if (parts.Length < 3) return;
        slug = parts[^2];
        _ = int.TryParse(parts[^1], out ordinal);
    }

    /// <summary>Lower-case letters and digits only: "KINGSTON SKC3000D2048G" and "kingston-skc3000d2048g" share one key.</summary>
    internal static string Key(string text)
    {
        Span<char> buf = text.Length <= 256 ? stackalloc char[text.Length] : new char[text.Length];
        var n = 0;
        foreach (var ch in text)
            if (char.IsLetterOrDigit(ch)) buf[n++] = char.ToLowerInvariant(ch);
        return new string(buf[..n]);
    }

    /// <summary>
    /// Same device? Equal keys, or one contains the other when the shorter one is long enough to be specific (a driver may
    /// cut a model name, LHM may add a vendor).
    /// </summary>
    internal static bool NameMatches(string slugOrName, string name)
    {
        var a = Key(slugOrName);
        var b = Key(name);
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return shorter.Length >= 8 && longer.Contains(shorter, StringComparison.Ordinal);
    }
}
