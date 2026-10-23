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
