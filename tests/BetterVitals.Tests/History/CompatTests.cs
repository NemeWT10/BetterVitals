using System.Text;
using Xunit;

namespace BetterVitals.Tests.History;

/// <summary>
/// Files written by older builds keep working: <c>Fixtures/v1</c> was recorded by the build before block format 2
/// (<see cref="Fixture"/>), together with its CSV exports. This build must answer every query on those files exactly like
/// on the files it writes itself from the same input, and export the same bytes.
/// </summary>
public class CompatTests
{
    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "History", "Fixtures", "v1");

    private static Rig OpenFixtureCopy(string name)
    {
        var rig = new Rig(name, Fixture.End);
        Directory.CreateDirectory(rig.Dir.Path);
        foreach (var f in Directory.GetFiles(FixtureDir, "*.bv?")) File.Copy(f, Path.Combine(rig.Dir.Path, Path.GetFileName(f)));
        Fixture.AddSensors(rig.Hub);
        rig.Open();
        return rig;
    }

    private static Rig WriteCurrent(string name)
    {
        var rig = new Rig(name, Fixture.Start);
        Fixture.Write(rig);
        rig.Service.Dispose();
        rig.Open(); // a fresh reader over the folder, recording off
        return rig;
    }

    [Fact]
    public void Raw_payloads_are_unchanged()
    {
        using var old = OpenFixtureCopy("compat-raw-v1");
        using var cur = WriteCurrent("compat-raw-cur");
        var a = Fixture.Payloads(old.Dir.Path);
        var b = Fixture.Payloads(cur.Dir.Path);
        Assert.Equal(a.Keys.Order(), b.Keys.Order());
        foreach (var name in a.Keys.Where(n => n.StartsWith("raw-", StringComparison.Ordinal)))
        {
            Assert.Equal(a[name].Count, b[name].Count);
            for (var i = 0; i < a[name].Count; i++) Assert.True(a[name][i].AsSpan().SequenceEqual(b[name][i]), $"{name} block {i}");
        }
        Assert.Equal(File.ReadAllBytes(Path.Combine(FixtureDir, "catalog.bvc")), File.ReadAllBytes(Path.Combine(cur.Dir.Path, "catalog.bvc")));
    }

    [Fact]
    public async Task Version1_files_answer_exactly_like_files_of_this_build()
    {
        using var old = OpenFixtureCopy("compat-q-v1");
        using var cur = WriteCurrent("compat-q-cur");
        var ids = old.Hub.Sensors.Select(s => s.Id).ToList();
        var t0 = Fixture.Start;
        var ranges = new (DateTime From, DateTime To)[]
        {
            (t0, Fixture.End),
            (t0.AddSeconds(17), Fixture.End.AddSeconds(-23)),
            (t0.AddMinutes(32), t0.AddMinutes(47)),
            (new DateTime(2026, 10, 1, 23, 59, 30, DateTimeKind.Utc), new DateTime(2026, 10, 2, 0, 0, 30, DateTimeKind.Utc)),
            (new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 2, 1, 0, 0, DateTimeKind.Utc)),
            (t0.AddDays(-1), t0.AddDays(1)),
        };
        foreach (var id in ids)
        {
            Assert.Equal(old.Service.GetCoverage(id), cur.Service.GetCoverage(id));
            foreach (var (from, to) in ranges)
            {
                Assert.Equal(await old.Service.GetStatsAsync(id, from, to), await cur.Service.GetStatsAsync(id, from, to));
                foreach (var max in new[] { 3, 40, 1500, 100_000 })
                {
                    var a = await old.Service.GetSeriesAsync(id, from, to, max);
                    var b = await cur.Service.GetSeriesAsync(id, from, to, max);
                    Assert.Equal(a, b);
                    if (from == t0 && to == Fixture.End && max == 1500) Assert.NotEmpty(a);
                }
            }
        }

        // the build that wrote the fixture exported these bytes; this build exports the same from both folders
        var fromOld = await Fixture.Exports(old.Service, ids);
        var fromCur = await Fixture.Exports(cur.Service, ids);
        foreach (var (name, text) in fromOld)
        {
            Assert.True(File.ReadAllBytes(Path.Combine(FixtureDir, name)).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(text)), name);
            Assert.Equal(text, fromCur[name]);
        }
        Assert.Empty(old.Warnings);
        Assert.Empty(cur.Warnings);
    }
}
