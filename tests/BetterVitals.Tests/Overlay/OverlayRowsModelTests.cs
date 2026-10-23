using System.Text.Json.Nodes;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Settings;
using BetterVitals.UI.Pages.OverlayEditor;
using Xunit;
using static BetterVitals.Tests.Overlay.OverlayFixtures;

namespace BetterVitals.Tests.Overlay;

/// <summary>Rows and boxes: the schema-1 migration, Normalize, profiles, and every structural edit on the model.</summary>
public class OverlayRowsModelTests
{
    private static string[] Ids(OverlayRow row) => [.. row.Items.Select(i => i.Id)];

    private static OverlayItem It(string id, params BetterVitals.Core.Sensors.SensorDescriptor[] sensors) => new() { Id = id, SensorIds = [.. sensors.Select(s => s.Id)] };

    /// <summary>FPS | CPU: temp, load | GPU: temp, hot spot — in the main box.</summary>
    private static OverlayLayout ThreeRows() => new()
    {
        Grouping = OverlayGrouping.Custom,
        Rows =
        [
            new() { Id = "r-fps", Items = [It("fps", FpsSensor)] },
            new() { Id = "r-cpu", Items = [It("cpu", CpuTemp), It("load", CpuLoad)] },
            new() { Id = "r-gpu", Items = [It("gpu", GpuTemp), It("hs", HotSpot)] },
        ],
    };

    // ───────────── migration ─────────────

    [Theory]
    [InlineData("Vertical", 3, OverlayGrouping.List)]
    [InlineData("Horizontal", 1, OverlayGrouping.OneLine)]
    [InlineData("Compact", 1, OverlayGrouping.OneLine)]
    public void A_schema_1_settings_file_becomes_rows_and_is_saved_in_the_new_shape(string arrangement, int rows, OverlayGrouping grouping)
    {
        var dir = Path.Combine(Path.GetTempPath(), "bv-rows-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, $$"""
                {
                  "SchemaVersion": 1,
                  "Overlay": {
                    "Enabled": true,
                    "Placement": { "Anchor": "TopRight", "OffsetX": 12 },
                    "Style": { "Arrangement": "{{arrangement}}", "FontFamily": "Geist" },
                    "Items": [
                      { "Id": "a", "SensorIds": ["{{FpsSensor.Id}}"], "ShowGraph": true },
                      { "Id": "b", "SensorIds": ["{{CpuTemp.Id}}"], "Label": "Chip" },
                      { "Id": "c", "SensorIds": ["{{GpuTemp.Id}}"] }
                    ]
                  }
                }
                """);
            var s = SettingsStore.Load(path);
            Assert.Equal(AppSettings.CurrentSchema, s.SchemaVersion);
            var o = s.Overlay;
            Assert.Null(o.Items);
            Assert.Empty(o.Boxes);
            Assert.Equal(rows, o.Rows.Count);
            Assert.Equal(["a", "b", "c"], o.AllItems().Select(i => i.Id));
            Assert.Equal(grouping, o.Grouping);
            Assert.Equal(arrangement, o.Style.Arrangement.ToString()); // the look stays as it was
            Assert.All(o.Rows, r => Assert.Null(r.Label)); // automatic: a row of one is labelled like its item
            Assert.Equal("Chip", o.AllItems().ElementAt(1).Label);
            Assert.Equal(OverlayAnchor.TopRight, o.Placement.Anchor);

            // saved again: rows, no flat list; and it loads back the same
            var store = SettingsStore.CreateDetached(path, s);
            store.Mutate("overlay", _ => { });
            store.Flush();
            var json = JsonNode.Parse(File.ReadAllText(path))!["Overlay"]!.AsObject();
            Assert.False(json.ContainsKey("Items"));
            Assert.False(json.ContainsKey("IsEmpty"));
            Assert.Equal(rows, json["Rows"]!.AsArray().Count);
            Assert.Equal(2, JsonNode.Parse(File.ReadAllText(path))!["SchemaVersion"]!.GetValue<int>());
            var again = SettingsStore.Load(path);
            Assert.Equal(LayoutJson.Text(o), LayoutJson.Text(again.Overlay));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_fresh_layout_groups_by_component_and_an_empty_old_one_stays_a_list()
    {
        Assert.Equal(OverlayGrouping.ByComponent, new OverlayLayout().Grouping);
        var old = new OverlayLayout { Items = [] };
        old.Normalize();
        Assert.Equal(OverlayGrouping.List, old.Grouping);
        Assert.True(old.IsEmpty);
    }

    [Fact]
    public void A_v1_profile_loads_as_rows_and_a_v2_profile_round_trips_rows_boxes_and_names()
    {
        var v1 = $$"""
            { "kind": "BetterVitals.OverlayProfile", "version": 1, "name": "Old HUD",
              "layout": { "style": { "arrangement": "Compact" }, "items": [ { "sensorIds": ["{{FpsSensor.Id}}"] }, { "sensorIds": ["{{CpuTemp.Id}}"] } ] } }
            """;
        var (name, old) = LayoutJson.ReadProfile(v1);
        Assert.Equal("Old HUD", name);
        Assert.Single(old.Rows);
        Assert.Equal(2, old.Rows[0].Items.Count);
        Assert.Equal(OverlayArrangement.Compact, old.Style.Arrangement);

        var layout = ThreeRows();
        layout.Rows[1].Label = "Processor";
        layout.Boxes.Add(new OverlayBox { Id = "box-1", Name = "Graphics", Placement = new() { Anchor = OverlayAnchor.BottomRight, OffsetY = 40, MonitorMode = OverlayMonitorMode.Specific, MonitorKey = "K" }, Rows = [layout.Rows[2]] });
        layout.Rows.RemoveAt(2);
        layout.Normalize();
        var text = LayoutJson.WriteProfile("New HUD", layout);
        Assert.Contains("\"Rows\"", text);
        Assert.Contains("\"Boxes\"", text);
        Assert.DoesNotContain("\"Items\": null", text);
        var (_, back) = LayoutJson.ReadProfile(text);
        Assert.Equal(LayoutJson.Text(layout), LayoutJson.Text(back));
        Assert.Equal("Graphics", back.Boxes[0].Name);
        Assert.Equal("Processor", back.Rows[1].Label);
    }

    // ───────────── Normalize ─────────────

    [Fact]
    public void Normalize_repairs_rows_boxes_ids_and_numbers()
    {
        var l = new OverlayLayout
        {
            Grouping = (OverlayGrouping)77,
            Rows =
            [
                null!,
                new() { Id = "", Label = "   ", Items = [It("x", CpuTemp), null!] },
                new() { Id = "dup", Items = [] }, // empty: goes
                new() { Id = "dup", Label = new string('L', 300), Items = null! },
                new() { Id = "dup", Items = [new() { Id = "x", SensorIds = [GpuTemp.Id, "", null!] }] }, // the same ids twice
            ],
            Boxes =
            [
                null!,
                new() { Id = "main", Placement = null!, Rows = [new() { Items = [It("b1", HotSpot)] }] }, // a box may not be called "main"
                new() { Id = "empty", Rows = [] },
                new() { Id = "nulls", Rows = null! },
                new() { Id = "absurd", Name = "  ", Placement = new() { Anchor = (OverlayAnchor)40, MonitorMode = (OverlayMonitorMode)9, OffsetX = double.NaN, OffsetY = 1e12, Margin = -5 }, Rows = [new() { Items = [It("b2", Ram)] }] },
            ],
        };
        l.Normalize();
        Assert.Equal(OverlayGrouping.Custom, l.Grouping);
        Assert.Equal(2, l.Rows.Count); // the null and the empty rows are gone
        Assert.Null(l.Rows[0].Label);
        Assert.Single(l.Rows[0].Items);
        Assert.Equal([GpuTemp.Id], l.Rows[1].Items[0].SensorIds);
        var ids = l.AllBoxes().Skip(1).Select(b => b.Id).Concat(l.AllBoxes().SelectMany(b => b.Rows).Select(r => r.Id)).Concat(l.AllItems().Select(i => i.Id)).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.DoesNotContain("main", l.Boxes.Select(b => b.Id));
        Assert.Equal(2, l.Boxes.Count); // empty boxes are gone
        var absurd = l.Boxes[1];
        Assert.Null(absurd.Name);
        Assert.Equal(OverlayAnchor.TopLeft, absurd.Placement.Anchor);
        Assert.Equal(OverlayMonitorMode.Primary, absurd.Placement.MonitorMode);
        Assert.Equal(0, absurd.Placement.OffsetX);
        Assert.Equal(10_000, absurd.Placement.OffsetY);
        Assert.Equal(0, absurd.Placement.Margin);
        Assert.NotNull(l.Boxes[0].Placement);
    }

    [Fact]
    public void Normalize_merges_boxes_beyond_the_limit_into_the_main_box_and_keeps_every_item()
    {
        var l = new OverlayLayout();
        for (var i = 0; i < 20; i++) l.Boxes.Add(new OverlayBox { Rows = [new() { Items = [new() { SensorIds = [CpuTemp.Id] }] }] });
        l.Normalize();
        Assert.Equal(OverlayLayout.MaxBoxes - 1, l.Boxes.Count);
        Assert.Equal(20 - (OverlayLayout.MaxBoxes - 1), l.Rows.Count);
        Assert.Equal(20, l.AllItems().Count());
    }

    [Fact]
    public void A_layout_with_both_rows_and_a_flat_list_keeps_both()
    {
        var l = ThreeRows();
        l.Items = [It("extra", Ram)];
        l.Normalize();
        Assert.Equal(4, l.Rows.Count);
        Assert.Equal("extra", l.Rows[^1].Items[0].Id);
        Assert.Equal(OverlayGrouping.Custom, l.Grouping); // not a fresh schema-1 file: the grouping stays
    }

    // ───────────── edits ─────────────

    [Fact]
    public void Moving_an_item_within_and_between_rows()
    {
        var l = ThreeRows();
        Assert.True(l.MoveItem("load", "r-cpu", "cpu")); // reorder within the row
        Assert.Equal(["load", "cpu"], Ids(l.Rows[1]));
        Assert.False(l.MoveItem("load", "r-cpu", "cpu")); // already there
        Assert.True(l.MoveItem("hs", "r-cpu", null)); // into another row, at its end
        Assert.Equal(["load", "cpu", "hs"], Ids(l.Rows[1]));
        Assert.Equal(["gpu"], Ids(l.Rows[2]));
        Assert.True(l.MoveItem("gpu", "r-fps", "fps")); // the last item leaves: the row goes
        Assert.Equal(2, l.Rows.Count);
        Assert.Equal(["gpu", "fps"], Ids(l.Rows[0]));
        Assert.False(l.MoveItem("nope", "r-fps", null));
        Assert.False(l.MoveItem("cpu", "nope", null));
        Assert.False(l.MoveItem("cpu", "r-fps", "not-in-that-row"));
    }

    [Fact]
    public void Moving_an_item_into_a_new_row_and_a_new_box()
    {
        var l = ThreeRows();
        Assert.True(l.MoveItemToNewRow("hs", OverlayLayout.MainBoxId, "r-cpu", "r-new"));
        Assert.Equal(["r-fps", "r-new", "r-cpu", "r-gpu"], l.Rows.Select(r => r.Id));
        Assert.Equal(["hs"], Ids(l.Rows[1]));
        Assert.False(l.MoveItemToNewRow("hs", OverlayLayout.MainBoxId, "r-cpu", "r-x")); // alone in its row, same place
        Assert.False(l.MoveItemToNewRow("fps", OverlayLayout.MainBoxId, "r-fps", "r-x"));

        var at = l.NewBoxPlacement();
        Assert.Equal(OverlayAnchor.TopRight, at.Anchor); // the main box is top left
        Assert.True(l.MoveItemToNewBox("hs", "box-a", "row-a", at));
        Assert.Equal(3, l.Rows.Count);
        var box = Assert.Single(l.Boxes);
        Assert.Equal("box-a", box.Id);
        Assert.Equal(["hs"], Ids(box.Rows[0]));
        Assert.False(l.MoveItemToNewBox("hs", "box-b", "row-b", l.NewBoxPlacement())); // already a box of its own
        Assert.Equal(OverlayAnchor.BottomLeft, l.NewBoxPlacement().Anchor); // the next free corner

        // the box's only item goes back: the box goes too
        Assert.True(l.MoveItemToNewRow("hs", OverlayLayout.MainBoxId, null, "r-back"));
        Assert.Empty(l.Boxes);
        Assert.Equal("r-back", l.Rows[^1].Id);
    }

    [Fact]
    public void Moving_rows_between_boxes_and_detaching_a_row()
    {
        var l = ThreeRows();
        Assert.True(l.MoveRowToNewBox("r-gpu", "box-g", l.NewBoxPlacement()));
        Assert.Equal(["r-fps", "r-cpu"], l.Rows.Select(r => r.Id));
        Assert.Equal(["r-gpu"], l.Boxes[0].Rows.Select(r => r.Id));
        Assert.False(l.MoveRowToNewBox("r-gpu", "box-x", new OverlayPlacement())); // the box's only row

        Assert.True(l.MoveRow("r-fps", "box-g", "r-gpu")); // into the box, first
        Assert.Equal(["r-fps", "r-gpu"], l.Boxes[0].Rows.Select(r => r.Id));
        Assert.True(l.MoveRow("r-fps", "box-g", null)); // reorder inside the box
        Assert.Equal(["r-gpu", "r-fps"], l.Boxes[0].Rows.Select(r => r.Id));
        Assert.False(l.MoveRow("r-fps", "box-g", null));
        Assert.False(l.MoveRow("r-fps", "box-g", "r-fps"));

        // removing a box returns its rows to the main box, nothing is lost
        Assert.True(l.RemoveBox("box-g"));
        Assert.Empty(l.Boxes);
        Assert.Equal(["r-cpu", "r-gpu", "r-fps"], l.Rows.Select(r => r.Id));
        Assert.Equal(5, l.AllItems().Count());
        Assert.False(l.RemoveBox(OverlayLayout.MainBoxId));
    }

    [Fact]
    public void Removing_items_rows_and_reordering_boxes()
    {
        var l = ThreeRows();
        l.MoveRowToNewBox("r-fps", "b1", new OverlayPlacement());
        l.MoveRowToNewBox("r-gpu", "b2", new OverlayPlacement());
        Assert.Equal(["b1", "b2"], l.Boxes.Select(b => b.Id));
        Assert.True(l.MoveBox("b2", "b1"));
        Assert.Equal(["b2", "b1"], l.Boxes.Select(b => b.Id));
        Assert.False(l.MoveBox("b2", "b1"));
        Assert.True(l.MoveBox("b2", null));
        Assert.Equal(["b1", "b2"], l.Boxes.Select(b => b.Id));

        Assert.True(l.RemoveItem("fps")); // the box's last item: the box goes
        Assert.Equal(["b2"], l.Boxes.Select(b => b.Id));
        Assert.True(l.RemoveRow("r-gpu"));
        Assert.Empty(l.Boxes);
        Assert.True(l.RemoveRow("r-cpu"));
        Assert.True(l.IsEmpty);
        Assert.False(l.RemoveItem("cpu"));
    }

    [Fact]
    public void Only_so_many_boxes_can_be_made()
    {
        var l = new OverlayLayout { Grouping = OverlayGrouping.List };
        for (var i = 0; i < 12; i++) l.AddItem(It("i" + i, CpuTemp), "r" + i);
        for (var i = 0; i < 12; i++) l.MoveItemToNewBox("i" + i, "b" + i, "rb" + i, l.NewBoxPlacement());
        Assert.Equal(OverlayLayout.MaxBoxes - 1, l.Boxes.Count);
        Assert.False(l.CanAddBox());
        Assert.False(l.MoveRowToNewBox(l.Rows[0].Id, "late", new OverlayPlacement()));
        Assert.Equal(12, l.AllItems().Count());
        // every corner and edge middle is handed out once, then the top center again
        Assert.Equal(OverlayLayout.MaxBoxes, l.AllBoxes().Select(b => b.Placement.Anchor).Distinct().Count());
    }

    [Fact]
    public void Names_of_rows_and_boxes()
    {
        var l = ThreeRows();
        Assert.True(l.SetRowLabel("r-cpu", "  Processor  "));
        Assert.Equal("Processor", l.Rows[1].Label);
        Assert.True(l.SetRowLabel("r-cpu", "   "));
        Assert.Null(l.Rows[1].Label);
        Assert.True(l.SetRowLabel("r-cpu", new string('x', 200)));
        Assert.Equal(64, l.Rows[1].Label!.Length);
        Assert.False(l.SetRowLabel("nope", "x"));
        l.MoveRowToNewBox("r-gpu", "b", new OverlayPlacement());
        Assert.True(l.SetBoxName("b", "GPU"));
        Assert.Equal("GPU", l.Boxes[0].Name);
        Assert.False(l.SetBoxName(OverlayLayout.MainBoxId, "x"));
    }

    // ───────────── simple mode ─────────────

    [Fact]
    public void Grouping_by_component_puts_each_component_in_one_row_in_first_seen_order()
    {
        var gpu2 = Sensor("gpu/amd-radeon-graphics/0", BetterVitals.Core.Sensors.HardwareKind.Gpu, BetterVitals.Core.Sensors.SensorKind.Load, "D3D 3D");
        var l = new OverlayLayout { Grouping = OverlayGrouping.List };
        foreach (var (id, s) in new[] { ("gpu", GpuTemp), ("fps", FpsSensor), ("cpu", CpuTemp), ("hs", HotSpot), ("igpu", gpu2), ("load", CpuLoad), ("low", Low1) })
            l.AddItem(It(id, s), "r-" + id);
        Assert.Equal(7, l.Rows.Count);
        l.Regroup(OverlayGrouping.ByComponent, "g");
        Assert.Equal(OverlayGrouping.ByComponent, l.Grouping);
        Assert.Equal([["gpu", "hs"], ["fps", "low"], ["cpu", "load"], ["igpu"]], l.Rows.Select(Ids));
        Assert.Equal("r-igpu", l.Rows[3].Id); // a row that keeps its items keeps its id (and name)

        // a new item joins its component's row
        l.AddItem(It("clock", GpuClock), "r-clock");
        Assert.Equal(["gpu", "hs", "clock"], Ids(l.Rows[0]));
        l.AddItem(It("ram", Ram), "r-ram");
        Assert.Equal(["ram"], Ids(l.Rows[^1]));

        l.Regroup(OverlayGrouping.OneLine, "o");
        var line = Assert.Single(l.Rows);
        Assert.Equal(9, line.Items.Count);
        l.AddItem(It("nvme", Nvme), "r-nvme");
        Assert.Single(l.Rows);

        l.Regroup(OverlayGrouping.List, "l");
        Assert.Equal(10, l.Rows.Count);
        Assert.All(l.Rows, r => Assert.Single(r.Items));
        Assert.Equal(OverlayArrangement.Compact, OverlayLayoutEdits.ArrangementFor(OverlayGrouping.OneLine));
        Assert.Equal(OverlayArrangement.Vertical, OverlayLayoutEdits.ArrangementFor(OverlayGrouping.ByComponent));
    }

    [Fact]
    public void The_flat_order_list_regroups_or_joins_the_neighbours_row()
    {
        var l = ThreeRows();
        l.Regroup(OverlayGrouping.ByComponent, "g"); // the same rows
        Assert.Equal(["r-fps", "r-cpu", "r-gpu"], l.Rows.Select(r => r.Id));
        Assert.True(l.MoveFlat("hs", 0, "m")); // the hot spot first: the GPU row comes first
        Assert.Equal([["hs", "gpu"], ["fps"], ["cpu", "load"]], l.Rows.Select(Ids));
        Assert.False(l.MoveFlat("hs", 0, "m"));

        var custom = ThreeRows();
        Assert.True(custom.MoveFlat("fps", 2, "m")); // third, after "load": into the CPU row
        Assert.Equal([["cpu", "load", "fps"], ["gpu", "hs"]], custom.Rows.Select(Ids));
        Assert.True(custom.MoveFlat("hs", 0, "m")); // the very first: into the first row
        Assert.Equal(["hs", "cpu", "load", "fps"], Ids(custom.Rows[0]));
    }

    [Fact]
    public void Component_of_an_item_from_its_sensor_id()
    {
        Assert.Equal("cpu", OverlayLayoutEdits.ComponentKey(It("a", CpuTemp)));
        Assert.Equal(GpuHw, OverlayLayoutEdits.ComponentKey(It("a", HotSpot)));
        Assert.Equal("game", OverlayLayoutEdits.ComponentKey(It("a", FpsSensor)));
        Assert.Equal("", OverlayLayoutEdits.ComponentKey(new OverlayItem()));
        Assert.Equal(BetterVitals.Core.Sensors.HardwareKind.Gpu, OverlayLayoutEdits.ComponentKind(It("a", HotSpot)));
        Assert.Equal(BetterVitals.Core.Sensors.HardwareKind.Other, OverlayLayoutEdits.ComponentKind(new OverlayItem { SensorIds = ["garbage"] }));
    }
}
