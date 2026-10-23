using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BetterVitals.Core.History;
using BetterVitals.Core.Sensors;
using BetterVitals.Platform;
using BetterVitals.Services;
using BetterVitals.UI.Charts;
using BetterVitals.UI.Pages.History;
using BetterVitals.UI.Panel;
using Xunit;

namespace BetterVitals.Tests.Ui.History;

[Collection(UiCollection.Name)]
public sealed class HistoryPageTests
{
    private const long H = TimeSpan.TicksPerHour;
    private const long M = TimeSpan.TicksPerMinute;

    [Fact]
    public void Registers_as_the_history_page()
    {
        var info = PageRegistry.Find(PageIds.History)!;
        Assert.Equal(20, info.Order);
        Assert.Equal("Icon.History", info.IconKey);
        Assert.True(info.Priority >= 1);
    }

    [Fact]
    public void Shows_the_key_sensors_live_for_the_last_hour() => Sta.Run(() =>
    {
        using var rig = new HistoryRig();
        rig.Show();
        Assert.Equal(["Core (Tctl/Tdie)", "GPU Core", "CPU Total"], rig.Page.ShownIds.Select(id => rig.Hub.Find(id)!.Name));
        Assert.Equal(RangePreset.Hour1, rig.Page.Preset);
        Assert.True(rig.Page.IsLiveView);
        Assert.Equal(new TimeView(rig.Now - H, rig.Now), rig.Page.Chart.View);
        Assert.All(rig.Page.Chart.Series, s => Assert.NotEmpty(s.Points));
        Assert.Equal(["°C", "°C", "%"], rig.Page.Chart.Series.Select(s => s.Unit));
        Assert.Equal(3, rig.Page.StatsPanel.Rows.Count);
        Assert.All(rig.Page.StatsPanel.Rows, r => Assert.NotNull(r.Stats));
        Assert.Equal(HistoryEmpty.None, rig.Page.EmptyState);
        Assert.Equal(0, rig.Page.NoteCount);
    });

    [Fact]
    public void A_sensor_from_another_page_is_preselected_and_kept() => Sta.Run(() =>
    {
        using var rig = new HistoryRig();
        var hot = rig.Sensor("GPU Hot Spot", SensorKind.Temperature);
        rig.Show(hot.Id);
        Assert.Contains(hot.Id, rig.Page.ShownIds);
        rig.Page.ApplyPreset(RangePreset.Hour6, animate: false);
        rig.Page.OnNavigatedFrom();
        rig.Page.OnNavigatedTo(null); // back again: selection and range are remembered
        rig.Settle();
        Assert.Contains(hot.Id, rig.Page.ShownIds);
        Assert.Equal(RangePreset.Hour6, rig.Page.Preset);
    });

    [Fact]
    public void At_most_six_series_and_the_picker_follows() => Sta.Run(() =>
    {
        using var rig = new HistoryRig();
        rig.Show();
        var picker = rig.Page.Picker;
        picker.Refresh();
        foreach (var e in picker.Visible.Where(e => !e.IsHeader && !e.IsShown).Take(5).ToList()) picker.Activate(e);
        rig.Settle();
        Assert.Equal(HistoryModel.MaxSeries, rig.Page.ShownIds.Count);
        var other = picker.Visible.First(e => !e.IsHeader && !e.IsShown);
        Assert.False(other.Enabled);
        picker.Activate(other);
        Assert.Equal(HistoryModel.MaxSeries, rig.Page.ShownIds.Count);
        var shown = picker.Visible.First(e => e.IsShown);
        picker.Activate(shown); // click again = remove
        Assert.Equal(HistoryModel.MaxSeries - 1, rig.Page.ShownIds.Count);
        Assert.True(other.Enabled);
        // a third unit and more get their own scale
        Assert.True(rig.Page.Chart.OwnScaleUnits.Count > 0);
    });

    [Fact]
    public void Picker_search_matches_names_hardware_and_kinds() => Sta.Run(() =>
    {
        using var rig = new HistoryRig("aurora", "en");
        rig.Show();
        var picker = rig.Page.Picker;
        picker.Refresh();
        Assert.Contains(picker.Visible, e => e.IsHeader && e.Title == "NVIDIA GeForce RTX 5080");
        picker.Search.Text = "hot spot";
        var rows = picker.Visible.Where(e => !e.IsHeader).ToList();
        Assert.Equal("GPU Hot Spot", Assert.Single(rows).Name);
        Assert.Single(picker.Visible, e => e.IsHeader);
        picker.Search.Text = "rtx temperature";
        Assert.All(picker.Visible.Where(e => !e.IsHeader), e => Assert.Equal(SensorKind.Temperature, e.Descriptor!.Kind));
        picker.Search.Text = "zzz";
        Assert.Empty(picker.Visible);
        picker.Search.Text = "hot spot";
        picker.Activate(picker.Visible.Single(e => !e.IsHeader));
        Assert.Contains(rig.Page.ShownIds, id => rig.Hub.Find(id)!.Name == "GPU Hot Spot");
    });

    [Fact]
    public void Presets_zoom_and_back_to_live() => Sta.Run(() =>
    {
        using var rig = new HistoryRig();
        rig.Show();
        var before = rig.Page.QueryCount;
        rig.Page.ApplyPreset(RangePreset.Day7, animate: false);
        rig.Settle();
        Assert.Equal(7 * 24 * H, rig.Page.Chart.View.Span);
        Assert.Equal(rig.Now, rig.Page.Chart.View.To);
        Assert.True(rig.Page.QueryCount > before);
        // a 7-day window is served from coarse buckets: peaks stay (max ≥ every avg)
        var s = rig.Page.Chart.Series[0];
        Assert.True(s.Bucket >= M);
        Assert.All(s.Points, p => Assert.True(p.Max >= p.Avg && p.Min <= p.Avg));

        var plot = rig.Page.Chart.PlotRect;
        var queries = rig.Page.QueryCount;
        rig.Page.Chart.Wheel(new Point(plot.Left + plot.Width * 0.3, plot.Top + 10), 240);
        Assert.Equal(RangePreset.None, rig.Page.Preset);
        Assert.False(rig.Page.IsLiveView);
        Assert.True(Sta.PumpUntil(() => rig.Page.QueryCount > queries, 3000), "zoom re-queries (debounced)");
        rig.Settle();
        Assert.True(rig.Page.Chart.Series[0].Bucket < s.Bucket || rig.Page.Chart.View.Span < 7 * 24 * H);
    });

    [Fact]
    public void Live_append_follows_now_at_most_once_a_second() => Sta.Run(() =>
    {
        using var rig = new HistoryRig();
        rig.Show();
        var first = rig.Page.Chart.Series[0];
        var lastBefore = first.Points[^1].TimeUtcTicks;
        HistoryRenderTests.FeedLive(rig, 30);
        Assert.Equal(rig.Now, rig.Page.Chart.View.To);
        Assert.Equal(H, rig.Page.Chart.View.Span);
        Assert.True(first.Points[^1].TimeUtcTicks > lastBefore);
        Assert.True(first.Points[^1].TimeUtcTicks >= rig.Now - first.Bucket);

        // a frame from the engine thread reaches the UI only through ApplyLive (coalesced)
        Assert.True(rig.Page.IsLiveView);
        rig.Page.Chart.Wheel(new Point(rig.Page.Chart.PlotRect.Left + 10, 50), 120); // zoom into the past: no longer live
        var view = rig.Page.Chart.View;
        HistoryRenderTests.FeedLive(rig, 5);
        Assert.Equal(view, rig.Page.Chart.View);
    });

    [Fact]
    public void A_selection_gets_its_own_exact_stats() => Sta.Run(() =>
    {
        using var rig = new HistoryRig();
        rig.Show();
        var preview = (PreviewHistory)rig.History!;
        var queries = preview.StatsQueries;
        var sel = (rig.Now - 40 * M, rig.Now - 25 * M);
        rig.Page.Chart.SetSelection(sel);
        rig.Settle();
        Assert.True(preview.StatsQueries > queries);
        Assert.StartsWith(HistoryModel.RangeText(sel.Item1, sel.Item2, rig.Now, HistoryRig.Warsaw, BetterVitals.Localization.Loc.I.Culture), rig.Page.StatsPanel.ScopeText);
        var row = rig.Page.StatsPanel.Rows[0];
        Assert.InRange(row.Stats!.Value.MinTimeUtcTicks, sel.Item1, sel.Item2);
        Assert.InRange(row.Stats!.Value.MaxTimeUtcTicks, sel.Item1, sel.Item2);
        var exact = preview.GetStatsAsync(row.Descriptor!.Id, new DateTime(sel.Item1, DateTimeKind.Utc), new DateTime(sel.Item2, DateTimeKind.Utc)).Result!.Value;
        Assert.Equal(exact.Max, row.Stats.Value.Max);

        rig.Page.Chart.SetSelection(null);
        rig.Settle();
        Assert.StartsWith(HistoryModel.RangeText(rig.Now - H, rig.Now, rig.Now, HistoryRig.Warsaw, BetterVitals.Localization.Loc.I.Culture), rig.Page.StatsPanel.ScopeText);
    });

    [Fact]
    public void Live_only_shows_fifteen_minutes_with_a_note_and_a_way_to_record() => Sta.Run(() =>
    {
        using var rig = new HistoryRig(recording: false);
        rig.Show();
        Assert.Equal(RangePreset.Min15, rig.Page.Preset);
        rig.Page.ApplyPreset(RangePreset.Hour24, animate: false);
        Assert.Equal(RangePreset.Min15, rig.Page.Preset);
        Assert.Equal(1, rig.Page.NoteCount);
        Assert.Contains(BetterVitals.Localization.Loc.T("history.liveOnly.banner"), rig.Page.NoteTexts);
        ClickNoteAction(rig.Page);
        Assert.Equal(PageIds.Sensors, Assert.Single(rig.Panel.Navigations).Id);
        // the session line ("since start") appears in live-only stats
        Assert.All(rig.Page.StatsPanel.Rows, r => Assert.NotNull(r.Session));

        rig.Store.Mutate("recording", s => s.Recording.Enabled = true);
        Sta.Pump(30);
        Assert.Equal(0, rig.Page.NoteCount);
    });

    [Fact]
    public void A_sensor_that_is_not_recorded_can_be_recorded_with_undo() => Sta.Run(() =>
    {
        using var rig = new HistoryRig(record: d => d.Name != "GPU Core");
        rig.Show();
        Assert.Equal(1, rig.Page.NoteCount);
        var gpu = rig.Page.ShownIds.Select(id => rig.Hub.Find(id)!).Single(d => d.Name == "GPU Core");
        ClickNoteAction(rig.Page);
        Assert.Contains(gpu.Id, rig.Store.Current.Recording.SensorIds);
        var toast = Assert.Single(rig.Panel.Toasts);
        Assert.Contains("GPU Core", toast.Text);
        Sta.Pump(30);
        Assert.Equal(0, rig.Page.NoteCount);
        toast.Action!();
        Assert.DoesNotContain(gpu.Id, rig.Store.Current.Recording.SensorIds);
    });

    [Fact]
    public void Empty_states() => Sta.Run(() =>
    {
        using (var rig = new HistoryRig(hub: NullSensorHub.Instance))
        {
            rig.Show();
            Assert.Equal(HistoryEmpty.PickSensor, rig.Page.EmptyState);
            Assert.Null(rig.Page.StatsPanel.Rows.FirstOrDefault());
        }
        using (var rig = new HistoryRig(history: new FakeHistory(coverage: null)))
        {
            rig.Show();
            Assert.Equal(HistoryEmpty.NothingRecorded, rig.Page.EmptyState);
        }
        using (var rig = new HistoryRig(history: new FakeHistory(coverage: (HistoryRig.NowUtc.AddDays(-9), HistoryRig.NowUtc.AddDays(-8)))))
        {
            rig.Show();
            Assert.Equal(HistoryEmpty.NothingInRange, rig.Page.EmptyState);
        }
        using (var rig = new HistoryRig(history: new FakeHistory(coverage: null), record: _ => false))
        {
            rig.Show();
            Assert.Equal(HistoryEmpty.NotRecorded, rig.Page.EmptyState);
        }
    });

    [Fact]
    public void The_page_costs_nothing_while_hidden() => Sta.Run(() =>
    {
        var watches = new List<(string[] Ids, Handle H)>();
        var saved = AppServices.WatchHistory;
        AppServices.WatchHistory = ids =>
        {
            var h = new Handle();
            watches.Add((ids.ToArray(), h));
            return h;
        };
        try
        {
            using var rig = new HistoryRig();
            Assert.Empty(watches); // constructing the page starts nothing
            rig.Show();
            Assert.Equal(3, Assert.Single(watches).Ids.Length);
            rig.Page.TogglePicked(rig.Sensor("GPU Hot Spot", SensorKind.Temperature));
            Assert.Equal(2, watches.Count);
            Assert.True(watches[0].H.Disposed);
            Assert.False(watches[1].H.Disposed);
            rig.Page.OnNavigatedFrom();
            Assert.All(watches, w => Assert.True(w.H.Disposed));
            var queries = rig.Page.QueryCount;
            rig.Page.ApplyLive();
            rig.Page.Query(fade: false);
            Assert.Equal(queries, rig.Page.QueryCount);
        }
        finally
        {
            AppServices.WatchHistory = saved;
        }
    });

    [Fact]
    public void Custom_range_from_the_calendar() => Sta.Run(() =>
    {
        using var rig = new HistoryRig();
        rig.Show();
        var r = rig.Page.RangePickerPanel;
        r.Open(rig.Page.Chart.View);
        r.PickDay(new DateTime(2026, 10, 22));
        r.PickDay(new DateTime(2026, 10, 19)); // backwards: swapped
        r.SetTimes("08:00", "23:30");
        var range = r.Result()!.Value;
        Assert.Equal(new DateTime(2026, 10, 19, 6, 0, 0, DateTimeKind.Utc).Ticks, range.From); // CEST
        Assert.Equal(new DateTime(2026, 10, 22, 21, 30, 0, DateTimeKind.Utc).Ticks, range.To);
        r.SetTimes("8", "nonsense");
        Assert.Null(r.Result());
        r.SetTimes("08:00", "23:30");
        rig.Page.ApplyCustom(range.From, range.To);
        rig.Settle();
        Assert.Equal(RangePreset.Custom, rig.Page.Preset);
        Assert.False(rig.Page.IsLiveView);
        Assert.Equal(new TimeView(range.From, range.To), rig.Page.Chart.View);
    });

    [Fact]
    public void Export_defaults_and_writes_a_zip_with_an_open_folder_action() => Sta.Run(() =>
    {
        var saved = AppServices.Platform;
        var platform = new RecordingPlatform();
        AppServices.Platform = platform;
        var path = Path.Combine(HistoryRig.OutputDir, "export-test-" + Guid.NewGuid().ToString("N")[..6] + ".csv.zip");
        try
        {
            using var rig = new HistoryRig();
            rig.Show();
            rig.Page.PickPath = (name, zip) =>
            {
                Assert.Equal("BetterVitals-2026-10-23.csv.zip", name);
                Assert.True(zip);
                return path;
            };
            rig.Page.OpenExport();
            var sheet = rig.Page.Sheet!;
            var req = sheet.BuildRequest();
            Assert.Equal(rig.Page.ShownIds, req.SensorIds);
            Assert.Equal(HistoryModel.ExportRange.Visible, sheet.Range);
            Assert.Equal(ExportResolution.Raw, req.Resolution);
            Assert.True(req.Zip);
            Assert.Equal(rig.Now - H, req.FromUtc.Ticks);
            sheet.RunEstimate().Wait();
            Sta.Pump(30);
            Assert.Contains("KB", sheet.EstimateText);

            sheet.SetSelected(rig.Page.ShownIds[2], false);
            Assert.Equal(2, sheet.BuildRequest().SensorIds.Count);
            sheet.SelectResolution(ExportResolution.Minute);
            // as from the Save button: inside a dispatcher operation (the UI synchronization context)
            var task = System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(sheet.SaveAsync).Task.Unwrap();
            Assert.True(Sta.PumpUntil(() => task.IsCompleted, 10000));
            Assert.Null(rig.Page.Sheet); // closes after saving
            using (var zip = ZipFile.OpenRead(path))
            {
                var entry = Assert.Single(zip.Entries);
                using var reader = new StreamReader(entry.Open());
                var header = reader.ReadLine()!;
                Assert.StartsWith("time_utc,Core (Tctl/Tdie)", header);
                Assert.Equal(3, header.Split(',').Length);
                Assert.InRange(reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length, 55, 65); // 1 h of minutes
            }
            var toast = Assert.Single(rig.Panel.Toasts);
            Assert.Equal(BetterVitals.Localization.Loc.T("history.export.openFolder"), toast.ActionText);
            toast.Action!();
            Assert.Equal(HistoryRig.OutputDir, platform.Opened);

            // with a selection the sheet starts from it
            rig.Page.Chart.SetSelection((rig.Now - 20 * M, rig.Now - 5 * M));
            rig.Page.OpenExport();
            Assert.Equal(HistoryModel.ExportRange.Selection, rig.Page.Sheet!.Range);
            Assert.Equal(rig.Now - 20 * M, rig.Page.Sheet.BuildRequest().FromUtc.Ticks);
            rig.Page.Sheet.Close();
            Assert.Null(rig.Page.Sheet);
        }
        finally
        {
            AppServices.Platform = saved;
            if (File.Exists(path)) File.Delete(path);
        }
    });

    private static void ClickNoteAction(HistoryPage page)
    {
        var button = Descendants<Button>(page).First(b => b.Parent is DockPanel { Parent: Border { Tag: string } });
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var d in Descendants<T>(c)) yield return d;
        }
    }

    private sealed class Handle : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingPlatform : IPlatformService
    {
        public string? Opened { get; private set; }

        public void OpenFolder(string path) => Opened = path;

        public bool IsElevated => false;
        public bool IsInstalled => false;
        public Version? PawnIoVersion => null;
        public bool PawnIoOutdated => false;
        public Version PawnIoBundledVersion => new(2, 2);
        public SecurityInfo Security { get; } = new(null, null, null);
        public string? AntiCheatPauseReason => null;
        public Task<bool> InstallFullModeAsync(bool autostart, bool installPawnIo) => Task.FromResult(false);
        public Task<PawnIoInstallOutcome?> InstallPawnIoAsync() => Task.FromResult<PawnIoInstallOutcome?>(null);
        public Task<bool> SetAutostartAsync(bool enabled) => Task.FromResult(false);
        public bool AutostartEnabled => false;
        public UpdateUiState UpdateState => UpdateUiState.Idle;
        public BetterVitals.Platform.Updates.ReleaseInfo? AvailableRelease => null;
        public double UpdateProgress => 0;
        public event Action? StateChanged { add { } remove { } }
        public Task CheckForUpdatesAsync() => Task.CompletedTask;
        public Task DownloadUpdateAsync() => Task.CompletedTask;
        public void RestartToUpdate() { }
        public void OpenUrl(string url) { }
    }

    /// <summary>A store without points in any window; coverage as given.</summary>
    private sealed class FakeHistory((DateTime, DateTime)? coverage) : IHistoryQuery
    {
        public Task<IReadOnlyList<HistoryPoint>> GetSeriesAsync(string sensorId, DateTime fromUtc, DateTime toUtc, int maxPoints, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HistoryPoint>>([]);

        public Task<RangeStats?> GetStatsAsync(string sensorId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => Task.FromResult<RangeStats?>(null);

        public (DateTime FromUtc, DateTime ToUtc)? GetCoverage(string sensorId) => coverage;

        public HistoryUsage GetUsage() => new(0, 0, null);

        public long EstimateExportBytes(ExportRequest request) => 0;

        public Task ExportAsync(ExportRequest request, Stream output, IProgress<double>? progress = null, CancellationToken ct = default) => Task.CompletedTask;

        public Task ClearAsync() => Task.CompletedTask;
    }
}
