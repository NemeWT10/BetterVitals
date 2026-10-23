using System.Diagnostics;
using System.Windows;
using BetterVitals.Core.History;
using BetterVitals.Core.Sensors;
using BetterVitals.Services;
using BetterVitals.UI.Charts;
using BetterVitals.UI.Pages.History;
using Xunit;
using Xunit.Abstractions;

namespace BetterVitals.Tests.Ui.History;

/// <summary>
/// Offscreen renders of the History page in its main states (PNG under tests/output/ui-history, reviewed by eye) and
/// the chart redraw benchmark. Nothing is shown on screen.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class HistoryRenderTests(ITestOutputHelper output)
{
    private static void AssertPng(string path) => Assert.True(new FileInfo(path).Length > 8000, $"{path} looks empty");

    [Theory]
    [InlineData("aurora", "pl")]
    [InlineData("daylight", "en")]
    public void Page_with_data(string theme, string lang) => Sta.Run(() =>
    {
        using var rig = new HistoryRig(theme, lang);
        rig.Show();
        Assert.Equal(HistoryEmpty.None, rig.Page.EmptyState);
        AssertPng(rig.Save($"history_data_{theme}_{lang}"));

        rig.Page.ApplyPreset(RangePreset.Hour24, animate: false);
        rig.Settle();
        AssertPng(rig.Save($"history_24h_{theme}_{lang}"));

        rig.Page.ApplyPreset(RangePreset.Day7, animate: false);
        rig.Settle();
        AssertPng(rig.Save($"history_7d_{theme}_{lang}"));
    });

    [Theory]
    [InlineData("aurora", "pl")]
    [InlineData("daylight", "en")]
    public void Page_with_a_selection_and_the_crosshair(string theme, string lang) => Sta.Run(() =>
    {
        using var rig = new HistoryRig(theme, lang);
        rig.Show();
        rig.Page.ApplyPreset(RangePreset.Hour6, animate: false);
        rig.Settle();
        var now = rig.Now;
        rig.Page.Chart.SetSelection((now - TimeSpan.FromHours(3.2).Ticks, now - TimeSpan.FromHours(1.6).Ticks));
        rig.Settle();
        var plot = rig.Page.Chart.PlotRect;
        rig.Page.Chart.PointerMove(new Point(plot.Left + plot.Width * 0.62, plot.Top + 40));
        rig.Page.Chart.RenderNow();
        Assert.True(rig.Page.Chart.CrosshairVisible);
        Assert.NotNull(rig.Page.Chart.Selection);
        Assert.Equal(3, rig.Page.StatsPanel.Rows.Count);
        AssertPng(rig.Save($"history_selection_{theme}_{lang}"));
    });

    [Theory]
    [InlineData("aurora", "pl")]
    [InlineData("daylight", "en")]
    public void Page_live_only(string theme, string lang) => Sta.Run(() =>
    {
        using var rig = new HistoryRig(theme, lang, recording: false);
        rig.Show();
        // the live buffer fills from frames: feed a few minutes like the engine would
        FeedLive(rig, seconds: 90);
        rig.Page.QueryStats();
        rig.Settle();
        Assert.Equal(RangePreset.Min15, rig.Page.Preset);
        Assert.Equal(1, rig.Page.NoteCount);
        AssertPng(rig.Save($"history_liveonly_{theme}_{lang}"));
    });

    [Theory]
    [InlineData("aurora", "pl")]
    [InlineData("daylight", "en")]
    public void Page_empty_states(string theme, string lang) => Sta.Run(() =>
    {
        using (var rig = new HistoryRig(theme, lang, hub: NullSensorHub.Instance))
        {
            rig.Show();
            Assert.Equal(HistoryEmpty.PickSensor, rig.Page.EmptyState);
            AssertPng(rig.Save($"history_empty_pick_{theme}_{lang}"));
        }
        using (var rig = new HistoryRig(theme, lang, history: new EmptyHistory()))
        {
            rig.Show();
            Assert.Equal(HistoryEmpty.NothingRecorded, rig.Page.EmptyState);
            AssertPng(rig.Save($"history_empty_nodata_{theme}_{lang}"));
        }
        using (var rig = new HistoryRig(theme, lang, record: d => d.Kind != SensorKind.Temperature && d.Kind != SensorKind.Load))
        {
            rig.Show();
            // not recorded: only the 15-minute live buffer has data, and a note offers to record them
            Assert.Equal(HistoryEmpty.None, rig.Page.EmptyState);
            Assert.Equal(1, rig.Page.NoteCount);
            AssertPng(rig.Save($"history_notrecorded_{theme}_{lang}"));
        }
    });

    [Theory]
    [InlineData("aurora", "pl")]
    [InlineData("daylight", "en")]
    public void Popovers_and_the_export_sheet(string theme, string lang) => Sta.Run(() =>
    {
        using var rig = new HistoryRig(theme, lang);
        rig.Show();
        var picker = rig.Page.Picker;
        picker.Refresh();
        AssertPng(rig.SavePanel(picker, $"history_picker_{theme}_{lang}", 420, 480, theme));
        picker.Search.Text = "gpu";
        AssertPng(rig.SavePanel(rig.Page.Picker, $"history_picker_search_{theme}_{lang}", 420, 480, theme));

        var range = rig.Page.RangePickerPanel;
        range.Open(rig.Page.Chart.View);
        range.PickDay(new DateTime(2026, 10, 19));
        range.PickDay(new DateTime(2026, 10, 22));
        AssertPng(rig.SavePanel(range, $"history_custom_{theme}_{lang}", 340, 520, theme));

        rig.Page.Chart.SetSelection((rig.Now - TimeSpan.FromMinutes(40).Ticks, rig.Now - TimeSpan.FromMinutes(10).Ticks));
        rig.Settle();
        rig.Page.OpenExport();
        Assert.NotNull(rig.Page.Sheet);
        rig.Page.Sheet!.RunEstimate().Wait();
        rig.Settle();
        AssertPng(rig.Save($"history_export_{theme}_{lang}"));
    });

    [Fact]
    public void Page_over_a_real_history_service() => Sta.Run(() =>
    {
        using var data = new BetterVitals.Tests.History.Rig("ui-real", HistoryRig.NowUtc.AddHours(-6));
        var temp = data.Hub.Add("CPU Package", SensorKind.Temperature);
        var load = data.Hub.Add("CPU Total", SensorKind.Load);
        var gpu = data.Hub.Add("GPU Core", SensorKind.Temperature);
        var power = data.Hub.Add("Package", SensorKind.Power);
        var svc = data.Open();
        svc.Configure(BetterVitals.Tests.History.Rig.Settings(true, 2, [temp, load, gpu, power]));
        var sw = Stopwatch.StartNew();
        data.Feed(HistoryRig.NowUtc.AddHours(-6), 6 * 3600, 2, (i, t) => BetterVitals.Tests.History.Signals.Slow(data.Hub.Sensors[i].Kind, i, t), interval: 2);
        svc.Flush();
        output.WriteLine($"fed 6 h × 4 sensors in {sw.ElapsedMilliseconds} ms");

        // query cost as the page asks (900 px wide: ~1300 buckets over the padded window)
        foreach (var span in new[] { TimeSpan.FromHours(1), TimeSpan.FromHours(6) })
        {
            var to = HistoryRig.NowUtc;
            var from = to - span;
            sw.Restart();
            var pts = svc.GetSeriesAsync(temp.Id, from, to, 1300).Result;
            var q = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var st = svc.GetStatsAsync(temp.Id, from, to).Result;
            output.WriteLine($"{span.TotalHours} h: series {pts.Count} pts in {q:0.0} ms, stats in {sw.Elapsed.TotalMilliseconds:0.0} ms (count {st?.Count})");
        }

        using var rig = new HistoryRig("graphite", "en", hub: data.Hub, history: svc);
        rig.Store.Mutate("recording", s => s.Recording.SensorIds.UnionWith([temp.Id, load.Id, gpu.Id, power.Id]));
        rig.Show(temp.Id);
        rig.Page.TogglePicked(gpu);
        rig.Page.TogglePicked(power);
        rig.Page.ApplyPreset(RangePreset.Hour6, animate: false);
        rig.Settle();
        Assert.Equal(HistoryEmpty.None, rig.Page.EmptyState);
        Assert.All(rig.Page.StatsPanel.Rows, r => Assert.NotNull(r.Stats));
        AssertPng(rig.Save("history_real_service_graphite_en"));
    });

    [Fact]
    public void Chart_redraw_2000_points_x_4_series() => Sta.Run(() =>
    {
        var host = new System.Windows.Controls.Border { Width = 1000, Height = 380, Resources = HistoryRig.Resources("aurora") };
        var chart = new TimeChart { Clock = () => HistoryRig.NowUtc.Ticks, Zone = HistoryRig.Warsaw, Animations = false };
        host.Child = chart;
        var now = HistoryRig.NowUtc.Ticks;
        var step = TimeSpan.FromHours(24).Ticks / 2000;
        var series = new List<ChartSeries>();
        string[] units = ["°C", "°C", "%", "W"];
        for (var s = 0; s < 4; s++)
        {
            var pts = new List<HistoryPoint>(2000);
            for (var i = 0; i < 2000; i++)
            {
                var t = now - TimeSpan.FromHours(24).Ticks + i * step;
                var v = (float)(50 + 20 * Math.Sin(i / 90.0 + s) + 5 * Math.Sin(i / 7.0));
                pts.Add(new HistoryPoint(t, v - 3, v, v + 4));
            }
            series.Add(new ChartSeries { Id = "s" + s, Name = "Series " + s, Unit = units[s], ColorIndex = s, Points = pts, Bucket = step });
        }
        chart.SetSeries(series);
        chart.SetView(new TimeView(now - TimeSpan.FromHours(24).Ticks, now), animate: false);
        HistoryRig.SavePng(host, "chart_perf_2000x4", 1000, 380);
        var times = new List<double>();
        for (var i = 0; i < 30; i++)
        {
            chart.RenderNow();
            times.Add(chart.LastRedrawMs);
        }
        times.Sort();
        var median = times[times.Count / 2];
        output.WriteLine($"redraw 4 × 2000 points: median {median:0.00} ms, min {times[0]:0.00}, max {times[^1]:0.00}");
        Assert.True(median < 40, $"redraw too slow: {median:0.0} ms");

        // crosshair only: the overlay layer
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 200; i++) chart.PointerMove(new Point(100 + i * 4, 120));
        Sta.DoEvents();
        output.WriteLine($"200 crosshair moves (coalesced): {sw.Elapsed.TotalMilliseconds:0.0} ms");
    });

    /// <summary>Time passes: one live sample per second per shown sensor (values from the preview's signal), like the engine sends them.</summary>
    internal static void FeedLive(HistoryRig rig, int seconds)
    {
        var preview = (PreviewHistory)rig.History!;
        var ids = rig.Page.ShownIds.Select(id => rig.Hub.Find(id)!).ToList();
        for (var k = 0; k < seconds; k++)
        {
            rig.Now += TimeSpan.TicksPerSecond;
            var t = rig.Now;
            rig.Page.AppendSamples(ids.Select(d => d.Index).ToArray(), ids.Select(d => preview.Sample(d, t)).ToArray(), ids.Select(_ => t).ToArray());
        }
    }

    /// <summary>A store with nothing recorded.</summary>
    private sealed class EmptyHistory : IHistoryQuery
    {
        public Task<IReadOnlyList<HistoryPoint>> GetSeriesAsync(string sensorId, DateTime fromUtc, DateTime toUtc, int maxPoints, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HistoryPoint>>([]);

        public Task<RangeStats?> GetStatsAsync(string sensorId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => Task.FromResult<RangeStats?>(null);

        public (DateTime FromUtc, DateTime ToUtc)? GetCoverage(string sensorId) => null;

        public HistoryUsage GetUsage() => new(0, 0, null);

        public long EstimateExportBytes(ExportRequest request) => 0;

        public Task ExportAsync(ExportRequest request, Stream output, IProgress<double>? progress = null, CancellationToken ct = default) => Task.CompletedTask;

        public Task ClearAsync() => Task.CompletedTask;
    }
}
