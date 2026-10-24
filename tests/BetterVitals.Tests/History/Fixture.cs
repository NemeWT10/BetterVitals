using System.Text;
using BetterVitals.Core.History;
using BetterVitals.Core.Sensors;
using BetterVitals.History;
using BetterVitals.History.Storage;

namespace BetterVitals.Tests.History;

/// <summary>
/// Deterministic recording used for the format compatibility tests. <c>Fixtures/v1</c> holds the files this produced with
/// block format version 1 (raw and rollup blocks v1) plus the CSV exports of that build, so every later build must read
/// them and answer exactly like the files it writes itself from the same input.
/// </summary>
internal static class Fixture
{
    public static readonly DateTime Start = new(2026, 10, 1, 22, 58, 0, DateTimeKind.Utc);

    /// <summary>2 h 3 min: crosses midnight (two raw files) and three hour boundaries.</summary>
    public const int Seconds = 2 * 3600 + 3 * 60;

    public static DateTime End => Start.AddSeconds(Seconds);

    public static readonly SensorKind[] Kinds =
    [
        SensorKind.Temperature, SensorKind.Load, SensorKind.Clock, SensorKind.Throughput, SensorKind.Voltage, SensorKind.Fan,
    ];

    public static List<SensorDescriptor> AddSensors(FakeHub hub) => Kinds.Select((k, i) => hub.Add($"S{i}", k)).ToList();

    public static float Value(int sensor, long ts)
    {
        var minute = new DateTime(ts, DateTimeKind.Utc).TimeOfDay.TotalMinutes;
        if (sensor == 1 && minute is >= 23 * 60 + 30 and < 23 * 60 + 41) return float.NaN; // a gap of 11 minutes
        if (sensor == 5 && (ts / TimeSpan.TicksPerSecond) % 7 == 0) return float.NaN; // scattered gaps
        return Signals.Slow(Kinds[sensor], sensor, ts);
    }

    /// <summary>Records the fixture into <paramref name="rig"/>'s folder at 1 s and writes everything out.</summary>
    public static void Write(Rig rig)
    {
        var sensors = AddSensors(rig.Hub);
        var svc = rig.Open();
        svc.Configure(Rig.Settings(true, 1, sensors));
        rig.Feed(Start, Seconds, 1, Value);
        svc.Configure(Rig.Settings(false, 1, sensors)); // final flush, then nothing more is written
    }

    /// <summary>CSV exports of the fixture (invariant culture, not zipped): 10 raw minutes around midnight, all minutes, all hours.</summary>
    public static async Task<Dictionary<string, string>> Exports(HistoryService svc, IReadOnlyList<string> ids)
    {
        var result = new Dictionary<string, string>();
        async Task One(string name, DateTime from, DateTime to, ExportResolution res)
        {
            var ms = new MemoryStream();
            await svc.ExportAsync(new([.. ids], from, to, res, false), ms);
            result[name] = Encoding.UTF8.GetString(ms.ToArray());
        }
        await One("export-raw-midnight.csv", new DateTime(2026, 10, 1, 23, 55, 0, DateTimeKind.Utc), new DateTime(2026, 10, 2, 0, 5, 0, DateTimeKind.Utc), ExportResolution.Raw);
        await One("export-minute.csv", Start, End, ExportResolution.Minute);
        await One("export-hour.csv", Start, End, ExportResolution.Hour);
        return result;
    }

    /// <summary>Decoded (uncompressed) payloads of every block in a history folder, by file name, in file order.</summary>
    public static Dictionary<string, List<byte[]>> Payloads(string dir)
    {
        var result = new Dictionary<string, List<byte[]>>();
        foreach (var f in HistoryFiles.List(dir))
        {
            var file = File.ReadAllBytes(f.Path);
            var blocks = new List<BlockRef>();
            BlockScanner.Scan(file, blocks);
            var list = new List<byte[]>();
            var inf = new Inflater();
            foreach (var b in blocks)
            {
                inf.Begin(file, b, b.Header.RawLength);
                list.Add(inf.Data.ToArray());
                inf.End();
            }
            result[Path.GetFileName(f.Path)] = list;
        }
        return result;
    }
}
