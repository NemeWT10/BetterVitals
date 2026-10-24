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

    /// <summary>
    /// Disk use per day for the summary line: samples × bytes per sample after compression (measured by the history once
    /// it has written enough, else a rough ~0.4 B), plus rollups.
    /// </summary>
    public static long EstimateBytesPerDay(int sensors, int intervalSeconds, double? bytesPerSample = null)
        => intervalSeconds <= 0 ? 0 : (long)(sensors * (86_400.0 / intervalSeconds) * (bytesPerSample ?? 0.4) + sensors * 1440 * 2.0);

    /// <summary>Bytes one recorded sensor adds per day as minute summaries (min / max / sum / count, ~3 B a minute).</summary>
    public const double MinuteBytesPerSensorDay = 4_500;

    /// <summary>Bytes one recorded sensor adds per day as hour summaries (~8 B an hour with the block overhead).</summary>
    public const double HourBytesPerSensorDay = 200;

    /// <summary>
    /// What each level takes once its retention is full: detail (samples × measured bytes per sample, else ~0.6 B),
    /// minute and hour summaries. A level kept forever counts one year — it grows by that much every year.
    /// </summary>
    public static (long Raw, long Minute, long Hour) EstimateRetainedBytes(RecordingSettings recording, int sensors, double? bytesPerSample = null)
    {
        static int Days(int days) => days <= 0 ? 365 : Math.Min(days, 36_500);
        var interval = Math.Clamp(recording.IntervalSeconds, 1, 60);
        var rawPerDay = Math.Max(0, sensors) * (86_400.0 / interval) * (bytesPerSample ?? 0.6);
        return (
            (long)(rawPerDay * Math.Clamp(recording.RawRetentionDays, 1, 36_500)),
            (long)(Math.Max(0, sensors) * MinuteBytesPerSensorDay * Days(recording.MinuteRetentionDays)),
            (long)(Math.Max(0, sensors) * HourBytesPerSensorDay * Days(recording.HourRetentionDays)));
    }
}
