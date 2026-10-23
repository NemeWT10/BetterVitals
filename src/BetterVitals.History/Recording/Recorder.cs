using BetterVitals.Core.Sensors;
using BetterVitals.History.Storage;

namespace BetterVitals.History.Recording;

/// <summary>
/// What gets recorded right now: resolved columns (sorted by catalog slot), the sample interval and a small pool of
/// preallocated block buffers. Immutable except for the engine-thread freshness state and the hub indexes.
/// </summary>
internal sealed class RecordPlan
{
    public const int PoolSize = 4;

    public RecordPlan(IReadOnlyList<(CatalogEntry Entry, SensorDescriptor Sensor)> columns, long step)
    {
        var n = columns.Count;
        Step = step;
        Rows = (int)((TimeGrid.Minute + step - 1) / step);
        Ids = new string[n];
        Slots = new ushort[n];
        Kinds = new SensorKind[n];
        HubIndex = new int[n];
        StaleLimit = new long[n];
        LastSeq = new int[n];
        LastFresh = new long[n];
        LastSample = new long[n];
        for (var i = 0; i < n; i++)
        {
            var (e, d) = columns[i];
            Ids[i] = e.Id;
            Slots[i] = e.Slot;
            Kinds[i] = e.Kind;
            HubIndex[i] = d.Index;
            StaleLimit[i] = 2 * Math.Max(step, d.MinInterval.Ticks) + 5 * TimeGrid.Second;
            LastSample[i] = long.MinValue;
        }
        Pool = new BlockBuffer[PoolSize];
        for (var i = 0; i < PoolSize; i++) Pool[i] = new BlockBuffer(this);
    }

    public long Step { get; }

    /// <summary>Rows per block = samples per minute.</summary>
    public int Rows { get; }

    public int Columns => Slots.Length;

    public readonly string[] Ids;
    public readonly ushort[] Slots;
    public readonly SensorKind[] Kinds;
    public readonly int[] HubIndex;
    public readonly long[] StaleLimit;
    public readonly BlockBuffer[] Pool;

    // engine thread only
    public readonly int[] LastSeq;
    public readonly long[] LastFresh;

    /// <summary>Time of the last valid sample per column (engine writes, others read).</summary>
    public readonly long[] LastSample;

    public int ColumnOf(int slot) => Array.BinarySearch(Slots, (ushort)slot) is var i and >= 0 ? i : -1;

    /// <summary>Current value of column <paramref name="c"/> or NaN (absent, never read, or stale).</summary>
    public float Read(int c, SensorFrame f, long ts)
    {
        var idx = HubIndex[c];
        if ((uint)idx >= (uint)f.Count) return float.NaN;
        var seq = f.UpdatedAt[idx];
        if (seq == 0) return float.NaN;
        if (seq != LastSeq[c])
        {
            LastSeq[c] = seq;
            LastFresh[c] = ts;
        }
        return ts - LastFresh[c] > StaleLimit[c] ? float.NaN : f.Values[idx];
    }
}

internal enum BufferState : byte
{
    Free,
    Current,
    Sealed,
    InFlight,
}

/// <summary>Up to one minute of samples of one plan, column-major (<c>Values[col * Rows + row]</c>).</summary>
internal sealed class BlockBuffer(RecordPlan plan)
{
    public RecordPlan Plan { get; } = plan;
    public float[] Values { get; } = new float[plan.Columns * plan.Rows];
    public long Start;
    public int Count;
    public BufferState State;

    public long End => Start + Count * Plan.Step;
}

/// <summary>
/// Turns engine frames into minute blocks. <see cref="OnFrame"/> runs on the engine thread: it copies into
/// preallocated buffers under a short lock, never allocates and never does I/O. The flush thread takes sealed blocks.
/// </summary>
internal sealed class Recorder
{
    private const int MaxSealed = 32;

    private readonly Lock _gate = new();
    private RecordPlan? _plan;
    private BlockBuffer? _current;
    private readonly BlockBuffer[] _sealed = new BlockBuffer[MaxSealed];
    private int _sealedCount;
    private long _lastSlot = long.MinValue;
    private long _dropped;

    /// <summary>Samples lost because no buffer was free (flush stalled, e.g. disk full).</summary>
    public long DroppedSamples => Interlocked.Read(ref _dropped);

    public RecordPlan? Plan => Volatile.Read(ref _plan);

    /// <summary>Publishes a plan; the engine adopts it on the next frame (the open block is sealed then).</summary>
    public void SetPlan(RecordPlan? plan) => Volatile.Write(ref _plan, plan);

    public void OnFrame(SensorFrame f)
    {
        var plan = Volatile.Read(ref _plan);
        lock (_gate)
        {
            if (_current is not null && _current.Plan != plan) SealLocked();
            if (plan is null || plan.Columns == 0) return;

            var ts = f.TimestampUtcTicks;
            var slot = TimeGrid.Floor(ts, plan.Step);
            if (slot == _lastSlot && _current is not null) return;

            if (_current is not null
                && (slot < _lastSlot
                    || TimeGrid.Floor(slot, TimeGrid.Minute) != TimeGrid.Floor(_current.Start, TimeGrid.Minute)
                    || (slot - _current.Start) / plan.Step >= plan.Rows))
                SealLocked();

            if (slot == _lastSlot && _current is null) return; // this slot was already sampled (then force-sealed)
            _lastSlot = slot;

            if (_current is null)
            {
                var buf = TakeBufferLocked(plan);
                if (buf is null)
                {
                    _dropped++;
                    return;
                }
                buf.Start = slot;
                buf.Count = 0;
                buf.State = BufferState.Current;
                _current = buf;
            }

            var b = _current;
            var row = (int)((slot - b.Start) / plan.Step);
            var rows = plan.Rows;
            var values = b.Values;
            for (var r = b.Count; r < row; r++)
            for (var c = 0; c < plan.Columns; c++)
                values[c * rows + r] = float.NaN;
            for (var c = 0; c < plan.Columns; c++)
            {
                var v = plan.Read(c, f, ts);
                values[c * rows + row] = v;
                if (!float.IsNaN(v)) plan.LastSample[c] = slot;
            }
            b.Count = row + 1;
        }
    }

    /// <summary>Seals the open block when its minute is over (or always with <paramref name="force"/>).</summary>
    public void SealIfDue(long nowTicks, bool force)
    {
        lock (_gate)
        {
            if (_current is null) return;
            if (force || nowTicks >= TimeGrid.Floor(_current.Start, TimeGrid.Minute) + TimeGrid.Minute) SealLocked();
        }
    }

    /// <summary>Marks every sealed block in-flight and adds it to <paramref name="into"/> (oldest first).</summary>
    public void TakeSealed(List<BlockBuffer> into)
    {
        lock (_gate)
        {
            for (var i = 0; i < _sealedCount; i++)
            {
                var b = _sealed[i];
                if (b.State != BufferState.Sealed) continue;
                b.State = BufferState.InFlight;
                into.Add(b);
            }
        }
    }

    /// <summary>Written blocks go back to their pool; unwritten ones wait for the next flush.</summary>
    public void Release(List<BlockBuffer> blocks, int writtenCount)
    {
        lock (_gate)
        {
            for (var i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                if (b.State != BufferState.InFlight) continue;
                if (i < writtenCount) RemoveSealedLocked(b);
                else b.State = BufferState.Sealed;
            }
        }
    }

    /// <summary>Drops everything not written yet ("clear history").</summary>
    public void DiscardPending()
    {
        lock (_gate)
        {
            for (var i = _sealedCount - 1; i >= 0; i--)
                if (_sealed[i].State == BufferState.Sealed) RemoveSealedLocked(_sealed[i]);
            if (_current is not null)
            {
                _current.Count = 0;
                _current.State = BufferState.Free;
                _current = null;
            }
        }
    }

    /// <summary>Start of the oldest sample not written yet (long.MaxValue when none).</summary>
    public long EarliestPendingStart()
    {
        lock (_gate)
        {
            var best = long.MaxValue;
            for (var i = 0; i < _sealedCount; i++)
                if (_sealed[i].Count > 0 && _sealed[i].Start < best) best = _sealed[i].Start;
            if (_current is { Count: > 0 } c && c.Start < best) best = c.Start;
            return best;
        }
    }

    /// <summary>Visits pending (unwritten) samples of the given slots with time in [from, to), quantized like on disk.</summary>
    public void VisitPending<TV>(ReadOnlySpan<int> slots, long from, long to, ref TV visitor) where TV : IRecVisitor
    {
        lock (_gate)
        {
            for (var i = 0; i < _sealedCount; i++) VisitBlock(_sealed[i], slots, from, to, ref visitor);
            if (_current is not null) VisitBlock(_current, slots, from, to, ref visitor);
        }
    }

    /// <summary>Test / diagnostics: number of sealed blocks waiting.</summary>
    public int SealedCount
    {
        get
        {
            lock (_gate) return _sealedCount;
        }
    }

    private static void VisitBlock<TV>(BlockBuffer b, ReadOnlySpan<int> slots, long from, long to, ref TV visitor) where TV : IRecVisitor
    {
        if (b.Count == 0 || b.End <= from || b.Start >= to) return;
        var plan = b.Plan;
        for (var o = 0; o < slots.Length; o++)
        {
            if (slots[o] < 0) continue;
            var c = plan.ColumnOf(slots[o]);
            if (c < 0) continue;
            var quantum = Quant.Of(plan.Kinds[c]);
            var t = b.Start;
            for (var r = 0; r < b.Count; r++, t += plan.Step)
            {
                if (t < from || t >= to) continue;
                if (Quant.TryQuantize(b.Values[c * plan.Rows + r], quantum, out var q)) visitor.Visit(o, Rec.Sample(t, q));
            }
        }
    }

    private BlockBuffer? TakeBufferLocked(RecordPlan plan)
    {
        foreach (var b in plan.Pool)
            if (b.State == BufferState.Free) return b;
        // all busy: overwrite the oldest sealed block of this plan (data loss, but bounded memory and no allocation)
        for (var i = 0; i < _sealedCount; i++)
        {
            var b = _sealed[i];
            if (b.Plan != plan || b.State != BufferState.Sealed) continue;
            _dropped += b.Count;
            RemoveSealedLocked(b);
            return b;
        }
        return null;
    }

    private void SealLocked()
    {
        var b = _current!;
        _current = null;
        if (b.Count == 0 || _sealedCount == MaxSealed)
        {
            if (b.Count > 0) _dropped += b.Count;
            b.Count = 0;
            b.State = BufferState.Free;
            return;
        }
        b.State = BufferState.Sealed;
        _sealed[_sealedCount++] = b;
    }

    private void RemoveSealedLocked(BlockBuffer b)
    {
        var i = Array.IndexOf(_sealed, b, 0, _sealedCount);
        if (i < 0) return;
        Array.Copy(_sealed, i + 1, _sealed, i, _sealedCount - i - 1);
        _sealed[--_sealedCount] = null!;
        b.Count = 0;
        b.State = BufferState.Free;
    }
}
