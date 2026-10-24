using BetterVitals.Core.History;
using BetterVitals.Core.Sensors;
using BetterVitals.History.Storage;

namespace BetterVitals.History;

/// <summary>A sensor as seen by a query: its catalog slot (disk data) and whether the live ring adds to it.</summary>
internal sealed class SensorRef
{
    public required string Id { get; init; }
    public required SensorKind Kind { get; init; }
    public required string Name { get; init; }
    public required string Unit { get; init; }

    /// <summary>Catalog slot, -1 = never recorded.</summary>
    public int Slot { get; init; } = -1;

    /// <summary>Ring rows count for this sensor (it is not being recorded right now).</summary>
    public bool UseRing { get; init; }

    /// <summary>Ring rows at or before this time are already stored on disk.</summary>
    public long RingAfter { get; init; } = long.MinValue;

    public double Quantum => Quant.Of(Kind);
}

internal sealed partial class HistoryStore
{
    /// <summary>Data boundaries of one read (see the class remarks).</summary>
    internal readonly record struct Bounds(long MinuteLimit, long HourLimit, long RawFrom, long MinuteFrom);

    /// <summary>Per-read context: open files, requested sensors and the level boundaries.</summary>
    internal sealed class Reader(HistoryStore store, ReadSession session, SensorRef[] sensors) : IDisposable
    {
        public readonly SensorRef[] Sensors = sensors;
        public readonly int[] Slots = sensors.Select(s => s.Slot).ToArray();
        public ReadSession Session { get; } = session;
        public Bounds B { get; private set; } = store.ComputeBounds(session, sensors);

        public void Refresh()
        {
            Session.Refresh();
            B = store.ComputeBounds(Session, Sensors);
        }

        /// <summary>Records of exactly <paramref name="level"/> (disk + pending + ring) with time in [a, b).</summary>
        public void ReadLevel<TV>(Level level, long a, long b, ref TV v) where TV : IRecVisitor
        {
            if (a >= b) return;
            Session.Read(level, a, b, Slots, ref v);
            switch (level)
            {
                case Level.Raw:
                    store._recorder.VisitPending(Slots, a, b, ref v);
                    for (var i = 0; i < Sensors.Length; i++)
                        if (Sensors[i].UseRing) store._ring.Visit(Sensors[i].Id, a, b, Sensors[i].RingAfter, i, ref v);
                    break;
                case Level.Minute:
                    VisitPending(store._minutes, Level.Minute, a, b, ref v);
                    break;
                default:
                    VisitPending(store._hours, Level.Hour, a, b, ref v);
                    break;
            }
        }

        /// <summary>Raw data that is not in files: recorder buffers and the live ring.</summary>
        public void ReadUnwritten<TV>(long a, long b, ref TV v) where TV : IRecVisitor
        {
            store._recorder.VisitPending(Slots, a, b, ref v);
            for (var i = 0; i < Sensors.Length; i++)
                if (Sensors[i].UseRing) store._ring.Visit(Sensors[i].Id, a, b, Sensors[i].RingAfter, i, ref v);
        }

        /// <summary>Visits pending rollup buckets of the requested slots.</summary>
        public void ReadPendingRollups<TV>(ref TV v) where TV : IRecVisitor
        {
            VisitPending(store._minutes, Level.Minute, long.MinValue, long.MaxValue, ref v);
            VisitPending(store._hours, Level.Hour, long.MinValue, long.MaxValue, ref v);
        }

        /// <summary>Like <see cref="ReadLevel"/>, but where the level was already deleted by retention the next coarser one fills in.</summary>
        public void ReadFallback<TV>(Level level, long a, long b, ref TV v) where TV : IRecVisitor
        {
            if (a >= b) return;
            switch (level)
            {
                case Level.Raw:
                    if (a < B.RawFrom) ReadFallback(Level.Minute, a, Math.Min(b, B.RawFrom), ref v);
                    ReadLevel(Level.Raw, Math.Max(a, B.RawFrom), b, ref v);
                    break;
                case Level.Minute:
                    if (a < B.MinuteFrom) ReadLevel(Level.Hour, a, Math.Min(b, B.MinuteFrom), ref v);
                    ReadLevel(Level.Minute, Math.Max(a, B.MinuteFrom), b, ref v);
                    break;
                default:
                    ReadLevel(Level.Hour, a, b, ref v);
                    break;
            }
        }

        /// <summary>
        /// Everything in [a, b) once, using data of <paramref name="finest"/> resolution or coarser where only that exists:
        /// hour rollups below the hour limit (when finest is hour), minute rollups below the minute limit, raw above.
        /// </summary>
        public void ReadPlanned<TV>(Level finest, long a, long b, ref TV v) where TV : IRecVisitor
        {
            if (a >= b) return;
            var mw = Math.Clamp(B.MinuteLimit, a, b);
            switch (finest)
            {
                case Level.Raw:
                    ReadFallback(Level.Raw, a, b, ref v);
                    break;
                case Level.Minute:
                    ReadFallback(Level.Minute, a, mw, ref v);
                    ReadFallback(Level.Raw, mw, b, ref v);
                    break;
                default:
                    var hw = Math.Clamp(B.HourLimit, a, mw);
                    ReadLevel(Level.Hour, a, hw, ref v);
                    ReadFallback(Level.Minute, hw, mw, ref v);
                    ReadFallback(Level.Raw, mw, b, ref v);
                    break;
            }
        }

        private void VisitPending<TV>(List<PendingBucket> list, Level level, long a, long b, ref TV v) where TV : IRecVisitor
        {
            foreach (var p in list)
            {
                if (p.Start < a || p.Start >= b) continue;
                for (var i = 0; i < Slots.Length; i++)
                {
                    if (Slots[i] < 0) continue;
                    var c = p.ColumnOf(Slots[i]);
                    if (c < 0 || p.Aggs[c].Count == 0) continue;
                    v.Visit(i, p.Aggs[c].ToRec(p.Start, level));
                }
            }
        }

        public void Dispose() => Session.Dispose();
    }

    public Reader OpenReader(SensorRef[] sensors)
    {
        EnsureLoaded();
        return new Reader(this, OpenSession(), sensors);
    }

    private Bounds ComputeBounds(ReadSession s, SensorRef[] sensors)
    {
        var pendingRaw = _recorder.EarliestPendingStart();
        var ring = long.MaxValue;
        foreach (var r in sensors)
            if (r.UseRing) ring = Math.Min(ring, _ring.FirstTime(r.Id, r.RingAfter));
        var newest = Math.Min(pendingRaw, ring);
        var mw = newest == long.MaxValue ? long.MaxValue : TimeGrid.Floor(newest, TimeGrid.Minute);
        var pm = Math.Min(EarliestPendingMinute, mw);
        var hw = pm == long.MaxValue ? long.MaxValue : TimeGrid.Floor(pm, TimeGrid.Hour);
        var rawFrom = Math.Min(s.OldestStart(Level.Raw), newest);
        var minuteFrom = Math.Min(s.OldestStart(Level.Minute), EarliestPendingMinute);
        return new Bounds(mw, hw, rawFrom, minuteFrom);
    }

    // ── series ──

    /// <summary>Upper bound for one chart request (bucket arrays are allocated per query).</summary>
    public const int MaxPoints = 1_000_000;

    private static readonly long[] NiceSteps =
    [
        1 * TimeGrid.Second, 2 * TimeGrid.Second, 5 * TimeGrid.Second, 10 * TimeGrid.Second, 15 * TimeGrid.Second, 30 * TimeGrid.Second,
        1 * TimeGrid.Minute, 2 * TimeGrid.Minute, 5 * TimeGrid.Minute, 10 * TimeGrid.Minute, 15 * TimeGrid.Minute, 30 * TimeGrid.Minute,
        1 * TimeGrid.Hour, 2 * TimeGrid.Hour, 3 * TimeGrid.Hour, 6 * TimeGrid.Hour, 12 * TimeGrid.Hour,
        1 * TimeGrid.Day, 2 * TimeGrid.Day, 7 * TimeGrid.Day, 14 * TimeGrid.Day, 30 * TimeGrid.Day, 90 * TimeGrid.Day, 365 * TimeGrid.Day,
    ];

    /// <summary>Bucket width so that [from, to) splits into at most <paramref name="maxPoints"/> aligned buckets.</summary>
    internal static long ChooseBucket(long from, long to, int maxPoints, long baseStep)
    {
        maxPoints = Math.Max(1, maxPoints);
        foreach (var w in NiceSteps)
        {
            if (w < baseStep) continue;
            if (BucketCount(from, to, w) <= maxPoints) return w;
        }
        var step = Math.Max(TimeGrid.Day, (to - from) / maxPoints);
        while (BucketCount(from, to, step) > maxPoints && step < long.MaxValue / 4) step *= 2;
        return step;
    }

    internal static long BucketCount(long from, long to, long w)
    {
        var start = TimeGrid.Floor(from, w);
        return (to - start + w - 1) / w;
    }

    internal static Level LevelFor(long bucket) => bucket >= TimeGrid.Hour ? Level.Hour : bucket >= TimeGrid.Minute ? Level.Minute : Level.Raw;

    /// <summary>Accumulates records into fixed buckets.</summary>
    internal sealed class Buckets : IRecVisitor
    {
        public readonly long Start;
        public readonly long Width;
        public readonly long[] Min;
        public readonly long[] Max;
        public readonly long[] Sum;
        public readonly long[] Count;

        public Buckets(long from, long to, long width)
        {
            Width = width;
            Start = TimeGrid.Floor(from, width);
            var n = (int)Math.Max(1, (to - Start + width - 1) / width);
            Min = new long[n];
            Max = new long[n];
            Sum = new long[n];
            Count = new long[n];
        }

        public void Visit(int ordinal, in Rec rec)
        {
            var i = (rec.Time - Start) / Width;
            if ((ulong)i >= (ulong)Count.Length || rec.Count == 0) return;
            if (Count[i] == 0)
            {
                Min[i] = rec.Min;
                Max[i] = rec.Max;
            }
            else
            {
                if (rec.Min < Min[i]) Min[i] = rec.Min;
                if (rec.Max > Max[i]) Max[i] = rec.Max;
            }
            Sum[i] += rec.Sum;
            Count[i] += rec.Count;
        }

        public List<HistoryPoint> ToPoints(double quantum)
        {
            var list = new List<HistoryPoint>();
            for (var i = 0; i < Count.Length; i++)
            {
                if (Count[i] == 0) continue;
                list.Add(new HistoryPoint(Start + i * Width, Quant.ToFloat(Min[i], quantum), (float)(Sum[i] * quantum / Count[i]),
                    Quant.ToFloat(Max[i], quantum)));
            }
            return list;
        }
    }

    public List<HistoryPoint> Series(SensorRef sensor, long from, long to, int maxPoints, long baseStep, CancellationToken ct)
    {
        if (to <= from) return [];
        var width = ChooseBucket(from, to, Math.Clamp(maxPoints, 1, MaxPoints), baseStep);
        var buckets = new Buckets(from, to, width);
        using var r = OpenReader([sensor]);
        ct.ThrowIfCancellationRequested();
        // read whole buckets (the first one starts before `from`), so coarse rollups are never cut off
        r.ReadPlanned(LevelFor(width), buckets.Start, to, ref buckets);
        return buckets.ToPoints(sensor.Quantum);
    }

    // ── stats ──

    private sealed class StatsVisitor : IRecVisitor
    {
        public long Min, Max, Sum, Count;
        public long MinTime = long.MaxValue, MaxTime = long.MaxValue;
        public Level MinLevel, MaxLevel;

        public void Visit(int ordinal, in Rec rec)
        {
            if (rec.Count == 0) return;
            if (Count == 0 || rec.Min < Min || (rec.Min == Min && rec.Time < MinTime))
            {
                Min = rec.Min;
                MinTime = rec.Time;
                MinLevel = rec.Level;
            }
            if (Count == 0 || rec.Max > Max || (rec.Max == Max && rec.Time < MaxTime))
            {
                Max = rec.Max;
                MaxTime = rec.Time;
                MaxLevel = rec.Level;
            }
            Sum += rec.Sum;
            Count += rec.Count;
        }
    }

    /// <summary>Earliest record whose min (or max) equals a value.</summary>
    private sealed class FindVisitor(long value, bool isMin) : IRecVisitor
    {
        public long Time = long.MaxValue;

        public void Visit(int ordinal, in Rec rec)
        {
            if (rec.Count > 0 && (isMin ? rec.Min : rec.Max) == value && rec.Time < Time) Time = rec.Time;
        }
    }

    /// <summary>
    /// Exact min / avg / max over [from, to): whole hours from hour rollups, whole minutes from minute rollups,
    /// the rest from raw samples (rollups are exact sums of the quantized raw samples, so the result equals a raw scan);
    /// the times of min / max are then refined down to the raw sample where raw data still exists.
    /// </summary>
    public RangeStats? Stats(SensorRef sensor, long from, long to, CancellationToken ct)
    {
        if (to <= from) return null;
        using var r = OpenReader([sensor]);
        var v = new StatsVisitor();
        var b = r.B;

        var h0 = TimeGrid.Ceil(from, TimeGrid.Hour);
        var h1 = b.HourLimit == long.MaxValue ? TimeGrid.Floor(to, TimeGrid.Hour) : TimeGrid.Floor(Math.Min(to, b.HourLimit), TimeGrid.Hour);
        Span<(long A, long B)> rest = stackalloc (long, long)[2];
        var restCount = 0;
        if (h1 > h0)
        {
            r.ReadLevel(Level.Hour, h0, h1, ref v);
            rest[restCount++] = (from, h0);
            rest[restCount++] = (h1, to);
        }
        else
        {
            rest[restCount++] = (from, to);
        }
        ct.ThrowIfCancellationRequested();

        for (var i = 0; i < restCount; i++)
        {
            var (a, e) = rest[i];
            if (a >= e) continue;
            var m0 = TimeGrid.Ceil(a, TimeGrid.Minute);
            var m1 = TimeGrid.Floor(Math.Min(e, b.MinuteLimit), TimeGrid.Minute);
            if (m1 > m0)
            {
                r.ReadFallback(Level.Raw, a, m0, ref v);
                r.ReadFallback(Level.Minute, m0, m1, ref v);
                r.ReadFallback(Level.Raw, m1, e, ref v);
            }
            else
            {
                r.ReadFallback(Level.Raw, a, e, ref v);
            }
        }
        if (v.Count == 0) return null;
        ct.ThrowIfCancellationRequested();

        var minTime = Refine(r, v.MinLevel, v.MinTime, v.Min, true, from, to);
        var maxTime = Refine(r, v.MaxLevel, v.MaxTime, v.Max, false, from, to);
        var q = sensor.Quantum;
        return new RangeStats(Quant.ToFloat(v.Min, q), (float)(v.Sum * q / v.Count), Quant.ToFloat(v.Max, q), minTime, maxTime, v.Count);
    }

    /// <summary>Bucket time → time of the first sample with that value (as precise as the remaining data allows).</summary>
    private static long Refine(Reader r, Level level, long time, long value, bool isMin, long from, long to)
    {
        if (level == Level.Hour)
        {
            var f = new FindVisitor(value, isMin);
            r.ReadLevel(Level.Minute, Math.Max(time, from), Math.Min(time + TimeGrid.Hour, to), ref f);
            if (f.Time == long.MaxValue) return Math.Max(time, from);
            time = f.Time;
            level = Level.Minute;
        }
        if (level == Level.Minute)
        {
            var f = new FindVisitor(value, isMin);
            r.ReadLevel(Level.Raw, Math.Max(time, from), Math.Min(time + TimeGrid.Minute, to), ref f);
            if (f.Time != long.MaxValue) time = f.Time;
        }
        return time;
    }

    // ── coverage ──

    private sealed class SpanVisitor : IRecVisitor
    {
        public long First = long.MaxValue;
        public long Last = long.MinValue;

        public void Visit(int ordinal, in Rec rec)
        {
            if (rec.Count == 0) return;
            if (rec.Time < First) First = rec.Time;
            if (rec.Time > Last) Last = rec.Time;
        }
    }

    public (long First, long Last)? Coverage(SensorRef sensor)
    {
        using var r = OpenReader([sensor]);
        // per level: [0] raw (files + buffers + ring), [1] minute, [2] hour (files + pending); rollups carry bucket starts
        var edges = new SpanVisitor[3];
        for (var i = 0; i < 3; i++) edges[i] = new SpanVisitor();
        if (sensor.Slot >= 0)
        {
            foreach (var level in (ReadOnlySpan<Level>)[Level.Hour, Level.Minute, Level.Raw])
            {
                var e = edges[(int)level];
                var files = r.Session.Files(level).ToList();
                for (var i = 0; i < files.Count; i++)
                {
                    var t = r.Session.EdgeTime(files[i], r.Slots, first: true);
                    if (t == long.MaxValue) continue;
                    e.Visit(0, new Rec { Time = t, Count = 1 });
                    break;
                }
                for (var i = files.Count - 1; i >= 0; i--)
                {
                    var t = r.Session.EdgeTime(files[i], r.Slots, first: false);
                    if (t == long.MinValue) continue;
                    e.Visit(0, new Rec { Time = t, Count = 1 });
                    break;
                }
            }
        }
        r.ReadUnwritten(long.MinValue, long.MaxValue, ref edges[0]);
        var pending = new LevelSplit(edges);
        r.ReadPendingRollups(ref pending);

        // the finest level wins unless a coarser one reaches clearly further (finer data already deleted)
        long first = edges[0].First, last = edges[0].Last;
        for (var l = 1; l < 3; l++)
        {
            var step = TimeGrid.Step((Level)l);
            if (edges[l].First != long.MaxValue && (first == long.MaxValue || edges[l].First < TimeGrid.Floor(first, step))) first = edges[l].First;
            if (edges[l].Last != long.MinValue && edges[l].Last > last) last = edges[l].Last;
        }
        return first == long.MaxValue ? null : (first, last);
    }

    private readonly struct LevelSplit(SpanVisitor[] edges) : IRecVisitor
    {
        public void Visit(int ordinal, in Rec rec) => edges[(int)rec.Level].Visit(ordinal, rec);
    }
}
