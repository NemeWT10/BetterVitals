using System.Globalization;
using BetterVitals.Core.History;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.UI.Charts;

namespace BetterVitals.UI.Pages.History;

/// <summary>The range buttons above the chart. <see cref="None"/> = the user zoomed / panned away from a preset.</summary>
public enum RangePreset : byte
{
    None,
    Min15,
    Hour1,
    Hour6,
    Hour24,
    Day7,
    Day30,
    Custom,
}

/// <summary>Pure rules of the History page (ranges, labels, units, buckets, export requests) — unit-tested without WPF.</summary>
public static class HistoryModel
{
    /// <summary>Most series on one chart.</summary>
    public const int MaxSeries = 6;

    /// <summary>Length of the live buffer the history keeps for watched sensors (and of the live-only view).</summary>
    public static readonly TimeSpan LiveBuffer = TimeSpan.FromMinutes(15);

    public static readonly RangePreset[] Presets =
        [RangePreset.Min15, RangePreset.Hour1, RangePreset.Hour6, RangePreset.Hour24, RangePreset.Day7, RangePreset.Day30];

    public static TimeSpan Length(RangePreset p) => p switch
    {
        RangePreset.Min15 => TimeSpan.FromMinutes(15),
        RangePreset.Hour1 => TimeSpan.FromHours(1),
        RangePreset.Hour6 => TimeSpan.FromHours(6),
        RangePreset.Hour24 => TimeSpan.FromHours(24),
        RangePreset.Day7 => TimeSpan.FromDays(7),
        RangePreset.Day30 => TimeSpan.FromDays(30),
        _ => TimeSpan.Zero,
    };

    public static string LabelKey(RangePreset p) => p switch
    {
        RangePreset.Min15 => "history.preset.15m",
        RangePreset.Hour1 => "history.preset.1h",
        RangePreset.Hour6 => "history.preset.6h",
        RangePreset.Hour24 => "history.preset.24h",
        RangePreset.Day7 => "history.preset.7d",
        RangePreset.Day30 => "history.preset.30d",
        _ => "history.preset.custom",
    };

    /// <summary>The window of a preset: it always ends at <paramref name="nowUtc"/> (live).</summary>
    public static TimeView ViewOf(RangePreset p, long nowUtc) => new(nowUtc - Length(p).Ticks, nowUtc);

    /// <summary>The preset a window matches (same length, ending now), or None.</summary>
    public static RangePreset Match(TimeView v, long nowUtc)
    {
        if (!IsLive(v, nowUtc)) return RangePreset.None;
        foreach (var p in Presets)
            if (Math.Abs(v.Span - Length(p).Ticks) <= Length(p).Ticks / 200) return p;
        return RangePreset.None;
    }

    /// <summary>The window follows "now": it ends within a few seconds (or 0.5 % of its length) of the present.</summary>
    public static bool IsLive(TimeView v, long nowUtc) => v.To >= nowUtc - Math.Max(3 * TimeSpan.TicksPerSecond, v.Span / 200);

    /// <summary>Live-only mode (recording off): only the 15-minute preset makes sense.</summary>
    public static bool PresetEnabled(RangePreset p, bool liveOnly) => !liveOnly || p == RangePreset.Min15;

    // ───────────── text ─────────────

    /// <summary>"1 h 15 min", "45 s", "7 dni 3 h" (largest two units).</summary>
    public static string Duration(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalMinutes < 1) return Loc.T("history.dur.s", (int)Math.Round(d.TotalSeconds));
        if (d.TotalHours < 1) return Loc.T("history.dur.min", (int)Math.Round(d.TotalMinutes));
        if (d.TotalDays < 1)
        {
            var h = (int)d.TotalHours;
            var m = (int)Math.Round(d.TotalMinutes - h * 60);
            if (m == 60)
            {
                h++;
                m = 0;
            }
            return m == 0 ? Loc.T("history.dur.h", h) : Loc.T("history.dur.h", h) + " " + Loc.T("history.dur.min", m);
        }
        var days = (int)d.TotalDays;
        var hours = (int)Math.Round(d.TotalHours - days * 24);
        if (hours == 24)
        {
            days++;
            hours = 0;
        }
        var text = Loc.Plural("history.dur.days", days);
        return hours == 0 ? text : text + " " + Loc.T("history.dur.h", hours);
    }

    /// <summary>
    /// A local-time label of a range: "dziś, 13:05 – 14:05", "wczoraj, 22:10 – dziś, 01:15", "20 paź, 08:00 – 23 paź, 14:05".
    /// </summary>
    public static string RangeText(long fromUtc, long toUtc, long nowUtc, TimeZoneInfo zone, CultureInfo culture)
    {
        var a = Local(fromUtc, zone);
        var b = Local(toUtc, zone);
        var today = Local(nowUtc, zone).Date;
        var t = TimeTicks.ShortTime(culture);
        var withSeconds = toUtc - fromUtc < 5 * TimeSpan.TicksPerMinute;
        if (withSeconds) t = TimeTicks.LongTime(culture);
        string Day(DateTime d) => d.Date == today ? Loc.T("history.today") : d.Date == today.AddDays(-1) ? Loc.T("history.yesterday") : TimeTicks.DayLabel(d, culture);
        var head = Capitalize(Day(a), culture);
        return a.Date == b.Date
            ? $"{head}, {a.ToString(t, culture)} – {b.ToString(t, culture)}"
            : $"{head}, {a.ToString(t, culture)} – {Day(b)}, {b.ToString(t, culture)}";
    }

    /// <summary>Time of a min / max: "14:05:12" today, "22 paź 14:05" on another day.</summary>
    public static string MomentText(long utc, long nowUtc, TimeZoneInfo zone, CultureInfo culture)
    {
        var a = Local(utc, zone);
        var today = Local(nowUtc, zone).Date;
        return a.Date == today
            ? a.ToString(TimeTicks.LongTime(culture), culture)
            : TimeTicks.DayLabel(a, culture) + " " + a.ToString(TimeTicks.ShortTime(culture), culture);
    }

    private static DateTime Local(long utc, TimeZoneInfo zone) => TimeZoneInfo.ConvertTimeFromUtc(new DateTime(Math.Max(0, utc), DateTimeKind.Utc), zone);

    private static string Capitalize(string s, CultureInfo culture) => s.Length == 0 ? s : char.ToUpper(s[0], culture) + s[1..];

    /// <summary>"1,2 MB" / "980 KB" in the UI culture (decimal units, like Explorer's rounding).</summary>
    public static string Bytes(long bytes, CultureInfo culture)
    {
        if (bytes < 1000) return bytes.ToString(culture) + " B";
        double v = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        var i = -1;
        while (v >= 1000 && i < units.Length - 1)
        {
            v /= 1000;
            i++;
        }
        return v.ToString(v >= 100 ? "F0" : "F1", culture) + " " + units[i];
    }

    // ───────────── values ─────────────

    /// <summary>How a sensor's base-unit values are shown: value × Scale + Offset, in Unit, with Decimals.</summary>
    public readonly record struct DisplayUnit(string Unit, double Scale, double Offset, int Decimals)
    {
        public float Apply(float v) => float.IsNaN(v) ? v : (float)(v * Scale + Offset);

        public HistoryPoint Apply(HistoryPoint p) => new(p.TimeUtcTicks, Apply(p.Min), Apply(p.Avg), Apply(p.Max));

        public string Format(double v, CultureInfo culture) => double.IsNaN(v) ? "—" : v.ToString("F" + Decimals, culture);
    }

    /// <summary>
    /// The display unit of a sensor: °F when chosen, throughput scaled to the largest value (B/s … GB/s), everything
    /// else in its base unit with the panel's default decimals.
    /// </summary>
    public static DisplayUnit DisplayOf(SensorKind kind, string baseUnit, TemperatureUnit temperature, double maxValue = double.NaN)
    {
        var unit = baseUnit.Length > 0 ? baseUnit : SensorUnits.Unit(kind);
        var dec = SensorUnits.DefaultDecimals(kind);
        if (kind == SensorKind.Temperature && temperature == TemperatureUnit.Fahrenheit) return new DisplayUnit("°F", 9.0 / 5, 32, dec);
        if (kind == SensorKind.Throughput)
        {
            var a = double.IsNaN(maxValue) ? 0 : Math.Abs(maxValue);
            return a >= 1e9 ? new DisplayUnit("GB/s", 1e-9, 0, 2)
                : a >= 1e6 ? new DisplayUnit("MB/s", 1e-6, 0, 1)
                : a >= 1e3 ? new DisplayUnit("KB/s", 1e-3, 0, 1)
                : new DisplayUnit("B/s", 1, 0, 0);
        }
        return new DisplayUnit(unit, 1, 0, dec);
    }

    // ───────────── queries ─────────────

    private static readonly long[] NiceSteps =
    [
        TimeSpan.TicksPerSecond, 2 * TimeSpan.TicksPerSecond, 5 * TimeSpan.TicksPerSecond, 10 * TimeSpan.TicksPerSecond,
        15 * TimeSpan.TicksPerSecond, 30 * TimeSpan.TicksPerSecond,
        TimeSpan.TicksPerMinute, 2 * TimeSpan.TicksPerMinute, 5 * TimeSpan.TicksPerMinute, 10 * TimeSpan.TicksPerMinute,
        15 * TimeSpan.TicksPerMinute, 30 * TimeSpan.TicksPerMinute,
        TimeSpan.TicksPerHour, 2 * TimeSpan.TicksPerHour, 3 * TimeSpan.TicksPerHour, 6 * TimeSpan.TicksPerHour, 12 * TimeSpan.TicksPerHour,
        TimeSpan.TicksPerDay, 2 * TimeSpan.TicksPerDay, 7 * TimeSpan.TicksPerDay, 14 * TimeSpan.TicksPerDay, 30 * TimeSpan.TicksPerDay,
        90 * TimeSpan.TicksPerDay, 365 * TimeSpan.TicksPerDay,
    ];

    /// <summary>
    /// The bucket width the history picks for a query (mirrors the store: the smallest nice step ≥ the sample step
    /// that splits [from, to) into at most <paramref name="maxPoints"/> aligned buckets). The chart uses it to place
    /// points at bucket centres, break lines at gaps and merge live samples into the same grid.
    /// </summary>
    public static long BucketFor(long from, long to, int maxPoints, long baseStep)
    {
        maxPoints = Math.Max(1, maxPoints);
        foreach (var w in NiceSteps)
        {
            if (w < baseStep) continue;
            var start = from / w * w;
            if ((to - start + w - 1) / w <= maxPoints) return w;
        }
        return Math.Max(TimeSpan.TicksPerDay, (to - from) / maxPoints);
    }

    /// <summary>
    /// The window to ask the history for: the visible one plus a quarter on each side (panning shows data at once),
    /// never past now + a minute; and the bucket budget (≈ 1.5 points per pixel of the padded window).
    /// </summary>
    public static (long From, long To, int MaxPoints) QueryWindow(TimeView view, double plotWidthPx, long nowUtc)
    {
        var pad = view.Span / 4;
        var from = view.From - pad;
        var to = Math.Min(view.To + pad, nowUtc + TimeSpan.TicksPerMinute);
        if (to <= from) to = from + TimeSpan.TicksPerSecond;
        var px = Math.Clamp(plotWidthPx, 100, 4000);
        var max = (int)Math.Clamp(px * 1.5 * (to - from) / Math.Max(1, view.Span), 50, 8000);
        return (from, to, max);
    }

    // ───────────── export ─────────────

    public enum ExportRange : byte
    {
        Visible,
        Selection,
        All,
    }

    /// <summary>
    /// The export request from the dialog's choices: sensors in chart order, the range (visible window, selection or
    /// everything the history covers — at most until now), resolution, ZIP.
    /// </summary>
    public static ExportRequest BuildExport(IEnumerable<string> sensorIds, ExportRange range, TimeView visible, (long From, long To)? selection,
        (DateTime FromUtc, DateTime ToUtc)? coverage, long nowUtc, ExportResolution resolution, bool zip)
    {
        long from, to;
        switch (range)
        {
            case ExportRange.Selection when selection is { } s:
                (from, to) = (s.From, s.To);
                break;
            case ExportRange.All:
                from = coverage?.FromUtc.Ticks ?? visible.From;
                to = coverage?.ToUtc.Ticks + TimeSpan.TicksPerSecond ?? visible.To;
                break;
            default:
                (from, to) = (visible.From, visible.To);
                break;
        }
        to = Math.Min(to, nowUtc);
        if (to <= from) to = from + TimeSpan.TicksPerSecond;
        var ids = sensorIds.Distinct(StringComparer.Ordinal).ToList();
        return new ExportRequest(ids, new DateTime(from, DateTimeKind.Utc), new DateTime(to, DateTimeKind.Utc), resolution, zip);
    }

    /// <summary>"BetterVitals-2026-10-23.csv.zip", or "BetterVitals-2026-10-20_2026-10-23.csv" for a range of days (local dates).</summary>
    public static string ExportFileName(ExportRequest r, TimeZoneInfo zone)
    {
        var a = TimeZoneInfo.ConvertTimeFromUtc(r.FromUtc, zone).Date;
        var b = TimeZoneInfo.ConvertTimeFromUtc(r.ToUtc.AddTicks(-1), zone).Date;
        var name = "BetterVitals-" + a.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (b > a) name += "_" + b.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return name + (r.Zip ? ".csv.zip" : ".csv");
    }
}
