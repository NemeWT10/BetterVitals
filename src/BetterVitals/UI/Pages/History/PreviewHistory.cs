using System.IO;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using BetterVitals.Core.History;
using BetterVitals.Core.Sensors;
using BetterVitals.UI.Charts;

namespace BetterVitals.UI.Pages.History;

/// <summary>
/// Test / snapshot stand-in for the history (used when <c>AppEnv.TestMode</c> and no store is attached): weeks of
/// plausible, deterministic data for the hub's sensors — quiet office hours, evening gaming with FPS, nights with the
/// PC off (gaps), one afternoon asleep — computed on the fly per bucket, so it costs no memory. Recorded sensors
/// reach back <c>days</c>; the others only have the 15-minute live buffer, like the real service.
/// </summary>
internal sealed class PreviewHistory : IHistoryQuery
{
    private const long Step = 2 * TimeSpan.TicksPerSecond;

    private readonly ISensorHub _hub;
    private readonly Func<long> _now;
    private readonly Func<string, bool> _recorded;
    private readonly TimeZoneInfo _zone;
    private readonly int _days;

    public PreviewHistory(ISensorHub hub, Func<long>? now = null, Func<string, bool>? recorded = null, TimeZoneInfo? zone = null, int days = 35)
    {
        _hub = hub;
        _now = now ?? (static () => DateTime.UtcNow.Ticks);
        _recorded = recorded ?? (static _ => true);
        _zone = zone ?? TimeZoneInfo.Local;
        _days = days;
    }

    /// <summary>Queries answered (tests).</summary>
    public int SeriesQueries;

    public int StatsQueries;

    private readonly record struct Profile(float Lo, float Hi, float Noise, uint Seed, bool GameOnly, bool Flat);

    private Profile ProfileOf(SensorDescriptor d)
    {
        var seed = StableHash(d.Id);
        var n = d.Name;
        bool Has(string s) => n.Contains(s, StringComparison.OrdinalIgnoreCase);
        var gpu = d.HardwareKind == HardwareKind.Gpu;
        var (lo, hi, noise) = d.Kind switch
        {
            SensorKind.Temperature when Has("Hot Spot") => (41f, 94f, 0.03f),
            SensorKind.Temperature when Has("Junction") => (40f, 88f, 0.03f),
            SensorKind.Temperature when gpu => (34f, 77f, 0.025f),
            SensorKind.Temperature when d.HardwareKind == HardwareKind.Cpu => (39f, 84f, 0.05f),
            SensorKind.Temperature when d.HardwareKind == HardwareKind.Storage => (33f, 55f, 0.01f),
            SensorKind.Temperature => (29f, 46f, 0.015f),
            SensorKind.Load when d.HardwareKind == HardwareKind.Memory => (31f, 72f, 0.02f),
            SensorKind.Load when gpu => (1f, 99f, 0.08f),
            SensorKind.Load => (2f, 88f, 0.12f),
            SensorKind.Clock when gpu && Has("Memory") => (405f, 15000f, 0f),
            SensorKind.Clock when gpu => (210f, 2850f, 0.04f),
            SensorKind.Clock => (3550f, 5240f, 0.05f),
            SensorKind.Power when gpu => (17f, 352f, 0.06f),
            SensorKind.Power => (24f, 132f, 0.08f),
            SensorKind.Voltage => (0.96f, 1.33f, 0.04f),
            SensorKind.SmallData => (950f, 13800f, 0.01f),
            SensorKind.Data => (7.5f, 22f, 0.01f),
            SensorKind.Fan => (0f, 2350f, 0.03f),
            SensorKind.Throughput => (2e3f, 2.4e7f, 0.3f),
            SensorKind.Fps => (58f, 162f, 0.06f),
            SensorKind.FrameTime => (6.2f, 17f, 0.06f),
            _ => (0f, 100f, 0.05f),
        };
        return new Profile(lo, hi, noise, seed, d.HardwareKind == HardwareKind.Game, d.Kind == SensorKind.Clock && gpu && Has("Memory"));
    }

    /// <summary>The PC is on: off at night (≈01:30–07:40 local), asleep one afternoon two days ago.</summary>
    internal bool Online(long utc)
    {
        var now = _now();
        if (now - utc < 3 * TimeSpan.TicksPerHour) return true; // the recent past is always there
        var local = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(utc, DateTimeKind.Utc), _zone);
        var day = (int)(local.Date - DateTime.MinValue).TotalDays;
        var minute = local.Hour * 60 + local.Minute;
        var wake = 7 * 60 + 40 + (int)(Hash((uint)day, 7) % 50);
        var sleep = 60 + (int)(Hash((uint)day, 3) % 70);
        if (minute >= sleep && minute < wake) return false;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(now, DateTimeKind.Utc), _zone);
        if (local.Date == nowLocal.Date.AddDays(-2) && minute is >= 13 * 60 + 10 and < 14 * 60 + 25) return false;
        return true;
    }

    /// <summary>How busy the PC is (0–1) at a local time: office hours, evening games, late browsing.</summary>
    private double Activity(long utc, uint seed, out bool gaming)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(utc, DateTimeKind.Utc), _zone);
        var h = local.TimeOfDay.TotalHours;
        var day = (uint)(local.Date - DateTime.MinValue).TotalDays;
        var gameStart = 19 + Hash(day, 11) % 120 / 60.0;
        var gameEnd = 22.2 + Hash(day, 13) % 90 / 60.0;
        gaming = h >= gameStart && h < gameEnd && Hash(day, 17) % 5 != 0;
        var t = utc / (double)TimeSpan.TicksPerSecond;
        var wobble = 0.5 + 0.5 * Octave(t, 1500, seed + 9);
        if (gaming) return 0.78 + 0.16 * wobble;
        if (h is >= 8.5 and < 18) return 0.12 + 0.22 * wobble * wobble;
        return 0.08 + 0.12 * wobble;
    }

    /// <summary>Organic, deterministic noise in about −1…1: smooth value noise over four time scales (6 s … 40 min).</summary>
    private static double Noise(long utc, uint seed)
    {
        var t = utc / (double)TimeSpan.TicksPerSecond;
        return 0.22 * Octave(t, 6, seed) + 0.34 * Octave(t, 47, seed + 1) + 0.30 * Octave(t, 310, seed + 2) + 0.24 * Octave(t, 2400, seed + 3);
    }

    private static double Octave(double t, double period, uint seed)
    {
        var x = t / period;
        var k = Math.Floor(x);
        var f = x - k;
        var a = Knot((long)k, seed);
        var b = Knot((long)k + 1, seed);
        var s = f * f * (3 - 2 * f);
        return a + (b - a) * s;
    }

    private static double Knot(long k, uint seed) => Hash((uint)k ^ (uint)(k >> 32) * 0x85EBCA6Bu, seed) / (double)uint.MaxValue * 2 - 1;

    /// <summary>FNV-1a of the sensor id: the same data in every process (string.GetHashCode is randomized per process).</summary>
    private static uint StableHash(string s)
    {
        var h = 2166136261u;
        foreach (var c in s) h = (h ^ c) * 16777619u;
        return h;
    }

    private static uint Hash(uint a, uint b)
    {
        var x = a * 0x9E3779B1u ^ (b + 0x7F4A7C15u);
        x ^= x >> 15;
        x *= 0x2C1B3C6Du;
        x ^= x >> 12;
        return x;
    }

    /// <summary>The sample at a time, NaN where there is none (PC off, FPS outside games).</summary>
    internal float Sample(SensorDescriptor d, long utc)
    {
        if (!Online(utc)) return float.NaN;
        var p = ProfileOf(d);
        if (p.Flat) return Activity(utc, p.Seed, out _) > 0.11 ? p.Hi : p.Lo;
        var a = Activity(utc, p.Seed, out var gaming);
        if (p.GameOnly && !gaming) return float.NaN;
        var x = a + Noise(utc, p.Seed) * p.Noise * 2.2;
        if (p.GameOnly) x = 0.35 + Noise(utc, p.Seed) * 0.28;
        if (d.Kind == SensorKind.Fan && a < 0.3) x = 0; // zero-RPM fans at idle
        return (float)(p.Lo + (p.Hi - p.Lo) * Math.Clamp(x, 0, 1));
    }

    private (long From, long To)? Available(string id)
    {
        var now = _now();
        return _recorded(id)
            ? (now - _days * TimeSpan.TicksPerDay, now)
            : (now - HistoryModel.LiveBuffer.Ticks, now);
    }

    public Task<IReadOnlyList<HistoryPoint>> GetSeriesAsync(string sensorId, DateTime fromUtc, DateTime toUtc, int maxPoints, CancellationToken ct = default)
    {
        Interlocked.Increment(ref SeriesQueries);
        return Task.Run<IReadOnlyList<HistoryPoint>>(() =>
        {
            var d = _hub.Find(sensorId);
            if (d is null || Available(sensorId) is not { } av) return [];
            var baseStep = _recorded(sensorId) ? Step : TimeSpan.TicksPerSecond;
            var from = fromUtc.Ticks;
            var to = toUtc.Ticks;
            var bucket = HistoryModel.BucketFor(from, to, maxPoints, baseStep);
            var list = new List<HistoryPoint>();
            var subs = (int)Math.Clamp(bucket / baseStep, 1, 24);
            for (var b = from / bucket * bucket; b < to; b += bucket)
            {
                ct.ThrowIfCancellationRequested();
                if (b + bucket <= av.From || b > av.To) continue;
                float mn = float.MaxValue, mx = float.MinValue;
                double sum = 0;
                var n = 0;
                for (var k = 0; k < subs; k++)
                {
                    var t = b + bucket * k / subs;
                    if (t < av.From || t > av.To) continue;
                    var v = Sample(d, t);
                    if (float.IsNaN(v)) continue;
                    mn = Math.Min(mn, v);
                    mx = Math.Max(mx, v);
                    sum += v;
                    n++;
                }
                if (n > 0) list.Add(new HistoryPoint(b, mn, (float)(sum / n), mx));
            }
            return list;
        }, ct);
    }

    public Task<RangeStats?> GetStatsAsync(string sensorId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        Interlocked.Increment(ref StatsQueries);
        return Task.Run<RangeStats?>(() =>
        {
            var d = _hub.Find(sensorId);
            if (d is null || Available(sensorId) is not { } av) return null;
            var from = Math.Max(fromUtc.Ticks, av.From);
            var to = Math.Min(toUtc.Ticks, av.To);
            if (to <= from) return null;
            var step = Math.Max(Step, (to - from) / 20000 / Step * Step);
            float mn = float.MaxValue, mx = float.MinValue;
            long tMin = 0, tMax = 0, n = 0;
            double sum = 0;
            for (var t = from / Step * Step; t < to; t += step)
            {
                ct.ThrowIfCancellationRequested();
                var v = Sample(d, t);
                if (float.IsNaN(v)) continue;
                if (v < mn)
                {
                    mn = v;
                    tMin = t;
                }
                if (v > mx)
                {
                    mx = v;
                    tMax = t;
                }
                sum += v;
                n++;
            }
            return n == 0 ? null : new RangeStats(mn, (float)(sum / n), mx, tMin, tMax, n * (step / Step));
        }, ct);
    }

    public (DateTime FromUtc, DateTime ToUtc)? GetCoverage(string sensorId)
        => _hub.Find(sensorId) is not null && Available(sensorId) is { } av
            ? (new DateTime(av.From, DateTimeKind.Utc), new DateTime(av.To, DateTimeKind.Utc))
            : null;

    public HistoryUsage GetUsage() => new(61L << 20, 2_100_000, new DateTime(_now() - _days * TimeSpan.TicksPerDay, DateTimeKind.Utc));

    public long EstimateExportBytes(ExportRequest request)
    {
        var span = Math.Max(0, (request.ToUtc - request.FromUtc).Ticks);
        var (rows, perValue) = request.Resolution switch
        {
            ExportResolution.Raw => (span / Step, 6.0),
            ExportResolution.Minute => (span / TimeSpan.TicksPerMinute, 18.0),
            _ => (span / TimeSpan.TicksPerHour, 18.0),
        };
        var bytes = (long)(rows * (20 + request.SensorIds.Count * perValue));
        return request.Zip ? bytes / 9 : bytes;
    }

    public Task ExportAsync(ExportRequest request, Stream output, IProgress<double>? progress = null, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var sensors = request.SensorIds.Select(_hub.Find).OfType<SensorDescriptor>().ToList();
            var step = request.Resolution switch
            {
                ExportResolution.Raw => Step,
                ExportResolution.Minute => TimeSpan.TicksPerMinute,
                _ => TimeSpan.TicksPerHour,
            };
            Stream target = output;
            ZipArchive? zip = null;
            if (request.Zip)
            {
                zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                target = zip.CreateEntry("BetterVitals.csv", CompressionLevel.Optimal).Open();
            }
            try
            {
                using var w = new StreamWriter(target, new UTF8Encoding(false), 1 << 16, leaveOpen: zip is null);
                w.Write("time_utc");
                foreach (var s in sensors) w.Write("," + s.Name + " [" + s.DisplayUnit + "]");
                w.WriteLine();
                var from = request.FromUtc.Ticks / step * step;
                var to = request.ToUtc.Ticks;
                for (var t = from; t < to; t += step)
                {
                    ct.ThrowIfCancellationRequested();
                    w.Write(new DateTime(t, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
                    foreach (var s in sensors)
                    {
                        var v = Sample(s, t);
                        w.Write(',');
                        if (!float.IsNaN(v)) w.Write(v.ToString("0.##", CultureInfo.InvariantCulture));
                    }
                    w.WriteLine();
                    if ((t - from) / step % 2048 == 0) progress?.Report((t - from) / (double)Math.Max(1, to - from));
                }
                progress?.Report(1);
            }
            finally
            {
                zip?.Dispose();
            }
        }, ct);

    public Task ClearAsync() => Task.CompletedTask;
}
