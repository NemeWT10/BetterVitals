namespace BetterVitals.Core.History;

/// <summary>One chart point: the min / average / max of the samples in its time bucket (UTC ticks of bucket start).</summary>
public readonly record struct HistoryPoint(long TimeUtcTicks, float Min, float Avg, float Max);

/// <summary>Exact statistics of a time range (from raw samples where they exist, else from rollups).</summary>
public readonly record struct RangeStats(float Min, float Avg, float Max, long MinTimeUtcTicks, long MaxTimeUtcTicks, long Count);

/// <summary>Disk use of the history; <paramref name="BytesPerValue"/> is measured this session (null until enough was written).</summary>
public readonly record struct HistoryUsage(long BytesOnDisk, long EstimatedBytesPerDay, DateTime? OldestUtc, double? BytesPerValue = null);

public enum ExportResolution : byte
{
    /// <summary>Every stored sample.</summary>
    Raw,

    /// <summary>One row per minute with min / avg / max.</summary>
    Minute,

    /// <summary>One row per hour with min / avg / max.</summary>
    Hour,
}

/// <summary>"Export to file" — as light as possible (user decision): chosen sensors, range, resolution, optional ZIP.</summary>
public sealed record ExportRequest(
    IReadOnlyList<string> SensorIds,
    DateTime FromUtc,
    DateTime ToUtc,
    ExportResolution Resolution,
    bool Zip);

/// <summary>
/// Read side of the history, used by the UI (charts, stats, export, settings page). Implemented by BetterVitals.History.
/// With recording off it answers from the in-memory live buffer (last ~15 min of the sensors the UI watches) —
/// callers do not need to know which. All methods are thread-safe and never block the caller for long.
/// </summary>
public interface IHistoryQuery
{
    /// <summary>
    /// Points for a chart over [from, to): picks raw / 1 min / 1 h data so the result has at most
    /// <paramref name="maxPoints"/> buckets; every bucket carries min / avg / max, so peaks never vanish when zoomed out.
    /// Gaps (app not running, PC asleep) are simply missing buckets.
    /// </summary>
    Task<IReadOnlyList<HistoryPoint>> GetSeriesAsync(string sensorId, DateTime fromUtc, DateTime toUtc, int maxPoints, CancellationToken ct = default);

    /// <summary>Min / avg / max (+ when) over the range, or null when there is no data.</summary>
    Task<RangeStats?> GetStatsAsync(string sensorId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>First and last time with data for the sensor (null when none).</summary>
    (DateTime FromUtc, DateTime ToUtc)? GetCoverage(string sensorId);

    HistoryUsage GetUsage();

    /// <summary>Rough output size before exporting (shown in the dialog).</summary>
    long EstimateExportBytes(ExportRequest request);

    /// <summary>Writes CSV (or a ZIP with the CSV) to <paramref name="output"/>.</summary>
    Task ExportAsync(ExportRequest request, Stream output, IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>Deletes all stored history (settings untouched).</summary>
    Task ClearAsync();
}
