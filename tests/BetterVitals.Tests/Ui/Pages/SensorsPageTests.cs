using Xunit;
using PageUi = BetterVitals.UI.Pages.Shared.Ui;
using BetterVitals.Core.Catalog;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.UI.Pages.Sensors;
using BetterVitals.UI.Pages.Shared;
using BetterVitals.UI.Panel;

namespace BetterVitals.Tests.Ui.Pages;

[Collection(UiCollection.Name)]
public sealed class SensorsPageTests
{
    [Fact]
    public void Presets_replace_the_recorded_set_and_undo_restores_it() => Sta.Run(() =>
    {
        using var env = new PageEnv();
        using var pc = new FakePc();
        var key = FriendlyCatalog.KeyPresetSensorIds(FriendlyCatalog.Resolve(pc.Engine.Hardware, pc.Engine.Sensors));
        env.Store.Mutate("recording", s => s.Recording.SensorIds.Add("gone/with/old-hardware"));

        env.Actions.ApplyPreset(RecordingPreset.Key, pc.Engine);
        var ids = env.Store.Current.Recording.SensorIds;
        Assert.True(key.IsSubsetOf(ids));
        Assert.Equal(key.Count + 1, ids.Count); // sensors of hardware not present now are left alone
        Assert.True(env.Store.Current.Recording.KeyPresetApplied);
        Assert.StartsWith("“Key” preset: ", env.Toasts[^1].Text);

        env.Actions.ApplyPreset(RecordingPreset.All, pc.Engine);
        Assert.Equal(pc.Engine.Sensors.Count + 1, env.Store.Current.Recording.SensorIds.Count);

        env.Actions.ApplyPreset(RecordingPreset.None, pc.Engine);
        Assert.Equal(["gone/with/old-hardware"], env.Store.Current.Recording.SensorIds);
        // "Nothing" opts out of the key sensors: the policy must not re-add them
        Assert.True(key.All(env.Exclusions.Contains));
        Assert.Empty(RecordingPolicy.MissingKeySensors(env.Store.Current.Recording, FriendlyCatalog.Resolve(pc.Engine.Hardware, pc.Engine.Sensors), env.Exclusions.Snapshot()));

        env.Undo();
        Assert.Equal(pc.Engine.Sensors.Count + 1, env.Store.Current.Recording.SensorIds.Count);
        Assert.DoesNotContain(key, env.Exclusions.Contains);
    });

    [Fact]
    public void A_key_sensor_switched_off_stays_off_for_the_policy() => Sta.Run(() =>
    {
        using var env = new PageEnv();
        using var pc = new FakePc();
        var items = FriendlyCatalog.Resolve(pc.Engine.Hardware, pc.Engine.Sensors);
        env.Actions.ApplyPreset(RecordingPreset.Key, pc.Engine);
        var hot = pc.Sensor("NVIDIA GeForce RTX 5080", SensorKind.Temperature, "GPU Hot Spot");
        env.Actions.SetRecorded([hot], false);
        Assert.Empty(RecordingPolicy.MissingKeySensors(env.Store.Current.Recording, items, env.Exclusions.Snapshot()));
        Assert.Equal([hot.Id], RecordingPolicy.MissingKeySensors(env.Store.Current.Recording, items, new HashSet<string>()));
    });

    [Fact]
    public void Filters_search_kind_chips_and_only_selected() => Sta.Run(() =>
    {
        using var env = new PageEnv();
        using var pc = new FakePc();
        var model = new SensorsListModel(pc.Engine, env.Actions);
        var all = model.VisibleCount;
        Assert.Equal(pc.Engine.Sensors.Count, all);
        Assert.Equal(model.Sections.Count + all, model.Items.Count);
        Assert.Contains(KindGroup.Temperature, model.AvailableKinds);
        Assert.DoesNotContain(KindGroup.Other, model.AvailableKinds);

        model.Search = "hot spot";
        Assert.Equal(1, model.VisibleCount);
        Assert.Equal(2, model.Items.Count); // one header + one row
        model.Search = "kingston"; // hardware name
        Assert.Equal(pc.Engine.Sensors.Count(s => pc.Engine.FindHardware(s.HardwareId)!.Name.StartsWith("KINGSTON")), model.VisibleCount);
        model.Search = "";

        model.ToggleKind(KindGroup.Fan);
        Assert.All(model.Items.OfType<SensorRowModel>(), r => Assert.Equal(KindGroup.Fan, r.Group));
        Assert.DoesNotContain(model.Items.OfType<SectionModel>(), s => s.Group.Kind == HardwareKind.Cpu); // no fans on the CPU
        model.ToggleKind(KindGroup.Fan);
        Assert.Equal(all, model.VisibleCount);

        var hot = pc.Sensor("NVIDIA GeForce RTX 5080", SensorKind.Temperature, "GPU Hot Spot");
        env.Actions.SetOnOverlay([hot], true);
        model.Sync(env.Store.Current);
        model.OnlySelected = true;
        Assert.Equal([hot.Id], model.Items.OfType<SensorRowModel>().Select(r => r.Sensor.Id));
        model.ClearFilters();
        Assert.False(model.HasFilter);
        Assert.Equal(all, model.VisibleCount);
    });

    [Fact]
    public void Section_switch_is_tri_state_over_the_filtered_rows() => Sta.Run(() =>
    {
        using var env = new PageEnv();
        using var pc = new FakePc();
        var model = new SensorsListModel(pc.Engine, env.Actions);
        var gpu = model.Sections.Single(s => s.Name == "NVIDIA GeForce RTX 5080");
        Assert.False(gpu.RecordState);

        var hot = gpu.Rows.Single(r => r.Name == "GPU Hot Spot");
        hot.ToggleRecorded();
        model.Sync(env.Store.Current);
        Assert.Null(gpu.RecordState);

        gpu.ToggleRecord(); // partial → all on
        model.Sync(env.Store.Current);
        Assert.True(gpu.RecordState);
        Assert.All(gpu.Rows, r => Assert.True(r.IsRecorded));

        gpu.ToggleRecord(); // all on → all off
        model.Sync(env.Store.Current);
        Assert.False(gpu.RecordState);
        env.Undo();
        model.Sync(env.Store.Current);
        Assert.True(gpu.RecordState);

        // with a filter the switch covers only what is listed
        model.ToggleKind(KindGroup.Temperature);
        gpu.ToggleOverlay();
        model.Sync(env.Store.Current);
        Assert.True(gpu.OverlayState);
        Assert.Equal(3, env.Store.Current.Overlay.Items.Count);
        model.ToggleKind(KindGroup.Temperature);
        Assert.Null(gpu.OverlayState);
    });

    [Fact]
    public void Collapsing_a_section_edits_the_list_in_place() => Sta.Run(() =>
    {
        using var env = new PageEnv();
        using var pc = new FakePc();
        var model = new SensorsListModel(pc.Engine, env.Actions);
        var items = model.Items;
        var cpu = model.Sections[0];
        cpu.ToggleCollapsed();
        Assert.Same(items, model.Items);
        Assert.Equal(model.Sections.Count + pc.Engine.Sensors.Count - cpu.Rows.Count, model.Items.Count);
        Assert.IsType<SectionModel>(model.Items[1]);
        cpu.ToggleCollapsed();
        Assert.Equal(cpu.Rows[0], model.Items[1]);
        model.SetAllCollapsed(true);
        Assert.Equal(model.Sections.Count, model.Items.Count);
    });

    [Fact]
    public void Status_summary_reads_like_the_design() => Sta.Run(() =>
    {
        using var env = new PageEnv(language: "pl");
        using var pc = new FakePc();
        var r = new RecordingSettings { IntervalSeconds = 2 };
        foreach (var s in pc.Engine.Sensors.Take(42)) r.SensorIds.Add(s.Id);
        r.SensorIds.Add("not/present");
        var perDay = PageUi.Bytes(RecordingPolicy.EstimateBytesPerDay(42, 2), Loc.I.Culture);
        Assert.Equal($"42 czujniki · ~{perDay}/dzień · co 2 s · zajęte 61 MB", SensorsPage.Summary(r, pc.Engine, 61_000_000));
        Assert.Equal($"42 czujniki · ~{perDay}/dzień · co 2 s", SensorsPage.Summary(r, pc.Engine, null));
        r.Enabled = false;
        Assert.Equal("42 czujniki · nic nie jest zapisywane", SensorsPage.Summary(r, pc.Engine, null));
        Loc.I.SetLanguage("en");
        r.Enabled = true;
        Assert.StartsWith("42 sensors · ~", SensorsPage.Summary(r, pc.Engine, null));
    });

    [Fact]
    public void Master_switch_and_history_column_follow_recording() => Sta.Run(() =>
    {
        using var env = new PageEnv();
        using var pc = new FakePc();
        var page = new SensorsPage(pc.Engine, env.Actions, null);
        page.OnNavigatedTo(null);
        Assert.False(page.LiveOnlyVisible);
        env.Actions.SetRecordingEnabled(false);
        Assert.False(env.Store.Current.Recording.Enabled);
        Assert.True(page.LiveOnlyVisible);
        var row = page.Model.AllRows.First();
        Assert.Equal(0.4, row.HistoryOpacity);
        Assert.Equal(Loc.T("sensors.row.historyOffTip"), row.HistoryTip);
        env.Undo();
        Assert.True(env.Store.Current.Recording.Enabled);
        Assert.False(page.LiveOnlyVisible);
        Assert.Equal(1, row.HistoryOpacity);
        page.OnNavigatedFrom();
    });

    [Fact]
    public void Status_link_and_banner_navigate_to_settings_sections() => Sta.Run(() =>
    {
        using var env = new PageEnv();
        env.Actions.Navigate(PageIds.Settings, UI.Pages.Overview.SettingsLinks.Recording);
        Assert.Equal((PageIds.Settings, (object?)"recording"), env.Navigations[^1]);
    });

    [Theory]
    [InlineData("aurora", "en")]
    [InlineData("daylight", "pl")]
    [InlineData("graphite", "en")]
    [InlineData("neon", "pl")]
    [InlineData("sunset", "en")]
    public void Sensors_page_renders_virtualized_and_demands_only_shown_rows(string theme, string language) => Sta.Run(() =>
    {
        using var env = new PageEnv(language: language);
        using var pc = new FakePc();
        env.Actions.ApplyPreset(RecordingPreset.Key, pc.Engine);
        var page = new SensorsPage(pc.Engine, env.Actions, null);
        using var host = new HiddenHost(page, 884, 740, theme);
        page.OnNavigatedTo(null);
        for (var i = 0; i < 30; i++)
        {
            pc.Run(TimeSpan.FromSeconds(1));
            page.Apply();
        }
        host.Settle(120);
        page.Apply();
        var realized = HiddenHost.Descendants<LiveSensorRowView>(page.List).Count();
        Assert.InRange(realized, 5, 40); // virtualized: only what fits (plus a little cache)
        Assert.True(page.DemandedIds.Count > 0 && page.DemandedIds.Count < pc.Engine.Sensors.Count);
        Assert.Contains(pc.Engine.Demands, d => d.Consumer == "sensors" && d.Sensors == page.DemandedIds.Count);
        var path = host.Save($"sensors_{theme}_{language}");
        Assert.True(new FileInfo(path).Length > 20_000);

        // live only + a filter
        env.Actions.SetRecordingEnabled(false);
        page.Model.ToggleKind(KindGroup.Temperature);
        host.Settle(120);
        page.Apply();
        host.Save($"sensors_liveonly_{theme}_{language}");

        page.OnNavigatedFrom();
        Assert.DoesNotContain(pc.Engine.Demands, d => d.Consumer == "sensors");
        Assert.DoesNotContain(Loc.MissingKeys, k => k.StartsWith("sensors.", StringComparison.Ordinal));
    });

    [Fact]
    public void Four_hundred_sensors_stay_virtualized_and_quick() => Sta.Run(() =>
    {
        using var env = new PageEnv();
        using var hub = new BigHub(400);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var page = new SensorsPage(hub, env.Actions, null);
        using var host = new HiddenHost(page, 1000, 800);
        page.OnNavigatedTo(null);
        host.Settle(100);
        var build = sw.ElapsedMilliseconds;
        Assert.Equal(400, page.Model.VisibleCount);
        Assert.InRange(HiddenHost.Descendants<LiveSensorRowView>(page.List).Count(), 5, 40);

        // scrolling through the whole list realizes rows as needed and keeps the demand on screen
        var scroller = HiddenHost.Descendants<System.Windows.Controls.ScrollViewer>(page.List).First();
        sw.Restart();
        for (var y = 0.0; y < scroller.ScrollableHeight; y += 300)
        {
            scroller.ScrollToVerticalOffset(y);
            host.Root.UpdateLayout();
            page.Apply();
        }
        var scroll = sw.ElapsedMilliseconds;
        Assert.InRange(HiddenHost.Descendants<LiveSensorRowView>(page.List).Count(), 5, 40);
        Assert.InRange(page.DemandedIds.Count, 5, 40);
        Assert.True(build < 4000, $"build {build} ms");
        Assert.True(scroll < 6000, $"scroll {scroll} ms");
        page.OnNavigatedFrom();
    });

    /// <summary>A hub with many sensors over a few hardware nodes (no engine thread).</summary>
    private sealed class BigHub : ISensorHub, IDisposable
    {
        private readonly List<HardwareDescriptor> _hw = [];
        private readonly List<SensorDescriptor> _sensors = [];
        private readonly SensorFrame _frame;

        public BigHub(int count)
        {
            var kinds = new[] { SensorKind.Temperature, SensorKind.Clock, SensorKind.Load, SensorKind.Voltage, SensorKind.Power, SensorKind.Fan };
            for (var h = 0; h < 8; h++)
                _hw.Add(new HardwareDescriptor { Id = $"cpu/x/{h}", Name = $"Device {h}", Kind = h == 0 ? HardwareKind.Cpu : HardwareKind.Controller, Source = "fake" });
            for (var i = 0; i < count; i++)
            {
                var hw = _hw[i % _hw.Count];
                _sensors.Add(new SensorDescriptor { Id = $"{hw.Id}/s{i}", HardwareId = hw.Id, Name = $"Sensor #{i}", Kind = kinds[i % kinds.Length], HardwareKind = hw.Kind, Order = i, Index = i });
            }
            _frame = new SensorFrame(count);
            _frame.Count = count;
            for (var i = 0; i < count; i++) _frame.Values[i] = 40 + i % 50;
        }

        public IReadOnlyList<HardwareDescriptor> Hardware => _hw;
        public IReadOnlyList<SensorDescriptor> Sensors => _sensors;
        public bool IsPresent(int index) => true;
        public SensorDescriptor? Find(string sensorId) => _sensors.FirstOrDefault(s => s.Id == sensorId);
        public HardwareDescriptor? FindHardware(string hardwareId) => _hw.FirstOrDefault(h => h.Id == hardwareId);
        public SensorFrame Latest => _frame;
        public SessionStats Stats { get; } = new();
        public IDemand Demand(string consumer, IEnumerable<string> sensorIds, TimeSpan interval) => new NoDemand();
        public event Action<SensorFrame>? FrameReady { add { } remove { } }
        public event Action? InventoryChanged { add { } remove { } }
        public IReadOnlyList<(string Source, SourceStatus Status)> SourceStatuses => [];
        public void Dispose() { }

        private sealed class NoDemand : IDemand
        {
            public void Update(IEnumerable<string> sensorIds, TimeSpan interval) { }
            public void Dispose() { }
        }
    }
}
