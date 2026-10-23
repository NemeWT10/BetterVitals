using System.Buffers;
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
/// Read-side helper for one query / export: loads files into pooled buffers (CRC-checked block index),
/// decodes the requested columns and feeds records to a visitor. Corrupted files / blocks are skipped and reported once.
/// Not thread-safe; dispose to return buffers.
/// </summary>
internal sealed class ReadSession(string dir, Action<string, string> warnOnce) : IDisposable
{
    private const int MaxCached = 3;

    private sealed class Loaded
    {
        public required HistFile File;
        public required byte[] Buffer;
        public required int Length;
        public readonly List<BlockRef> Blocks = [];
    }

    private List<HistFile>? _files;
    private readonly List<Loaded> _cache = [];
    private readonly Inflater _inflater = new();
    private readonly ColumnTable _table = new();

    public string Dir { get; } = dir;

    public IReadOnlyList<HistFile> AllFiles => _files ??= HistoryFiles.List(Dir);

    /// <summary>Forget the listing and any file that changed size (export runs across flushes).</summary>
    public void Refresh()
    {
        _files = null;
        var current = AllFiles;
        for (var i = _cache.Count - 1; i >= 0; i--)
        {
            var c = _cache[i];
            var now = current.FirstOrDefault(f => f.Path == c.File.Path);
            if (now is null || now.Length != c.File.Length)
            {
                ArrayPool<byte>.Shared.Return(c.Buffer);
                _cache.RemoveAt(i);
            }
        }
    }

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
        var loaded = Load(f);
        if (loaded is null) return;
        foreach (var b in loaded.Blocks)
        {
            if (b.Header.Type != f.Level || b.Header.End <= from || b.Header.Start >= to) continue;
            try
            {
                DecodeBlock(loaded.Buffer, b, from, to, slots, ref visitor);
            }
            catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or IOException)
            {
                warnOnce(f.Path + "#" + b.PayloadOffset, $"history: skipped corrupted block in {Path.GetFileName(f.Path)} @{b.PayloadOffset}: {ex.Message}");
            }
            finally
            {
                _inflater.End();
            }
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
        var loaded = Load(f);
        var v = new EdgeVisitor { First = long.MaxValue, Last = long.MinValue };
        if (loaded is null) return first ? v.First : v.Last;
        var blocks = loaded.Blocks;
        for (var k = 0; k < blocks.Count; k++)
        {
            var b = blocks[first ? k : blocks.Count - 1 - k];
            if (b.Header.Type != f.Level) continue;
            if (first ? b.Header.Start >= v.First : b.Header.End <= v.Last) continue;
            try
            {
                DecodeBlock(loaded.Buffer, b, long.MinValue, long.MaxValue, slots, ref v);
            }
            catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or IOException)
            {
                warnOnce(f.Path + "#" + b.PayloadOffset, $"history: skipped corrupted block in {Path.GetFileName(f.Path)} @{b.PayloadOffset}: {ex.Message}");
            }
            finally
            {
                _inflater.End();
            }
        }
        return first ? v.First : v.Last;
    }

    /// <summary>Fully inflates every valid block of <paramref name="level"/> with start ≥ <paramref name="minStart"/>.</summary>
    public void VisitBlocks(Level level, long minStart, BlockAction action)
    {
        foreach (var f in AllFiles)
        {
            if (f.Level != level || f.End <= minStart) continue;
            var loaded = Load(f);
            if (loaded is null) continue;
            foreach (var b in loaded.Blocks)
            {
                if (b.Header.Type != level || b.Header.Start < minStart) continue;
                try
                {
                    _inflater.Begin(loaded.Buffer, b, b.Header.RawLength);
                    var data = _inflater.Data;
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
                    _inflater.End();
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
            var loaded = Load(files[i]);
            if (loaded is null) continue;
            foreach (var b in loaded.Blocks)
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
            var loaded = Load(f);
            if (loaded is null) continue;
            var best = long.MaxValue;
            foreach (var b in loaded.Blocks)
                if (b.Header.Type == level && b.Header.Start < best) best = b.Header.Start;
            if (best != long.MaxValue) return best;
        }
        return long.MaxValue;
    }

    private void DecodeBlock<TV>(byte[] file, in BlockRef b, long from, long to, ReadOnlySpan<int> slots, ref TV visitor) where TV : IRecVisitor
    {
        var h = b.Header;
        _inflater.Begin(file, b, ColumnTable.MaxSize(h.Columns));
        var dataStart = _table.Parse(_inflater.Data);
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
        _inflater.Fill(need);
        var data = _inflater.Data;
        if (data.Length < need) throw new InvalidDataException("short payload");
        _ = dataStart;
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

    private Loaded? Load(HistFile f)
    {
        foreach (var c in _cache)
            if (c.File.Path == f.Path) return c;

        byte[]? buffer = null;
        try
        {
            using var fs = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            var len = fs.Length;
            if (len > int.MaxValue / 2) throw new IOException("file too large");
            buffer = ArrayPool<byte>.Shared.Rent((int)Math.Max(len, 1));
            var read = 0;
            while (read < len)
            {
                var n = fs.Read(buffer, read, (int)len - read);
                if (n <= 0) break;
                read += n;
            }
            var loaded = new Loaded { File = f with { Length = read }, Buffer = buffer, Length = read };
            var bad = BlockScanner.Scan(buffer.AsSpan(0, read), loaded.Blocks);
            if (bad > 0) warnOnce(f.Path + "#scan" + read, $"history: {bad} corrupted region(s) skipped in {Path.GetFileName(f.Path)}");
            if (_cache.Count >= MaxCached)
            {
                ArrayPool<byte>.Shared.Return(_cache[0].Buffer);
                _cache.RemoveAt(0);
            }
            _cache.Add(loaded);
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
            warnOnce(f.Path, $"history: cannot read {Path.GetFileName(f.Path)}: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        _inflater.End();
        foreach (var c in _cache) ArrayPool<byte>.Shared.Return(c.Buffer);
        _cache.Clear();
    }
}
