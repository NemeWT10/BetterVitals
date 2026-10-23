using System.Globalization;
using System.IO.Compression;
using System.Text;
using BetterVitals.Core.History;
using BetterVitals.History.Storage;

namespace BetterVitals.History;

/// <summary>
/// "As light as possible" export: CSV in the Windows regional format (Excel opens it in columns: "45,05" split by ';' in
/// Polish), one time column (UTC, "2026-10-23 20:10:00", which Excel reads as a date) and one column per sensor
/// ("Name [unit]"; minute / hour exports have "… min", "… avg", "… max"), numbers with the minimal decimals of the
/// storage quantum and no trailing zeros; optionally a ZIP with that single CSV entry. Streams in time chunks, so memory
/// stays around a megabyte whatever the range.
/// </summary>
internal static class CsvExport
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>The list separator and decimal separator of a culture, made safe for CSV.</summary>
    internal readonly struct Dialect
    {
        public readonly char Separator;
        public readonly NumberFormatInfo Numbers;

        public Dialect(CultureInfo culture)
        {
            var dec = culture.NumberFormat.NumberDecimalSeparator is "," ? "," : ".";
            var list = culture.TextInfo.ListSeparator;
            Separator = list.Length == 1 && list != dec && list[0] is not ('"' or '\r' or '\n' or '-' or ':' or ' ') ? list[0] : dec == "," ? ';' : ',';
            Numbers = (NumberFormatInfo)NumberFormatInfo.InvariantInfo.Clone();
            Numbers.NumberDecimalSeparator = dec;
        }
    }

    public static long Estimate(int sensors, ExportResolution resolution, long from, long to, long rawStep, bool zip)
    {
        if (to <= from || sensors == 0) return 0;
        var step = resolution switch
        {
            ExportResolution.Minute => TimeGrid.Minute,
            ExportResolution.Hour => TimeGrid.Hour,
            _ => Math.Max(TimeGrid.Second, rawStep),
        };
        var rows = (to - from + step - 1) / step;
        var rowBytes = 22 + sensors * (resolution == ExportResolution.Raw ? 7L : 21L);
        var csv = 64 + sensors * (resolution == ExportResolution.Raw ? 24L : 80L) + rows * rowBytes;
        return zip ? csv / 8 + 256 : csv;
    }

    public static void Write(HistoryStore store, SensorRef[] sensors, ExportRequest req, long from, long to, Stream output, Dialect dialect,
        IProgress<double>? progress, CancellationToken ct)
    {
        ZipArchive? zip = null;
        Stream target = output;
        if (req.Zip)
        {
            zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            var name = $"bettervitals-{new DateTime(TimeGrid.Utc(req.FromUtc)):yyyyMMdd-HHmm}-{new DateTime(TimeGrid.Utc(req.ToUtc)):yyyyMMdd-HHmm}.csv";
            target = zip.CreateEntry(name, CompressionLevel.SmallestSize).Open();
        }
        try
        {
            // UTF-8 with BOM: Excel otherwise shows "Â°C"
            using var w = new StreamWriter(target, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 1 << 16, leaveOpen: zip is null);
            w.NewLine = "\r\n";
            WriteHeader(w, sensors, req.Resolution, dialect.Separator);
            if (to > from && sensors.Length > 0) WriteRows(w, store, sensors, req.Resolution, from, to, dialect, progress, ct);
            w.Flush();
        }
        finally
        {
            if (zip is not null)
            {
                target.Dispose();
                zip.Dispose();
            }
        }
        progress?.Report(1);
    }

    private static void WriteHeader(StreamWriter w, SensorRef[] sensors, ExportResolution res, char sep)
    {
        w.Write("time (UTC)");
        foreach (var s in sensors)
        {
            var title = s.Unit.Length > 0 ? $"{s.Name} [{s.Unit}]" : s.Name;
            if (res == ExportResolution.Raw)
            {
                w.Write(sep);
                w.Write(Escape(title, sep));
            }
            else
            {
                foreach (var suffix in (ReadOnlySpan<string>)[" min", " avg", " max"])
                {
                    w.Write(sep);
                    w.Write(Escape(title + suffix, sep));
                }
            }
        }
        w.WriteLine();
    }

    internal static string Escape(string s, char sep = ',')
        => s.AsSpan().IndexOfAny("\"\r\n") < 0 && !s.Contains(sep) ? s : "\"" + s.Replace("\"", "\"\"") + "\"";

    /// <summary>Accumulates records of all sensors into a fixed time grid for one chunk.</summary>
    private sealed class Grid(int sensors, long width, int cells) : IRecVisitor
    {
        public readonly long Width = width;
        public readonly int Cells = cells;
        public long Start;
        public readonly long[] Min = new long[sensors * cells];
        public readonly long[] Max = new long[sensors * cells];
        public readonly long[] Sum = new long[sensors * cells];
        public readonly long[] Count = new long[sensors * cells];

        public void Reset(long start)
        {
            Start = start;
            Array.Clear(Count);
            Array.Clear(Sum);
        }

        public void Visit(int ordinal, in Rec rec)
        {
            if (rec.Count == 0) return;
            var cell = (rec.Time - Start) / Width;
            if ((ulong)cell >= (ulong)Cells) return;
            var i = ordinal * Cells + (int)cell;
            if (Count[i] == 0)
            {
                Min[i] = rec.Min;
                Max[i] = rec.Max;
            }
            else
            {
                if (rec.Min < Min[i]) Min[i] = rec.Min;
                if (rec.Max > Max[i]) Max[i] = rec.Max;
            }
            Sum[i] += rec.Sum;
            Count[i] += rec.Count;
        }
    }

    private static void WriteRows(StreamWriter w, HistoryStore store, SensorRef[] sensors, ExportResolution res, long from, long to, Dialect dialect,
        IProgress<double>? progress, CancellationToken ct)
    {
        var (width, cells, level) = res switch
        {
            ExportResolution.Minute => (TimeGrid.Minute, 360, Level.Minute), // 6 h per chunk
            ExportResolution.Hour => (TimeGrid.Hour, 360, Level.Hour), // 15 days per chunk
            _ => (TimeGrid.Second, 600, Level.Raw), // 10 min per chunk
        };
        var formats = sensors.Select(s => new QuantumFormat(s.Quantum)).ToArray();
        var grid = new Grid(sensors.Length, width, cells);
        var chunk = width * cells;
        Span<char> buf = stackalloc char[64];
        var sep = dialect.Separator;
        var numbers = dialect.Numbers;
        HistoryStore.Reader? reader = null;
        try
        {
            for (var c = TimeGrid.Floor(from, width); c < to; c += chunk)
            {
                ct.ThrowIfCancellationRequested();
                var a = Math.Max(c, from);
                var e = Math.Min(c + chunk, to);
                grid.Reset(c);
                lock (store.Gate)
                {
                    if (reader is null) reader = store.OpenReader(sensors);
                    else reader.Refresh();
                    if (level == Level.Raw) reader.ReadLevel(Level.Raw, a, e, ref grid);
                    else reader.ReadPlanned(level, a, e, ref grid);
                }

                for (var cell = 0; cell < cells; cell++)
                {
                    var any = false;
                    for (var s = 0; s < sensors.Length && !any; s++) any = grid.Count[s * cells + cell] > 0;
                    if (!any) continue;
                    var t = new DateTime(c + cell * width, DateTimeKind.Utc);
                    t.TryFormat(buf, out var n, TimeFormat, CultureInfo.InvariantCulture);
                    w.Write(buf[..n]);
                    for (var s = 0; s < sensors.Length; s++)
                    {
                        var i = s * cells + cell;
                        var count = grid.Count[i];
                        var f = formats[s];
                        if (res == ExportResolution.Raw)
                        {
                            w.Write(sep);
                            if (count > 0) WriteNumber(w, buf, count == 1 ? f.Exact(grid.Sum[i]) : f.Average(grid.Sum[i], count), numbers);
                            continue;
                        }
                        if (count == 0)
                        {
                            w.Write(sep);
                            w.Write(sep);
                            w.Write(sep);
                            continue;
                        }
                        w.Write(sep);
                        WriteNumber(w, buf, f.Exact(grid.Min[i]), numbers);
                        w.Write(sep);
                        WriteNumber(w, buf, f.Average(grid.Sum[i], count), numbers);
                        w.Write(sep);
                        WriteNumber(w, buf, f.Exact(grid.Max[i]), numbers);
                    }
                    w.WriteLine();
                }
                progress?.Report((double)(e - from) / (to - from));
            }
        }
        finally
        {
            if (reader is not null)
                lock (store.Gate)
                    reader.Dispose();
        }
    }

    private static void WriteNumber(StreamWriter w, Span<char> buf, decimal value, NumberFormatInfo numbers)
    {
        if (value.TryFormat(buf, out var n, "0.############", numbers)) w.Write(buf[..n]);
    }

    /// <summary>Exact decimal rendering of quantized values (q × quantum) with the quantum's number of decimals.</summary>
    internal readonly struct QuantumFormat
    {
        private readonly decimal _quantum;
        private readonly int _scale;

        public QuantumFormat(double quantum)
        {
            _quantum = (decimal)quantum;
            _scale = (decimal.GetBits(_quantum)[3] >> 16) & 0xFF;
        }

        public decimal Exact(long q) => q * _quantum;

        public decimal Average(long sum, long count) => Math.Round(sum * _quantum / count, _scale, MidpointRounding.AwayFromZero);
    }
}
