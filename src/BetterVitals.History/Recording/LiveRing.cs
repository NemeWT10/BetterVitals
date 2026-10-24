using BetterVitals.Core.Sensors;
using BetterVitals.History.Storage;

namespace BetterVitals.History.Recording;

/// <summary>
/// In-memory ring of the last <see cref="Rows"/> rows (at most one per second, i.e. ≥ 15 min) of the watched and
/// recorded sensors, kept also when recording is off. 900 rows × 4 B per sensor (~3.6 KB each).
/// A sensor that is being recorded and not watched keeps only its newest <see cref="ShortRows"/> rows (360 B): queries
/// read recorded sensors from the store, and the ring is read for them only after recording stops, from their last
/// recorded sample on — which those newest rows cover.
/// <see cref="OnFrame"/> runs on the engine thread (short lock, no allocation).
/// </summary>
internal sealed class LiveRing
{
    public const int Rows = 900;

    /// <summary>Rows kept for a recorded, unwatched sensor (divides <see cref="Rows"/>; ≥ the longest record interval + slack).</summary>
    public const int ShortRows = 90;

    private sealed class Column(string id)
    {
        public readonly string Id = id;
        public float[] Data = NaNs(ShortRows);
        public int HubIndex = -1;
        public SensorKind Kind;
        public long StaleLimit = 7 * TimeGrid.Second;
        public int LastSeq;
        public long LastFresh;

        public bool Full => Data.Length == Rows;
    }

    private static float[] NaNs(int n)
    {
        var d = new float[n];
        d.AsSpan().Fill(float.NaN);
        return d;
    }

    private readonly Lock _gate = new();
    private readonly long[] _times = new long[Rows];
    private int _head;
    private int _count;
    private long _lastSecond = long.MinValue;
    private Column[] _columns = [];
    private Dictionary<string, Column> _byId = new(StringComparer.Ordinal);

    /// <summary>Approximate memory held by the ring.</summary>
    public long Bytes
    {
        get
        {
            lock (_gate)
            {
                var b = _times.Length * 8L;
                foreach (var c in _columns) b += c.Data.Length * 4L + 64;
                return b;
            }
        }
    }

    /// <summary>
    /// Replaces the column set; data of sensors that stay is kept. Descriptor null = not known (yet). <c>Full</c> false =
    /// the sensor is recorded and not watched: only its newest <see cref="ShortRows"/> rows are kept.
    /// </summary>
    public void SetColumns(IReadOnlyList<(string Id, SensorDescriptor? Sensor, bool Full)> sensors)
    {
        lock (_gate)
        {
            var map = new Dictionary<string, Column>(sensors.Count, StringComparer.Ordinal);
            var list = new Column[sensors.Count];
            var n = 0;
            foreach (var (id, d, full) in sensors)
            {
                if (map.ContainsKey(id)) continue;
                if (!_byId.TryGetValue(id, out var col))
                {
                    col = new Column(id);
                    if (full) col.Data = NaNs(Rows);
                }
                else if (col.Full != full)
                {
                    Resize(col, full ? Rows : ShortRows);
                }
                if (d is not null)
                {
                    col.HubIndex = d.Index;
                    col.Kind = d.Kind;
                    col.StaleLimit = 2 * Math.Max(TimeGrid.Second, d.MinInterval.Ticks) + 5 * TimeGrid.Second;
                }
                map[id] = col;
                list[n++] = col;
            }
            _columns = n == list.Length ? list : list[..n];
            _byId = map;
        }
    }

    /// <summary>Moves the newest rows (as many as both sizes hold) into an array of <paramref name="size"/> rows.</summary>
    private void Resize(Column c, int size)
    {
        var data = NaNs(size);
        var keep = Math.Min(_count, Math.Min(size, c.Data.Length));
        for (var age = 0; age < keep; age++)
        {
            var p = (_head - 1 - age + Rows) % Rows;
            data[p % size] = c.Data[p % c.Data.Length];
        }
        c.Data = data;
    }

    /// <summary>Value of the row at ring position <paramref name="p"/> (NaN where a short column no longer holds it).</summary>
    private float Value(Column c, int p)
    {
        var d = c.Data;
        if (d.Length == Rows) return d[p];
        var age = (_head - 1 - p + Rows) % Rows;
        return age < d.Length ? d[p % d.Length] : float.NaN;
    }

    public void OnFrame(SensorFrame f)
    {
        var ts = f.TimestampUtcTicks;
        var sec = TimeGrid.Floor(ts, TimeGrid.Second);
        lock (_gate)
        {
            var cols = _columns;
            if (cols.Length == 0 || sec == _lastSecond) return;
            if (sec < _lastSecond)
            {
                // clock went back: the ring must stay time-ordered
                _count = 0;
                _head = 0;
            }
            _lastSecond = sec;
            _times[_head] = sec;
            foreach (var c in cols) c.Data[_head % c.Data.Length] = Read(c, f, ts);
            _head = _head + 1 == Rows ? 0 : _head + 1;
            if (_count < Rows) _count++;
        }
    }

    public bool Has(string id)
    {
        lock (_gate) return _byId.ContainsKey(id);
    }

    public SensorKind? KindOf(string id)
    {
        lock (_gate) return _byId.TryGetValue(id, out var c) && c.HubIndex >= 0 ? c.Kind : null;
    }

    /// <summary>Time of the first row after <paramref name="after"/> that holds a value (long.MaxValue when none).</summary>
    public long FirstTime(string id, long after)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var c)) return long.MaxValue;
            var start = _head - _count;
            if (start < 0) start += Rows;
            for (var i = 0; i < _count; i++)
            {
                var p = (start + i) % Rows;
                if (_times[p] > after && !float.IsNaN(Value(c, p))) return _times[p];
            }
            return long.MaxValue;
        }
    }

    /// <summary>Last row time with a value (long.MinValue when none).</summary>
    public long LastTime(string id)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var c)) return long.MinValue;
            for (var i = 1; i <= _count; i++)
            {
                var p = (_head - i + Rows) % Rows;
                if (!float.IsNaN(Value(c, p))) return _times[p];
            }
            return long.MinValue;
        }
    }

    /// <summary>Visits rows with time in [from, to) and &gt; <paramref name="after"/>, quantized like stored samples.</summary>
    public void Visit<TV>(string id, long from, long to, long after, int ordinal, ref TV visitor) where TV : IRecVisitor
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var c) || c.HubIndex < 0) return;
            var quantum = Quant.Of(c.Kind);
            var start = _head - _count;
            if (start < 0) start += Rows;
            for (var i = 0; i < _count; i++)
            {
                var p = (start + i) % Rows;
                var t = _times[p];
                if (t < from || t >= to || t <= after) continue;
                if (Quant.TryQuantize(Value(c, p), quantum, out var q)) visitor.Visit(ordinal, Rec.Sample(t, q));
            }
        }
    }

    private static float Read(Column c, SensorFrame f, long ts)
    {
        var idx = c.HubIndex;
        if ((uint)idx >= (uint)f.Count) return float.NaN;
        var seq = f.UpdatedAt[idx];
        if (seq == 0) return float.NaN;
        if (seq != c.LastSeq)
        {
            c.LastSeq = seq;
            c.LastFresh = ts;
        }
        return ts - c.LastFresh > c.StaleLimit ? float.NaN : f.Values[idx];
    }
}
