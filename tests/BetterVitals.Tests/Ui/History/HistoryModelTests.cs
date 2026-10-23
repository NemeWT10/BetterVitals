using System.Globalization;
using BetterVitals.Core.History;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.UI.Charts;
using BetterVitals.UI.Pages.History;
using Xunit;

namespace BetterVitals.Tests.Ui.History;

[Collection(UiCollection.Name)]
public sealed class HistoryModelTests
{
    private static readonly TimeZoneInfo Warsaw = HistoryRig.Warsaw;
    private static readonly long Now = HistoryRig.NowUtc.Ticks; // 20:40 in Warsaw
    private const long H = TimeSpan.TicksPerHour;

    private static void In(string lang, Action body)
    {
        var before = Loc.I.Language;
        Loc.I.SetLanguage(lang);
        try
        {
            body();
        }
        finally
        {
            Loc.I.SetLanguage(before);
        }
    }

    [Fact]
    public void Presets_end_now_and_are_recognised()
    {
        Assert.Equal(6, HistoryModel.Presets.Length);
        foreach (var p in HistoryModel.Presets)
        {
            var v = HistoryModel.ViewOf(p, Now);
            Assert.Equal(Now, v.To);
            Assert.Equal(HistoryModel.Length(p).Ticks, v.Span);
            Assert.Equal(p, HistoryModel.Match(v, Now));
            Assert.True(Loc.Has(HistoryModel.LabelKey(p)));
        }
        Assert.Equal(RangePreset.None, HistoryModel.Match(new TimeView(Now - 2 * H, Now - H), Now));
        Assert.Equal(RangePreset.None, HistoryModel.Match(new TimeView(Now - 2 * H, Now), Now));
        Assert.True(HistoryModel.IsLive(new TimeView(Now - H, Now - TimeSpan.TicksPerSecond), Now));
        Assert.False(HistoryModel.IsLive(new TimeView(Now - 2 * H, Now - H), Now));
    }

    [Fact]
    public void Live_only_allows_the_fifteen_minutes()
    {
        Assert.True(HistoryModel.PresetEnabled(RangePreset.Min15, liveOnly: true));
        Assert.False(HistoryModel.PresetEnabled(RangePreset.Hour1, liveOnly: true));
        Assert.All(HistoryModel.Presets, p => Assert.True(HistoryModel.PresetEnabled(p, liveOnly: false)));
    }

    [Fact]
    public void Durations_in_both_languages()
    {
        In("pl", () =>
        {
            Assert.Equal("45 s", HistoryModel.Duration(TimeSpan.FromSeconds(45)));
            Assert.Equal("15 min", HistoryModel.Duration(TimeSpan.FromMinutes(15)));
            Assert.Equal("1 h 15 min", HistoryModel.Duration(TimeSpan.FromMinutes(75)));
            Assert.Equal("1 dzień", HistoryModel.Duration(TimeSpan.FromDays(1)));
            Assert.Equal("7 dni", HistoryModel.Duration(TimeSpan.FromDays(7)));
            Assert.Equal("3 dni 1 h", HistoryModel.Duration(TimeSpan.FromHours(73)));
        });
        In("en", () =>
        {
            Assert.Equal("1 day", HistoryModel.Duration(TimeSpan.FromDays(1)));
            Assert.Equal("2 days 6 h", HistoryModel.Duration(TimeSpan.FromHours(54)));
        });
    }

    [Fact]
    public void Range_text_uses_today_yesterday_and_the_local_clock()
    {
        In("pl", () =>
        {
            var pl = CultureInfo.GetCultureInfo("pl-PL");
            Assert.Equal("Dziś, 19:40 – 20:40", HistoryModel.RangeText(Now - H, Now, Now, Warsaw, pl));
            Assert.Equal("Wczoraj, 20:40 – dziś, 20:40", HistoryModel.RangeText(Now - 24 * H, Now, Now, Warsaw, pl));
            Assert.Equal("16 paź, 20:40 – dziś, 20:40", HistoryModel.RangeText(Now - 7 * 24 * H, Now, Now, Warsaw, pl));
            Assert.Equal("20:38:12", HistoryModel.MomentText(Now - 108 * TimeSpan.TicksPerSecond, Now, Warsaw, pl));
            Assert.Equal("22 paź 21:54", HistoryModel.MomentText(new DateTime(2026, 10, 22, 19, 54, 0, DateTimeKind.Utc).Ticks, Now, Warsaw, pl));
        });
        In("en", () =>
        {
            var en = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("Today, 7:40 PM – 8:40 PM", HistoryModel.RangeText(Now - H, Now, Now, Warsaw, en));
        });
    }

    [Fact]
    public void Sizes_use_the_ui_culture()
    {
        Assert.Equal("1,2 MB", HistoryModel.Bytes(1_234_000, CultureInfo.GetCultureInfo("pl-PL")));
        Assert.Equal("980 B", HistoryModel.Bytes(980, CultureInfo.InvariantCulture));
        Assert.Equal("153 KB", HistoryModel.Bytes(153_000, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Display_units_convert_fahrenheit_and_scale_throughput()
    {
        var c = HistoryModel.DisplayOf(SensorKind.Temperature, "", TemperatureUnit.Celsius);
        Assert.Equal("°C", c.Unit);
        Assert.Equal(50f, c.Apply(50f));
        var f = HistoryModel.DisplayOf(SensorKind.Temperature, "", TemperatureUnit.Fahrenheit);
        Assert.Equal("°F", f.Unit);
        Assert.Equal(122f, f.Apply(50f), 3);
        var p = f.Apply(new HistoryPoint(1, 0, 50, 100));
        Assert.Equal(32f, p.Min, 3);
        Assert.Equal(212f, p.Max, 3);
        var mb = HistoryModel.DisplayOf(SensorKind.Throughput, "", default, 2.4e7);
        Assert.Equal("MB/s", mb.Unit);
        Assert.Equal(24f, mb.Apply(2.4e7f), 3);
        Assert.Equal("KB/s", HistoryModel.DisplayOf(SensorKind.Throughput, "", default, 5e3).Unit);
        Assert.Equal("RPM", HistoryModel.DisplayOf(SensorKind.Fan, "", default).Unit);
        Assert.Equal("—", c.Format(double.NaN, CultureInfo.InvariantCulture));
        Assert.Equal("45,3", c.Format(45.26, CultureInfo.GetCultureInfo("pl-PL")));
    }

    [Fact]
    public void Buckets_match_what_the_store_picks()
    {
        var rnd = new Random(7);
        for (var i = 0; i < 400; i++)
        {
            var from = Now - (long)(rnd.NextDouble() * 40 * 24 * H);
            var to = from + (long)(rnd.NextDouble() * 31 * 24 * H) + TimeSpan.TicksPerMinute;
            var max = rnd.Next(50, 4000);
            var step = (rnd.Next(4) switch { 0 => 1, 1 => 2, 2 => 5, _ => 10 }) * TimeSpan.TicksPerSecond;
            Assert.Equal(BetterVitals.History.HistoryStore.ChooseBucket(from, to, max, step), HistoryModel.BucketFor(from, to, max, step));
        }
    }

    [Fact]
    public void The_query_window_pads_the_view_and_never_reaches_far_into_the_future()
    {
        var v = new TimeView(Now - H, Now);
        var (from, to, max) = HistoryModel.QueryWindow(v, 800, Now);
        Assert.Equal(Now - H - H / 4, from);
        Assert.Equal(Now + TimeSpan.TicksPerMinute, to);
        Assert.InRange(max, 1200, 1600);
        var past = new TimeView(Now - 30 * 24 * H, Now - 29 * 24 * H);
        var w = HistoryModel.QueryWindow(past, 800, Now);
        Assert.Equal(past.To + past.Span / 4, w.To);
    }

    [Fact]
    public void Export_requests_from_the_dialog_choices()
    {
        var visible = new TimeView(Now - H, Now);
        var sel = (Now - 40 * TimeSpan.TicksPerMinute, Now - 10 * TimeSpan.TicksPerMinute);
        var cov = (new DateTime(Now - 9 * 24 * H, DateTimeKind.Utc), new DateTime(Now - 60 * TimeSpan.TicksPerSecond, DateTimeKind.Utc));

        var r = HistoryModel.BuildExport(["a", "b", "a"], HistoryModel.ExportRange.Visible, visible, sel, cov, Now, ExportResolution.Raw, true);
        Assert.Equal(["a", "b"], r.SensorIds);
        Assert.Equal(visible.From, r.FromUtc.Ticks);
        Assert.Equal(visible.To, r.ToUtc.Ticks);
        Assert.Equal(DateTimeKind.Utc, r.FromUtc.Kind);
        Assert.True(r.Zip);

        var s = HistoryModel.BuildExport(["a"], HistoryModel.ExportRange.Selection, visible, sel, cov, Now, ExportResolution.Minute, false);
        Assert.Equal(sel.Item1, s.FromUtc.Ticks);
        Assert.Equal(sel.Item2, s.ToUtc.Ticks);
        Assert.Equal(ExportResolution.Minute, s.Resolution);

        var noSel = HistoryModel.BuildExport(["a"], HistoryModel.ExportRange.Selection, visible, null, cov, Now, ExportResolution.Raw, true);
        Assert.Equal(visible.From, noSel.FromUtc.Ticks);

        var all = HistoryModel.BuildExport(["a"], HistoryModel.ExportRange.All, visible, sel, cov, Now, ExportResolution.Hour, true);
        Assert.Equal(cov.Item1, all.FromUtc);
        Assert.True(all.ToUtc.Ticks <= Now);

        Assert.Equal("BetterVitals-2026-10-23.csv.zip", HistoryModel.ExportFileName(r, Warsaw));
        Assert.Equal("BetterVitals-2026-10-14_2026-10-23.csv", HistoryModel.ExportFileName(all with { Zip = false }, Warsaw));
    }

    [Fact]
    public void Local_stats_from_chart_points()
    {
        var pts = new List<HistoryPoint> { new(10, 5, 6, 7), new(20, 1, 4, 9), new(30, 3, 5, 6) };
        var s = HistoryPage.LocalStats(pts, 0, 25)!.Value;
        Assert.Equal(1, s.Min);
        Assert.Equal(9, s.Max);
        Assert.Equal(20, s.MinTimeUtcTicks);
        Assert.Equal(5, s.Avg, 3);
        Assert.Equal(2, s.Count);
        Assert.Null(HistoryPage.LocalStats(pts, 40, 50));
    }

    [Fact]
    public void Every_history_key_has_both_languages_and_matching_placeholders()
    {
        var keys = Loc.AllEntries.Where(e => e.Key.StartsWith("history.", StringComparison.Ordinal)).ToList();
        Assert.True(keys.Count > 80);
        foreach (var (key, (pl, en)) in keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(pl), key);
            Assert.False(string.IsNullOrWhiteSpace(en), key);
            static string Holes(string s) => string.Concat(System.Text.RegularExpressions.Regex.Matches(s, @"\{\d\}").Select(m => m.Value).Order());
            Assert.Equal(Holes(pl), Holes(en));
        }
    }
}
