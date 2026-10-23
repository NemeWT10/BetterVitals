using System.Globalization;

namespace BetterVitals.UI.Charts;

/// <summary>A tick of the time axis: UTC ticks, the label in local time and whether it starts a new day / month (drawn stronger).</summary>
public readonly record struct TimeTick(long Utc, string Label, bool Major);

/// <summary>
/// "Smart" local-time ticks for a time axis: a step from a ladder (1 s … 1 year) so labels stay at least
/// <c>minSpacing</c> px apart, aligned to the local wall clock (full minutes, full hours, local midnight, Mondays,
/// first of the month). DST-safe: ticks are generated as local wall-clock times and converted to UTC, skipping
/// invalid (spring-forward) times and duplicate (fall-back) ones. Labels follow the UI culture (PL: 24 h clock).
/// </summary>
public static class TimeTicks
{
    private const long S = TimeSpan.TicksPerSecond;
    private const long M = TimeSpan.TicksPerMinute;
    private const long H = TimeSpan.TicksPerHour;
    private const long D = TimeSpan.TicksPerDay;

    internal enum StepKind : byte
    {
        Fixed,
        Week,
        Month,
        Year,
    }

    /// <summary>The ladder: (approximate length, kind, count). Months / years are calendar steps.</summary>
    internal static readonly (long Ticks, StepKind Kind, int Count)[] Ladder =
    [
        (1 * S, StepKind.Fixed, 0), (2 * S, StepKind.Fixed, 0), (5 * S, StepKind.Fixed, 0), (10 * S, StepKind.Fixed, 0),
        (15 * S, StepKind.Fixed, 0), (30 * S, StepKind.Fixed, 0),
        (1 * M, StepKind.Fixed, 0), (2 * M, StepKind.Fixed, 0), (5 * M, StepKind.Fixed, 0), (10 * M, StepKind.Fixed, 0),
        (15 * M, StepKind.Fixed, 0), (30 * M, StepKind.Fixed, 0),
        (1 * H, StepKind.Fixed, 0), (2 * H, StepKind.Fixed, 0), (3 * H, StepKind.Fixed, 0), (6 * H, StepKind.Fixed, 0),
        (12 * H, StepKind.Fixed, 0),
        (1 * D, StepKind.Fixed, 0), (2 * D, StepKind.Fixed, 0),
        (7 * D, StepKind.Week, 1), (14 * D, StepKind.Week, 2),
        (30 * D, StepKind.Month, 1), (91 * D, StepKind.Month, 3), (182 * D, StepKind.Month, 6),
        (365 * D, StepKind.Year, 1), (2 * 365 * D, StepKind.Year, 2), (5 * 365 * D, StepKind.Year, 5), (10 * 365 * D, StepKind.Year, 10),
    ];

    /// <summary>Index into <see cref="Ladder"/> of the smallest step whose labels are at least <paramref name="minSpacing"/> px apart.</summary>
    public static int PickStep(long spanTicks, double widthPx, double minSpacing)
    {
        if (spanTicks <= 0 || widthPx <= 0) return Ladder.Length - 1;
        var pxPerTick = widthPx / spanTicks;
        for (var i = 0; i < Ladder.Length; i++)
            if (Ladder[i].Ticks * pxPerTick >= minSpacing) return i;
        return Ladder.Length - 1;
    }

    /// <summary>Length of a ladder step (approximate for months / years).</summary>
    public static long StepTicks(int step) => Ladder[Math.Clamp(step, 0, Ladder.Length - 1)].Ticks;

    /// <summary>Ticks inside [from, to] (UTC ticks) for a plot <paramref name="widthPx"/> wide.</summary>
    public static List<TimeTick> Build(long fromUtc, long toUtc, double widthPx, TimeZoneInfo zone, CultureInfo culture, double minSpacing = 84)
        => Build(fromUtc, toUtc, PickStep(toUtc - fromUtc, widthPx, minSpacing), zone, culture);

    public static List<TimeTick> Build(long fromUtc, long toUtc, int step, TimeZoneInfo zone, CultureInfo culture)
    {
        var result = new List<TimeTick>();
        if (toUtc <= fromUtc) return result;
        step = Math.Clamp(step, 0, Ladder.Length - 1);
        var (len, kind, count) = Ladder[step];
        var localFrom = ToLocal(fromUtc, zone);
        var wall = Align(localFrom, len, kind, count, culture);
        var last = long.MinValue;
        var guard = 0;
        while (guard++ < 2000)
        {
            if (!zone.IsInvalidTime(wall))
            {
                var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), zone).Ticks;
                if (utc > toUtc) break;
                if (utc >= fromUtc && utc > last)
                {
                    result.Add(new TimeTick(utc, Label(wall, len, kind, culture, out var major), major));
                    last = utc;
                }
            }
            wall = Next(wall, len, kind, count);
        }
        return result;
    }

    private static DateTime ToLocal(long utc, TimeZoneInfo zone)
        => DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(new DateTime(utc, DateTimeKind.Utc), zone), DateTimeKind.Unspecified);

    /// <summary>The first aligned wall-clock time at or before <paramref name="local"/>.</summary>
    private static DateTime Align(DateTime local, long len, StepKind kind, int count, CultureInfo culture)
    {
        switch (kind)
        {
            case StepKind.Year:
                return new DateTime(local.Year / count * count, 1, 1);
            case StepKind.Month:
            {
                var m = (local.Month - 1) / count * count + 1;
                return new DateTime(local.Year, m, 1);
            }
            case StepKind.Week:
            {
                var first = culture.DateTimeFormat.FirstDayOfWeek;
                var diff = ((int)local.DayOfWeek - (int)first + 7) % 7;
                var d = local.Date.AddDays(-diff);
                if (count > 1)
                {
                    // two-week steps: keep a fixed parity (weeks since a known Monday)
                    var weeks = (long)Math.Floor((d - new DateTime(2001, 1, 1)).TotalDays / 7);
                    if (weeks % count != 0) d = d.AddDays(-7 * (weeks % count));
                }
                return d;
            }
            default:
                if (len >= D)
                {
                    var days = (long)(local.Date - DateTime.MinValue).TotalDays;
                    var lenDays = len / D;
                    return DateTime.MinValue.AddDays(days / lenDays * lenDays);
                }
                // within a day: from local midnight (hours stay on full local hours whatever the offset)
                var midnight = local.Date;
                var into = local.Ticks - midnight.Ticks;
                return midnight.AddTicks(into / len * len);
        }
    }

    private static DateTime Next(DateTime wall, long len, StepKind kind, int count) => kind switch
    {
        StepKind.Year => wall.AddYears(count),
        StepKind.Month => wall.AddMonths(count),
        StepKind.Week => wall.AddDays(7 * count),
        _ => len >= D ? wall.AddDays(len / D) : NextWithinDay(wall, len),
    };

    /// <summary>Steps within a day restart at each local midnight (a 5 h step would drift otherwise; ours divide 24 h anyway).</summary>
    private static DateTime NextWithinDay(DateTime wall, long len)
    {
        var next = wall.AddTicks(len);
        return next.Date != wall.Date && next.TimeOfDay != TimeSpan.Zero ? next.Date : next;
    }

    /// <summary>The label of a tick: time of day, or the date at a day / month boundary.</summary>
    internal static string Label(DateTime wall, long len, StepKind kind, CultureInfo culture, out bool major)
    {
        major = false;
        switch (kind)
        {
            case StepKind.Year:
                major = true;
                return wall.ToString("yyyy", culture);
            case StepKind.Month:
                major = wall.Month == 1;
                return wall.Month == 1 ? wall.ToString("yyyy", culture) : MonthLabel(wall, culture);
            case StepKind.Week:
                major = wall.Day <= 7; // the first week of a month reads as a month start
                return DayLabel(wall, culture);
        }
        if (len >= D)
        {
            major = wall.Day == 1;
            return DayLabel(wall, culture);
        }
        if (wall.TimeOfDay == TimeSpan.Zero)
        {
            major = true;
            return DayLabel(wall, culture);
        }
        return wall.ToString(len < M ? LongTime(culture) : ShortTime(culture), culture);
    }

    /// <summary>"23 paź" / "Oct 23".</summary>
    public static string DayLabel(DateTime wall, CultureInfo culture) => wall.ToString(DayPattern(culture), culture);

    public static string DayPattern(CultureInfo culture) => culture.TwoLetterISOLanguageName == "pl" ? "d MMM" : "MMM d";

    /// <summary>"paź 2026" style month labels without the year (the year shows at January).</summary>
    private static string MonthLabel(DateTime wall, CultureInfo culture)
    {
        var name = culture.DateTimeFormat.GetAbbreviatedMonthName(wall.Month);
        return name.TrimEnd('.');
    }

    /// <summary>The culture's short time pattern ("HH:mm" for PL, "h:mm tt" for EN).</summary>
    public static string ShortTime(CultureInfo culture) => culture.DateTimeFormat.ShortTimePattern;

    public static string LongTime(CultureInfo culture) => culture.DateTimeFormat.LongTimePattern;
}
