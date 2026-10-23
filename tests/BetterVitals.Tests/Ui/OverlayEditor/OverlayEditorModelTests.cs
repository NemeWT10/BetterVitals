using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Overlay;
using BetterVitals.UI.Pages.OverlayEditor;
using BetterVitals.UI.Panel;
using Xunit;

namespace BetterVitals.Tests.Ui.OverlayEditor;

[Collection(UiCollection.Name)]
public sealed class OverlayEditorModelTests
{
    [Fact]
    public void Page_is_registered_as_the_overlay_page()
    {
        var page = PageRegistry.Find(PageIds.Overlay);
        Assert.NotNull(page);
        Assert.Equal(40, page!.Order);
        Assert.Equal("Icon.Overlay", page.IconKey);
        Assert.Equal(1, page.Priority);
        Assert.True(page.InNav);
    }

    [Fact]
    public void Simple_toggling_appends_in_click_order_and_removes()
    {
        using var f = new EditorFixture();
        var m = new OverlayEditorModel(f.Store);
        m.Toggle(f.F("gpu.temp"));
        m.Toggle(f.F("fps.fps"));
        m.Toggle(f.F("cpu.clocks"));
        var items = f.Store.Current.Overlay.AllItems().ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal(f.F("gpu.temp").SensorIds, items[0].SensorIds);
        Assert.Equal(f.F("fps.fps").SensorIds, items[1].SensorIds);
        Assert.Equal(8, items[2].SensorIds.Count); // every core of the 9800X3D
        Assert.Equal(1, m.OrderOf(f.F("gpu.temp")));
        Assert.Equal(3, m.OrderOf(f.F("cpu.clocks")));

        m.Toggle(f.F("fps.fps")); // off again
        Assert.Equal(2, f.Store.Current.Overlay.AllItems().Count());
        Assert.Equal(0, m.OrderOf(f.F("fps.fps")));
        Assert.Equal(2, m.OrderOf(f.F("cpu.clocks")));

        m.Toggle(f.F("fps.fps")); // back on: now last
        Assert.Equal(3, m.OrderOf(f.F("fps.fps")));
        // working copy and settings agree, and never share objects
        Assert.Equal(LayoutJson.Text(m.Working), LayoutJson.Text(f.Store.Current.Overlay));
        Assert.NotSame(m.Working.AllItems().First(), f.Store.Current.Overlay.AllItems().First());
        Assert.NotSame(m.Working.Rows[0], f.Store.Current.Overlay.Rows[0]);
    }

    [Fact]
    public void Reordering_moves_by_id()
    {
        using var f = new EditorFixture();
        var m = new OverlayEditorModel(f.Store);
        m.SetGrouping(OverlayGrouping.List);
        foreach (var key in new[] { "fps.fps", "cpu.temp", "gpu.temp", "gpu.hotspot" }) m.Toggle(f.F(key));
        var ids = m.Working.AllItems().Select(i => i.Id).ToList();
        m.Move(ids[3], 0);
        Assert.Equal([ids[3], ids[0], ids[1], ids[2]], f.Store.Current.Overlay.AllItems().Select(i => i.Id));
        Assert.All(f.Store.Current.Overlay.Rows, r => Assert.Single(r.Items)); // a list: one item per row
        m.Move(ids[3], 99); // clamped to the end
        Assert.Equal([ids[0], ids[1], ids[2], ids[3]], f.Store.Current.Overlay.AllItems().Select(i => i.Id));
        m.Move("missing", 0);
        Assert.Equal(4, f.Store.Current.Overlay.AllItems().Count());
        Assert.Equal(LayoutJson.Text(m.Working), LayoutJson.Text(f.Store.Current.Overlay));
    }

    [Fact]
    public void Starter_set_adds_fps_cpu_and_gpu_temperature_once()
    {
        using var f = new EditorFixture();
        var m = new OverlayEditorModel(f.Store);
        Assert.Equal(3, m.AddDefaults(f.Friendly));
        Assert.Equal(0, m.AddDefaults(f.Friendly));
        Assert.Equal(["fps.fps", "cpu.temp", "gpu.temp"], f.Store.Current.Overlay.AllItems().Select(i => f.Friendly.First(x => OverlayEditorModel.Matches(i, x)).Key));
        Assert.Equal(3, f.Store.Current.Overlay.Rows.Count); // a row per component
    }

    [Theory]
    [InlineData(OverlayAnchor.TopLeft)]
    [InlineData(OverlayAnchor.TopRight)]
    [InlineData(OverlayAnchor.BottomLeft)]
    [InlineData(OverlayAnchor.BottomRight)]
    public void Corner_maps_to_anchor_without_offset_and_lands_in_that_corner(OverlayAnchor corner)
    {
        using var f = new EditorFixture();
        var m = new OverlayEditorModel(f.Store);
        m.Edit(l =>
        {
            l.Placement.OffsetX = 40;
            l.Placement.OffsetY = -12;
        });
        m.SetCorner(corner);
        var p = f.Store.Current.Overlay.Placement;
        Assert.Equal(corner, p.Anchor);
        Assert.Equal(0, p.OffsetX);
        Assert.Equal(0, p.OffsetY);
        Assert.Equal(OverlayEditorModel.DefaultMargin, p.Margin);
        Assert.Equal(corner, OverlayEditorModel.NearestCorner(p.Anchor));

        // on a 4K screen the overlay sits margin × scale from both edges of that corner
        var area = new PixelRect(0, 0, 3840, 2160);
        var scale = OverlayPlacementMath.Scale(new OverlayStyle(), 2160);
        var (x, y) = OverlayPlacementMath.Position(p, area, 300, 200, scale);
        var m48 = (int)(24 * scale);
        Assert.Equal(corner is OverlayAnchor.TopLeft or OverlayAnchor.BottomLeft ? m48 : 3840 - m48 - 300, x);
        Assert.Equal(corner is OverlayAnchor.TopLeft or OverlayAnchor.TopRight ? m48 : 2160 - m48 - 200, y);
        Assert.False(OverlayEditorModel.HasCustomPlacement(f.Store.Current.Overlay));
    }

    [Fact]
    public void Corner_rejects_non_corners_and_centers_lean_top_left()
    {
        using var f = new EditorFixture();
        var m = new OverlayEditorModel(f.Store);
        Assert.Throws<ArgumentOutOfRangeException>(() => m.SetCorner(OverlayAnchor.Center));
        Assert.Equal(OverlayAnchor.TopLeft, OverlayEditorModel.NearestCorner(OverlayAnchor.Center));
        Assert.Equal(OverlayAnchor.TopRight, OverlayEditorModel.NearestCorner(OverlayAnchor.MiddleRight));
        Assert.Equal(OverlayAnchor.BottomLeft, OverlayEditorModel.NearestCorner(OverlayAnchor.BottomCenter));
        Assert.Equal(OverlayAnchor.BottomRight, OverlayEditorModel.NearestCorner(OverlayAnchor.BottomRight));
    }

    [Fact]
    public void Switching_modes_keeps_the_whole_layout()
    {
        using var f = new EditorFixture();
        var m = new OverlayEditorModel(f.Store);
        m.SetAdvanced(true);
        m.Toggle(f.F("cpu.temp"));
        m.Toggle(f.F("cpu.loads"));
        var id = m.Working.AllItems().ElementAt(1).Id;
        m.Edit(l =>
        {
            l.Style.FontFamily = "Orbitron";
            l.Style.ScalePercent = 135;
            l.Style.BackgroundColor = "#202030";
            l.Placement.Anchor = OverlayAnchor.MiddleRight;
            l.Placement.OffsetX = 12;
        });
        m.EditItem(id, i =>
        {
            i.Label = "Cores";
            i.MultiDisplay = MultiValueDisplay.Grid;
            i.Thresholds = [new OverlayThreshold { Value = 90, Color = "#FF0000" }];
        });
        var advanced = LayoutJson.Text(f.Store.Current.Overlay);

        m.SetAdvanced(false);
        Assert.False(f.Store.Current.Overlay.Advanced);
        Assert.True(OverlayEditorModel.HasCustomizations(f.Store.Current.Overlay));
        m.SetAdvanced(true);
        Assert.Equal(advanced, LayoutJson.Text(f.Store.Current.Overlay));

        // simple mode's "Restore default look": same items and order, plain look, nearest corner
        m.SetAdvanced(false);
        m.ResetLook();
        var l = f.Store.Current.Overlay;
        Assert.False(OverlayEditorModel.HasCustomizations(l));
        var items = l.AllItems().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(id, items[1].Id);
        Assert.Equal(8, items[1].SensorIds.Count);
        Assert.Null(items[1].Label);
        Assert.Equal(OverlayAnchor.TopRight, l.Placement.Anchor);
        Assert.Equal("Inter", l.Style.FontFamily);
    }

    [Fact]
    public void Deferred_edits_reach_the_settings_on_flush_and_survive_a_reload()
    {
        using var f = new EditorFixture();
        var m = new OverlayEditorModel(f.Store);
        var changes = 0;
        f.Store.Changed += _ => changes++;
        m.Edit(l => l.Style.ScalePercent = 120, deferred: true);
        m.Edit(l => l.Style.ScalePercent = 140, deferred: true);
        Assert.Equal(140, m.Working.Style.ScalePercent);
        Assert.Equal(100, f.Store.Current.Overlay.Style.ScalePercent);
        Assert.Equal(0, changes);

        // the tray turns the overlay on meanwhile: the editor reloads and keeps its pending slider value
        f.Store.Mutate("overlay", s => s.Overlay.Enabled = true);
        m.OnStoreChanged("overlay");
        Assert.True(m.Working.Enabled);
        Assert.Equal(140, m.Working.Style.ScalePercent);

        m.Flush();
        Assert.Equal(140, f.Store.Current.Overlay.Style.ScalePercent);
        Assert.True(f.Store.Current.Overlay.Enabled);
        Assert.False(m.HasPending);
    }

    [Fact]
    public void Reset_restores_the_default_overlay_but_keeps_on_off_and_mode()
    {
        using var f = new EditorFixture();
        var m = new OverlayEditorModel(f.Store);
        m.Edit(l =>
        {
            l.Enabled = true;
            l.Advanced = true;
            l.RefreshMs = 250;
            l.HideFromCapture = false;
            l.Style.Arrangement = OverlayArrangement.Compact;
        });
        m.Toggle(f.F("ram.used"));
        var reloads = 0;
        m.Reloaded += () => reloads++;
        m.ResetAll(f.Friendly);
        var l = f.Store.Current.Overlay;
        Assert.True(l.Enabled);
        Assert.True(l.Advanced);
        Assert.Equal(1000, l.RefreshMs);
        Assert.True(l.HideFromCapture);
        Assert.Equal(OverlayArrangement.Vertical, l.Style.Arrangement);
        Assert.Equal(3, l.AllItems().Count());
        Assert.Equal(1, reloads);
        Assert.Equal(LayoutJson.Text(m.Working), LayoutJson.Text(l));
    }

    [Fact]
    public void Profile_export_import_round_trip()
    {
        using var f = new EditorFixture();
        var m = new OverlayEditorModel(f.Store);
        m.Toggle(f.F("fps.fps"));
        m.Toggle(f.F("cpu.clocks"));
        m.Edit(l =>
        {
            l.Enabled = true;
            l.Visibility = OverlayVisibility.GamesOnly;
            l.RefreshMs = 500;
            l.Style.FontFamily = "Rajdhani";
            l.Style.Arrangement = OverlayArrangement.Horizontal;
            l.Style.LabelColor = "#80FFCC00";
            l.Placement.MonitorMode = OverlayMonitorMode.Specific;
            l.Placement.MonitorKey = @"\\?\DISPLAY#DEL4321#5&1";
            l.Placement.Anchor = OverlayAnchor.BottomCenter;
            l.Placement.OffsetY = 30.5;
        });
        m.EditItem(m.Working.AllItems().ElementAt(1).Id, i =>
        {
            i.MultiDisplay = MultiValueDisplay.Rows;
            i.Aggregation = ValueAggregation.Average;
            i.AggregationSeconds = 10;
            i.Decimals = 2;
            i.ShowUnit = false;
            i.ShowGraph = true;
            i.FontFamily = "JetBrains Mono";
            i.ValueSize = 20;
            i.Thresholds = [new() { Value = 5000, Color = "#FBBF24" }, new() { Value = 5400, Color = "#F43F5E" }];
        });

        var json = LayoutJson.WriteProfile("My HUD", m.Working);
        Assert.Contains("\"kind\": \"BetterVitals.OverlayProfile\"", json);
        Assert.Contains("\"Horizontal\"", json); // enums by name, readable
        var (name, layout) = LayoutJson.ReadProfile(json);
        Assert.Equal("My HUD", name);
        Assert.Equal(LayoutJson.Text(m.Working), LayoutJson.Text(layout));

        // import into another state: everything but on / off and the mode comes from the file
        m.Edit(l =>
        {
            l.Enabled = false;
            l.Advanced = true;
        });
        m.ResetAll(f.Friendly);
        m.Replace(layout);
        var now = f.Store.Current.Overlay;
        Assert.False(now.Enabled);
        Assert.True(now.Advanced);
        Assert.Equal(OverlayVisibility.GamesOnly, now.Visibility);
        Assert.Equal("Rajdhani", now.Style.FontFamily);
        Assert.Equal(2, now.AllItems().Count());
        Assert.Equal(5400, now.AllItems().ElementAt(1).Thresholds![1].Value);

        // named profiles on disk
        var store = new OverlayProfileStore(f.ProfileDir);
        store.Save("My HUD: 4K", m.Working);
        Assert.Equal(["My HUD: 4K"], store.List());
        Assert.Equal(LayoutJson.Text(m.Working), LayoutJson.Text(store.Load("My HUD: 4K")!));
        store.Delete("My HUD: 4K");
        Assert.Empty(store.List());
    }

    [Fact]
    public void Import_accepts_a_bare_layout_and_rejects_anything_else()
    {
        var bare = """{ "items": [ { "id": "a", "sensorIds": ["x/y/0/fps/fps"] }, { "id": "a", "sensorIds": ["x/y/0/fps/1-low"] } ], "refreshMs": 99999, "style": { "scalePercent": 900 } }""";
        var (_, layout) = LayoutJson.ReadProfile(bare);
        var items = layout.AllItems().ToList();
        Assert.Equal(2, items.Count);
        Assert.NotEqual(items[0].Id, items[1].Id); // duplicate editor ids repaired
        Assert.Equal(2, layout.Rows.Count); // a flat v1 list: a row per item, as it always looked
        Assert.Equal(OverlayGrouping.List, layout.Grouping);
        Assert.Equal(2000, layout.RefreshMs); // normalized
        Assert.Equal(200, layout.Style.ScalePercent);

        var settings = """{ "schemaVersion": 1, "overlay": { "items": [], "style": { "fontFamily": "Geist" } } }""";
        Assert.Equal("Geist", LayoutJson.ReadProfile(settings).Layout.Style.FontFamily);

        Assert.Throws<FormatException>(() => LayoutJson.ReadProfile("not json"));
        Assert.Throws<FormatException>(() => LayoutJson.ReadProfile("[1, 2]"));
        Assert.Throws<FormatException>(() => LayoutJson.ReadProfile("""{ "hello": "world" }"""));
        Assert.Throws<FormatException>(() => LayoutJson.ReadProfile("""{ "items": 5 }"""));
    }

    [Theory]
    [InlineData("#FF8800", "#FF8800")]
    [InlineData("ff8800", "#FF8800")]
    [InlineData("  #f80 ", "#FF8800")]
    [InlineData("#8f80", "#88FF8800")]
    [InlineData("#80FF8800", "#80FF8800")]
    [InlineData("#FFFF8800", "#FF8800")]
    public void Color_text_parses_and_normalizes(string input, string expected)
    {
        Assert.True(ColorText.TryParse(input, out var c));
        Assert.Equal(expected, ColorText.Format(c));
        Assert.Equal(expected, ColorText.Normalize(input));
        Assert.Equal(c, Rgba.TryParse(expected)); // the renderer reads what the editor stores
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#12")]
    [InlineData("#12345")]
    [InlineData("#GG0000")]
    [InlineData("red")]
    [InlineData("#1234567890")]
    public void Color_text_rejects_garbage(string? input)
    {
        Assert.False(ColorText.TryParse(input, out _));
        Assert.Null(ColorText.Normalize(input));
    }

    [Fact]
    public void Thresholds_editing()
    {
        var theme = OverlayTheme.Default;
        var cpu = new SensorDescriptor { Id = "c", HardwareId = "h", Name = "Core (Tctl/Tdie)", Kind = SensorKind.Temperature, HardwareKind = HardwareKind.Cpu };
        var hot = new SensorDescriptor { Id = "g", HardwareId = "h", Name = "GPU Hot Spot", Kind = SensorKind.Temperature, HardwareKind = HardwareKind.Gpu };
        var load = new SensorDescriptor { Id = "l", HardwareId = "h", Name = "CPU Total", Kind = SensorKind.Load, HardwareKind = HardwareKind.Cpu };

        var item = new OverlayItem();
        Assert.Equal(ThresholdMode.Default, ThresholdEditing.Mode(item));
        Assert.Equal([80f, 90f], ThresholdEditing.Effective(item, cpu, theme).Select(t => t.Value));
        Assert.Equal([90f, 100f], ThresholdEditing.Defaults(hot, theme).Select(t => t.Value));
        Assert.Equal(theme.Warning.ToString(), ThresholdEditing.Defaults(cpu, theme)[0].Color);
        Assert.Empty(ThresholdEditing.Defaults(load, theme));
        Assert.Equal([80f, 95f], ThresholdEditing.Seed(load, theme).Select(t => t.Value)); // a load starts with 80 / 95 %

        item.Thresholds = [];
        Assert.Equal(ThresholdMode.Off, ThresholdEditing.Mode(item));

        var rows = ThresholdEditing.Seed(cpu, theme);
        item.Thresholds = rows;
        Assert.Equal(ThresholdMode.Custom, ThresholdEditing.Mode(item));
        var added = ThresholdEditing.Add(rows, theme);
        Assert.Equal([80f, 90f, 100f], added.Select(t => t.Value));
        Assert.Equal(theme.Danger.ToString(), added[2].Color);
        Assert.Equal(2, rows.Count); // edits never change the list they got

        var moved = ThresholdEditing.Sorted(ThresholdEditing.SetValue(added, 2, 70));
        Assert.Equal([70f, 80f, 90f], moved.Select(t => t.Value));
        var recolored = ThresholdEditing.SetColor(moved, 0, "0f0");
        Assert.Equal("#00FF00", recolored[0].Color);
        Assert.Equal(moved[0].Color, ThresholdEditing.SetColor(moved, 0, "not a color")[0].Color);
        Assert.Equal([70f, 90f], ThresholdEditing.Remove(recolored, 1).Select(t => t.Value));
        Assert.Null(ThresholdEditing.ColorFor(recolored, 60));
        Assert.Equal("#00FF00", ThresholdEditing.ColorFor(recolored, 75));
        Assert.Equal(recolored[2].Color, ThresholdEditing.ColorFor(recolored, 95));

        // °F in the editor, °C in the settings
        Assert.Equal(176, ThresholdEditing.ToDisplay(80, SensorKind.Temperature, TemperatureUnit.Fahrenheit), 3);
        Assert.Equal(80f, ThresholdEditing.FromDisplay(176, SensorKind.Temperature, TemperatureUnit.Fahrenheit), 3);
        Assert.Equal(95, ThresholdEditing.ToDisplay(95, SensorKind.Load, TemperatureUnit.Fahrenheit));
    }

    [Fact]
    public void Threshold_edits_through_the_model_never_share_lists()
    {
        using var f = new EditorFixture();
        var m = new OverlayEditorModel(f.Store);
        m.Toggle(f.F("cpu.temp"));
        var mine = m.Working.AllItems().First();
        var id = mine.Id;
        var rows = ThresholdEditing.Seed(f.Engine.Find(mine.SensorIds[0]), OverlayTheme.Default);
        m.EditItem(id, i => i.Thresholds = ThresholdEditing.Copy(rows));
        var stored = f.Store.Current.Overlay.AllItems().First();
        Assert.NotSame(mine.Thresholds, stored.Thresholds);
        mine.Thresholds![0].Value = 1; // a stray in-place change of the working copy
        Assert.Equal(80, stored.Thresholds![0].Value);
    }

    [Fact]
    public void Preview_values_come_from_the_hub_with_history_and_example_fps()
    {
        using var f = new EditorFixture();
        var feed = new PreviewFeed(f.Engine);
        var layout = new OverlayLayout { Items = [] };
        layout.Items.Add(new OverlayItem { SensorIds = [.. f.F("cpu.temp").SensorIds] });
        layout.Items.Add(new OverlayItem { SensorIds = [.. f.F("cpu.clocks").SensorIds] });
        layout.Items.Add(new OverlayItem { SensorIds = ["game/foreground/0/fps/framerate-not-there"] });
        for (var i = 0; i < 5; i++)
        {
            f.Advance(1);
            feed.Sample(layout.AllItems().SelectMany(x => x.SensorIds));
        }
        var values = feed.Build(layout);
        Assert.Equal(1 + 8 + 1, values.Count);
        Assert.All(values.Take(9), v => Assert.False(float.IsNaN(v.Value)));
        Assert.Equal(5, values[0].History!.Count);
        var fps = values[^1];
        Assert.Equal(SensorKind.Fps, fps.Sensor.Kind);
        Assert.Equal(144, fps.Value); // no game: an example value, and the page says so
        Assert.True(feed.UsesExampleValues(layout));
    }
}
