using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using BetterVitals.Core.Sensors;

namespace BetterVitals.Core.Glossary;

/// <summary>A short explanation of a sensor for less technical users (hover bubble).</summary>
public sealed record Explanation(string What, string? Watch);

/// <summary>
/// Sensor-name explanations in Polish and English, from the embedded glossary.json. Loaded lazily on the first
/// lookup (nothing is read at start-up), results cached per (kind, hardware, name, language). Matching: the first entry
/// whose kind, optional hardware and name pattern fit ({n} = digits, * = anything, case-insensitive); otherwise the
/// generic text for the kind, so every sensor gets something. Never throws.
/// </summary>
public static class Glossary
{
    private sealed record Entry(SensorKind Kind, HardwareKind? Hw, Regex Pattern, Text What, Text? Watch);

    private sealed record Text(string En, string Pl)
    {
        public string Get(bool polish) => polish && Pl.Length > 0 ? Pl : En;
    }

    private static readonly Lazy<(List<Entry> Entries, Dictionary<SensorKind, Text> Fallbacks)> Data = new(Load, isThreadSafe: true);
    private static readonly ConcurrentDictionary<(SensorKind, HardwareKind, string, bool), Explanation?> Cache = new();

    /// <summary>Explanation for a sensor, or null if the glossary is missing / has nothing at all.</summary>
    public static Explanation? Lookup(SensorKind kind, HardwareKind hardware, string name, bool polish)
        => Cache.GetOrAdd((kind, hardware, name, polish), static key => Resolve(key.Item1, key.Item2, key.Item3, key.Item4));

    private static Explanation? Resolve(SensorKind kind, HardwareKind hardware, string name, bool polish)
    {
        try
        {
            var (entries, fallbacks) = Data.Value;
            foreach (var e in entries)
            {
                if (e.Kind != kind || (e.Hw is { } hw && hw != hardware)) continue;
                if (e.Pattern.IsMatch(name)) return new Explanation(e.What.Get(polish), e.Watch?.Get(polish));
            }
            return fallbacks.TryGetValue(kind, out var f) ? new Explanation(f.Get(polish), null) : null;
        }
        catch (Exception ex)
        {
            Log.Warn("glossary: lookup failed: " + ex.Message);
            return null;
        }
    }

    private static (List<Entry>, Dictionary<SensorKind, Text>) Load()
    {
        var entries = new List<Entry>();
        var fallbacks = new Dictionary<SensorKind, Text>();
        try
        {
            using var stream = typeof(Glossary).Assembly.GetManifestResourceStream("BetterVitals.Glossary.json");
            if (stream is null) return (entries, fallbacks);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.TryGetProperty("entries", out var list))
            {
                foreach (var el in list.EnumerateArray())
                {
                    if (!Enum.TryParse<SensorKind>(Str(el, "kind"), true, out var kind)) continue;
                    HardwareKind? hw = Enum.TryParse<HardwareKind>(Str(el, "hw"), true, out var h) ? h : null;
                    var match = Str(el, "match");
                    if (string.IsNullOrEmpty(match) || ReadText(el, "what") is not { } what) continue;
                    entries.Add(new Entry(kind, hw, ToRegex(match), what, ReadText(el, "watch")));
                }
            }
            if (root.TryGetProperty("fallbacks", out var fb))
            {
                foreach (var p in fb.EnumerateObject())
                {
                    if (Enum.TryParse<SensorKind>(p.Name, true, out var kind) && ReadText(p.Value) is { } t) fallbacks[kind] = t;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("glossary: could not load", ex);
        }
        return (entries, fallbacks);
    }

    /// <summary>"Core #{n} VID" → ^Core\ \#\d+\ VID$ (ignore case).</summary>
    internal static Regex ToRegex(string pattern)
    {
        var parts = pattern.Split(["{n}", "*"], StringSplitOptions.None);
        var sb = new System.Text.StringBuilder("^");
        var pos = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            sb.Append(Regex.Escape(parts[i]));
            pos += parts[i].Length;
            if (i == parts.Length - 1) break;
            if (pattern.AsSpan(pos).StartsWith("{n}"))
            {
                sb.Append(@"\d+");
                pos += 3;
            }
            else
            {
                sb.Append(".*");
                pos += 1;
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static Text? ReadText(JsonElement el, string name) => el.TryGetProperty(name, out var v) ? ReadText(v) : null;

    private static Text? ReadText(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var en = Str(el, "en") ?? "";
        var pl = Str(el, "pl") ?? "";
        return en.Length == 0 && pl.Length == 0 ? null : new Text(en.Length > 0 ? en : pl, pl);
    }
}
