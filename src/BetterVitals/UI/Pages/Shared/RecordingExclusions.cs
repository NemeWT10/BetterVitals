using System.IO;
using System.Text.Json;
using BetterVitals.Core;

namespace BetterVitals.UI.Pages.Shared;

/// <summary>
/// Sensors the user explicitly switched off recording for — the <c>userRemoved</c> set of
/// <see cref="Core.Catalog.RecordingPolicy.MissingKeySensors"/>, so a key sensor taken out of the "Key" preset is never
/// re-added when the policy tops the recorded set up (new hardware, next start). Switching a sensor on again takes it
/// out of the set; the "Key" and "All" presets clear it for what they add, "Nothing" puts every key sensor in.
/// The app's instance lives in <c>RecordingSettings.RemovedSensorIds</c> (settings.json); a file-backed instance exists for tests.
/// Thread-safe.
/// </summary>
public sealed class RecordingExclusions
{
    private static RecordingExclusions? _instance;

    private readonly string? _path;
    private readonly bool _inSettings;
    private readonly Lock _gate = new();
    private HashSet<string>? _ids;

    public RecordingExclusions(string? path) => _path = path;

    private RecordingExclusions(bool inSettings) => _inSettings = inSettings;

    /// <summary>The app's set (next to settings.json; in memory only when no data folder is set up).</summary>
    public static RecordingExclusions I
    {
        get => _instance ??= Core.Settings.SettingsStore.I is not null ? new RecordingExclusions(inSettings: true) : new RecordingExclusions((string?)null);
        set => _instance = value;
    }

    /// <summary>After any change (caller's thread).</summary>
    public event Action? Changed;

    public bool Contains(string id)
    {
        lock (_gate) return Ids.Contains(id);
    }

    /// <summary>A copy of the set (pass to RecordingPolicy, keep for undo).</summary>
    public HashSet<string> Snapshot()
    {
        lock (_gate) return new HashSet<string>(Ids, StringComparer.Ordinal);
    }

    public void Exclude(IEnumerable<string> ids) => Change(set =>
    {
        var changed = false;
        foreach (var id in ids) changed |= set.Add(id);
        return changed;
    });

    public void Include(IEnumerable<string> ids) => Change(set =>
    {
        var changed = false;
        foreach (var id in ids) changed |= set.Remove(id);
        return changed;
    });

    /// <summary>Back to an earlier <see cref="Snapshot"/> (undo).</summary>
    public void Restore(IReadOnlySet<string> snapshot) => Change(set =>
    {
        if (set.SetEquals(snapshot)) return false;
        set.Clear();
        set.UnionWith(snapshot);
        return true;
    });

    private HashSet<string> Ids => _inSettings ? Core.Settings.SettingsStore.I.Current.Recording.RemovedSensorIds : _ids ??= Load();

    private void Change(Func<HashSet<string>, bool> change)
    {
        string[]? toSave;
        lock (_gate)
        {
            if (!change(Ids)) return;
            toSave = _path is null ? null : Ids.Order(StringComparer.Ordinal).ToArray();
        }
        // in settings: mark dirty, save (debounced) and let listeners know
        if (_inSettings) Core.Settings.SettingsStore.I.Mutate("recording", static _ => { });
        if (toSave is not null) Save(toSave);
        Changed?.Invoke();
    }

    private HashSet<string> Load()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (_path is null || !File.Exists(_path)) return set;
        try
        {
            if (JsonSerializer.Deserialize<string[]>(File.ReadAllText(_path)) is { } ids)
                foreach (var id in ids)
                    if (!string.IsNullOrWhiteSpace(id)) set.Add(id);
        }
        catch (Exception ex)
        {
            Log.Warn($"recording exclusions: unreadable, starting empty ({ex.Message})");
        }
        return set;
    }

    private void Save(string[] ids)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path!)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(ids));
            File.Move(tmp, _path!, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"recording exclusions: save failed ({ex.Message})");
        }
    }
}
