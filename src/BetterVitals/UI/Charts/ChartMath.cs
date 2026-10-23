using BetterVitals.Core.History;

namespace BetterVitals.UI.Charts;

/// <summary>A nice value scale: [Min, Max] with labelled ticks every <see cref="Step"/>; <see cref="Decimals"/> for the labels.</summary>
public readonly record struct ValueScale(double Min, double Max, double Step, int Decimals)
{
    public double Span => Max - Min;

    /// <summary>The tick values from Min to Max.</summary>
    public IEnumerable<double> Ticks()
    {
        if (Step <= 0 || Span <= 0) yield break;
        var n = (int)Math.Round(Span / Step);
        for (var i = 0; i <= n && i < 64; i++) yield return Min + i * Step;
    }

    public static ValueScale Lerp(ValueScale a, ValueScale b, double t)
        => t >= 1 ? b : t <= 0 ? a : new ValueScale(a.Min + (b.Min - a.Min) * t, a.Max + (b.Max - a.Max) * t, b.Step, b.Decimals);
}

/// <summary>Where a unit's values are drawn: the left axis, the right axis, or an unlabelled own scale (third unit and on).</summary>
public enum AxisSide : byte
{
    Left,
    Right,
    Own,
}

/// <summary>The visible time window in UTC ticks.</summary>
public readonly record struct TimeView(long From, long To)
{
    public long Span => To - From;

    public bool Contains(long t) => t >= From && t <= To;
}

/// <summary>
/// The math behind <see cref="TimeChart"/>, free of WPF so it is unit-tested: nice value scales, axis grouping by
/// unit, gap breaking, nearest-point hit tests, zoom / pan / selection in time, live append into buckets, and the
/// per-pixel decimation that keeps geometry O(width).
/// </summary>
public static class ChartMath
{
    // ───────────── value scales ─────────────

    /// <summary>
    /// A "nice" scale (steps 1 / 2 / 2.5 / 5 × 10ⁿ) covering [lo, hi] with ticks at least <paramref name="minSpacing"/> px apart.
    /// Percent units always show 0–100 (values above 100 extend it); non-negative data near zero starts at 0;
    /// a flat line gets a little room around it.
    /// </summary>
    public static ValueScale Nice(double lo, double hi, double heightPx, string unit, double minSpacing = 34)
    {
        if (double.IsNaN(lo) || double.IsNaN(hi) || double.IsInfinity(lo) || double.IsInfinity(hi))
        {
            lo = 0;
            hi = 1;
        }
        (lo, hi) = Padded(lo, hi, unit);
        var target = Math.Max(2, Math.Floor(heightPx / minSpacing));
        var step = NiceStep((hi - lo) / target);
        var min = Math.Floor(lo / step + 1e-9) * step;
        var max = Math.Ceiling(hi / step - 1e-9) * step;
        if (max - min < step) max = min + step;
        return new ValueScale(min, max, step, DecimalsFor(step));
    }

    /// <summary>
    /// Left and right scales that share one grid: the same number of intervals on both sides (so the right labels sit
    /// on the left axis' grid lines), picking the interval count (2 … what fits) that wastes the least height. Steps
    /// may also be 1.5 / 3 / 4 / 6 / 8 × 10ⁿ here; percent stays exactly 0–100.
    /// </summary>
    public static (ValueScale Left, ValueScale Right) Paired(double lo1, double hi1, string unit1, double lo2, double hi2, string unit2,
        double heightPx, double minSpacing = 34)
    {
        var target = (int)Math.Max(2, Math.Floor(heightPx / minSpacing));
        var (alo, ahi) = Padded(Finite(lo1, 0), Finite(hi1, 1), unit1);
        var (blo, bhi) = Padded(Finite(lo2, 0), Finite(hi2, 1), unit2);
        (ValueScale, ValueScale)? best = null;
        var bestScore = double.MaxValue;
        for (var n = 2; n <= target; n++)
        {
            if (WithCount(alo, ahi, n, unit1) is not { } sa || WithCount(blo, bhi, n, unit2) is not { } sb) continue;
            // least wasted height; among equals a few more grid lines read better
            var score = sa.Span / Math.Max(1e-12, ahi - alo) + sb.Span / Math.Max(1e-12, bhi - blo) - 0.04 * n;
            if (score < bestScore)
            {
                bestScore = score;
                best = (sa, sb);
            }
        }
        return best ?? (Nice(lo1, hi1, heightPx, unit1, minSpacing), Nice(lo2, hi2, heightPx, unit2, minSpacing));
    }

    private static readonly double[] PairMultipliers = [1, 1.5, 2, 2.5, 3, 4, 5, 6, 8];

    /// <summary>
    /// The smallest pair-friendly step that covers [lo, hi] with exactly <paramref name="n"/> intervals (null for a
    /// percent axis that cannot show 0–100 in n even steps).
    /// </summary>
    internal static ValueScale? WithCount(double lo, double hi, int n, string unit = "")
    {
        if (n < 1 || hi <= lo) return null;
        if (unit == "%" && lo >= 0 && hi <= 100)
        {
            var s = 100.0 / n;
            return IsPairStep(s) ? new ValueScale(0, 100, s, DecimalsFor(s)) : null;
        }
        var rough = (hi - lo) / n;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        for (var k = 0; k < 3; k++, mag *= 10)
        {
            foreach (var m in PairMultipliers)
            {
                var step = m * mag;
                if (step < rough * (1 - 1e-9)) continue;
                var min = Math.Floor(lo / step + 1e-9) * step;
                if (min + n * step >= hi - 1e-9) return new ValueScale(min, min + n * step, step, DecimalsFor(step));
            }
        }
        return null;
    }

    private static bool IsPairStep(double step)
    {
        var mag = Math.Pow(10, Math.Floor(Math.Log10(step) + 1e-9));
        var m = step / mag;
        foreach (var p in PairMultipliers)
            if (Math.Abs(m - p) < 1e-6) return true;
        return Math.Abs(m - 10) < 1e-6;
    }

    private static double Finite(double v, double fallback) => double.IsNaN(v) || double.IsInfinity(v) ? fallback : v;

    /// <summary>The range <see cref="Nice"/> fits its ticks to (percent 0–100, zero when near, a little headroom).</summary>
    private static (double Lo, double Hi) Padded(double lo, double hi, string unit)
    {
        if (double.IsNaN(lo) || double.IsNaN(hi)) return (0, 1);
        if (hi < lo) (lo, hi) = (hi, lo);
        if (unit == "%") return (Math.Min(0, lo), Math.Max(100, hi));
        if (hi - lo < 1e-9)
        {
            var pad = Math.Abs(hi) > 1e-9 ? Math.Abs(hi) * 0.05 : 1;
            return (lo - pad, hi + pad);
        }
        var p = (hi - lo) * 0.06;
        return (lo >= 0 && lo < (hi - lo) * 0.5 ? 0 : lo - p, hi + p);
    }

    /// <summary>The smallest of 1 / 2 / 2.5 / 5 / 10 × 10ⁿ that is ≥ <paramref name="rough"/>.</summary>
    public static double NiceStep(double rough)
    {
        if (rough <= 0 || double.IsNaN(rough)) return 1;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        foreach (var m in (ReadOnlySpan<double>)[1, 2, 2.5, 5, 10])
            if (m * mag >= rough * (1 - 1e-9)) return m * mag;
        return 10 * mag;
    }

    /// <summary>Label decimals for a step (0.25 → 2, 2.5 → 1, 5 → 0).</summary>
    public static int DecimalsFor(double step)
    {
        if (step <= 0) return 0;
        for (var d = 0; d <= 6; d++)
        {
            var scaled = step * Math.Pow(10, d);
            if (Math.Abs(scaled - Math.Round(scaled)) < 1e-6 * Math.Max(1, scaled)) return d;
        }
        return 6;
    }

    // ───────────── axis grouping ─────────────

    /// <summary>
    /// Assigns each series (by its display unit, in order) to an axis: the first unit left, the second right, every
    /// further unit its own unlabelled scale. Returns the side per series and the units that went to own scales.
    /// </summary>
    public static (AxisSide[] Sides, string? Left, string? Right, IReadOnlyList<string> Own) AssignAxes(IReadOnlyList<string> units)
    {
        var sides = new AxisSide[units.Count];
        string? left = null, right = null;
        var own = new List<string>();
        for (var i = 0; i < units.Count; i++)
        {
            var u = units[i];
            if (left is null || left == u)
            {
                left = u;
                sides[i] = AxisSide.Left;
            }
            else if (right is null || right == u)
            {
                right = u;
                sides[i] = AxisSide.Right;
            }
            else
            {
                sides[i] = AxisSide.Own;
                if (!own.Contains(u)) own.Add(u);
            }
        }
        return (sides, left, right, own);
    }

    // ───────────── gaps ─────────────

    /// <summary>The typical spacing of the points (median of up to 64 deltas spread over the series); 0 when fewer than 2.</summary>
    public static long TypicalStep(ReadOnlySpan<HistoryPoint> pts)
    {
        if (pts.Length < 2) return 0;
        var n = Math.Min(64, pts.Length - 1);
        Span<long> d = stackalloc long[n];
        var stride = (pts.Length - 1) / (double)n;
        for (var i = 0; i < n; i++)
        {
            var j = (int)(i * stride);
            d[i] = pts[j + 1].TimeUtcTicks - pts[j].TimeUtcTicks;
        }
        d.Sort();
        return Math.Max(1, d[n / 2]);
    }

    /// <summary>A jump wider than this breaks the line (one missing bucket is tolerated, a real gap is not).</summary>
    public static long GapThreshold(long bucket) => bucket <= 0 ? long.MaxValue : bucket * 5 / 2;

    /// <summary>
    /// Splits the points into continuous runs [Start, End) — a gap (app off, PC asleep, no data) is never bridged by
    /// a line. <paramref name="gap"/> = the largest allowed spacing (<see cref="GapThreshold"/>).
    /// </summary>
    public static void Segments(ReadOnlySpan<HistoryPoint> pts, long gap, List<(int Start, int End)> output)
    {
        output.Clear();
        if (pts.Length == 0) return;
        var start = 0;
        for (var i = 1; i < pts.Length; i++)
        {
            if (pts[i].TimeUtcTicks - pts[i - 1].TimeUtcTicks > gap)
            {
                output.Add((start, i));
                start = i;
            }
        }
        output.Add((start, pts.Length));
    }

    // ───────────── hit testing ─────────────

    /// <summary>First index with time ≥ <paramref name="t"/> (binary search over sorted points).</summary>
    public static int LowerBound(ReadOnlySpan<HistoryPoint> pts, long t)
    {
        int lo = 0, hi = pts.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (pts[mid].TimeUtcTicks < t) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// The point whose bucket centre is nearest to <paramref name="t"/>, or -1 when the nearest is further than
    /// <paramref name="tolerance"/> (the cursor is over a gap).
    /// </summary>
    public static int Nearest(ReadOnlySpan<HistoryPoint> pts, long t, long bucket, long tolerance)
    {
        if (pts.Length == 0) return -1;
        var half = bucket / 2;
        var i = LowerBound(pts, t - half);
        var best = -1;
        var bestD = long.MaxValue;
        for (var j = Math.Max(0, i - 1); j <= Math.Min(pts.Length - 1, i + 1); j++)
        {
            var d = Math.Abs(pts[j].TimeUtcTicks + half - t);
            if (d < bestD)
            {
                bestD = d;
                best = j;
            }
        }
        return bestD <= tolerance ? best : -1;
    }

    /// <summary>The time at x inside a plot [left, left + width].</summary>
    public static long TimeAt(TimeView v, double left, double width, double x)
        => width <= 0 ? v.From : v.From + (long)((x - left) / width * v.Span);

    public static double XOf(TimeView v, double left, double width, long t)
        => v.Span <= 0 ? left : left + (t - v.From) / (double)v.Span * width;

    // ───────────── zoom, pan, selection ─────────────

    /// <summary>Shortest and longest window a user can zoom to.</summary>
    public const long MinSpan = TimeSpan.TicksPerMinute;

    public const long MaxSpan = 400 * TimeSpan.TicksPerDay;

    /// <summary>
    /// Zooms by <paramref name="factor"/> (&lt; 1 = in) keeping the time under the cursor (at <paramref name="anchor"/>,
    /// 0–1 across the plot) in place; the window never reaches past <paramref name="latest"/> (now).
    /// </summary>
    public static TimeView ZoomAt(TimeView v, double anchor, double factor, long latest)
    {
        anchor = Math.Clamp(anchor, 0, 1);
        var span = (long)Math.Clamp(v.Span * factor, MinSpan, MaxSpan);
        var at = v.From + (long)(anchor * v.Span);
        var from = at - (long)(anchor * span);
        return Clamp(new TimeView(from, from + span), latest);
    }

    /// <summary>Moves the window by <paramref name="delta"/> ticks (positive = later), never past <paramref name="latest"/>.</summary>
    public static TimeView Pan(TimeView v, long delta, long latest) => Clamp(new TimeView(v.From + delta, v.To + delta), latest);

    /// <summary>Keeps the span and shifts the window back so it ends at or before <paramref name="latest"/>.</summary>
    public static TimeView Clamp(TimeView v, long latest)
    {
        if (v.To <= latest) return v;
        var shift = v.To - latest;
        return new TimeView(v.From - shift, latest);
    }

    /// <summary>
    /// A drag from x1 to x2 as a time range (ordered, clamped to the plot), or null when it is narrower than
    /// <paramref name="minPx"/> (a click, not a selection).
    /// </summary>
    public static (long From, long To)? SelectionRange(TimeView v, double left, double width, double x1, double x2, double minPx = 4)
    {
        if (Math.Abs(x2 - x1) < minPx || width <= 0) return null;
        var a = Math.Clamp(Math.Min(x1, x2), left, left + width);
        var b = Math.Clamp(Math.Max(x1, x2), left, left + width);
        if (b - a < minPx) return null;
        return (TimeAt(v, left, width, a), TimeAt(v, left, width, b));
    }

    // ───────────── live append ─────────────

    /// <summary>
    /// Adds a live sample to a series of buckets <paramref name="bucket"/> wide: inside the last bucket it updates its
    /// min / max / average, later it opens a new bucket on the same grid. Older or NaN samples are ignored.
    /// <paramref name="state"/> keeps the running count of the last bucket. Returns true when the points changed.
    /// </summary>
    public static bool AppendLive(List<HistoryPoint> pts, ref LiveBucket state, long t, float value, long bucket, long sample = TimeSpan.TicksPerSecond)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) return false;
        bucket = Math.Max(1, bucket);
        if (pts.Count == 0)
        {
            var first = t - t % bucket; // on the same grid the history uses
            pts.Add(new HistoryPoint(first, value, value, value));
            state = new LiveBucket(first, 1, value, t);
            return true;
        }
        var last = pts[^1];
        if (t <= state.LastSample && state.Start == last.TimeUtcTicks) return false;
        if (t < last.TimeUtcTicks) return false;
        if (t < last.TimeUtcTicks + bucket)
        {
            if (state.Start != last.TimeUtcTicks)
            {
                // a bucket from the query: assume it holds the samples up to now at the live rate
                var n = Math.Max(1, (t - last.TimeUtcTicks) / Math.Max(1, sample));
                state = new LiveBucket(last.TimeUtcTicks, n, last.Avg * (double)n, t - 1);
            }
            if (t <= state.LastSample) return false;
            var count = state.Count + 1;
            var sum = state.Sum + value;
            state = new LiveBucket(state.Start, count, sum, t);
            pts[^1] = new HistoryPoint(last.TimeUtcTicks, Math.Min(last.Min, value), (float)(sum / count), Math.Max(last.Max, value));
            return true;
        }
        var start = last.TimeUtcTicks + (t - last.TimeUtcTicks) / bucket * bucket;
        pts.Add(new HistoryPoint(start, value, value, value));
        state = new LiveBucket(start, 1, value, t);
        return true;
    }

    /// <summary>Drops points older than <paramref name="before"/> (keeps one so the line still enters from the left).</summary>
    public static void TrimBefore(List<HistoryPoint> pts, long before)
    {
        var i = LowerBound(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pts), before);
        if (i > 1) pts.RemoveRange(0, i - 1);
    }

    // ───────────── decimation ─────────────

    /// <summary>
    /// Merges the points of <paramref name="pts"/>[start, end) that fall into the same device pixel column into one
    /// (min of mins, max of maxes, mean of averages), so drawing stays O(plot width) whatever the data density.
    /// </summary>
    public static void Decimate(ReadOnlySpan<HistoryPoint> pts, int start, int end, TimeView v, double left, double width, double pixelsPerDip,
        long half, List<HistoryPoint> output)
    {
        output.Clear();
        if (end <= start) return;
        var col = long.MinValue;
        float mn = 0, mx = 0;
        double sum = 0;
        var n = 0;
        long t0 = 0;
        var scale = pixelsPerDip <= 0 ? 1 : pixelsPerDip;
        for (var i = start; i < end; i++)
        {
            var p = pts[i];
            var c = (long)Math.Floor(XOf(v, left, width, p.TimeUtcTicks + half) * scale);
            if (c != col)
            {
                if (n > 0) output.Add(new HistoryPoint(t0, mn, (float)(sum / n), mx));
                col = c;
                t0 = p.TimeUtcTicks;
                mn = p.Min;
                mx = p.Max;
                sum = p.Avg;
                n = 1;
            }
            else
            {
                if (p.Min < mn) mn = p.Min;
                if (p.Max > mx) mx = p.Max;
                sum += p.Avg;
                n++;
            }
        }
        if (n > 0) output.Add(new HistoryPoint(t0, mn, (float)(sum / n), mx));
    }

    /// <summary>Min of mins / max of maxes of the points inside [from, to] (NaN, NaN when none).</summary>
    public static (double Lo, double Hi) Extent(ReadOnlySpan<HistoryPoint> pts, long from, long to)
    {
        double lo = double.MaxValue, hi = double.MinValue;
        for (var i = Math.Max(0, LowerBound(pts, from) - 1); i < pts.Length; i++)
        {
            var p = pts[i];
            if (p.TimeUtcTicks > to) break;
            if (float.IsNaN(p.Min) || float.IsNaN(p.Max)) continue;
            if (p.Min < lo) lo = p.Min;
            if (p.Max > hi) hi = p.Max;
        }
        return lo == double.MaxValue ? (double.NaN, double.NaN) : (lo, hi);
    }
}

/// <summary>Running state of the bucket that live samples are merged into.</summary>
public readonly record struct LiveBucket(long Start, long Count, double Sum, long LastSample);
