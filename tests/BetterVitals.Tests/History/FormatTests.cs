using System.IO.Compression;
using BetterVitals.Core.Sensors;
using BetterVitals.History.Storage;
using Xunit;

namespace BetterVitals.Tests.History;

public class FormatTests
{
    private sealed class Collect : IRecVisitor
    {
        public readonly List<(int O, Rec R)> Items = [];

        public void Visit(int ordinal, in Rec rec) => Items.Add((ordinal, rec));
    }

    [Fact]
    public void Varint_and_zigzag_round_trip()
    {
        long[] values = [0, 1, -1, 63, -64, 64, 127, 128, -129, 1 << 20, -(1L << 40), 1L << 40, long.MaxValue / 4, long.MinValue / 4];
        var buf = new byte[Varint.MaxBytes * values.Length];
        var pos = 0;
        foreach (var v in values) Varint.Write(buf, ref pos, Varint.ZigZag(v));
        var read = 0;
        foreach (var v in values) Assert.Equal(v, Varint.UnZigZag(Varint.Read(buf.AsSpan(0, pos), ref read)));
        Assert.Equal(pos, read);
        Assert.Throws<InvalidDataException>(() =>
        {
            var p = 0;
            Varint.Read([0x80, 0x80], ref p);
        });
    }

    [Fact]
    public void Crc32_matches_reference()
    {
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
        var data = new byte[1000];
        new Random(5).NextBytes(data);
        Assert.Equal(Crc32.Compute(data), Crc32.Append(Crc32.Compute(data.AsSpan(0, 333)), data.AsSpan(333)));
        // bitwise reference
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        Assert.Equal(~crc, Crc32.Compute(data));
    }

    public static IEnumerable<object[]> AllKinds() => Enum.GetValues<SensorKind>().Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Raw_block_round_trips_at_every_quantum(SensorKind kind)
    {
        var quantum = Quant.Of(kind);
        const int rows = 60;
        var rnd = new Random((int)kind + 1);
        var slots = new ushort[] { 0, 3, 7, 300 };
        var kinds = new[] { kind, kind, kind, kind };
        var values = new float[slots.Length * rows];
        for (var c = 0; c < slots.Length; c++)
        for (var r = 0; r < rows; r++)
        {
            float v = c switch
            {
                0 => (float)(rnd.NextDouble() * 1000 * quantum * 1000), // big jumps
                1 => (float)(50 + r * quantum * 0.6), // sub-quantum steps (trend → delta mode)
                2 => r % 7 == 3 ? float.NaN : (float)(-rnd.NextDouble() * 100), // negatives and gaps
                _ => r == 0 ? float.NaN : (float)(1000 * quantum + rnd.Next(3) * quantum), // jitter (→ offset mode), leading gap
            };
            if (c == 0 && r == 10) v = float.PositiveInfinity;
            values[c * rows + r] = v;
        }

        var enc = new BlockEncoder();
        var len = enc.EncodeRaw(slots, kinds, values, rows, rows);
        var ms = new MemoryStream();
        var start = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc).Ticks;
        enc.AppendBlock(ms, Level.Raw, start, TimeSpan.TicksPerSecond, rows, slots.Length, len);
        var file = ms.ToArray();

        var blocks = new List<BlockRef>();
        Assert.Equal(0, BlockScanner.Scan(file, blocks));
        var b = Assert.Single(blocks);
        Assert.Equal(start, b.Header.Start);
        Assert.Equal(rows, b.Header.SampleCount);

        var inf = new Inflater();
        inf.Begin(file, b, b.Header.RawLength);
        var table = new ColumnTable();
        table.Parse(inf.Data);
        Assert.Equal(slots.Length, table.Count);
        Assert.Equal(0, table.Modes[1]);
        Assert.Equal(1, table.Modes[3]);
        for (var c = 0; c < slots.Length; c++)
        {
            Assert.Equal(slots[c], table.Slots[c]);
            Assert.Equal(kind, table.Kinds[c]);
            var v = new Collect();
            ColumnDecoder.Raw(inf.Data.Slice(table.Offsets[c], table.Lengths[c]), table.Modes[c], b.Header, long.MinValue, long.MaxValue, c, ref v);
            var expected = Enumerable.Range(0, rows)
                .Select(r => (r, ok: Quant.TryQuantize(values[c * rows + r], quantum, out var q), q))
                .Where(x => x.ok).ToList();
            Assert.Equal(expected.Count, v.Items.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(start + expected[i].r * TimeSpan.TicksPerSecond, v.Items[i].R.Time);
                Assert.Equal(expected[i].q, v.Items[i].R.Min);
                // decoded value is within half a quantum of the input
                var orig = values[c * rows + expected[i].r];
                var back = Quant.ToFloat(v.Items[i].R.Min, quantum);
                Assert.True(Math.Abs(orig - back) <= quantum * 0.5 + Math.Abs(orig) * 1e-6, $"{kind}: {orig} -> {back}");
            }
        }
        inf.End();
    }

    [Fact]
    public void Rollup_block_round_trips()
    {
        var slots = new ushort[] { 1, 2 };
        var kinds = new[] { SensorKind.Temperature, SensorKind.Throughput };
        const int buckets = 60;
        var aggs = new Rec[slots.Length * buckets];
        var rnd = new Random(3);
        for (var i = 0; i < aggs.Length; i++)
        {
            if (i % 9 == 4) continue; // empty bucket
            var min = rnd.NextInt64(-100_000, 100_000);
            var count = rnd.Next(1, 60);
            var max = min + rnd.Next(0, 5000);
            aggs[i] = new Rec { Min = min, Max = max, Count = count, Sum = min * count + rnd.Next(0, (int)(max - min) * count + 1) };
        }
        var enc = new BlockEncoder();
        var len = enc.EncodeRollup(slots, kinds, aggs, buckets);
        var ms = new MemoryStream();
        enc.AppendBlock(ms, Level.Minute, 0, TimeSpan.TicksPerMinute, buckets, slots.Length, len);
        var file = ms.ToArray();
        var blocks = new List<BlockRef>();
        BlockScanner.Scan(file, blocks);
        var b = Assert.Single(blocks);
        var inf = new Inflater();
        inf.Begin(file, b, b.Header.RawLength);
        var table = new ColumnTable();
        table.Parse(inf.Data);
        for (var c = 0; c < slots.Length; c++)
        {
            var v = new Collect();
            ColumnDecoder.Rollup(inf.Data.Slice(table.Offsets[c], table.Lengths[c]), b.Header, long.MinValue, long.MaxValue, c, ref v);
            var expected = Enumerable.Range(0, buckets).Where(k => aggs[c * buckets + k].Count > 0).ToList();
            Assert.Equal(expected.Count, v.Items.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                var e = aggs[c * buckets + expected[i]];
                var r = v.Items[i].R;
                Assert.Equal(expected[i] * TimeSpan.TicksPerMinute, r.Time);
                Assert.Equal((e.Min, e.Max, e.Sum, e.Count), (r.Min, r.Max, r.Sum, r.Count));
                Assert.Equal(Level.Minute, r.Level);
            }
        }
        inf.End();
    }

    /// <summary>Column shapes for the rollup encodings: constant counts, gaps, all empty, extreme values, a single bucket.</summary>
    private static Rec[] RollupColumns(int buckets, out ushort[] slots, out SensorKind[] kinds)
    {
        slots = [0, 4, 5, 9, 12, 40];
        kinds = [SensorKind.Temperature, SensorKind.Throughput, SensorKind.Load, SensorKind.Voltage, SensorKind.Clock, SensorKind.Fan];
        var aggs = new Rec[slots.Length * buckets];
        var rnd = new Random(11);
        for (var b = 0; b < buckets; b++)
        {
            // 0: every bucket full (constant count), slowly moving
            var m = 900 + b / 3;
            aggs[0 * buckets + b] = new Rec { Min = m, Max = m + 2, Count = 30, Sum = m * 30L + rnd.Next(0, 61) };
            // 1: random counts and gaps, wide ranges
            if (b % 5 != 2)
            {
                var min = rnd.NextInt64(-1_000_000, 1_000_000);
                var count = rnd.Next(1, 61);
                var max = min + rnd.Next(0, 100_000);
                aggs[1 * buckets + b] = new Rec { Min = min, Max = max, Count = count, Sum = min * count + rnd.NextInt64(0, (max - min) * count + 1) };
            }
            // 2: all empty
            // 3: extreme quantized values (|q| = 2^40) with the largest bucket counts of an hour rollup
            var lo = b % 2 == 0 ? -Quant.Limit : Quant.Limit - 7;
            aggs[3 * buckets + b] = new Rec { Min = lo, Max = b % 2 == 0 ? Quant.Limit : Quant.Limit, Count = 3600, Sum = b % 2 == 0 ? 0 : (Quant.Limit - 3) * 3600 };
            // 4: constant count, constant value
            aggs[4 * buckets + b] = new Rec { Min = 52000, Max = 52000, Count = 60, Sum = 52000 * 60L };
            // 5: only the last bucket
            if (b == buckets - 1) aggs[5 * buckets + b] = new Rec { Min = -5, Max = 7, Count = 3, Sum = 1 };
        }
        return aggs;
    }

    private static List<(int Col, Rec R)> DecodeAll(byte[] file, int columns)
    {
        var blocks = new List<BlockRef>();
        Assert.Equal(0, BlockScanner.Scan(file, blocks));
        var b = Assert.Single(blocks);
        var inf = new Inflater();
        inf.Begin(file, b, b.Header.RawLength);
        var table = new ColumnTable();
        table.Parse(inf.Data);
        Assert.Equal(columns, table.Count);
        var all = new List<(int, Rec)>();
        for (var c = 0; c < table.Count; c++)
        {
            var v = new Collect();
            ColumnDecoder.Rollup(inf.Data.Slice(table.Offsets[c], table.Lengths[c]), b.Header, long.MinValue, long.MaxValue, c, ref v);
            all.AddRange(v.Items);
        }
        inf.End();
        return all;
    }

    [Theory]
    [InlineData(60)]
    [InlineData(24)]
    [InlineData(1)]
    public void Rollup_version_2_decodes_exactly_like_version_1(int buckets)
    {
        var aggs = RollupColumns(buckets, out var slots, out var kinds);
        var start = new DateTime(2026, 10, 23, 5, 0, 0, DateTimeKind.Utc).Ticks;

        // version 2: what this build writes
        var enc = new BlockEncoder();
        var len = enc.EncodeRollup(slots, kinds, aggs, buckets);
        var ms = new MemoryStream();
        enc.AppendBlock(ms, Level.Minute, start, TimeSpan.TicksPerMinute, buckets, slots.Length, len);
        var v2 = ms.ToArray();
        Assert.Equal(2, v2[4]);

        // version 1: built by hand like older builds did
        var data = new byte[1 << 16];
        var table = new byte[ColumnTable.MaxSize(slots.Length)];
        var tp = 0;
        Varint.Write(table, ref tp, (ulong)slots.Length);
        var dp = 0;
        for (var c = 0; c < slots.Length; c++)
        {
            var end = BlockEncoder.WriteRollupV1(data, dp, aggs.AsSpan(c * buckets, buckets));
            Varint.Write(table, ref tp, (ulong)(slots[c] - (c == 0 ? 0 : slots[c - 1])));
            table[tp++] = (byte)kinds[c];
            Varint.Write(table, ref tp, (ulong)(end - dp));
            dp = end;
        }
        var payload = table.AsSpan(0, tp).ToArray().Concat(data.AsSpan(0, dp).ToArray()).ToArray();
        var z = new MemoryStream();
        z.Write(new byte[BlockHeader.Size]);
        using (var d = new DeflateStream(z, CompressionLevel.Optimal, leaveOpen: true)) d.Write(payload);
        var v1 = z.ToArray();
        BlockScanner.Seal(v1, new BlockHeader
        {
            Type = Level.Minute, Version = 1, SampleCount = buckets, Start = start, Step = TimeSpan.TicksPerMinute, Columns = slots.Length,
            RawLength = payload.Length, PayloadLength = v1.Length - BlockHeader.Size,
        });
        Assert.Equal(1, v1[4]);

        var a = DecodeAll(v1, slots.Length);
        var b = DecodeAll(v2, slots.Length);
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Col, b[i].Col);
            Assert.Equal((a[i].R.Time, a[i].R.Min, a[i].R.Max, a[i].R.Sum, a[i].R.Count, a[i].R.Level), (b[i].R.Time, b[i].R.Min, b[i].R.Max, b[i].R.Sum, b[i].R.Count, b[i].R.Level));
        }
        // and both equal the input
        var expected = Enumerable.Range(0, slots.Length * buckets).Where(i => aggs[i].Count > 0).Select(i => aggs[i]).ToList();
        Assert.Equal(expected.Select(e => (e.Min, e.Max, e.Sum, e.Count)), b.Select(x => (x.R.Min, x.R.Max, x.R.Sum, x.R.Count)));
    }

    [Fact]
    public void Raw_blocks_stay_version_1_and_unknown_versions_are_rejected()
    {
        var enc = new BlockEncoder();
        var len = enc.EncodeRaw([0], [SensorKind.Load], Enumerable.Range(0, 60).Select(i => (float)i).ToArray(), 60, 60);
        var ms = new MemoryStream();
        enc.AppendBlock(ms, Level.Raw, 0, TimeSpan.TicksPerSecond, 60, 1, len);
        var file = ms.ToArray();
        Assert.Equal(1, file[4]);
        Assert.True(BlockHeader.TryRead(file, out _));
        foreach (var (version, type) in new (byte, byte)[] { (2, 0), (3, 1), (0, 1), (255, 2) })
        {
            var x = (byte[])file.Clone();
            x[4] = version;
            x[5] = type;
            Assert.False(BlockHeader.TryRead(x, out _), $"v{version} type {type}");
        }
    }

    [Fact]
    public void Scanner_skips_torn_tail_and_corrupted_blocks()
    {
        var enc = new BlockEncoder();
        var ms = new MemoryStream();
        var slots = new ushort[] { 0 };
        var kinds = new[] { SensorKind.Load };
        var offsets = new List<long>();
        for (var k = 0; k < 5; k++)
        {
            var values = Enumerable.Range(0, 60).Select(i => (float)(k * 100 + i)).ToArray();
            var len = enc.EncodeRaw(slots, kinds, values, 60, 60);
            offsets.Add(ms.Length);
            enc.AppendBlock(ms, Level.Raw, k * TimeSpan.TicksPerMinute, TimeSpan.TicksPerSecond, 60, 1, len);
        }
        var file = ms.ToArray();

        // flip a payload byte of block #2 → CRC fails, the other four survive
        var bad = (byte[])file.Clone();
        bad[offsets[2] + BlockHeader.Size + 3] ^= 0x55;
        var blocks = new List<BlockRef>();
        Assert.True(BlockScanner.Scan(bad, blocks) > 0);
        Assert.Equal(new long[] { 0, 1, 3, 4 }, blocks.Select(b => b.Header.Start / TimeSpan.TicksPerMinute).ToArray());

        // torn last block (power loss mid-write), then a later good block appended after the garbage
        var torn = file.AsSpan(0, (int)offsets[4] + 20).ToArray();
        var after = new MemoryStream();
        after.Write(torn);
        var len2 = enc.EncodeRaw(slots, kinds, new float[60], 60, 60);
        enc.AppendBlock(after, Level.Raw, 9 * TimeSpan.TicksPerMinute, TimeSpan.TicksPerSecond, 60, 1, len2);
        blocks.Clear();
        BlockScanner.Scan(after.ToArray(), blocks);
        Assert.Equal(new long[] { 0, 1, 2, 3, 9 }, blocks.Select(b => b.Header.Start / TimeSpan.TicksPerMinute).ToArray());

        // garbage only
        blocks.Clear();
        BlockScanner.Scan(new byte[100], blocks);
        Assert.Empty(blocks);
    }

    [Fact]
    public void Catalog_round_trips_and_survives_torn_record()
    {
        using var dir = new TestDir("catalog");
        Directory.CreateDirectory(dir.Path);
        var hub = new FakeHub();
        var a = hub.Add("CPU Package", SensorKind.Temperature);
        var b = hub.Add("GPU Hot Spot", SensorKind.Temperature);
        var c1 = new Catalog();
        var ea = c1.GetOrAdd(a);
        var eb = c1.GetOrAdd(b);
        Assert.True(c1.HasUnpersisted);
        c1.Persist(dir.Path);
        Assert.False(c1.HasUnpersisted);

        var path = Path.Combine(dir.Path, Catalog.FileName);
        File.AppendAllText(path, "BVCE\u0010garbage"); // torn tail
        var c2 = new Catalog();
        Assert.Equal(1, c2.Load(dir.Path));
        Assert.Equal(ea.Slot, c2.Find(a.Id)!.Slot);
        Assert.Equal(eb.Slot, c2.Find(b.Id)!.Slot);
        Assert.Equal("GPU Hot Spot", c2.BySlot(eb.Slot)!.Name);
        Assert.Equal("°C", c2.BySlot(eb.Slot)!.Unit);
        // slots after a damaged region are never reused
        var c = hub.Add("New", SensorKind.Load);
        Assert.True(c2.GetOrAdd(c).Slot > eb.Slot + 1);
    }

    [Fact]
    public void File_names_and_periods()
    {
        var t = new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc).Ticks;
        Assert.Equal("raw-20261231.bvr", HistoryFiles.NameFor(Level.Raw, t));
        Assert.Equal("min-202612.bvm", HistoryFiles.NameFor(Level.Minute, t));
        Assert.Equal("hour-2026.bvh", HistoryFiles.NameFor(Level.Hour, t));
        Assert.Equal(new DateTime(2027, 1, 1).Ticks, HistoryFiles.Period(Level.Minute, t).End);
        Assert.Equal(new DateTime(2026, 12, 31).Ticks, HistoryFiles.Period(Level.Raw, t).Start);
    }
}
