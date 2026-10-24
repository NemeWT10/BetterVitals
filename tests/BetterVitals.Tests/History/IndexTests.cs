using BetterVitals.Core.Sensors;
using BetterVitals.History.Storage;
using Xunit;

namespace BetterVitals.Tests.History;

/// <summary>The streamed block index finds exactly what the whole-file scan finds, and reads stay small.</summary>
public class IndexTests
{
    private static byte[] Blocks(int count, int seed, long firstMinute = 0)
    {
        var enc = new BlockEncoder();
        var ms = new MemoryStream();
        var rnd = new Random(seed);
        ushort[] slots = [0, 1, 2];
        SensorKind[] kinds = [SensorKind.Load, SensorKind.Temperature, SensorKind.Throughput];
        for (var k = 0; k < count; k++)
        {
            var rows = rnd.Next(1, 61);
            var values = new float[slots.Length * rows];
            for (var i = 0; i < values.Length; i++) values[i] = rnd.Next(4) == 0 ? float.NaN : (float)(rnd.NextDouble() * 1000);
            var len = enc.EncodeRaw(slots, kinds, values, rows, rows);
            enc.AppendBlock(ms, Level.Raw, (firstMinute + k) * TimeSpan.TicksPerMinute, TimeSpan.TicksPerSecond, rows, slots.Length, len);
        }
        return ms.ToArray();
    }

    private static byte[] Damage(byte[] file, Random rnd)
    {
        var f = file.ToList();
        switch (rnd.Next(8))
        {
            case 0:
                break;
            case 1: // bit rot
                for (var i = rnd.Next(1, 4); i > 0 && f.Count > 0; i--) f[rnd.Next(f.Count)] ^= (byte)rnd.Next(1, 256);
                break;
            case 2: // truncated
                f = f.Take(rnd.Next(f.Count + 1)).ToList();
                break;
            case 3: // torn block, then later blocks appended after the garbage
                f = f.Take(rnd.Next(f.Count + 1)).ToList();
                f.AddRange(Blocks(rnd.Next(1, 4), rnd.Next(), 100));
                break;
            case 4: // garbage with magic fragments inserted
                var junk = new byte[rnd.Next(1, 200)];
                rnd.NextBytes(junk);
                if (junk.Length > 8) "BVHB"u8.CopyTo(junk.AsSpan(rnd.Next(junk.Length - 4)));
                if (junk.Length > 8) "BVH"u8.CopyTo(junk.AsSpan(junk.Length - 3));
                f.InsertRange(rnd.Next(f.Count + 1), junk);
                break;
            case 5: // nothing usable
                f = [.. "BVHB"u8.ToArray(), .. new byte[rnd.Next(0, 80)]];
                break;
            case 6: // a header claims a huge payload
                if (f.Count > 40) f[28 + 2] = 0xF0;
                break;
            default: // magic split across a chunk border right before a good block
                f.InsertRange(0, "xxBVH"u8.ToArray());
                break;
        }
        return [.. f];
    }

    private static List<(int, long, int, int)> Keys(List<BlockRef> blocks)
        => blocks.Select(b => (b.PayloadOffset, b.Header.Start, b.Header.PayloadLength, b.Header.SampleCount)).ToList();

    [Fact]
    public void Streamed_scan_finds_what_the_whole_file_scan_finds()
    {
        using var dir = new TestDir("index-scan");
        Directory.CreateDirectory(dir.Path);
        var rnd = new Random(7);
        var index = new BlockIndex();
        for (var iter = 0; iter < 400; iter++)
        {
            var file = Damage(Blocks(rnd.Next(1, 10), iter), rnd);
            var path = Path.Combine(dir.Path, $"f{iter}.bvr");
            File.WriteAllBytes(path, file);
            var expected = new List<BlockRef>();
            var bad = BlockScanner.Scan(file, expected);
            foreach (var chunk in new[] { 37, 101, 4096, FileReader.DefaultChunk })
            {
                using var rd = FileReader.Open(index, path, chunk);
                var got = new List<BlockRef>();
                var bad2 = BlockIndex.Scan(rd, 0, rd.Length, got, out _);
                Assert.Equal(Keys(expected), Keys(got));
                Assert.Equal(bad, bad2);
            }
        }
    }

    [Fact]
    public void Grown_files_are_continued_damaged_or_recreated_ones_rescanned()
    {
        using var dir = new TestDir("index-grow");
        Directory.CreateDirectory(dir.Path);
        var path = Path.Combine(dir.Path, "raw-20261001.bvr");
        var warnings = new List<string>();
        var index = new BlockIndex();

        List<BlockRef> Indexed()
        {
            using var rd = FileReader.Open(index, path, 101);
            return index.Get(path, rd, (_, m) => warnings.Add(m)).Blocks.ToList();
        }

        List<BlockRef> Truth()
        {
            var l = new List<BlockRef>();
            BlockScanner.Scan(File.ReadAllBytes(path), l);
            return l;
        }

        File.WriteAllBytes(path, Blocks(5, 1));
        Assert.Equal(Keys(Truth()), Keys(Indexed()));
        Assert.Equal(5, Indexed().Count);

        // appended (what the writer does every minute): the clean index is continued
        using (var fs = new FileStream(path, FileMode.Append)) fs.Write(Blocks(3, 2, 5));
        var read = index.BytesRead;
        Assert.Equal(Keys(Truth()), Keys(Indexed()));
        Assert.True(index.BytesRead - read < new FileInfo(path).Length / 2, "only the new blocks are read");

        // a torn write, then good blocks after it: rescanned from the start, the same as the whole-file scan
        using (var fs = new FileStream(path, FileMode.Append)) fs.Write(Blocks(1, 3, 8).AsSpan(0, 50));
        Assert.Equal(Keys(Truth()), Keys(Indexed()));
        using (var fs = new FileStream(path, FileMode.Append)) fs.Write(Blocks(2, 4, 9));
        Assert.Equal(Keys(Truth()), Keys(Indexed()));
        Assert.Equal(10, Indexed().Count);
        Assert.NotEmpty(warnings);

        // deleted and written again with other (longer) content: a new file, not a grown one
        File.Delete(path);
        File.WriteAllBytes(path, Blocks(12, 5, 50));
        Assert.Equal(Keys(Truth()), Keys(Indexed()));
        Assert.Equal(12, Indexed().Count);

        // idle: everything is dropped
        Assert.True(index.CachedFiles > 0);
        Assert.False(index.TrimIfIdle(60_000));
        Assert.True(index.TrimIfIdle(0));
        Assert.Equal(0, index.CachedFiles);
    }

    [Fact]
    public void Only_the_most_recently_used_files_are_kept()
    {
        using var dir = new TestDir("index-lru");
        Directory.CreateDirectory(dir.Path);
        var index = new BlockIndex();
        var paths = Enumerable.Range(0, BlockIndex.MaxFiles + 5).Select(i => Path.Combine(dir.Path, $"f{i}.bvr")).ToList();
        foreach (var p in paths) File.WriteAllBytes(p, Blocks(2, p.Length));
        foreach (var p in paths)
        {
            using var rd = FileReader.Open(index, p);
            Assert.Equal(2, index.Get(p, rd, (_, _) => { }).Blocks.Count);
        }
        Assert.Equal(BlockIndex.MaxFiles, index.CachedFiles);
        var before = index.BytesRead;
        using (var rd = FileReader.Open(index, paths[^1])) index.Get(paths[^1], rd, (_, _) => { });
        Assert.Equal(before, index.BytesRead); // the newest is still cached
    }

    [Fact]
    public async Task Queries_read_only_the_blocks_they_need()
    {
        using var rig = new Rig("index-query", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        var kinds = Signals.TypicalMix(30);
        var sensors = kinds.Select((k, i) => rig.Hub.Add($"S{i}", k)).ToList();
        var svc = rig.Open();
        svc.Configure(Rig.Settings(true, 1, sensors));
        var end = rig.Feed(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), 6 * 3600, 1, (i, t) => Signals.Slow(kinds[i], i, t));
        svc.Flush();
        var to = new DateTime(end, DateTimeKind.Utc);
        var rawBytes = Directory.GetFiles(rig.Dir.Path, "raw-*").Sum(f => new FileInfo(f).Length);
        var index = svc.Store.Index;

        var first = await svc.GetSeriesAsync(sensors[0].Id, to.AddMinutes(-15), to, 1000);
        Assert.NotEmpty(first);
        var before = index.BytesRead;
        var again = await svc.GetSeriesAsync(sensors[3].Id, to.AddMinutes(-15), to, 1000);
        Assert.NotEmpty(again);
        // 15 of 360 raw blocks (plus the read window); the first query indexed the file once
        Assert.True(index.BytesRead - before < rawBytes / 5, $"{index.BytesRead - before} of {rawBytes} B");

        // a flush appends; the next query continues the index instead of rescanning
        rig.Feed(to, 120, 1, (i, t) => Signals.Slow(kinds[i], i, t));
        svc.Flush();
        before = index.BytesRead;
        var later = await svc.GetSeriesAsync(sensors[3].Id, to.AddMinutes(-15), to.AddMinutes(2), 2000);
        Assert.True(later.Count > again.Count);
        Assert.True(index.BytesRead - before < rawBytes / 5, $"{index.BytesRead - before} of {rawBytes} B");
        Assert.Empty(rig.Warnings);
    }
}
