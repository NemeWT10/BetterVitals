using System.Globalization;

namespace BetterVitals.History.Storage;

/// <summary>Callback for <see cref="ReadSession.VisitBlocks"/>: header, parsed column table and the inflated payload.</summary>
internal delegate void BlockAction(in BlockHeader header, ColumnTable table, ReadOnlySpan<byte> payload);

/// <summary>One history file: raw-yyyyMMdd.bvr (UTC day), min-yyyyMM.bvm (UTC month), hour-yyyy.bvh (UTC year).</summary>
internal sealed record HistFile(Level Level, long Start, long End, string Path, long Length);

/// <summary>File naming and listing. Blocks live in the file whose period contains the block start.</summary>
internal static class HistoryFiles
{
    public static string NameFor(Level level, long t)
    {
        var d = new DateTime(t, DateTimeKind.Utc);
        return level switch
        {
            Level.Raw => $"raw-{d:yyyyMMdd}.bvr",
            Level.Minute => $"min-{d:yyyyMM}.bvm",
            _ => $"hour-{d:yyyy}.bvh",
        };
    }

    /// <summary>[start, end) of the file period that contains <paramref name="t"/>.</summary>
    public static (long Start, long End) Period(Level level, long t)
    {
        var d = new DateTime(t, DateTimeKind.Utc);
        return level switch
        {
            Level.Raw => (d.Date.Ticks, d.Date.Ticks + TimeGrid.Day),
            Level.Minute => (new DateTime(d.Year, d.Month, 1).Ticks, SafeAddMonths(new DateTime(d.Year, d.Month, 1))),
            _ => (new DateTime(d.Year, 1, 1).Ticks, d.Year == 9999 ? DateTime.MaxValue.Ticks : new DateTime(d.Year + 1, 1, 1).Ticks),
        };
    }

    private static long SafeAddMonths(DateTime d) => d.Year == 9999 && d.Month == 12 ? DateTime.MaxValue.Ticks : d.AddMonths(1).Ticks;

    /// <summary>All history files in <paramref name="dir"/>, sorted by level then start. Unknown names are ignored.</summary>
    public static List<HistFile> List(string dir)
    {
        var list = new List<HistFile>();
        try
        {
            if (!Directory.Exists(dir)) return list;
            foreach (var info in new DirectoryInfo(dir).EnumerateFiles("*.bv?"))
                if (TryParse(info.Name, out var level, out var start))
                {
                    var (s, e) = Period(level, start);
                    list.Add(new HistFile(level, s, e, info.FullName, info.Length));
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // unreadable folder = no history
        }
        list.Sort((a, b) => a.Level != b.Level ? a.Level.CompareTo(b.Level) : a.Start.CompareTo(b.Start));
        return list;
    }

    private static bool TryParse(string name, out Level level, out long start)
    {
        level = Level.Raw;
        start = 0;
        string digits;
        string format;
        if (name.Length == 16 && name.StartsWith("raw-", StringComparison.Ordinal) && name.EndsWith(".bvr", StringComparison.Ordinal))
        {
            (digits, format, level) = (name[4..12], "yyyyMMdd", Level.Raw);
        }
        else if (name.Length == 14 && name.StartsWith("min-", StringComparison.Ordinal) && name.EndsWith(".bvm", StringComparison.Ordinal))
        {
            (digits, format, level) = (name[4..10], "yyyyMM", Level.Minute);
        }
        else if (name.Length == 13 && name.StartsWith("hour-", StringComparison.Ordinal) && name.EndsWith(".bvh", StringComparison.Ordinal))
        {
            (digits, format, level) = (name[5..9], "yyyy", Level.Hour);
        }
        else
        {
            return false;
        }
        if (!DateTime.TryParseExact(digits, format, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)) return false;
        start = d.Ticks;
        return true;
    }

    public static bool IsHistoryFile(string name) => TryParse(name, out _, out _) || name == Catalog.FileName;
}

/// <summary>
/// Read-side helper for one query / export: block lists come from the store's <see cref="BlockIndex"/> (built once per
/// file and kept while reads continue), and only the blocks a read needs are fetched, CRC-checked again and decoded.
/// Corrupted files / blocks are skipped and reported once. Not thread-safe; use under the store gate and dispose.
/// </summary>
internal sealed class ReadSession(string dir, Action<string, string> warnOnce, BlockIndex index) : IDisposable
{
    private List<HistFile>? _files;
    private Inflater? _inflater;
    private readonly ColumnTable _table = new();

    public ReadSession(string dir, Action<string, string> warnOnce) : this(dir, warnOnce, new BlockIndex())
    {
    }

    public string Dir { get; } = dir;

    public IReadOnlyList<HistFile> AllFiles => _files ??= HistoryFiles.List(Dir);

    private Inflater Inflater => _inflater ??= index.TakeInflater();

    /// <summary>Forget the listing (export runs across flushes); files that grew are rescanned from their old end.</summary>
    public void Refresh() => _files = null;

    public IEnumerable<HistFile> Files(Level level)
    {
        foreach (var f in AllFiles)
            if (f.Level == level) yield return f;
    }

    /// <summary>Start of the oldest file of the level (long.MaxValue when none).</summary>
    public long OldestStart(Level level)
    {
        foreach (var f in AllFiles)
            if (f.Level == level) return f.Start;
        return long.MaxValue;
    }

    /// <summary>Visits records of <paramref name="level"/> with time in [from, to) for the given catalog slots (-1 = skip).</summary>
    public void Read<TV>(Level level, long from, long to, ReadOnlySpan<int> slots, ref TV visitor) where TV : IRecVisitor
    {
        if (from >= to) return;
        foreach (var f in AllFiles)
        {
            if (f.Level != level || f.End <= from || f.Start >= to) continue;
            ReadFile(f, from, to, slots, ref visitor);
        }
    }

    public void ReadFile<TV>(HistFile f, long from, long to, ReadOnlySpan<int> slots, ref TV visitor) where TV : IRecVisitor
    {
        using var rd = Open(f, out var entry);
        if (rd is null) return;
        foreach (var b in entry!.Blocks)
        {
            if (b.Header.Type != f.Level || b.Header.End <= from || b.Header.Start >= to) continue;
            Decode(rd, f, b, from, to, slots, ref visitor);
        }
    }

    private struct EdgeVisitor : IRecVisitor
    {
        public long First;
        public long Last;

        public void Visit(int ordinal, in Rec rec)
        {
            if (rec.Count == 0) return;
            if (rec.Time < First) First = rec.Time;
            if (rec.Time > Last) Last = rec.Time;
        }
    }

    /// <summary>
    /// Earliest (or latest) record time of the slots in the file; decodes only blocks that can still improve the answer.
    /// Returns long.MaxValue / long.MinValue when the file has none.
    /// </summary>
    public long EdgeTime(HistFile f, ReadOnlySpan<int> slots, bool first)
    {
        var v = new EdgeVisitor { First = long.MaxValue, Last = long.MinValue };
        using var rd = Open(f, out var entry);
        if (rd is null) return first ? v.First : v.Last;
        var blocks = entry!.Blocks;
        for (var k = 0; k < blocks.Count; k++)
        {
            var b = blocks[first ? k : blocks.Count - 1 - k];
            if (b.Header.Type != f.Level) continue;
            if (first ? b.Header.Start >= v.First : b.Header.End <= v.Last) continue;
            Decode(rd, f, b, long.MinValue, long.MaxValue, slots, ref v);
        }
        return first ? v.First : v.Last;
    }

    /// <summary>
    /// Fully inflates every valid block of <paramref name="level"/> with start ≥ <paramref name="minStart"/> and
    /// end &gt; <paramref name="minEnd"/> (files that cannot hold such blocks are not opened).
    /// </summary>
    public void VisitBlocks(Level level, long minStart, long minEnd, BlockAction action)
    {
        foreach (var f in AllFiles)
        {
            // a block lies in the file of its start and spans at most a day (an hour rollup group)
            if (f.Level != level || f.End <= minStart || (minEnd != long.MinValue && f.End <= minEnd - TimeGrid.Day)) continue;
            using var rd = Open(f, out var entry);
            if (rd is null) continue;
            foreach (var b in entry!.Blocks)
            {
                if (b.Header.Type != level || b.Header.Start < minStart || b.Header.End <= minEnd) continue;
                try
                {
                    var at = Fetch(rd, f, b);
                    Inflater.Begin(rd.Buffer, at + BlockHeader.Size, b.Header.PayloadLength, b.Header.RawLength, b.Header.RawLength);
                    var data = Inflater.Data;
                    _table.Parse(data);
                    if (_table.Count != b.Header.Columns || _table.Count > 0 && _table.End(_table.Count - 1) > data.Length)
                        throw new InvalidDataException("bad column table");
                    action(b.Header, _table, data);
                }
                catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or IOException)
                {
                    warnOnce(f.Path + "#" + b.PayloadOffset, $"history: skipped corrupted block in {Path.GetFileName(f.Path)} @{b.PayloadOffset}: {ex.Message}");
                }
                finally
                {
                    _inflater?.End();
                }
            }
        }
    }

    /// <summary>Latest block end of a level (long.MinValue when there is none).</summary>
    public long MaxEnd(Level level)
    {
        var best = long.MinValue;
        var files = Files(level).ToList();
        for (var i = files.Count - 1; i >= 0; i--)
        {
            using var rd = Open(files[i], out var entry);
            if (rd is null) continue;
            foreach (var b in entry!.Blocks)
                if (b.Header.Type == level && b.Header.End > best) best = b.Header.End;
            if (best != long.MinValue) break;
        }
        return best;
    }

    /// <summary>Earliest block start of a level (long.MaxValue when there is none).</summary>
    public long MinStart(Level level)
    {
        foreach (var f in Files(level))
        {
            using var rd = Open(f, out var entry);
            if (rd is null) continue;
            var best = long.MaxValue;
            foreach (var b in entry!.Blocks)
                if (b.Header.Type == level && b.Header.Start < best) best = b.Header.Start;
            if (best != long.MaxValue) return best;
        }
        return long.MaxValue;
    }

    /// <summary>Decodes the requested columns of one block; a corrupted block is reported and skipped.</summary>
    private void Decode<TV>(FileReader rd, HistFile f, in BlockRef b, long from, long to, ReadOnlySpan<int> slots, ref TV visitor) where TV : IRecVisitor
    {
        try
        {
            var h = b.Header;
            var needed = false;
            for (var i = 0; i < slots.Length && !needed; i++) needed = slots[i] >= 0;
            if (!needed) return;
            var at = Fetch(rd, f, b);
            var inf = Inflater;
            inf.Begin(rd.Buffer, at + BlockHeader.Size, h.PayloadLength, h.RawLength, ColumnTable.MaxSize(h.Columns));
            _table.Parse(inf.Data);
            if (_table.Count != h.Columns) throw new InvalidDataException("column count mismatch");
            var need = 0;
            var any = false;
            for (var i = 0; i < slots.Length; i++)
            {
                if (slots[i] < 0) continue;
                var c = _table.IndexOf(slots[i]);
                if (c < 0) continue;
                any = true;
                need = Math.Max(need, _table.End(c));
            }
            if (!any) return;
            inf.Fill(need);
            var data = inf.Data;
            if (data.Length < need) throw new InvalidDataException("short payload");
            for (var i = 0; i < slots.Length; i++)
            {
                if (slots[i] < 0) continue;
                var c = _table.IndexOf(slots[i]);
                if (c < 0) continue;
                var col = data.Slice(_table.Offsets[c], _table.Lengths[c]);
                if (h.Type == Level.Raw) ColumnDecoder.Raw(col, _table.Modes[c], h, from, to, i, ref visitor);
                else ColumnDecoder.Rollup(col, h, from, to, i, ref visitor);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or IOException)
        {
            warnOnce(f.Path + "#" + b.PayloadOffset, $"history: skipped corrupted block in {Path.GetFileName(f.Path)} @{b.PayloadOffset}: {ex.Message}");
        }
        finally
        {
            _inflater?.End();
        }
    }

    /// <summary>Reads header + payload of a block into the reader's window and checks the CRC again; returns its offset there.</summary>
    private int Fetch(FileReader rd, HistFile f, in BlockRef b)
    {
        var size = BlockHeader.Size + b.Header.PayloadLength;
        if (!rd.TryLocate(b.PayloadOffset - BlockHeader.Size, size, out var at)) throw Changed(f, "block beyond the end of the file");
        var block = rd.Buffer.AsSpan(at, size);
        if (Crc32.Append(Crc32.Compute(block[..32]), block[BlockHeader.Size..]) != BlockHeader.StoredCrc(block)) throw Changed(f, "block changed on disk");
        return at;
    }

    private InvalidDataException Changed(HistFile f, string what)
    {
        index.Invalidate(f.Path);
        return new InvalidDataException(what);
    }

    /// <summary>Opens a file and gets its block list (null + warning when it cannot be read).</summary>
    private FileReader? Open(HistFile f, out BlockIndex.Entry? entry)
    {
        entry = null;
        FileReader? rd = null;
        try
        {
            rd = FileReader.Open(index, f.Path);
            entry = index.Get(f.Path, rd, warnOnce);
            return rd;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            rd?.Dispose();
            warnOnce(f.Path, $"history: cannot read {Path.GetFileName(f.Path)}: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_inflater is null) return;
        _inflater.End();
        index.ReturnInflater(_inflater);
        _inflater = null;
    }
}
