using System.Buffers.Binary;
using System.IO.Compression;
using BetterVitals.Core.Sensors;

namespace BetterVitals.History.Storage;

/// <summary>
/// Fixed 36-byte block header (little endian):
/// <code>
///  0 u32 magic "BVHB"     4 u8 version (raw 1, rollups 1 or 2)     5 u8 type (0 raw, 1 minute, 2 hour)
///  6 u16 sampleCount      8 i64 start (UTC ticks of row 0)
/// 16 u32 step (ms)       20 u16 columnCount  22 u16 flags (0)
/// 24 u32 rawLength       28 u32 payloadLength (Deflate bytes that follow)
/// 32 u32 crc32 over bytes 0..32 and the payload
/// </code>
/// </summary>
internal readonly struct BlockHeader
{
    public const int Size = 36;
    public const uint Magic = 0x42485642; // "BVHB"
    /// <summary>Raw blocks are written in version 1.</summary>
    public const byte RawVersion = 1;

    /// <summary>Rollup blocks are written in version 2 (see <see cref="BlockEncoder.EncodeRollup"/>); version 1 is still read.</summary>
    public const byte RollupVersion = 2;

    private const int MaxPayload = 16 << 20;

    public Level Type { get; init; }

    /// <summary>Payload encoding version; 0 (not set) is written as the current version of <see cref="Type"/>.</summary>
    public byte Version { get; init; }
    public int SampleCount { get; init; }
    public long Start { get; init; }
    public long Step { get; init; }
    public int Columns { get; init; }
    public int RawLength { get; init; }
    public int PayloadLength { get; init; }

    public long End => Start + SampleCount * Step;

    public void Write(Span<byte> dst)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dst, Magic);
        dst[4] = Version != 0 ? Version : Type == Level.Raw ? RawVersion : RollupVersion;
        dst[5] = (byte)Type;
        BinaryPrimitives.WriteUInt16LittleEndian(dst[6..], (ushort)SampleCount);
        BinaryPrimitives.WriteInt64LittleEndian(dst[8..], Start);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[16..], (uint)(Step / TimeSpan.TicksPerMillisecond));
        BinaryPrimitives.WriteUInt16LittleEndian(dst[20..], (ushort)Columns);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[22..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[24..], (uint)RawLength);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[28..], (uint)PayloadLength);
    }

    /// <summary>Parses and sanity-checks a header (CRC is checked separately).</summary>
    public static bool TryRead(ReadOnlySpan<byte> src, out BlockHeader h)
    {
        h = default;
        if (src.Length < Size || BinaryPrimitives.ReadUInt32LittleEndian(src) != Magic) return false;
        var version = src[4];
        if (src[5] > (byte)Level.Hour || !(version == 1 || (version == RollupVersion && src[5] != (byte)Level.Raw))) return false;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(src[6..]);
        var stepMs = BinaryPrimitives.ReadUInt32LittleEndian(src[16..]);
        var raw = BinaryPrimitives.ReadUInt32LittleEndian(src[24..]);
        var payload = BinaryPrimitives.ReadUInt32LittleEndian(src[28..]);
        var start = BinaryPrimitives.ReadInt64LittleEndian(src[8..]);
        if (count == 0 || stepMs == 0 || payload > MaxPayload || raw > 4 * MaxPayload) return false;
        if (start < 0 || start > DateTime.MaxValue.Ticks - (long)count * stepMs * TimeSpan.TicksPerMillisecond) return false;
        h = new BlockHeader
        {
            Type = (Level)src[5],
            Version = version,
            SampleCount = count,
            Start = start,
            Step = stepMs * TimeSpan.TicksPerMillisecond,
            Columns = BinaryPrimitives.ReadUInt16LittleEndian(src[20..]),
            RawLength = (int)raw,
            PayloadLength = (int)payload,
        };
        return true;
    }

    public static uint StoredCrc(ReadOnlySpan<byte> src) => BinaryPrimitives.ReadUInt32LittleEndian(src[32..]);
}

/// <summary>A verified block inside a file buffer.</summary>
internal readonly record struct BlockRef(int PayloadOffset, BlockHeader Header);

/// <summary>Finds valid blocks in a file image; skips torn / corrupted regions by resyncing on the magic.</summary>
internal static class BlockScanner
{
    private static ReadOnlySpan<byte> MagicBytes => "BVHB"u8;

    /// <summary>Appends every CRC-valid block; returns the number of corrupted regions skipped.</summary>
    public static int Scan(ReadOnlySpan<byte> file, List<BlockRef> blocks)
    {
        var bad = 0;
        var pos = 0;
        while (pos + BlockHeader.Size <= file.Length)
        {
            if (BlockHeader.TryRead(file[pos..], out var h)
                && pos + BlockHeader.Size + (long)h.PayloadLength <= file.Length
                && Verify(file.Slice(pos, BlockHeader.Size + h.PayloadLength)))
            {
                blocks.Add(new BlockRef(pos + BlockHeader.Size, h));
                pos += BlockHeader.Size + h.PayloadLength;
                continue;
            }
            bad++;
            var next = file[(pos + 1)..].IndexOf(MagicBytes);
            if (next < 0) break;
            pos += 1 + next;
        }
        return bad;
    }

    private static bool Verify(ReadOnlySpan<byte> block)
    {
        var crc = Crc32.Append(Crc32.Compute(block[..32]), block[BlockHeader.Size..]);
        return crc == BlockHeader.StoredCrc(block);
    }

    /// <summary>Builds header + CRC in front of an already compressed payload.</summary>
    public static void Seal(Span<byte> block, in BlockHeader h)
    {
        h.Write(block);
        var crc = Crc32.Append(Crc32.Compute(block[..32]), block.Slice(BlockHeader.Size, h.PayloadLength));
        BinaryPrimitives.WriteUInt32LittleEndian(block[32..], crc);
    }
}

/// <summary>Column table at the start of every (uncompressed) payload: count, then per column slot delta, kind, byte length.</summary>
internal sealed class ColumnTable
{
    public int Count;
    public ushort[] Slots = new ushort[64];
    public SensorKind[] Kinds = new SensorKind[64];

    /// <summary>Raw columns: 0 = delta coding, 1 = offset from the first value.</summary>
    public byte[] Modes = new byte[64];
    public int[] Offsets = new int[64];
    public int[] Lengths = new int[64];

    /// <summary>Upper bound of the table size for <paramref name="columns"/> columns.</summary>
    public static int MaxSize(int columns) => 5 + columns * 9;

    /// <summary>Parses the table; returns the payload offset where column data starts.</summary>
    public int Parse(ReadOnlySpan<byte> payload)
    {
        var pos = 0;
        var n = (int)Varint.Read(payload, ref pos);
        if (n > ushort.MaxValue) throw new InvalidDataException("bad column count");
        if (Slots.Length < n)
        {
            var size = Math.Max(n, Slots.Length * 2);
            Slots = new ushort[size];
            Kinds = new SensorKind[size];
            Modes = new byte[size];
            Offsets = new int[size];
            Lengths = new int[size];
        }
        var slot = 0L;
        for (var i = 0; i < n; i++)
        {
            slot += (long)Varint.Read(payload, ref pos);
            if (slot > ushort.MaxValue || (uint)pos >= (uint)payload.Length) throw new InvalidDataException("bad slot");
            Slots[i] = (ushort)slot;
            var kb = payload[pos++];
            Kinds[i] = (SensorKind)(kb & 0x7F);
            Modes[i] = (byte)(kb >> 7);
            Lengths[i] = (int)Varint.Read(payload, ref pos);
        }
        Count = n;
        var off = pos;
        for (var i = 0; i < n; i++)
        {
            Offsets[i] = off;
            off += Lengths[i];
        }
        return pos;
    }

    public int IndexOf(int slot)
    {
        // columns are sorted by slot
        var lo = 0;
        var hi = Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var s = Slots[mid];
            if (s == slot) return mid;
            if (s < slot) lo = mid + 1;
            else hi = mid - 1;
        }
        return -1;
    }

    public int End(int index) => Offsets[index] + Lengths[index];
}

/// <summary>
/// Encodes payloads (column table + columns) and compresses them into full blocks. Buffers grow to what the data
/// needs (not to the worst-case varint size), and the table and the column data are compressed straight from their
/// own buffers, so no assembled copy of the payload is kept.
/// </summary>
internal sealed class BlockEncoder
{
    // measured on raw minute blocks: level 7 is as small as level 6 (Optimal) or slightly smaller and mostly faster
    private static readonly ZLibCompressionOptions RawCompression = new() { CompressionLevel = 7 };

    private byte[] _data = new byte[4096];
    private byte[] _table = new byte[256];
    private int _dataLength;
    private int _tableLength;
    private readonly MemoryStream _compressed = new();
    private int[] _lengths = new int[128];
    private byte[] _modes = new byte[128];

    private void EnsureColumns(int cols)
    {
        if (_lengths.Length >= cols) return;
        var size = Math.Max(cols, _lengths.Length * 2);
        _lengths = new int[size];
        _modes = new byte[size];
    }

    /// <summary>
    /// Raw block: per column, <paramref name="rows"/> tokens: 0 = gap, else zigzag(q - reference) + 1.
    /// Mode 0 (delta): reference = previous value (starts at 0, carries across gaps) — best for trends.
    /// Mode 1 (offset): reference = the column's first value in the block (the first itself vs 0) — best for jitter around
    /// a level (voltages, fans, clocks). The encoder picks the mode with the lower estimated cost per column.
    /// <paramref name="values"/> is column-major with <paramref name="stride"/>. Returns the payload length.
    /// </summary>
    public int EncodeRaw(ReadOnlySpan<ushort> slots, ReadOnlySpan<SensorKind> kinds, ReadOnlySpan<float> values, int stride, int rows)
    {
        var cols = slots.Length;
        EnsureColumns(cols);
        var qs = rows <= 1024 ? stackalloc long[rows] : new long[rows];
        var scratch = rows <= 1024 ? stackalloc long[rows] : new long[rows];
        var pos = 0;
        for (var c = 0; c < cols; c++)
        {
            EnsureRoom(ref _data, pos, rows * Varint.MaxBytes);
            var start = pos;
            var quantum = Quant.Of(kinds[c]);
            var col = values.Slice(c * stride, rows);
            var n = 0;
            for (var r = 0; r < rows; r++)
                if (Quant.TryQuantize(col[r], quantum, out var q)) qs[n++] = q;
            var mode = n > 2 && Cost(qs[..n], 1, scratch) < Cost(qs[..n], 0, scratch) ? (byte)1 : (byte)0;
            _modes[c] = mode;

            long reference = 0;
            var first = true;
            for (var r = 0; r < rows; r++)
            {
                if (Quant.TryQuantize(col[r], quantum, out var q))
                {
                    Varint.Write(_data, ref pos, Varint.ZigZag(q - reference) + 1);
                    if (mode == 0 || first) reference = q;
                    first = false;
                }
                else
                {
                    _data[pos++] = 0;
                }
            }
            _lengths[c] = pos - start;
        }
        return Assemble(slots, kinds, pos);
    }

    /// <summary>Estimated bits of the tokens after the first: order-0 entropy plus the extra bytes of long varints.</summary>
    private static double Cost(ReadOnlySpan<long> qs, int mode, Span<long> scratch)
    {
        var n = qs.Length - 1;
        var d = scratch[..n];
        double extra = 0;
        for (var i = 0; i < n; i++)
        {
            d[i] = qs[i + 1] - (mode == 0 ? qs[i] : qs[0]);
            var z = Varint.ZigZag(d[i]) + 1;
            while (z >= 0x80)
            {
                extra += 8;
                z >>= 7;
            }
        }
        d.Sort();
        double bits = 0;
        var run = 1;
        for (var i = 1; i <= n; i++)
        {
            if (i < n && d[i] == d[i - 1])
            {
                run++;
                continue;
            }
            bits -= run * Math.Log2((double)run / n);
            run = 1;
        }
        return bits + extra;
    }

    /// <summary>
    /// Rollup block (version 2): per column four varint streams (homogeneous streams compress better than interleaved
    /// fields):
    /// <list type="number">
    /// <item>counts: 0 then c when every bucket holds c samples (the usual case), else 1 then the count of every bucket (0 = empty);</item>
    /// <item>for the non-empty buckets zigzag(min - previous min);</item>
    /// <item>zigzag(range - previous range), range = max - min;</item>
    /// <item>zigzag(2 × (sum - min × count) - range × count): the sum as its distance from the bucket's mid-range.</item>
    /// </list>
    /// Exact like version 1 (see <see cref="WriteRollupV1"/>) and 8–18 % smaller after Deflate (measured).
    /// <paramref name="aggs"/> is column-major with <paramref name="buckets"/> per column. Returns the payload length.
    /// </summary>
    public int EncodeRollup(ReadOnlySpan<ushort> slots, ReadOnlySpan<SensorKind> kinds, ReadOnlySpan<Rec> aggs, int buckets)
    {
        var cols = slots.Length;
        EnsureColumns(cols);
        var pos = 0;
        for (var c = 0; c < cols; c++)
        {
            EnsureRoom(ref _data, pos, 2 * Varint.MaxBytes + buckets * 4 * Varint.MaxBytes);
            var start = pos;
            _modes[c] = 0;
            var col = aggs.Slice(c * buckets, buckets);
            var same = true;
            foreach (ref readonly var a in col) same &= a.Count == col[0].Count;
            if (same)
            {
                Varint.Write(_data, ref pos, 0);
                Varint.Write(_data, ref pos, (ulong)col[0].Count);
            }
            else
            {
                Varint.Write(_data, ref pos, 1);
                foreach (ref readonly var a in col) Varint.Write(_data, ref pos, (ulong)a.Count);
            }
            long prevMin = 0;
            foreach (ref readonly var a in col)
            {
                if (a.Count == 0) continue;
                Varint.Write(_data, ref pos, Varint.ZigZag(a.Min - prevMin));
                prevMin = a.Min;
            }
            long prevRange = 0;
            foreach (ref readonly var a in col)
            {
                if (a.Count == 0) continue;
                Varint.Write(_data, ref pos, Varint.ZigZag(a.Max - a.Min - prevRange));
                prevRange = a.Max - a.Min;
            }
            foreach (ref readonly var a in col)
                if (a.Count != 0) Varint.Write(_data, ref pos, Varint.ZigZag(2 * (a.Sum - a.Min * a.Count) - (a.Max - a.Min) * a.Count));
            _lengths[c] = pos - start;
        }
        return Assemble(slots, kinds, pos);
    }

    /// <summary>
    /// Version 1 rollup columns (what older builds wrote; kept for tests): count of every bucket, then for the non-empty
    /// buckets zigzag(min - previous min), max - min, sum - min × count.
    /// </summary>
    internal static int WriteRollupV1(byte[] dst, int pos, ReadOnlySpan<Rec> col)
    {
        foreach (ref readonly var a in col) Varint.Write(dst, ref pos, (ulong)a.Count);
        long prevMin = 0;
        foreach (ref readonly var a in col)
        {
            if (a.Count == 0) continue;
            Varint.Write(dst, ref pos, Varint.ZigZag(a.Min - prevMin));
            prevMin = a.Min;
        }
        foreach (ref readonly var a in col)
            if (a.Count != 0) Varint.Write(dst, ref pos, (ulong)(a.Max - a.Min));
        foreach (ref readonly var a in col)
            if (a.Count != 0) Varint.Write(dst, ref pos, (ulong)(a.Sum - a.Min * a.Count));
        return pos;
    }

    /// <summary>Test / diagnostics: a copy of the uncompressed payload of the last Encode call.</summary>
    public byte[] PayloadCopy()
    {
        var p = new byte[_tableLength + _dataLength];
        _table.AsSpan(0, _tableLength).CopyTo(p);
        _data.AsSpan(0, _dataLength).CopyTo(p.AsSpan(_tableLength));
        return p;
    }

    /// <summary>Compresses the last payload and appends header + data to <paramref name="output"/>; returns the block size.</summary>
    public int AppendBlock(Stream output, Level type, long start, long step, int samples, int columns, int rawLength)
    {
        if (rawLength != _tableLength + _dataLength) throw new InvalidOperationException("payload length mismatch");
        _compressed.SetLength(0);
        _compressed.Write(stackalloc byte[BlockHeader.Size]);
        using (var z = type == Level.Raw
                   ? new DeflateStream(_compressed, RawCompression, leaveOpen: true)
                   : new DeflateStream(_compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(_table, 0, _tableLength);
            z.Write(_data, 0, _dataLength);
        }
        var total = (int)_compressed.Length;
        var h = new BlockHeader
        {
            Type = type,
            SampleCount = samples,
            Start = start,
            Step = step,
            Columns = columns,
            RawLength = rawLength,
            PayloadLength = total - BlockHeader.Size,
        };
        var buf = _compressed.GetBuffer();
        BlockScanner.Seal(buf.AsSpan(0, total), h);
        output.Write(buf, 0, total);
        return total;
    }

    private int Assemble(ReadOnlySpan<ushort> slots, ReadOnlySpan<SensorKind> kinds, int dataLength)
    {
        var cols = slots.Length;
        if (_table.Length < ColumnTable.MaxSize(cols)) _table = new byte[ColumnTable.MaxSize(cols)];
        var pos = 0;
        Varint.Write(_table, ref pos, (ulong)cols);
        var prev = 0;
        for (var c = 0; c < cols; c++)
        {
            if (slots[c] < prev) throw new InvalidOperationException("columns must be sorted by slot");
            Varint.Write(_table, ref pos, (ulong)(slots[c] - prev));
            prev = slots[c];
            _table[pos++] = (byte)((byte)kinds[c] | _modes[c] << 7);
            Varint.Write(_table, ref pos, (ulong)_lengths[c]);
        }
        _tableLength = pos;
        _dataLength = dataLength;
        return pos + dataLength;
    }

    /// <summary>Makes room for <paramref name="need"/> more bytes after <paramref name="pos"/> (grows by half, keeps the data).</summary>
    private static void EnsureRoom(ref byte[] buf, int pos, int need)
    {
        if (buf.Length - pos >= need) return;
        var size = Math.Max(pos + need, buf.Length + buf.Length / 2);
        var grown = new byte[(size + 4095) & ~4095];
        buf.AsSpan(0, pos).CopyTo(grown);
        buf = grown;
    }
}

/// <summary>Decodes columns of an inflated payload into records.</summary>
internal static class ColumnDecoder
{
    /// <summary>Raw column → one record per stored sample with time in [from, to).</summary>
    public static void Raw<TV>(ReadOnlySpan<byte> col, byte mode, in BlockHeader h, long from, long to, int ordinal, ref TV visitor)
        where TV : IRecVisitor
    {
        var pos = 0;
        long reference = 0;
        var first = true;
        var t = h.Start;
        for (var r = 0; r < h.SampleCount; r++, t += h.Step)
        {
            var token = Varint.Read(col, ref pos);
            if (token == 0) continue;
            var q = reference + Varint.UnZigZag(token - 1);
            if (mode == 0 || first) reference = q;
            first = false;
            if (t < from || t >= to) continue;
            visitor.Visit(ordinal, Rec.Sample(t, q));
        }
    }

    /// <summary>Rollup column → one record per non-empty bucket with start in [from, to).</summary>
    public static void Rollup<TV>(ReadOnlySpan<byte> col, in BlockHeader h, long from, long to, int ordinal, ref TV visitor)
        where TV : IRecVisitor
    {
        if (h.Version >= 2) RollupV2(col, h, from, to, ordinal, ref visitor);
        else RollupV1(col, h, from, to, ordinal, ref visitor);
    }

    /// <summary>Version 2 (see <see cref="BlockEncoder.EncodeRollup"/>).</summary>
    private static void RollupV2<TV>(ReadOnlySpan<byte> col, in BlockHeader h, long from, long to, int ordinal, ref TV visitor)
        where TV : IRecVisitor
    {
        var pc = 0;
        var flag = Varint.Read(col, ref pc);
        long constant = -1;
        int filled;
        if (flag == 0)
        {
            constant = (long)Varint.Read(col, ref pc);
            if (constant < 0) throw new InvalidDataException("bad count");
            filled = constant == 0 ? 0 : h.SampleCount;
        }
        else if (flag == 1)
        {
            filled = 0;
            var p = pc;
            for (var b = 0; b < h.SampleCount; b++)
                if (Varint.Read(col, ref p) != 0) filled++;
        }
        else
        {
            throw new InvalidDataException("bad count stream");
        }
        var pm = pc;
        if (constant < 0) Varint.Skip(col, ref pm, h.SampleCount);
        var pr = pm;
        Varint.Skip(col, ref pr, filled);
        var pe = pr;
        Varint.Skip(col, ref pe, filled);
        long prevMin = 0, prevRange = 0;
        var t = h.Start;
        for (var b = 0; b < h.SampleCount; b++, t += h.Step)
        {
            var count = constant >= 0 ? constant : (long)Varint.Read(col, ref pc);
            if (count == 0) continue;
            var min = prevMin + Varint.UnZigZag(Varint.Read(col, ref pm));
            var range = prevRange + Varint.UnZigZag(Varint.Read(col, ref pr));
            var twice = Varint.UnZigZag(Varint.Read(col, ref pe)) + range * count;
            if (range < 0 || (twice & 1) != 0) throw new InvalidDataException("bad rollup value");
            prevMin = min;
            prevRange = range;
            if (t < from || t >= to) continue;
            visitor.Visit(ordinal, new Rec { Time = t, Min = min, Max = min + range, Sum = (twice >> 1) + min * count, Count = count, Level = h.Type });
        }
    }

    /// <summary>Version 1: counts of all buckets, then for the non-empty ones min deltas, ranges, sum extras.</summary>
    private static void RollupV1<TV>(ReadOnlySpan<byte> col, in BlockHeader h, long from, long to, int ordinal, ref TV visitor)
        where TV : IRecVisitor
    {
        var pc = 0;
        var filled = 0;
        for (var b = 0; b < h.SampleCount; b++)
            if (Varint.Read(col, ref pc) != 0) filled++;
        var pm = pc;
        var pr = pm;
        Varint.Skip(col, ref pr, filled);
        var pe = pr;
        Varint.Skip(col, ref pe, filled);
        pc = 0;
        long prevMin = 0;
        var t = h.Start;
        for (var b = 0; b < h.SampleCount; b++, t += h.Step)
        {
            var count = (long)Varint.Read(col, ref pc);
            if (count == 0) continue;
            var min = prevMin + Varint.UnZigZag(Varint.Read(col, ref pm));
            var range = (long)Varint.Read(col, ref pr);
            var extra = (long)Varint.Read(col, ref pe);
            prevMin = min;
            if (t < from || t >= to) continue;
            visitor.Visit(ordinal, new Rec { Time = t, Min = min, Max = min + range, Sum = extra + min * count, Count = count, Level = h.Type });
        }
    }
}

/// <summary>Inflates (a prefix of) a payload into a reusable buffer.</summary>
internal sealed class Inflater
{
    private byte[] _out = new byte[16 * 1024];
    private DeflateStream? _z;
    private int _have;
    private int _target;

    public ReadOnlySpan<byte> Data => _out.AsSpan(0, _have);

    /// <summary>Starts a block; inflates at least <paramref name="need"/> bytes (or all when shorter).</summary>
    public void Begin(byte[] file, in BlockRef block, int need) => Begin(file, block.PayloadOffset, block.Header.PayloadLength, block.Header.RawLength, need);

    /// <summary>Starts a payload of <paramref name="length"/> compressed bytes at <paramref name="offset"/> of <paramref name="buffer"/>.</summary>
    public void Begin(byte[] buffer, int offset, int length, int rawLength, int need)
    {
        _z?.Dispose();
        _target = rawLength;
        if (_out.Length < _target) _out = new byte[Math.Max(_target, _out.Length * 2)];
        _z = new DeflateStream(new MemoryStream(buffer, offset, length, writable: false), CompressionMode.Decompress);
        _have = 0;
        Fill(need);
    }

    /// <summary>Ensures at least <paramref name="need"/> bytes are inflated (clamped to the payload's raw length).</summary>
    public void Fill(int need)
    {
        if (need > _target) need = _target;
        while (_have < need && _z is not null)
        {
            var n = _z.Read(_out, _have, need - _have);
            if (n <= 0) throw new InvalidDataException("payload shorter than its header says");
            _have += n;
        }
    }

    public void End()
    {
        _z?.Dispose();
        _z = null;
    }
}
