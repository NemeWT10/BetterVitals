using BetterVitals.Core.Sensors;
using BetterVitals.History.Recording;
using BetterVitals.History.Storage;

namespace BetterVitals.History;

/// <summary>Rollup aggregates (one per column) of one bucket that are not on disk yet.</summary>
internal sealed class PendingBucket
{
    public required long Start { get; init; }
    public required ushort[] Slots { get; init; }
    public required SensorKind[] Kinds { get; init; }
    public required Rec[] Aggs { get; init; }

    public int ColumnOf(int slot) => Array.BinarySearch(Slots, (ushort)slot) is var i and >= 0 ? i : -1;
}

/// <summary>
/// Disk side of the history: catalog, raw / minute / hour files, rollups not written yet, flush, recovery, retention.
/// Every member runs under <see cref="Gate"/> (queries, flush and clear are serialized; the engine never takes it).
///
/// Invariants that keep queries exact without double counting:
/// <list type="bullet">
/// <item>Raw: disk + recorder buffers hold every sample (until raw retention).</item>
/// <item>Minute level (files + <see cref="_minutes"/>) holds all data older than the oldest unwritten raw block.</item>
/// <item>Hour level (files + <see cref="_hours"/>) holds all data older than the oldest pending minute.</item>
/// <item>Rollups reach disk only for buckets that are over (wall clock and data), so after a crash the pending
/// rollups are rebuilt exactly from "raw blocks after the last minute block" and "minutes after the last hour block".</item>
/// </list>
/// </summary>
internal sealed partial class HistoryStore
{
    private readonly Recorder _recorder;
    private readonly LiveRing _ring;
    private readonly HistoryOptions _options;
    private readonly BlockEncoder _encoder = new();
    private readonly List<PendingBucket> _minutes = [];
    private readonly List<PendingBucket> _hours = [];
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private bool _loaded;
    private long _bytesWritten;
    private long _valuesWritten;

    public HistoryStore(string dir, Recorder recorder, LiveRing ring, HistoryOptions options)
    {
        Dir = dir;
        _recorder = recorder;
        _ring = ring;
        _options = options;
    }

    public Lock Gate { get; } = new();

    public string Dir { get; }

    public Catalog Catalog { get; } = new();

    /// <summary>Last flush skipped writing because the disk is (almost) full.</summary>
    public bool DiskLow { get; private set; }

    /// <summary>Measured bytes on disk per stored value (all levels), or null before enough data.</summary>
    public double? BytesPerValue => _valuesWritten >= 10_000 ? (double)_bytesWritten / _valuesWritten : null;

    private long Now => _options.Time.GetUtcNow().UtcTicks;

    public void WarnOnce(string key, string message)
    {
        lock (_warned)
        {
            if (_warned.Count > 1000 || !_warned.Add(key)) return;
        }
        try
        {
            _options.Warn(message);
        }
        catch
        {
            // logging must not break the store
        }
    }

    public ReadSession OpenSession() => new(Dir, WarnOnce);

    private readonly Lock _catalogGate = new();
    private bool _catalogLoaded;

    /// <summary>Loads the catalog once (any thread, fast).</summary>
    public void EnsureCatalog()
    {
        lock (_catalogGate)
        {
            if (_catalogLoaded) return;
            _catalogLoaded = true;
            var bad = Catalog.Load(Dir);
            if (bad > 0) WarnOnce("catalog", $"history: {bad} corrupted catalog record(s) skipped");
        }
    }

    /// <summary>Under <see cref="Gate"/>: catalog + rebuild of the pending rollups from the files (read only). Idempotent.</summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            EnsureCatalog();
            using var s = OpenSession();
            Recover(s);
        }
        catch (Exception ex)
        {
            WarnOnce("recover", $"history: recovery failed: {ex.Message}");
        }
    }

    private void Recover(ReadSession s)
    {
        _minutes.Clear();
        _hours.Clear();

        // raw blocks after the last minute block → pending minutes
        var minuteEnd = s.MaxEnd(Level.Minute);
        s.VisitBlocks(Level.Raw, minuteEnd, (in BlockHeader h, ColumnTable t, ReadOnlySpan<byte> data) =>
        {
            var n = t.Count;
            var aggs = new Rec[n];
            for (var c = 0; c < n; c++)
            {
                var v = new AggVisitor();
                ColumnDecoder.Raw(data.Slice(t.Offsets[c], t.Lengths[c]), t.Modes[c], h, long.MinValue, long.MaxValue, 0, ref v);
                aggs[c] = v.Acc;
            }
            AddPending(_minutes, TimeGrid.Floor(h.Start, TimeGrid.Minute), t.Slots.AsSpan(0, n).ToArray(), t.Kinds.AsSpan(0, n).ToArray(), aggs);
        });

        // minute buckets after the last hour block → pending hours
        var hourEnd = s.MaxEnd(Level.Hour);
        var byHour = new SortedDictionary<long, SortedDictionary<ushort, (SensorKind Kind, Rec Agg)>>();
        s.VisitBlocks(Level.Minute, long.MinValue, (in BlockHeader h, ColumnTable t, ReadOnlySpan<byte> data) =>
        {
            if (h.End <= hourEnd) return;
            for (var c = 0; c < t.Count; c++)
            {
                var v = new ListVisitor();
                ColumnDecoder.Rollup(data.Slice(t.Offsets[c], t.Lengths[c]), h, hourEnd, long.MaxValue, 0, ref v);
                foreach (var r in v.Items)
                {
                    var hour = TimeGrid.Floor(r.Time, TimeGrid.Hour);
                    if (!byHour.TryGetValue(hour, out var cols)) byHour[hour] = cols = [];
                    var agg = cols.TryGetValue(t.Slots[c], out var cur) ? cur.Agg : default;
                    agg.Merge(r);
                    cols[t.Slots[c]] = (t.Kinds[c], agg);
                }
            }
        });
        foreach (var (hour, cols) in byHour)
            AddPending(_hours, hour, cols.Keys.ToArray(), cols.Values.Select(x => x.Kind).ToArray(), cols.Values.Select(x => x.Agg).ToArray());
    }

    private static void AddPending(List<PendingBucket> list, long start, ushort[] slots, SensorKind[] kinds, Rec[] aggs)
    {
        var b = new PendingBucket { Start = start, Slots = slots, Kinds = kinds, Aggs = aggs };
        var i = list.Count;
        while (i > 0 && list[i - 1].Start > start) i--;
        list.Insert(i, b);
    }

    public long EarliestPendingMinute => _minutes.Count > 0 ? _minutes.Min(b => b.Start) : long.MaxValue;

    /// <summary>
    /// Writes sealed raw blocks and the rollups that are due. <paramref name="final"/> (suspend / exit / recording off)
    /// also seals the open block and writes every finished rollup bucket, not only finished hours / days.
    /// Never throws; returns the number of bytes appended.
    /// </summary>
    public long Flush(bool final)
    {
        EnsureLoaded();
        var now = Now;
        _recorder.SealIfDue(now, final);
        var blocks = new List<BlockBuffer>();
        _recorder.TakeSealed(blocks);
        var written = 0;
        long bytes = 0;
        try
        {
            if (blocks.Count == 0 && _minutes.Count == 0 && _hours.Count == 0) return 0;
            var free = _options.FreeSpace(Dir);
            if (free is { } f && f < _options.MinFreeBytes)
            {
                if (!DiskLow) WarnOnce("disklow" + now / TimeGrid.Day, $"history: only {f >> 20} MB free, recording paused");
                DiskLow = true;
                return 0;
            }

            Directory.CreateDirectory(Dir);
            if (blocks.Count > 0 && Catalog.HasUnpersisted) Catalog.Persist(Dir);

            // raw blocks, grouped by day file, in order
            var i = 0;
            while (i < blocks.Count)
            {
                var name = HistoryFiles.NameFor(Level.Raw, blocks[i].Start);
                using var fs = OpenAppend(name);
                while (i < blocks.Count && HistoryFiles.NameFor(Level.Raw, blocks[i].Start) == name)
                {
                    var b = blocks[i];
                    var p = b.Plan;
                    var len = _encoder.EncodeRaw(p.Slots, p.Kinds, b.Values, p.Rows, b.Count);
                    var size = _encoder.AppendBlock(fs, Level.Raw, b.Start, p.Step, b.Count, p.Columns, len);
                    bytes += size;
                    _valuesWritten += (long)b.Count * p.Columns;
                    AddMinuteFromRaw(b);
                    written++;
                    i++;
                }
            }

            var mwLimit = Math.Min(_recorder.EarliestPendingStart(), TimeGrid.Floor(now, TimeGrid.Minute));
            bytes += WriteRollups(_minutes, Level.Minute, mwLimit, final, _hours);
            var hwLimit = TimeGrid.Floor(Math.Min(EarliestPendingMinute, mwLimit), TimeGrid.Hour);
            bytes += WriteRollups(_hours, Level.Hour, hwLimit, final, null);
            DiskLow = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (IsDiskFull(ex)) DiskLow = true;
            WarnOnce("flush" + ex.GetType().Name + now / TimeGrid.Hour, $"history: write failed: {ex.Message}");
        }
        finally
        {
            _recorder.Release(blocks, written);
        }
        _bytesWritten += bytes;
        return bytes;
    }

    private FileStream OpenAppend(string name)
        => new(Path.Combine(Dir, name), FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete, 1);

    private static bool IsDiskFull(Exception ex) => (ex.HResult & 0xFFFF) is 0x70 or 0x27; // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL

    private void AddMinuteFromRaw(BlockBuffer b)
    {
        var p = b.Plan;
        var aggs = new Rec[p.Columns];
        for (var c = 0; c < p.Columns; c++)
        {
            var quantum = Quant.Of(p.Kinds[c]);
            var a = new Rec();
            for (var r = 0; r < b.Count; r++)
                if (Quant.TryQuantize(b.Values[c * p.Rows + r], quantum, out var q)) a.Merge(Rec.Sample(0, q));
            aggs[c] = a;
        }
        AddPending(_minutes, TimeGrid.Floor(b.Start, TimeGrid.Minute), p.Slots, p.Kinds, aggs);
    }

    /// <summary>
    /// Writes buckets of <paramref name="source"/> that are over (end ≤ limit). Normally only whole groups
    /// (an hour of minutes / a day of hours) go out as one block, so readers decode few large blocks;
    /// <paramref name="final"/> writes partial groups too. Written groups are promoted into <paramref name="promote"/>.
    /// </summary>
    private long WriteRollups(List<PendingBucket> source, Level level, long limit, bool final, List<PendingBucket>? promote)
    {
        var step = level == Level.Minute ? TimeGrid.Minute : TimeGrid.Hour;
        var group = level == Level.Minute ? TimeGrid.Hour : TimeGrid.Day;
        long bytes = 0;
        while (true)
        {
            long groupStart = long.MaxValue;
            foreach (var b in source)
                if (b.Start + step <= limit && (final || TimeGrid.Floor(b.Start, group) + group <= limit))
                    groupStart = Math.Min(groupStart, TimeGrid.Floor(b.Start, group));
            if (groupStart == long.MaxValue) return bytes;

            var members = source.Where(b => TimeGrid.Floor(b.Start, group) == groupStart && b.Start + step <= limit).ToList();
            var cols = new SortedDictionary<ushort, SensorKind>();
            long first = long.MaxValue, last = long.MinValue;
            foreach (var b in members)
            {
                first = Math.Min(first, b.Start);
                last = Math.Max(last, b.Start);
                for (var j = 0; j < b.Slots.Length; j++) cols.TryAdd(b.Slots[j], b.Kinds[j]);
            }
            var slots = cols.Keys.ToArray();
            var kinds = cols.Values.ToArray();
            var count = (int)((last - first) / step) + 1;
            var aggs = new Rec[slots.Length * count];
            var merged = new Rec[slots.Length];
            foreach (var b in members)
            {
                var idx = (int)((b.Start - first) / step);
                for (var j = 0; j < b.Slots.Length; j++)
                {
                    var c = Array.BinarySearch(slots, b.Slots[j]);
                    aggs[c * count + idx].Merge(b.Aggs[j]);
                    merged[c].Merge(b.Aggs[j]);
                }
            }
            var len = _encoder.EncodeRollup(slots, kinds, aggs, count);
            using (var fs = OpenAppend(HistoryFiles.NameFor(level, first)))
                bytes += _encoder.AppendBlock(fs, level, first, step, count, slots.Length, len);
            foreach (var b in members) source.Remove(b);
            if (promote is not null) AddPending(promote, groupStart, slots, kinds, merged);
        }
    }

    /// <summary>Deletes whole files older than the retention. Hour retention 0 = forever.</summary>
    public int RunRetention(int rawDays, int minuteDays, int hourDays)
    {
        var now = Now;
        var deleted = 0;
        foreach (var f in HistoryFiles.List(Dir))
        {
            var days = f.Level switch
            {
                Level.Raw => Math.Max(1, rawDays),
                Level.Minute => Math.Max(1, minuteDays),
                _ => hourDays,
            };
            if (days <= 0 || f.End > now - days * TimeGrid.Day) continue;
            try
            {
                File.Delete(f.Path);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                WarnOnce("ret" + f.Path, $"history: cannot delete {Path.GetFileName(f.Path)}: {ex.Message}");
            }
        }
        return deleted;
    }

    /// <summary>Deletes every history file and everything not written yet.</summary>
    public void Clear()
    {
        _recorder.DiscardPending();
        _minutes.Clear();
        _hours.Clear();
        try
        {
            if (Directory.Exists(Dir))
                foreach (var path in Directory.EnumerateFiles(Dir))
                {
                    if (!HistoryFiles.IsHistoryFile(Path.GetFileName(path))) continue;
                    try
                    {
                        File.Delete(path);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        WarnOnce("clear" + path, $"history: cannot delete {Path.GetFileName(path)}: {ex.Message}");
                    }
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WarnOnce("clear", $"history: clear failed: {ex.Message}");
        }
        Catalog.MarkAllUnpersisted();
    }

    /// <summary>Bytes of all history files (catalog included) and the oldest data time.</summary>
    public (long Bytes, long? Oldest) Usage()
    {
        long bytes = 0;
        try
        {
            if (Directory.Exists(Dir))
                foreach (var info in new DirectoryInfo(Dir).EnumerateFiles())
                    if (HistoryFiles.IsHistoryFile(info.Name)) bytes += info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
        var oldest = _recorder.EarliestPendingStart();
        var files = HistoryFiles.List(Dir);
        foreach (var level in (ReadOnlySpan<Level>)[Level.Hour, Level.Minute, Level.Raw])
            foreach (var f in files)
            {
                if (f.Level != level) continue;
                var t = FirstBlockStart(f.Path);
                if (t == long.MaxValue) continue;
                oldest = Math.Min(oldest, t);
                break;
            }
        return (bytes, oldest == long.MaxValue ? null : oldest);
    }

    /// <summary>Start of the first valid block among the first 256 KB of a file (blocks are appended in time order).</summary>
    private static long FirstBlockStart(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            var buf = new byte[(int)Math.Min(fs.Length, 256 * 1024)];
            var n = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
            var blocks = new List<BlockRef>();
            BlockScanner.Scan(buf.AsSpan(0, n), blocks);
            return blocks.Count > 0 ? blocks.Min(b => b.Header.Start) : long.MaxValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return long.MaxValue;
        }
    }

    /// <summary>Accumulates records into one aggregate.</summary>
    internal struct AggVisitor : IRecVisitor
    {
        public Rec Acc;

        public void Visit(int ordinal, in Rec rec) => Acc.Merge(rec);
    }

    /// <summary>Collects records.</summary>
    internal struct ListVisitor : IRecVisitor
    {
        private List<Rec>? _items;

        public readonly List<Rec> Items => _items ?? [];

        public void Visit(int ordinal, in Rec rec) => (_items ??= []).Add(rec);
    }
}
