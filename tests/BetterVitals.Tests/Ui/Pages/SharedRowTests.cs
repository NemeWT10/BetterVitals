using Xunit;
using PageUi = BetterVitals.UI.Pages.Shared.Ui;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.UI.Pages.Shared;
using BetterVitals.UI.Panel;

namespace BetterVitals.Tests.Ui.Pages;

[Collection(UiCollection.Name)]
public sealed class SharedRowTests
{
    [Fact]
    public void Hardware_groups_come_in_the_expected_order_with_children_merged()
    {
        using var pc = new FakePc();
        var groups = HardwareTree.Build(pc.Engine);
        Assert.Equal(
            ["AMD Ryzen 7 9800X3D", "NVIDIA GeForce RTX 5080", "AMD Radeon(TM) Graphics", "Generic Memory", "Gigabyte X870E AORUS ELITE WIFI7",
             "KINGSTON SKC3000D2048G", "ST4000DM004-2CV104", "Ethernet", "Foreground app"],
            groups.Select(g => g.Name));
        var board = groups.Single(g => g.Kind == HardwareKind.Motherboard);
        Assert.Equal(2, board.Members.Count);
        Assert.Contains(board.Sensors, s => s.Name == "VRM MOS");
        Assert.All(groups, g => Assert.NotEmpty(g.Sensors));
        // derived sensors sit on the CPU card
        Assert.Contains(groups[0].Sensors, s => s.Name == "CPU Clock Max");
    }

    [Fact]
    public void Absent_hardware_is_hidden()
    {
        using var pc = new FakePc();
        var hdd = pc.Hardware("ST4000DM004-2CV104");
        pc.Fake.SetPresent(hdd.Id, false);
        pc.Rediscover();
        Assert.DoesNotContain(HardwareTree.Build(pc.Engine), g => g.Name == "ST4000DM004-2CV104");
        pc.Fake.SetPresent(hdd.Id, true);
        pc.Rediscover();
        Assert.Contains(HardwareTree.Build(pc.Engine), g => g.Name == "ST4000DM004-2CV104");
    }

    [Fact]
    public void Kind_groups_cover_every_sensor_kind()
    {
        foreach (var kind in Enum.GetValues<SensorKind>()) Assert.True(Loc.Has(SensorGroups.LabelKey(SensorGroups.Of(kind))), kind.ToString());
        Assert.Equal(KindGroup.Fan, SensorGroups.Of(SensorKind.Control));
        Assert.Equal(KindGroup.Clock, SensorGroups.Of(SensorKind.Frequency));
    }

    [Fact]
    public void Record_switch_writes_settings_marks_exclusions_and_undoes() => Sta.Run(() =>
    {
        using var env = new PageEnv();
        using var pc = new FakePc();
        var core = pc.Sensor("AMD Ryzen 7 9800X3D", SensorKind.Temperature, "Core (Tctl/Tdie)");
        var row = new SensorRowModel(core, "AMD Ryzen 7 9800X3D", env.Actions);
        row.Sync(env.Store.Current);
        Assert.False(row.IsRecorded);

        row.ToggleRecorded();
        Assert.Contains(core.Id, env.Store.Current.Recording.SensorIds);
        Assert.Equal("Recording for history: Core (Tctl/Tdie)", env.Toasts[^1].Text);
        Assert.Equal("Undo", env.Toasts[^1].ActionText);
        row.Sync(env.Store.Current);
        Assert.True(row.IsRecorded);

        row.ToggleRecorded();
        Assert.DoesNotContain(core.Id, env.Store.Current.Recording.SensorIds);
        Assert.True(env.Exclusions.Contains(core.Id));

        env.Undo();
        Assert.Contains(core.Id, env.Store.Current.Recording.SensorIds);
        Assert.False(env.Exclusions.Contains(core.Id));

        // switching on takes a sensor out of the explicitly-removed set
        env.Exclusions.Exclude([core.Id]);
        env.Store.Mutate("recording", s => s.Recording.SensorIds.Remove(core.Id));
        env.Actions.SetRecorded([core], true);
        Assert.False(env.Exclusions.Contains(core.Id));
    });

    [Fact]
    public void Exclusions_survive_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), "bv-excl-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            new RecordingExclusions(path).Exclude(["a", "b"]);
            var again = new RecordingExclusions(path);
            Assert.True(again.Contains("a"));
            Assert.True(again.Contains("b"));
            again.Include(["a"]);
            Assert.False(new RecordingExclusions(path).Contains("a"));
            File.WriteAllText(path, "{ not json");
            Assert.Empty(new RecordingExclusions(path).Snapshot());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Overlay_switch_adds_a_single_item_and_removes_the_sensor_everywhere() => Sta.Run(() =>
    {
        using var env = new PageEnv();
        using var pc = new FakePc();
        var hot = pc.Sensor("NVIDIA GeForce RTX 5080", SensorKind.Temperature, "GPU Hot Spot");
        var core = pc.Sensor("NVIDIA GeForce RTX 5080", SensorKind.Temperature, "GPU Core");
        var multi = new OverlayItem { SensorIds = [core.Id, hot.Id] };
        env.Store.Mutate("overlay", s => s.Overlay.AddItem(multi, "row-1"));

        env.Actions.SetOnOverlay([hot], false);
        Assert.Equal([core.Id], multi.SensorIds);
        Assert.Single(env.Store.Current.Overlay.AllItems());

        env.Actions.SetOnOverlay([hot], true);
        Assert.Equal(2, env.Store.Current.Overlay.AllItems().Count());
        Assert.Equal([hot.Id], env.Store.Current.Overlay.AllItems().Last().SensorIds);
        Assert.Equal("On the overlay: GPU Hot Spot", env.Toasts[^1].Text);

        // removing the last sensor of an item removes the item; undo puts the multi item back intact
        env.Actions.SetOnOverlay([hot, core], false);
        Assert.True(env.Store.Current.Overlay.IsEmpty);
        env.Undo();
        Assert.Equal(2, env.Store.Current.Overlay.AllItems().Count());
        Assert.Same(multi, env.Store.Current.Overlay.AllItems().First());
        Assert.Equal([core.Id], multi.SensorIds);
        Assert.True(SensorToggles.IsOnOverlay(env.Store.Current, hot.Id));
    });

    [Fact]
    public void Pin_in_flyout_keeps_at_most_four() => Sta.Run(() =>
    {
        using var env = new PageEnv();
        using var pc = new FakePc();
        var sensors = pc.Engine.Sensors.Take(5).ToList();
        for (var i = 0; i < 4; i++) env.Actions.SetPinned(sensors[i], true);
        Assert.Equal(4, env.Store.Current.Flyout.PinnedSensorIds.Count);
        env.Actions.SetPinned(sensors[4], true);
        Assert.Equal(4, env.Store.Current.Flyout.PinnedSensorIds.Count);
        Assert.Equal(BetterVitals.UI.Toasts.ToastKind.Warning, env.Toasts[^1].Kind);
        env.Actions.SetPinned(sensors[0], false);
        Assert.DoesNotContain(sensors[0].Id, env.Store.Current.Flyout.PinnedSensorIds);
        env.Undo();
        Assert.Equal(sensors[0].Id, env.Store.Current.Flyout.PinnedSensorIds[0]);
    });

    [Fact]
    public void Context_menu_offers_the_four_actions_and_show_in_history_navigates() => Sta.Run(() =>
    {
        using var env = new PageEnv();
        using var pc = new FakePc();
        var s = pc.Sensor("NVIDIA GeForce RTX 5080", SensorKind.Temperature, "GPU Hot Spot");
        var row = new SensorRowModel(s, "NVIDIA GeForce RTX 5080", env.Actions);
        env.Store.Mutate("recording", x => x.Recording.SensorIds.Add(s.Id));
        row.Sync(env.Store.Current);
        var menu = SensorRowView.BuildMenu(row);
        var items = menu.Items.OfType<System.Windows.Controls.MenuItem>().ToList();
        Assert.Equal(["Record for history", "Show on overlay", "Pin in the flyout", "Show in history"], items.Select(i => (string)i.Header));
        Assert.True(items[0].IsChecked);
        Assert.False(items[1].IsChecked);
        items[3].RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
        Assert.Equal((PageIds.History, (object?)s.Id), env.Navigations[^1]);
        items[2].RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
        Assert.Contains(s.Id, env.Store.Current.Flyout.PinnedSensorIds);
    });

    [Fact]
    public void Row_formats_values_and_stats_in_the_ui_culture() => Sta.Run(() =>
    {
        using var env = new PageEnv(language: "pl");
        using var pc = new FakePc();
        pc.Run(TimeSpan.FromSeconds(20));
        // the board's sensors live on its SuperIO child
        var v = pc.Engine.Sensors.Single(x => x.Name == "+12V");
        var row = new SensorRowModel(v, "x", env.Actions);
        row.Refresh(pc.Engine.Latest, pc.Engine.Stats, TemperatureUnit.Celsius);
        Assert.Matches(@"^1\d,\d{3}$", row.Now);
        Assert.Equal("V", row.Unit);
        Assert.Matches(@"^1\d,\d{3}$", row.Min);
        Assert.Contains(" / ", row.MinMax);
        Assert.Equal(1, row.Series.Count);
        row.Refresh(pc.Engine.Latest, pc.Engine.Stats, TemperatureUnit.Celsius);
        Assert.Equal(1, row.Series.Count); // same tick, no duplicate point

        var hot = pc.Engine.Sensors.Single(x => x.Name == "GPU Hot Spot");
        var hotRow = new SensorRowModel(hot, "x", env.Actions);
        hotRow.Refresh(pc.Engine.Latest, pc.Engine.Stats, TemperatureUnit.Fahrenheit);
        Assert.Equal("°F", hotRow.Unit);

        var rate = pc.Engine.Sensors.First(x => x.Name == "Read Rate");
        var rateRow = new SensorRowModel(rate, "x", env.Actions);
        rateRow.Refresh(pc.Engine.Latest, pc.Engine.Stats, TemperatureUnit.Celsius);
        Assert.EndsWith("B/s", rateRow.Max); // throughput keeps its unit per value (it scales)
    });

    [Fact]
    public void Group_state_is_tri_state_and_a_click_turns_all_on_then_off()
    {
        Assert.False(SensorToggles.Combine(0, 5));
        Assert.Null(SensorToggles.Combine(2, 5));
        Assert.True(SensorToggles.Combine(5, 5));
        Assert.False(SensorToggles.Combine(0, 0));
        Assert.True(SensorToggles.NextGroupValue(false));
        Assert.True(SensorToggles.NextGroupValue(null));
        Assert.False(SensorToggles.NextGroupValue(true));
    }

    [Fact]
    public void Bytes_read_naturally_in_both_languages()
    {
        var pl = System.Globalization.CultureInfo.GetCultureInfo("pl-PL");
        var en = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        Assert.Equal("1,7 MB", PageUi.Bytes(1_700_000, pl));
        Assert.Equal("1.7 MB", PageUi.Bytes(1_700_000, en));
        Assert.Equal("61 MB", PageUi.Bytes(61_200_000, pl));
        Assert.Equal("820 KB", PageUi.Bytes(820_000, pl));
        Assert.Equal("0 B", PageUi.Bytes(0, pl));
    }

    [Fact]
    public void Friendly_names_resolve_without_a_loc_registration()
    {
        Sta.Run(() =>
        {
            using var env = new PageEnv(language: "pl");
            Assert.Equal("Temperatura CPU", PageUi.Friendly("friendly.cpu.temp"));
            Loc.I.SetLanguage("en");
            Assert.Equal("CPU temperature", PageUi.Friendly("friendly.cpu.temp"));
        });
    }
}
