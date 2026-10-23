namespace BetterVitals.Fps;

/// <summary>
/// Frame statistics of one process. All NaN when it does not present.
/// <list type="bullet">
/// <item><see cref="Framerate"/>: presents per second over the last ~1 s, ending at the newest present
/// ((n − 1) / (t_last − t_first)). Below 2 presents in that second: 1000 / the last frame time.</item>
/// <item><see cref="FrameTimeMs"/>: average frame time over the same ~1 s = 1000 / <see cref="Framerate"/>.</item>
/// <item><see cref="Low1"/> / <see cref="Low01"/> ("1% Low" / "0.1% Low"): 1000 / the 99th / 99.9th percentile
/// (nearest rank) of the frame times in the last 10 s. With fewer than 100 / 1000 frames this is the slowest frame.</item>
/// <item><see cref="FrameTimeMaxMs"/>: the slowest frame in the last 10 s.</item>
/// </list>
/// A frame time is the gap between two consecutive presents (CPU present-to-present, like PresentMon's
/// MsBetweenPresents). Gaps over <see cref="FrameStatsCalculator.GapMs"/> (app paused / loading) are not frames.
/// </summary>
public readonly record struct FrameStats(float Framerate, float FrameTimeMs, float Low1, float Low01, float FrameTimeMaxMs)
{
    /// <summary>No presents: every value NaN.</summary>
    public static FrameStats None { get; } = new(float.NaN, float.NaN, float.NaN, float.NaN, float.NaN);

    /// <summary>True when the process presented recently.</summary>
    public bool HasValue => !float.IsNaN(Framerate);
}

/// <summary>Pure frame-statistics math over present timestamps (QPC). Allocation-free after construction.</summary>
internal sealed class FrameStatsCalculator
{
    /// <summary>Window for <see cref="FrameStats.Framerate"/> / <see cref="FrameStats.FrameTimeMs"/>.</summary>
    public const double RecentSeconds = 1.0;

    /// <summary>Window for the lows and the max.</summary>
    public const double WindowSeconds = 10.0;

    /// <summary>A present-to-present gap above this is a pause, not a frame.</summary>
    public const double GapMs = 2000;

    /// <summary>
    /// No present for this long (QPC "now" vs the newest event) = not presenting. ETW real-time buffers are flushed every
    /// 1 s, so events arrive up to ~1 s late.
    /// </summary>
    public const double StaleSeconds = 3.0;

    private const int MaxKeys = 8;

    private readonly float[] _deltas;
    private readonly uint[] _keyIds = new uint[MaxKeys];
    private readonly int[] _keyCounts = new int[MaxKeys];

    public FrameStatsCalculator(int capacity) => _deltas = new float[capacity];

    /// <summary>
    /// Computes the stats from <paramref name="timestamps"/> (ascending; sorted in place if needed) with matching
    /// swap chain <paramref name="keys"/>. Only the swap chain that presented most in the last second counts
    /// (a launcher / UI swap chain in the same process does not inflate the number).
    /// </summary>
    public FrameStats Compute(Span<long> timestamps, Span<uint> keys, long nowQpc, long qpcFrequency)
    {
        var n = timestamps.Length;
        if (n == 0) return FrameStats.None;
        SortIfNeeded(timestamps, keys);

        var newest = timestamps[n - 1];
        if (nowQpc - newest > (long)(StaleSeconds * qpcFrequency)) return FrameStats.None;

        var recentFrom = newest - (long)(RecentSeconds * qpcFrequency);
        var windowFrom = newest - (long)(WindowSeconds * qpcFrequency);
        var gap = (long)(GapMs / 1000.0 * qpcFrequency);
        var key = DominantKey(timestamps, keys, recentFrom);
        var msPerTick = 1000.0 / qpcFrequency;

        var count = 0;
        var maxDelta = 0f;
        var lastDelta = float.NaN;
        var recentCount = 0;
        var recentFirst = 0L;
        var prev = long.MinValue;
        for (var i = 0; i < n; i++)
        {
            var t = timestamps[i];
            if (t < windowFrom || keys[i] != key) continue;
            if (t >= recentFrom)
            {
                if (recentCount == 0) recentFirst = t;
                recentCount++;
            }
            if (prev != long.MinValue)
            {
                var d = t - prev;
                if (d > 0 && d <= gap)
                {
                    var ms = (float)(d * msPerTick);
                    _deltas[count++] = ms;
                    if (ms > maxDelta) maxDelta = ms;
                    lastDelta = ms;
                }
                else if (d > gap)
                {
                    lastDelta = float.NaN;
                }
            }
            prev = t;
        }

        float fps;
        if (recentCount >= 2 && newest > recentFirst)
            fps = (float)((recentCount - 1) * (double)qpcFrequency / (newest - recentFirst));
        else if (!float.IsNaN(lastDelta) && lastDelta > 0)
            fps = 1000f / lastDelta;
        else
            return FrameStats.None;

        if (count == 0) return new FrameStats(fps, 1000f / fps, float.NaN, float.NaN, float.NaN);

        var deltas = _deltas.AsSpan(0, count);
        var r999 = Rank(0.999, count);
        var p999 = Select(deltas, r999);
        // after Select, [0, r999) holds the smaller values: the 99th percentile is among them
        var r99 = Rank(0.99, count);
        var p99 = r99 == r999 ? p999 : Select(deltas[..r999], r99);
        return new FrameStats(fps, 1000f / fps, 1000f / p99, 1000f / p999, maxDelta);
    }

    /// <summary>Nearest-rank index (0-based) of percentile <paramref name="p"/> among <paramref name="n"/> sorted values.</summary>
    internal static int Rank(double p, int n) => Math.Clamp((int)Math.Ceiling(p * n - 1e-9) - 1, 0, n - 1); // 0.99 × 1000 must be 990, not 990.0000001

    private uint DominantKey(Span<long> timestamps, Span<uint> keys, long recentFrom)
    {
        var distinct = 0;
        for (var i = timestamps.Length - 1; i >= 0 && timestamps[i] >= recentFrom; i--)
        {
            var k = keys[i];
            var j = 0;
            while (j < distinct && _keyIds[j] != k) j++;
            if (j == distinct)
            {
                if (distinct == MaxKeys) continue;
                _keyIds[distinct] = k;
                _keyCounts[distinct] = 0;
                distinct++;
            }
            _keyCounts[j]++;
        }
        if (distinct == 0) return keys[^1];
        var best = 0; // first seen = newest present wins ties
        for (var j = 1; j < distinct; j++)
        {
            if (_keyCounts[j] > _keyCounts[best]) best = j;
        }
        return _keyIds[best];
    }

    /// <summary>ETW may deliver slightly out of order; insertion sort is O(n) on nearly sorted data.</summary>
    private static void SortIfNeeded(Span<long> t, Span<uint> k)
    {
        for (var i = 1; i < t.Length; i++)
        {
            if (t[i] >= t[i - 1]) continue;
            var tv = t[i];
            var kv = k[i];
            var j = i - 1;
            while (j >= 0 && t[j] > tv)
            {
                t[j + 1] = t[j];
                k[j + 1] = k[j];
                j--;
            }
            t[j + 1] = tv;
            k[j + 1] = kv;
        }
    }

    /// <summary>
    /// Quickselect: reorders <paramref name="a"/> so that a[k] is the k-th smallest, everything before it ≤ a[k] and
    /// everything after ≥ a[k]. O(n) on average, in place.
    /// </summary>
    internal static float Select(Span<float> a, int k)
    {
        var lo = 0;
        var hi = a.Length - 1;
        while (lo < hi)
        {
            // median of three
            var mid = lo + ((hi - lo) >> 1);
            if (a[mid] < a[lo]) (a[mid], a[lo]) = (a[lo], a[mid]);
            if (a[hi] < a[lo]) (a[hi], a[lo]) = (a[lo], a[hi]);
            if (a[hi] < a[mid]) (a[hi], a[mid]) = (a[mid], a[hi]);
            var pivot = a[mid];
            var i = lo;
            var j = hi;
            while (i <= j)
            {
                while (a[i] < pivot) i++;
                while (a[j] > pivot) j--;
                if (i <= j)
                {
                    (a[i], a[j]) = (a[j], a[i]);
                    i++;
                    j--;
                }
            }
            if (k <= j) hi = j;
            else if (k >= i) lo = i;
            else return a[k];
        }
        return a[k];
    }
}
