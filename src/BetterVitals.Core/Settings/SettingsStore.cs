using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterVitals.Core.Settings;

/// <summary>
/// Loads, migrates and saves <see cref="AppSettings"/>. Saving is debounced (500 ms) and atomic (temp file + replace),
/// so a crash or power loss never leaves a half-written file. A file that cannot be read is kept as
/// settings.bad.json and the app starts with defaults instead of failing.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly Timer _saveTimer;
    private bool _dirty;

    public static SettingsStore I { get; private set; } = null!;

    public AppSettings Current { get; private set; }

    /// <summary>After a change, with the section name passed to <see cref="Mutate"/> ("general", "recording", "overlay"…). Caller's thread.</summary>
    public event Action<string>? Changed;

    private SettingsStore(string path, AppSettings settings)
    {
        _path = path;
        Current = settings;
        _saveTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public static SettingsStore Init(string path)
    {
        I = new SettingsStore(path, Load(path));
        return I;
    }

    /// <summary>For tests: an in-memory store over the given settings, saving to <paramref name="path"/>.</summary>
    internal static SettingsStore ForTests(string path, AppSettings settings) => I = new SettingsStore(path, settings);

    /// <summary>Applies a change, schedules the save and raises <see cref="Changed"/>.</summary>
    public void Mutate(string section, Action<AppSettings> change)
    {
        lock (_gate)
        {
            change(Current);
            _dirty = true;
        }
        _saveTimer.Change(500, Timeout.Infinite);
        Changed?.Invoke(section);
    }

    /// <summary>Writes pending changes now (exit, sleep, session end).</summary>
    public void Flush()
    {
        string json;
        lock (_gate)
        {
            if (!_dirty) return;
            json = JsonSerializer.Serialize(Current, Json);
            _dirty = false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("settings: save failed", ex);
            lock (_gate) _dirty = true;
        }
    }

    internal static AppSettings Load(string path)
    {
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
            return Migrate(settings);
        }
        catch (Exception ex)
        {
            Log.Error("settings: unreadable, starting with defaults (kept as settings.bad.json)", ex);
            try { File.Copy(path, Path.ChangeExtension(path, ".bad.json"), overwrite: true); } catch { /* best effort */ }
            return new AppSettings();
        }
    }

    /// <summary>Upgrades older schema versions in place. A file from a NEWER version is used as far as it is understood.</summary>
    internal static AppSettings Migrate(AppSettings s)
    {
        // schema 1 is the first; future: if (s.SchemaVersion < 2) { … s.SchemaVersion = 2; }
        s.General ??= new GeneralSettings();
        s.Recording ??= new RecordingSettings();
        s.Recording.SensorIds ??= new HashSet<string>(StringComparer.Ordinal);
        if (s.Recording.SensorIds.Comparer != StringComparer.Ordinal)
            s.Recording.SensorIds = new HashSet<string>(s.Recording.SensorIds, StringComparer.Ordinal);
        s.Overlay ??= new Overlay.OverlayLayout();
        s.Overlay.Normalize();
        s.Flyout ??= new FlyoutSettings();
        s.Updates ??= new UpdateSettings();
        s.Driver ??= new DriverSettings();
        if (s.SchemaVersion < AppSettings.CurrentSchema) s.SchemaVersion = AppSettings.CurrentSchema;
        return s;
    }
}
