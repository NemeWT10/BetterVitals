using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BetterVitals.Core.Overlay;

namespace BetterVitals.UI.Pages.OverlayEditor;

/// <summary>
/// JSON of overlay layouts: deep copies (the editor's working copy, preview renders on a worker thread) and the
/// profile file format. Same conventions as settings.json (enum names, case-insensitive, comments allowed).
/// </summary>
internal static class LayoutJson
{
    /// <summary>Marks a BetterVitals overlay profile file.</summary>
    public const string Kind = "BetterVitals.OverlayProfile";

    public const int Version = 1;

    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    public static T Clone<T>(T value) where T : class
        => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, Compact), Compact)!;

    /// <summary>Stable text of a value (equality checks: "is this still the default style?").</summary>
    public static string Text<T>(T value) => JsonSerializer.Serialize(value, Compact);

    /// <summary>A profile file: kind + version + name + the layout (Enabled / Advanced are ignored on import).</summary>
    public static string WriteProfile(string? name, OverlayLayout layout)
    {
        var node = new JsonObject
        {
            ["kind"] = Kind,
            ["version"] = Version,
            ["name"] = name ?? "",
            ["layout"] = JsonSerializer.SerializeToNode(layout, Options),
        };
        return node.ToJsonString(Options);
    }

    /// <summary>
    /// Reads a profile file (or a bare layout object, e.g. copied out of settings.json). Normalized; throws
    /// <see cref="FormatException"/> when the text is not an overlay layout.
    /// </summary>
    public static (string? Name, OverlayLayout Layout) ReadProfile(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw new FormatException("Not a JSON file.", ex);
        }
        if (root is not JsonObject obj) throw new FormatException("Not an overlay profile.");

        string? name = null;
        JsonNode? layoutNode = obj;
        if (Get(obj, "layout") is JsonObject inner)
        {
            layoutNode = inner;
            name = Get(obj, "name")?.GetValueKind() == JsonValueKind.String ? Get(obj, "name")!.GetValue<string>() : null;
        }
        else if (Get(obj, "overlay") is JsonObject fromSettings)
        {
            layoutNode = fromSettings; // a whole settings.json
        }
        if (layoutNode is not JsonObject lo || (Get(lo, "items") is null && Get(lo, "style") is null && Get(lo, "placement") is null))
            throw new FormatException("Not an overlay profile.");

        OverlayLayout? layout;
        try
        {
            layout = lo.Deserialize<OverlayLayout>(Options);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            throw new FormatException("The overlay profile is damaged.", ex);
        }
        if (layout is null) throw new FormatException("Not an overlay profile.");
        layout.Normalize();
        // a profile never brings two items with the same editor id
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in layout.Items)
            if (!seen.Add(item.Id)) item.Id = Guid.NewGuid().ToString("N");
        return (string.IsNullOrWhiteSpace(name) ? null : name.Trim(), layout);
    }

    private static JsonNode? Get(JsonObject obj, string name)
    {
        foreach (var (k, v) in obj)
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return v;
        return null;
    }
}

/// <summary>Named overlay profiles saved next to the settings (%APPDATA%\BetterVitals\overlay-profiles\*.json).</summary>
internal sealed class OverlayProfileStore(string directory)
{
    public string Directory { get; } = directory;

    public IReadOnlyList<string> List()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return [];
            return System.IO.Directory.EnumerateFiles(Directory, "*.json")
                .Select(f => ReadName(f) ?? Path.GetFileNameWithoutExtension(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Core.Log.Warn("overlay profiles: cannot list: " + ex.Message);
            return [];
        }
    }

    public bool Exists(string name) => File.Exists(PathOf(name));

    public void Save(string name, OverlayLayout layout)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathOf(name);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, LayoutJson.WriteProfile(name.Trim(), layout), Encoding.UTF8);
        File.Move(tmp, path, overwrite: true);
    }

    public OverlayLayout? Load(string name)
    {
        var path = PathOf(name);
        if (!File.Exists(path))
        {
            // the file may carry a different stem than its display name
            path = System.IO.Directory.Exists(Directory)
                ? System.IO.Directory.EnumerateFiles(Directory, "*.json").FirstOrDefault(f => string.Equals(ReadName(f), name, StringComparison.OrdinalIgnoreCase)) ?? path
                : path;
            if (!File.Exists(path)) return null;
        }
        return LayoutJson.ReadProfile(File.ReadAllText(path)).Layout;
    }

    public void Delete(string name)
    {
        var path = PathOf(name);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>"My HUD: 4K" → "My HUD_ 4K.json" (no path characters, never empty).</summary>
    internal string PathOf(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var stem = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim('.', ' ');
        if (stem.Length == 0) stem = "profile";
        if (stem.Length > 80) stem = stem[..80];
        return Path.Combine(Directory, stem + ".json");
    }

    private static string? ReadName(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } s
                ? s
                : null;
        }
        catch
        {
            return null;
        }
    }
}
