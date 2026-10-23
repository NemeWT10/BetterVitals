using BetterVitals.Core.Sensors;

namespace BetterVitals.History.Storage;

/// <summary>Storage resolution: values travel through the store as integers q = round(value / quantum).</summary>
internal static class Quant
{
    /// <summary>|q| is clamped to 2^40 so sums of a few million samples cannot overflow a long.</summary>
    public const long Limit = 1L << 40;

    public static double Of(SensorKind kind) => SensorUnits.Quantum(kind);

    /// <summary>False for NaN / infinity (a gap).</summary>
    public static bool TryQuantize(float value, double quantum, out long q)
    {
        if (!float.IsFinite(value))
        {
            q = 0;
            return false;
        }
        var x = Math.Round(value / quantum);
        q = x >= Limit ? Limit : x <= -Limit ? -Limit : (long)x;
        return true;
    }

    public static float ToFloat(long q, double quantum) => (float)(q * quantum);
}

/// <summary>Data resolution of a record / file.</summary>
internal enum Level : byte
{
    Raw = 0,
    Minute = 1,
    Hour = 2,
}

/// <summary>
/// One aggregate (or a single raw sample: Min = Max = Sum, Count = 1) in quantized units.
/// <see cref="Time"/> is the sample time or the bucket start (UTC ticks).
/// </summary>
internal struct Rec
{
    public long Time;
    public long Min;
    public long Max;
    public long Sum;
    public long Count;

    /// <summary>Resolution the record came from (set by the readers).</summary>
    public Level Level;

    public static Rec Sample(long time, long q) => new() { Time = time, Min = q, Max = q, Sum = q, Count = 1 };

    /// <summary>Merges <paramref name="o"/> into this (keeps this record's time).</summary>
    public void Merge(in Rec o)
    {
        if (o.Count == 0) return;
        if (Count == 0)
        {
            Min = o.Min;
            Max = o.Max;
        }
        else
        {
            if (o.Min < Min) Min = o.Min;
            if (o.Max > Max) Max = o.Max;
        }
        Sum += o.Sum;
        Count += o.Count;
    }
}

/// <summary>Receives records while a store read walks the data. <paramref name="ordinal"/> indexes the requested sensors.</summary>
internal interface IRecVisitor
{
    void Visit(int ordinal, in Rec rec);
}

internal static class TimeGrid
{
    public const long Second = TimeSpan.TicksPerSecond;
    public const long Minute = TimeSpan.TicksPerMinute;
    public const long Hour = TimeSpan.TicksPerHour;
    public const long Day = TimeSpan.TicksPerDay;

    /// <summary>UTC ticks; an Unspecified kind is taken as UTC (the API speaks UTC), only Local is converted.</summary>
    public static long Utc(DateTime d) => d.Kind == DateTimeKind.Local ? d.ToUniversalTime().Ticks : d.Ticks;

    public static long Floor(long t, long step) => t >= 0 ? t - t % step : t - ((t % step) + step) % step;

    public static long Ceil(long t, long step)
    {
        var f = Floor(t, step);
        return f == t ? t : f + step;
    }

    public static long Step(Level level) => level switch
    {
        Level.Minute => Minute,
        Level.Hour => Hour,
        _ => Second,
    };
}
