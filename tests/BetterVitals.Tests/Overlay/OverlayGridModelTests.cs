using System.Text.Json.Nodes;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.UI.Pages.OverlayEditor;
using Xunit;
using static BetterVitals.Tests.Overlay.OverlayFixtures;

namespace BetterVitals.Tests.Overlay;

/// <summary>
/// The grid model: migration of schema 1 (a flat list) and schema 2 (rows and boxes) to cells in at most two windows,
/// Normalize, profiles, and every edit the board makes (swap, insert into a row, new row, new column, move between
/// windows, remove, keyboard moves, the second window on and off), plus Simple mode's presets.
/// </summary>
public class OverlayGridModelTests
{
    private static OverlayItem It(string id, SensorDescriptor sensor, int row = 0, int column = 0) => new() { Id = id, SensorIds = [sensor.Id], Row = row, Column = column };

    /// <summary>A text picture of a window's grid: rows top to bottom, cells left to right, "." for a hole.</summary>
    private static string Grid(List<OverlayItem> cells)
    {
        var rows = OverlayGrid.RowCount(cells);
        var cols = OverlayGrid.ColumnCount(cells);
        return string.Join(" / ", Enumerable.Range(0, rows).Select(r => string.Join(" ", Enumerable.Range(0, cols).Select(c => OverlayGrid.At(cells, r, c)?.Id ?? "."))));
    }

    /// <summary>
    /// fps  .
    /// cpu  load
    /// gpu  hs
    /// </summary>
    private static OverlayLayout Table() => new()
    {
        Grouping = OverlayGrouping.Custom,
        Cells =
        [
            It("fps", FpsSensor, 0, 0),
            It("cpu", CpuTemp, 1, 0), It("load", CpuLoad, 1, 1),
            It("gpu", GpuTemp, 2, 0), It("hs", HotSpot, 2, 1),
        ],
    };

    private static GridDrop InMain(GridDropKind kind, int row, int column) => new(OverlayLayout.MainWindowId, kind, row, column);

    private static GridDrop InSecond(GridDropKind kind, int row, int column) => new(OverlayLayout.SecondWindowId, kind, row, column);

    // ───────────── migration ─────────────

    [Theory]
    [InlineData("Vertical", "a / b / c", OverlayGrouping.List)]
    [InlineData("Horizontal", "a b c", OverlayGrouping.OneLine)]
    [InlineData("Compact", "a b c", OverlayGrouping.OneLine)]
    public void A_schema_1_settings_file_becomes_cells_and_is_saved_in_the_new_shape(string arrangement, string grid, OverlayGrouping grouping)
    {
        var dir = Path.Combine(Path.GetTempPath(), "bv-grid-" + Guid.NewGuid().ToString("N"));
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
            Assert.Equal(3, AppSettings.CurrentSchema);
            Assert.Equal(AppSettings.CurrentSchema, s.SchemaVersion);
            var o = s.Overlay;
            Assert.Null(o.Items);
            Assert.False(o.Second.Enabled);
            Assert.Equal(grid, Grid(o.Cells));
            Assert.Equal(grouping, o.Grouping);
            Assert.Equal(arrangement, o.Style.Arrangement.ToString()); // the look stays as it was
            Assert.Equal("Chip", o.Cells[1].Label);
            Assert.Equal(OverlayAnchor.TopRight, o.Placement.Anchor);

            // saved again: cells, no older shape; and it loads back the same
            var store = SettingsStore.CreateDetached(path, s);
            store.Mutate("overlay", _ => { });
            store.Flush();
            var root = JsonNode.Parse(File.ReadAllText(path))!;
            var json = root["Overlay"]!.AsObject();
            Assert.False(json.ContainsKey("Items"));
            Assert.False(json.ContainsKey("Rows"));
            Assert.False(json.ContainsKey("Boxes"));
            Assert.False(json.ContainsKey("IsEmpty"));
            Assert.Equal(3, json["Cells"]!.AsArray().Count);
            Assert.True(json["Cells"]![2]!.AsObject().ContainsKey("Row"));
            Assert.Equal(3, root["SchemaVersion"]!.GetValue<int>());
            Assert.Equal(LayoutJson.Text(o), LayoutJson.Text(SettingsStore.Load(path).Overlay));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_schema_2_file_keeps_its_rows_as_rows_of_cells_and_its_boxes_in_the_second_window()
    {
        var json = $$"""
            {
              "SchemaVersion": 2,
              "Overlay": {
                "Grouping": "Custom",
                "Rows": [
                  { "Id": "r1", "Items": [ { "Id": "fps", "SensorIds": ["{{FpsSensor.Id}}"] }, { "Id": "low", "SensorIds": ["{{Low1.Id}}"] } ] },
                  { "Id": "r2", "Label": "Processor", "Items": [ { "Id": "cpu", "SensorIds": ["{{CpuTemp.Id}}"] }, { "Id": "load", "SensorIds": ["{{CpuLoad.Id}}"] }, { "Id": "pow", "SensorIds": ["{{CpuPower.Id}}"] } ] }
                ],
                "Boxes": [
                  { "Id": "b1", "Name": "GPU", "Placement": { "Anchor": "BottomRight", "OffsetY": 40, "MonitorMode": "Specific", "MonitorKey": "K" },
                    "Rows": [ { "Items": [ { "Id": "gpu", "SensorIds": ["{{GpuTemp.Id}}"] }, { "Id": "hs", "SensorIds": ["{{HotSpot.Id}}"] } ] } ] },
                  { "Id": "b2", "Placement": { "Anchor": "TopCenter" }, "Rows": [ { "Items": [ { "Id": "ram", "SensorIds": ["{{Ram.Id}}"] } ] } ] },
                  { "Id": "b3", "Rows": [] }
                ]
              }
            }
            """;
        var s = SettingsStore.Migrate(System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!);
        var o = s.Overlay;
        Assert.Null(o.Rows);
        Assert.Null(o.Boxes);
        Assert.Equal("fps low . / cpu load pow", Grid(o.Cells));
        Assert.Equal("Processor", o.FindItem("cpu")!.Label); // a row's own name labels its first cell
        Assert.True(o.Second.Enabled);
        Assert.Equal("gpu hs / ram .", Grid(o.Second.Cells)); // the first box is the second window, the next one joins it
        Assert.Equal(OverlayAnchor.BottomRight, o.Second.Placement.Anchor);
        Assert.Equal("K", o.Second.Placement.MonitorKey);
        Assert.Equal(40, o.Second.Placement.OffsetY);
        Assert.Equal(8, o.AllItems().Count()); // nothing lost
        Assert.Equal(OverlayGrouping.Custom, o.Grouping);
    }

    [Fact]
    public void A_fresh_layout_groups_by_component_and_an_empty_old_one_stays_a_list()
    {
        Assert.Equal(OverlayGrouping.ByComponent, new OverlayLayout().Grouping);
        var old = new OverlayLayout { Items = [] };
        old.Normalize();
        Assert.Equal(OverlayGrouping.List, old.Grouping);
        Assert.True(old.IsEmpty);
        Assert.Single(old.Windows());
    }

    [Fact]
    public void Profiles_v1_and_v2_load_as_cells_and_a_v3_profile_round_trips_both_windows()
    {
        var v1 = $$"""
            { "kind": "BetterVitals.OverlayProfile", "version": 1, "name": "Old HUD",
              "layout": { "style": { "arrangement": "Compact" }, "items": [ { "sensorIds": ["{{FpsSensor.Id}}"] }, { "sensorIds": ["{{CpuTemp.Id}}"] } ] } }
            """;
        var (name, old) = LayoutJson.ReadProfile(v1);
        Assert.Equal("Old HUD", name);
        Assert.Equal(OverlayGrouping.OneLine, old.Grouping);
        Assert.Equal([(0, 0), (0, 1)], old.Cells.Select(c => (c.Row, c.Column)));

        var v2 = $$"""
            { "kind": "BetterVitals.OverlayProfile", "version": 2, "name": "Rows",
              "layout": { "rows": [ { "items": [ { "id": "a", "sensorIds": ["{{CpuTemp.Id}}"] }, { "id": "b", "sensorIds": ["{{CpuLoad.Id}}"] } ] } ],
                          "boxes": [ { "placement": { "anchor": "BottomLeft" }, "rows": [ { "items": [ { "id": "c", "sensorIds": ["{{GpuTemp.Id}}"] } ] } ] } ] } }
            """;
        var (_, rows) = LayoutJson.ReadProfile(v2);
        Assert.Equal("a b", Grid(rows.Cells));
        Assert.Equal("c", Grid(rows.Second.Cells));
        Assert.Equal(OverlayAnchor.BottomLeft, rows.Second.Placement.Anchor);

        var layout = Table();
        layout.SetSecondWindow(true);
        Assert.True(layout.MoveCell("hs", InSecond(GridDropKind.Cell, 0, 0)));
        layout.Second.Placement.OffsetY = 40;
        layout.Normalize();
        var text = LayoutJson.WriteProfile("New HUD", layout);
        Assert.Equal(3, LayoutJson.Version);
        Assert.Contains("\"version\": 3", text);
        Assert.Contains("\"Cells\"", text);
        Assert.Contains("\"Second\"", text);
        Assert.DoesNotContain("\"Rows\"", text);
        Assert.DoesNotContain("\"Items\"", text);
        var (_, back) = LayoutJson.ReadProfile(text);
        Assert.Equal(LayoutJson.Text(layout), LayoutJson.Text(back));
        Assert.Equal("hs", Grid(back.Second.Cells));
    }

    // ───────────── Normalize ─────────────

    [Fact]
    public void Normalize_repairs_duplicate_cells_gaps_negative_and_absurd_indices_and_ids()
    {
        var l = new OverlayLayout
        {
            Grouping = (OverlayGrouping)77,
            Cells =
            [
                null!,
                It("a", CpuTemp, 5, 9),
                It("b", CpuLoad, 5, 9), // the same cell: moves right
                It("c", GpuTemp, -3, -1), // negative: the first row / column
                It("a", HotSpot, 40, 2), // a duplicate id gets a new one; row 40 → after the gap
                new() { Id = "", SensorIds = [Ram.Id, "", null!], Row = int.MaxValue, Column = 1_000_000_000 },
                new() { Id = "nothing", SensorIds = [] }, // no sensor: goes
                new() { Id = new string('x', 300), SensorIds = [Nvme.Id], Row = 5, Column = 3, Label = new string('L', 300) },
            ],
            Second = new() { Enabled = false, Placement = null!, Cells = [It("left", FpsSensor, 2, 2)] }, // off: its cell goes to the main window
            Style = { ScalePercent = double.NaN, ValueSize = 1e9, Padding = -4 },
            Placement = { Anchor = (OverlayAnchor)40, OffsetX = double.PositiveInfinity, Margin = -1 },
        };
        l.Normalize();
        Assert.Equal(OverlayGrouping.Custom, l.Grouping);
        var cells = l.Cells;
        // no two cells on one position, rows and columns dense, reading order
        Assert.Equal(cells.Count, cells.Select(c => (c.Row, c.Column)).Distinct().Count());
        Assert.Equal(Enumerable.Range(0, OverlayGrid.RowCount(cells)), cells.Select(c => c.Row).Distinct().Order());
        Assert.Equal(Enumerable.Range(0, OverlayGrid.ColumnCount(cells)), cells.Select(c => c.Column).Distinct().Order());
        Assert.Equal(OverlayGrid.Ordered(cells), cells);
        Assert.Equal(7, cells.Count);
        Assert.Equal(cells.Count, cells.Select(c => c.Id).Distinct().Count());
        Assert.DoesNotContain(cells, c => c.Id == "nothing" || c.Id.Length > 64);
        Assert.Equal((0, 0), (l.FindItem("c")!.Row, l.FindItem("c")!.Column));
        var a = l.FindItem("a")!;
        var b = l.FindItem("b")!;
        Assert.Equal(a.Row, b.Row);
        Assert.Equal(a.Column + 1, b.Column); // the later one moved right
        Assert.Equal([Ram.Id], cells.Single(c => c.SensorIds.Contains(Ram.Id)).SensorIds);
        Assert.Equal(64, cells.Single(c => c.SensorIds.Contains(Nvme.Id)).Label!.Length);
        Assert.Contains(cells, c => c.Id == "left"); // nothing lost
        Assert.Empty(l.Second.Cells);
        Assert.NotNull(l.Second.Placement);
        Assert.Equal(100, l.Style.ScalePercent);
        Assert.Equal(96, l.Style.ValueSize);
        Assert.Equal(0, l.Style.Padding);
        Assert.Equal(OverlayAnchor.TopLeft, l.Placement.Anchor);
        Assert.Equal(0, l.Placement.OffsetX);
        Assert.Equal(0, l.Placement.Margin);
    }

    [Fact]
    public void Normalize_keeps_holes_inside_a_row_but_collapses_empty_rows_and_columns()
    {
        var l = new OverlayLayout { Cells = [It("a", CpuTemp, 0, 0), It("b", CpuLoad, 0, 4), It("c", GpuTemp, 3, 2), It("d", HotSpot, 3, 4)] };
        l.Normalize();
        Assert.Equal("a . b / . c d", Grid(l.Cells));
        Assert.Single(l.Windows()); // a second window that is off is no window
    }

    [Fact]
    public void A_layout_with_cells_rows_and_a_flat_list_keeps_all_of_them()
    {
        var l = Table();
        l.Rows = [new OverlayRow { Items = [It("row", Ram)] }];
        l.Items = [It("flat", Nvme)];
        l.Normalize();
        Assert.Equal("fps . / cpu load / gpu hs / row . / flat .", Grid(l.Cells));
        Assert.Equal(OverlayGrouping.Custom, l.Grouping); // not a fresh schema-1 file: the grouping stays
    }

    // ───────────── dragging cells ─────────────

    [Fact]
    public void Dropping_a_cell_on_another_swaps_them_and_on_a_free_cell_moves_it()
    {
        var l = Table();
        Assert.True(l.MoveCell("hs", InMain(GridDropKind.Cell, 0, 0)));
        Assert.Equal("hs . / cpu load / gpu fps", Grid(l.Cells));
        Assert.True(l.MoveCell("load", InMain(GridDropKind.Cell, 0, 1))); // a hole: moved there
        Assert.Equal("hs load / cpu . / gpu fps", Grid(l.Cells));
        Assert.False(l.MoveCell("load", InMain(GridDropKind.Cell, 0, 1))); // where it is: nothing changes
        Assert.False(l.MoveCell("missing", InMain(GridDropKind.Cell, 0, 0)));
        Assert.False(l.MoveCell("hs", new GridDrop("nope", GridDropKind.Cell, 0, 0)));
    }

    [Fact]
    public void Dropping_between_two_cells_inserts_into_the_row_and_pushes_up_to_the_first_gap()
    {
        var l = new OverlayLayout { Cells = [It("a", CpuTemp, 0, 0), It("b", CpuLoad, 0, 1), It("c", CpuPower, 0, 3), It("x", GpuTemp, 1, 0), It("z", Vram, 1, 2), It("y", HotSpot, 1, 3)] };
        l.Normalize();
        Assert.Equal("a b . c / x . z y", Grid(l.Cells));
        Assert.True(l.MoveCell("x", InMain(GridDropKind.InsertInRow, 0, 1))); // before b: b moves into the gap, c stays
        Assert.Equal("a x b c / . . z y", Grid(l.Cells));
        Assert.True(l.MoveCell("y", InMain(GridDropKind.InsertInRow, 0, 0))); // at the very start: everything moves along
        Assert.Equal("y a x b c / . . z . .", Grid(l.Cells));
        Assert.True(l.MoveCell("c", InMain(GridDropKind.InsertInRow, 0, 2))); // within its own row
        Assert.Equal("y a c x b / . . z . .", Grid(l.Cells));
        Assert.True(l.MoveCell("z", InMain(GridDropKind.InsertInRow, 0, 5))); // at the end of a row; its old row goes
        Assert.Equal("y a c x b z", Grid(l.Cells));
    }

    [Fact]
    public void Dropping_above_below_or_beside_the_rows_makes_a_new_row_or_column()
    {
        var l = Table();
        Assert.True(l.MoveCell("hs", InMain(GridDropKind.NewRow, 0, 0))); // above the first row
        Assert.Equal("hs . / fps . / cpu load / gpu .", Grid(l.Cells));
        Assert.True(l.MoveCell("load", InMain(GridDropKind.NewRow, 4, 1))); // below the last row, in column 1
        Assert.Equal("hs . / fps . / cpu . / gpu . / . load", Grid(l.Cells));
        Assert.True(l.MoveCell("fps", InMain(GridDropKind.NewRow, 3, 0))); // between two rows
        Assert.Equal("hs . / cpu . / fps . / gpu . / . load", Grid(l.Cells));
        Assert.True(l.MoveCell("gpu", InMain(GridDropKind.Cell, 1, 2))); // right of the last column: a new column
        Assert.Equal("hs . . / cpu . gpu / fps . . / . load .", Grid(l.Cells));
        Assert.False(l.MoveCell("hs", InMain(GridDropKind.NewRow, 0, 0))); // alone in its row, at its place: nothing changes
    }

    [Fact]
    public void Cells_move_between_the_windows_and_turning_the_second_window_off_brings_them_back()
    {
        var l = Table();
        Assert.False(l.MoveCell("gpu", InSecond(GridDropKind.NewRow, 0, 0))); // the second window is off
        Assert.True(l.SetSecondWindow(true));
        Assert.False(l.SetSecondWindow(true));
        Assert.NotEqual(l.Placement.Anchor, l.Second.Placement.Anchor);
        Assert.Equal(2, l.Windows().Count());
        Assert.Empty(l.Second.Cells);

        Assert.True(l.MoveCell("gpu", InSecond(GridDropKind.NewRow, 0, 0))); // onto the empty board
        Assert.True(l.MoveCell("hs", InSecond(GridDropKind.InsertInRow, 0, 1)));
        Assert.Equal("fps . / cpu load", Grid(l.Cells));
        Assert.Equal("gpu hs", Grid(l.Second.Cells));
        Assert.True(l.MoveCell("fps", InSecond(GridDropKind.Cell, 0, 1))); // a swap across the windows
        Assert.Equal("hs . / cpu load", Grid(l.Cells));
        Assert.Equal("gpu fps", Grid(l.Second.Cells));

        Assert.True(l.SetSecondWindow(false)); // off: its cells come back below, nothing is lost
        Assert.Equal("hs . / cpu load / gpu fps", Grid(l.Cells));
        Assert.Empty(l.Second.Cells);
        Assert.Single(l.Windows());
        Assert.Equal(OverlayGrouping.Custom, l.Grouping);
        Assert.Equal(5, l.AllItems().Count());
    }

    [Fact]
    public void Removing_a_cell_leaves_a_hole_or_collapses_its_row_and_column()
    {
        var l = Table();
        Assert.True(l.RemoveCell("cpu"));
        Assert.Equal("fps . / . load / gpu hs", Grid(l.Cells));
        Assert.True(l.RemoveCell("fps"));
        Assert.Equal(". load / gpu hs", Grid(l.Cells));
        Assert.True(l.RemoveCell("load"));
        Assert.True(l.RemoveCell("hs"));
        Assert.Equal("gpu", Grid(l.Cells));
        Assert.False(l.RemoveCell("hs"));
    }

    [Fact]
    public void A_sensor_dragged_in_from_the_list_lands_where_it_is_dropped_and_makes_room_on_a_filled_cell()
    {
        var l = Table();
        Assert.True(l.AddCell(It("ram", Ram), InMain(GridDropKind.Cell, 1, 0))); // on "cpu": cpu and load move right
        Assert.Equal("fps . . / ram cpu load / gpu hs .", Grid(l.Cells));
        Assert.True(l.AddCell(It("nvme", Nvme), InMain(GridDropKind.Cell, 0, 1))); // a free cell
        Assert.Equal("fps nvme . / ram cpu load / gpu hs .", Grid(l.Cells));
        Assert.True(l.AddCell(It("low", Low1), InMain(GridDropKind.NewRow, 3, 0)));
        Assert.Equal("low", l.Cells[^1].Id);
        Assert.False(l.AddCell(It("x", Vram), new GridDrop("nope", GridDropKind.Cell, 0, 0)));
    }

    // ───────────── the keyboard ─────────────

    [Fact]
    public void Arrow_keys_swap_with_the_neighbour_or_move_into_a_free_cell_and_past_the_edge_make_room()
    {
        var l = Table();
        Assert.True(l.MoveBy("fps", 0, 1)); // right: into the free cell
        Assert.Equal(". fps / cpu load / gpu hs", Grid(l.Cells));
        Assert.True(l.MoveBy("fps", 1, 0)); // down: swaps with "load"
        Assert.Equal(". load / cpu fps / gpu hs", Grid(l.Cells));
        Assert.True(l.MoveBy("cpu", 0, 1)); // right: swaps with "fps"
        Assert.Equal(". load / fps cpu / gpu hs", Grid(l.Cells));
        Assert.True(l.MoveBy("hs", 0, 1)); // right past the last column: a new column
        Assert.Equal(". load . / fps cpu . / gpu . hs", Grid(l.Cells));
        Assert.True(l.MoveBy("gpu", 1, 0)); // down past the last row: a row of its own
        Assert.Equal(". load . / fps cpu . / . . hs / gpu . .", Grid(l.Cells));
        Assert.False(l.MoveBy("load", -1, 0)); // up past the first row, but alone in it already: nothing changes
        Assert.True(l.MoveBy("cpu", -1, 0)); // up: swaps with "load"
        Assert.Equal(". cpu . / fps load . / . . hs / gpu . .", Grid(l.Cells));
        Assert.True(l.MoveBy("fps", -1, 0)); // up into a free cell
        Assert.Equal("fps cpu . / . load . / . . hs / gpu . .", Grid(l.Cells));
        Assert.True(l.MoveBy("cpu", -1, 0)); // up past the first row with company: a new first row
        Assert.Equal(". cpu . / fps . . / . load . / . . hs / gpu . .", Grid(l.Cells));
        Assert.False(l.MoveBy("gpu", 0, -1)); // left of the first column: nothing
        Assert.False(l.MoveBy("missing", 1, 0));
    }

    // ───────────── Simple mode's presets ─────────────

    [Fact]
    public void The_presets_place_the_cells_in_reading_order()
    {
        var igpu = Sensor("gpu/amd-radeon-graphics/0", HardwareKind.Gpu, SensorKind.Load, "D3D 3D");
        var l = new OverlayLayout { Grouping = OverlayGrouping.List };
        foreach (var (id, s) in new[] { ("gpu", GpuTemp), ("fps", FpsSensor), ("cpu", CpuTemp), ("hs", HotSpot), ("igpu", igpu), ("load", CpuLoad), ("low", Low1) })
            l.AddItem(It(id, s));
        Assert.Equal("gpu / fps / cpu / hs / igpu / load / low", Grid(l.Cells));

        l.Regroup(OverlayGrouping.ByComponent);
        Assert.Equal(OverlayGrouping.ByComponent, l.Grouping);
        Assert.Equal("gpu hs / fps low / cpu load / igpu .", Grid(l.Cells));
        l.AddItem(It("clock", GpuClock)); // joins its component's row
        Assert.Equal("gpu hs clock / fps low . / cpu load . / igpu . .", Grid(l.Cells));
        l.AddItem(It("ram", Ram)); // a new component: a new row
        Assert.Equal("ram", OverlayGrid.At(l.Cells, 4, 0)!.Id);

        l.Regroup(OverlayGrouping.OneLine);
        Assert.Equal(1, OverlayGrid.RowCount(l.Cells));
        Assert.Equal(9, OverlayGrid.ColumnCount(l.Cells));
        l.Regroup(OverlayGrouping.List);
        Assert.Equal(1, OverlayGrid.ColumnCount(l.Cells));
        Assert.Equal(["gpu", "hs", "clock", "fps", "low", "cpu", "load", "igpu", "ram"], l.Cells.Select(c => c.Id));

        // a grid arranged by hand: a new item gets a row of its own at the bottom
        l.Grouping = OverlayGrouping.Custom;
        l.AddItem(It("nvme", Nvme));
        Assert.Equal((9, 0), (l.Cells[^1].Row, l.Cells[^1].Column));
        Assert.Equal(OverlayArrangement.Compact, OverlayGridEdits.ArrangementFor(OverlayGrouping.OneLine));
        Assert.Equal(OverlayArrangement.Vertical, OverlayGridEdits.ArrangementFor(OverlayGrouping.ByComponent));
    }

    [Fact]
    public void The_order_list_places_again_with_a_preset_or_joins_the_neighbours_row_by_hand()
    {
        var l = Table();
        l.Regroup(OverlayGrouping.ByComponent); // the same rows
        Assert.Equal("fps . / cpu load / gpu hs", Grid(l.Cells));
        Assert.True(l.MoveFlat("hs", 0)); // the hot spot first: the GPU row comes first
        Assert.Equal("hs gpu / fps . / cpu load", Grid(l.Cells));
        Assert.False(l.MoveFlat("hs", 0));

        var custom = Table();
        Assert.True(custom.MoveFlat("fps", 2)); // third, after "load": into the CPU row
        Assert.Equal("cpu load fps / gpu hs .", Grid(custom.Cells));
        Assert.True(custom.MoveFlat("hs", 0)); // the very first: before "cpu"
        Assert.Equal("hs cpu load fps / gpu . . .", Grid(custom.Cells));
    }

    [Fact]
    public void Component_of_an_item_from_its_sensor_id()
    {
        Assert.Equal("cpu", OverlayGridEdits.ComponentKey(It("a", CpuTemp)));
        Assert.Equal(GpuHw, OverlayGridEdits.ComponentKey(It("a", HotSpot)));
        Assert.Equal("game", OverlayGridEdits.ComponentKey(It("a", FpsSensor)));
        Assert.Equal("", OverlayGridEdits.ComponentKey(new OverlayItem()));
        Assert.Equal(HardwareKind.Gpu, OverlayGridEdits.ComponentKind(It("a", HotSpot)));
        Assert.Equal(HardwareKind.Other, OverlayGridEdits.ComponentKind(new OverlayItem { SensorIds = ["garbage"] }));
    }

    [Fact]
    public void Edits_replayed_on_a_copy_give_the_same_layout()
    {
        // the editor applies every edit to its working copy and again to the settings: both must end up equal
        var a = Table();
        a.Normalize();
        var b = LayoutJson.Clone(a);
        var rnd = new Random(5);
        var ids = a.AllItems().Select(i => i.Id).ToArray();
        for (var i = 0; i < 400; i++)
        {
            var id = ids[rnd.Next(ids.Length)];
            var window = rnd.Next(4) == 0 ? OverlayLayout.SecondWindowId : OverlayLayout.MainWindowId;
            var drop = new GridDrop(window, (GridDropKind)rnd.Next(3), rnd.Next(-1, 5), rnd.Next(-1, 5));
            var on = rnd.Next(2) == 0;
            var step = rnd.Next(-1, 2);
            Func<OverlayLayout, bool> edit = rnd.Next(6) switch
            {
                0 => l => l.SetSecondWindow(on),
                1 => l => l.MoveBy(id, step, 0),
                2 => l => l.MoveBy(id, 0, step),
                _ => l => l.MoveCell(id, drop),
            };
            Assert.Equal(edit(a), edit(b));
            Assert.Equal(LayoutJson.Text(a), LayoutJson.Text(b));
        }
        Assert.Equal(5, a.AllItems().Count());
    }
}
