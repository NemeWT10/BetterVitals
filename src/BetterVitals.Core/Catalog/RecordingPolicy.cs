using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;

namespace BetterVitals.Core.Catalog;

/// <summary>
/// Keeps the recorded set sensible without the user doing anything: the "Key" preset on first run, and the key
/// sensors of hardware that shows up later (a new GPU, a second drive) as long as the user has not opted out of the
/// preset. Sensors the user switched off stay off. Pure — the caller applies the result through SettingsStore.
/// </summary>
public static class RecordingPolicy
{
    /// <summary>
    /// Sensor ids to ADD to <paramref name="recording"/> for the current inventory (empty when nothing changes).
    /// <paramref name="userRemoved"/> = ids the user explicitly switched off (never re-added).
    /// </summary>
    public static IReadOnlyCollection<string> MissingKeySensors(RecordingSettings recording, IReadOnlyList<FriendlyItem> items, IReadOnlySet<string> userRemoved)
    {
        if (!recording.KeyPresetApplied && recording.SensorIds.Count > 0) return []; // the user built their own set before any preset
        var key = FriendlyCatalog.KeyPresetSensorIds(items);
        key.ExceptWith(recording.SensorIds);
        key.ExceptWith(userRemoved);
        return key;
    }

    /// <summary>The ids that are recorded right now and exist on this PC (for the "N sensors · X MB/day" summary).</summary>
    public static int CountPresent(RecordingSettings recording, ISensorHub hub)
    {
        var n = 0;
        foreach (var id in recording.SensorIds)
            if (hub.Find(id) is { } d && hub.IsPresent(d.Index)) n++;
        return n;
    }

    /// <summary>Rough disk use per day for the summary line (~0.4 B per sample after compression, plus rollups).</summary>
    public static long EstimateBytesPerDay(int sensors, int intervalSeconds)
        => intervalSeconds <= 0 ? 0 : (long)(sensors * (86_400.0 / intervalSeconds) * 0.4 + sensors * 1440 * 2.0);
}
