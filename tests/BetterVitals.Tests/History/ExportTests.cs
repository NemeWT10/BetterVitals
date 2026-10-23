using System.IO.Compression;
using System.Text;
using BetterVitals.Core.History;
using BetterVitals.Core.Sensors;
using BetterVitals.History;
using Xunit;

namespace BetterVitals.Tests.History;

public class ExportTests
{
    private static readonly DateTime T0 = new(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);

    private static (Rig Rig, SensorDescriptor Temp, SensorDescriptor Volt, SensorDescriptor Name) Setup()
    {
        var rig = new Rig("export", T0);
        var temp = rig.Hub.Add("CPU Package", SensorKind.Temperature);
        var volt = rig.Hub.Add("Vcore", SensorKind.Voltage);
        var odd = rig.Hub.Add("Fan, \"Pump\"", SensorKind.Fan);
        var svc = rig.Open();
        svc.Configure(Rig.Settings(true, 1, [temp, volt, odd]));
        // temp: 45, 45.05, 45.1 … ; volt: 1.2 / 1.2005; fan has a gap in the 2nd minute
        rig.Feed(T0, 180, 1, (i, t) =>
        {
            var s = (int)((t - T0.Ticks) / TimeSpan.TicksPerSecond);
            return i switch
            {
                0 => 45f + s % 3 * 0.05f,
                1 => s % 2 == 0 ? 1.2f : 1.2005f,
                _ => s is >= 60 and < 120 ? float.NaN : 1000 + s,
            };
        }, interval: 1);
        svc.Suspend();
        return (rig, temp, volt, odd);
    }

    private static string ToText(byte[] bytes)
    {
        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble), "UTF-8 BOM for Excel");
        return Encoding.UTF8.GetString(bytes.AsSpan(Encoding.UTF8.Preamble.Length));
    }

    [Fact]
    public async Task Raw_csv_format()
    {
        var (rig, temp, volt, odd) = Setup();
        using var _ = rig;
        var ms = new MemoryStream();
        await rig.Service.ExportAsync(new([temp.Id, volt.Id, odd.Id], T0, T0.AddMinutes(3), ExportResolution.Raw, false), ms);
        var lines = ToText(ms.ToArray()).Split("\r\n");
        Assert.Equal("time,CPU Package [°C],Vcore [V],\"Fan, \"\"Pump\"\" [RPM]\"", lines[0]);
        Assert.Equal("2026-10-20T10:00:00Z,45,1.2,1000", lines[1]);
        Assert.Equal("2026-10-20T10:00:01Z,45.05,1.2005,1001", lines[2]);
        Assert.Equal("2026-10-20T10:00:02Z,45.1,1.2,1002", lines[3]);
        Assert.Equal("2026-10-20T10:01:00Z,45,1.2,", lines[61]);
        Assert.Equal(180 + 2, lines.Length); // header + rows + final empty
        Assert.Equal("", lines[^1]);

        var estimate = rig.Service.EstimateExportBytes(new([temp.Id, volt.Id, odd.Id], T0, T0.AddMinutes(3), ExportResolution.Raw, false));
        Assert.InRange(estimate, ms.Length / 3, ms.Length * 3);
    }

    [Fact]
    public async Task Minute_csv_has_min_avg_max()
    {
        var (rig, temp, _, odd) = Setup();
        using var __ = rig;
        var ms = new MemoryStream();
        await rig.Service.ExportAsync(new([temp.Id, odd.Id], T0, T0.AddMinutes(3), ExportResolution.Minute, false), ms);
        var lines = ToText(ms.ToArray()).Split("\r\n");
        Assert.Equal("time,CPU Package [°C] min,CPU Package [°C] avg,CPU Package [°C] max,"
            + "\"Fan, \"\"Pump\"\" [RPM] min\",\"Fan, \"\"Pump\"\" [RPM] avg\",\"Fan, \"\"Pump\"\" [RPM] max\"", lines[0]);
        // averages carry the quantum's decimals (fan: 1 RPM → 1029.5 rounds to 1030)
        Assert.Equal("2026-10-20T10:00:00Z,45,45.05,45.1,1000,1030,1059", lines[1]);
        Assert.Equal("2026-10-20T10:01:00Z,45,45.05,45.1,,,", lines[2]);
        Assert.Equal("2026-10-20T10:02:00Z,45,45.05,45.1,1120,1150,1179", lines[3]);
        Assert.Equal(5, lines.Length);

        var hour = new MemoryStream();
        await rig.Service.ExportAsync(new([temp.Id], T0.AddHours(-1), T0.AddHours(1), ExportResolution.Hour, false), hour);
        var hl = ToText(hour.ToArray()).Split("\r\n");
        Assert.Equal("2026-10-20T10:00:00Z,45,45.05,45.1", hl[1]);
        Assert.Equal(3, hl.Length);
    }

    [Fact]
    public async Task Zip_holds_the_same_csv()
    {
        var (rig, temp, volt, odd) = Setup();
        using var _ = rig;
        var req = new ExportRequest([temp.Id, volt.Id, odd.Id], T0, T0.AddMinutes(3), ExportResolution.Raw, false);
        var csv = new MemoryStream();
        await rig.Service.ExportAsync(req, csv);
        var zipped = new MemoryStream();
        var progress = new List<double>();
        await rig.Service.ExportAsync(req with { Zip = true }, zipped, new SyncProgress(progress));
        Assert.True(zipped.Length < csv.Length);
        Assert.Equal(1.0, progress[^1]);

        zipped.Position = 0;
        using var zip = new ZipArchive(zipped, ZipArchiveMode.Read);
        var entry = Assert.Single(zip.Entries);
        Assert.EndsWith(".csv", entry.Name);
        using var es = entry.Open();
        var inner = new MemoryStream();
        es.CopyTo(inner);
        Assert.Equal(csv.ToArray(), inner.ToArray());

        var est = rig.Service.EstimateExportBytes(req with { Zip = true });
        Assert.True(est < rig.Service.EstimateExportBytes(req));
    }

    [Fact]
    public void Number_format_is_exact_and_minimal()
    {
        var t = new CsvExport.QuantumFormat(0.05);
        Assert.Equal("45.35", t.Exact(907).ToString("0.############", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("45", t.Exact(900).ToString("0.############", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(45.37m, t.Average(907 * 2 + 908, 3));
        var v = new CsvExport.QuantumFormat(0.0005);
        Assert.Equal(1.2005m, v.Exact(2401));
        Assert.Equal(-0.001m, v.Exact(-2));
        Assert.Equal("a", CsvExport.Escape("a"));
        Assert.Equal("\"a,b\"", CsvExport.Escape("a,b"));
    }

    private sealed class SyncProgress(List<double> list) : IProgress<double>
    {
        public void Report(double value)
        {
            lock (list) list.Add(value);
        }
    }
}
