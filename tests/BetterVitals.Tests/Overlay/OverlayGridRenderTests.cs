using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Overlay;
using BetterVitals.Overlay.Rendering;
using Xunit;
using static BetterVitals.Tests.Overlay.OverlayFixtures;

namespace BetterVitals.Tests.Overlay;

/// <summary>
/// How a window's grid is measured and drawn: a table whose columns line up (label, number, unit and sparkline parts),
/// a label column for the first column, small labels where the row label does not say it, holes, both windows, the
/// geometry the editor's board uses. PNGs in tests/output/overlay.
/// </summary>
public class OverlayGridRenderTests
{
    private static readonly Offscreen.Backdrop GameBackdrop = new(new Rgba(0x3A, 0x55, 0x7A), new Rgba(0xC9, 0x8B, 0x5A), 24);
    private static readonly SensorDescriptor[] Cores = CoreClocks(8);

    internal static List<PreviewSensor> Values()
    {
        var values = new List<PreviewSensor>
        {
            new(FpsSensor, 141, Wave(141, 12)), new(Low1, 118), new(FrameTime, 7.1f),
            new(CpuTemp, 61), new(CpuLoad, 38), new(CpuPower, 88),
            new(GpuTemp, 66), new(HotSpot, 76), new(GpuClock, 2856), new(GpuLoad, 98), new(Vram, 11234),
            new(Ram, 18.7f), new(Nvme, 44),
        };
        values.AddRange(Cores.Select((c, i) => new PreviewSensor(c, 4700 + i * 47)));
        return values;
    }

    private static OverlayItem At(int row, int column, OverlayItem item)
    {
        item.Row = row;
        item.Column = column;
        return item;
    }

    private static OverlayItem At(int row, int column, SensorDescriptor sensor) => At(row, column, Item(sensor));

    /// <summary>What "Rows by component" makes of FPS, CPU, GPU and RAM values.</summary>
    internal static OverlayLayout ByComponent() => new()
    {
        Grouping = OverlayGrouping.ByComponent,
        Cells =
        [
            At(0, 0, new OverlayItem { SensorIds = [FpsSensor.Id], ShowGraph = true }), At(0, 1, Low1),
            At(1, 0, CpuTemp), At(1, 1, new OverlayItem { SensorIds = [.. Cores.Select(c => c.Id)] }), At(1, 2, CpuLoad),
            At(2, 0, GpuTemp), At(2, 1, HotSpot), At(2, 2, GpuClock), At(2, 3, GpuLoad),
            At(3, 0, Ram),
        ],
    };

    /// <summary>A 3 × 3 table: one row per component, temperature | load | clock or power.</summary>
    internal static OverlayLayout NineCells() => new()
    {
        Grouping = OverlayGrouping.Custom,
        Cells =
        [
            At(0, 0, CpuTemp), At(0, 1, CpuLoad), At(0, 2, CpuPower),
            At(1, 0, GpuTemp), At(1, 1, GpuLoad), At(1, 2, GpuClock),
            At(2, 0, HotSpot), At(2, 1, Vram), At(2, 2, FpsSensor),
        ],
    };

    private static void Save(OverlayBitmap bmp, string name) => Offscreen.WritePng(bmp, Path.Combine(OutputDir, name));

    [Fact]
    public void Rows_by_component_label_each_row_once_and_keep_the_hot_spot_label()
    {
        using var s = OverlayRenderTests.Build(ByComponent(), Values(), 1080);
        Assert.Equal(["FPS", "CPU", "GPU", "RAM"], s.Rows.Select(r => r.Label?.Text));
        // an item whose label equals the row's draws none; the others keep theirs ("GPU HS", not "Hot Spot")
        Assert.Equal([null, "1% low"], s.Rows[0].Items.Select(i => i.Tag?.Text));
        Assert.Equal([null, "Cores", null], s.Rows[1].Items.Select(i => i.Tag?.Text));
        Assert.Equal([null, "GPU HS", null, null], s.Rows[2].Items.Select(i => i.Tag?.Text));
        Assert.Null(s.Rows[3].Items[0].Tag);
        Assert.Equal(["141", "118"], s.Rows[0].Items.Select(i => new string(i.Main.Text)));
        Assert.Equal("76", new string(s.Rows[2].Items[1].Main.Text));
    }

    [Theory]
    [InlineData(OverlayArrangement.Vertical)]
    [InlineData(OverlayArrangement.Horizontal)]
    [InlineData(OverlayArrangement.Compact)]
    public void Columns_line_up_like_a_table_and_never_overlap(OverlayArrangement arrangement)
    {
        foreach (var layout in new[] { ByComponent(), NineCells() })
        {
            layout.Style.Arrangement = arrangement;
            using var s = OverlayRenderTests.Build(layout, Values(), 1080);
            Assert.Equal(layout.Cells.Max(c => c.Column) + 1, s.ColumnCount);
            for (var c = 0; c < s.ColumnCount; c++)
            {
                var column = s.Items.Where(i => i.Column == c).ToList();
                // one edge for the numbers of a column, one start for its labels
                Assert.Single(column.Select(i => i.ValueRight).Distinct());
                if (arrangement != OverlayArrangement.Vertical || c > 0)
                    Assert.Single(column.Select(i => arrangement == OverlayArrangement.Vertical ? i.TagX : i.LabelX).Distinct());
                Assert.All(column, i => Assert.InRange(i.ValueRight, s.ColumnLeft[c], s.ColumnRight[c] + 0.01f));
                if (c > 0) Assert.True(s.ColumnLeft[c] > s.ColumnRight[c - 1], $"column {c} starts after column {c - 1} ends");
            }
            foreach (var row in s.Rows)
            {
                for (var k = 1; k < row.Items.Count; k++)
                {
                    var prev = row.Items[k - 1];
                    var it = row.Items[k];
                    Assert.True(it.ValueRight - it.Main.Width > prev.ValueRight, "values of a row never overlap");
                }
                Assert.True(row.Items[^1].ValueRight < s.Width, "inside the panel");
                Assert.Single(row.Items.Select(i => i.Baseline).Distinct()); // one baseline per row
            }
            for (var r = 1; r < s.Rows.Count; r++) Assert.True(s.Rows[r].Top >= s.Rows[r - 1].Top + s.Rows[r - 1].Height);
        }
    }

    [Fact]
    public void Vertical_rows_put_a_dot_between_their_values_and_keep_the_cells_apart()
    {
        using var s = OverlayRenderTests.Build(ByComponent(), Values(), 1080);
        foreach (var row in s.Rows)
        {
            Assert.Equal(row.Items.Count - 1, row.Dots.Count);
            for (var k = 1; k < row.Items.Count; k++)
            {
                var prev = row.Items[k - 1];
                var it = row.Items[k];
                Assert.True(it.TagX >= prev.CellEnd + 8, $"cell {k} starts after the previous one ends");
                Assert.InRange(row.Dots[k - 1], prev.CellEnd, it.TagX);
                Assert.True(it.ValueRight - it.Main.Width >= it.TagX + (it.Tag?.Width ?? 0), "the value starts after its label");
            }
        }
    }

    [Fact]
    public void A_sparkline_at_the_end_of_a_row_runs_into_the_free_space_and_one_inside_a_row_widens_its_column()
    {
        var values = Values();
        var alone = ByComponent();
        alone.Cells.RemoveAll(c => c.SensorIds[0] == Low1.Id); // FPS + sparkline alone in its row
        using var free = OverlayRenderTests.Build(alone, values, 1080);
        var fps = free.Rows[0].Items[0];
        var cpu = free.Rows[1].Items[0];
        Assert.True(fps.CellEnd > free.ColumnRight[0], "the sparkline runs past column 0");
        Assert.True(free.ColumnLeft[1] < fps.CellEnd, "column 1 does not wait for it");
        Assert.True(free.Width >= fps.CellEnd, "inside the panel");
        Assert.Equal(cpu.ValueRight, fps.ValueRight);

        using var inside = OverlayRenderTests.Build(ByComponent(), values, 1080); // FPS + sparkline, then 1% low
        Assert.True(inside.ColumnLeft[1] > inside.Rows[0].Items[0].CellEnd, "column 1 starts after the sparkline in every row");
        Assert.Single(inside.Items.Where(i => i.Column == 1).Select(i => i.TagX).Distinct());
    }

    [Fact]
    public void A_hole_keeps_the_cells_after_it_in_their_column()
    {
        var layout = NineCells();
        layout.Cells.RemoveAll(c => c.Row == 0 && c.Column == 1); // CPU: temp, _, power
        using var s = OverlayRenderTests.Build(layout, Values(), 1080);
        var power = s.Rows[0].Items[1];
        var clock = s.Rows[1].Items[2];
        Assert.Equal(2, power.Column);
        Assert.Equal(clock.ValueRight, power.ValueRight);
        Assert.Single(s.Rows[0].Dots); // one dot, before the power
        Assert.InRange(s.Rows[0].Dots[0], s.ColumnRight[1], s.ColumnLeft[2]);

        // a row whose first column is empty has no row label: its cells show their own
        layout.Cells.RemoveAll(c => c.Row == 1 && c.Column == 0);
        using var t = OverlayRenderTests.Build(layout, Values(), 1080);
        Assert.Null(t.Rows[1].Label);
        Assert.Equal(["GPU", "GPU"], t.Rows[1].Items.Select(i => i.Tag?.Text));
        Assert.Equal(t.Rows[0].Items[1].ValueRight, t.Rows[1].Items[1].ValueRight);
    }

    [Fact]
    public void The_first_cells_own_label_names_its_row()
    {
        var layout = ByComponent();
        layout.Cells.First(c => c.SensorIds[0] == GpuTemp.Id).Label = "Graphics";
        using var s = OverlayRenderTests.Build(layout, Values(), 1080);
        Assert.Equal("Graphics", s.Rows[2].Label!.Text);
        Assert.Equal([null, "GPU HS", "GPU", "GPU"], s.Rows[2].Items.Select(i => i.Tag?.Text));
        Assert.Equal("CPU", s.Rows[1].Label!.Text); // untouched rows stay automatic
    }

    [Fact]
    public void A_mixed_row_takes_its_first_items_label()
    {
        var layout = new OverlayLayout { Cells = [At(0, 0, FpsSensor), At(0, 1, CpuTemp), At(0, 2, GpuTemp)] };
        using var s = OverlayRenderTests.Build(layout, Values(), 1080);
        Assert.Equal("FPS", s.Rows[0].Label!.Text);
        Assert.Equal([null, "CPU", "GPU"], s.Rows[0].Items.Select(i => i.Tag?.Text));
    }

    [Fact]
    public void Rows_by_component_are_shorter_than_the_list_and_scale_with_the_monitor()
    {
        var values = Values();
        using var rows = OverlayRenderTests.Build(ByComponent(), values, 1080);
        var list = ByComponent();
        list.Regroup(OverlayGrouping.List);
        using var lines = OverlayRenderTests.Build(list, values, 1080);
        Assert.Equal(10, lines.Rows.Count);
        Assert.Equal(1, lines.ColumnCount);
        Assert.True(rows.Height < lines.Height * 0.5, $"{rows.Height} vs {lines.Height}");
        using var uhd = OverlayRenderTests.Build(ByComponent(), values, 2160);
        Assert.InRange(uhd.Width / (double)rows.Width, 1.85, 2.15);
        Assert.InRange(uhd.Height / (double)rows.Height, 1.85, 2.15);
    }

    [Fact]
    public void Every_window_renders_only_its_own_cells()
    {
        var layout = ByComponent();
        layout.Normalize();
        layout.SetSecondWindow(true);
        foreach (var gpu in layout.Cells.Where(c => c.Row == 2).ToList())
            Assert.True(layout.MoveCell(gpu.Id, new GridDrop(OverlayLayout.SecondWindowId, GridDropKind.Cell, 0, gpu.Column)));
        var values = Values();
        var windows = OverlayHost.RenderWindowsToBitmaps(layout, OverlayTheme.Default, values, 1080);
        Assert.Equal([OverlayLayout.MainWindowId, OverlayLayout.SecondWindowId], windows.Select(w => w.WindowId));
        using var main = OverlayRenderTests.Build(layout, values, 1080);
        using var second = OverlayRenderTests.Build(layout, values, 1080, windowId: OverlayLayout.SecondWindowId);
        Assert.Equal(["FPS", "CPU", "RAM"], main.Rows.Select(r => r.Label!.Text));
        Assert.Equal(["GPU"], second.Rows.Select(r => r.Label!.Text));
        Assert.Equal(4, second.ColumnCount);
        Assert.Equal((windows[1].Bitmap.Width, windows[1].Bitmap.Height), (second.Width, second.Height));
        foreach (var (id, bmp) in windows) Save(bmp, $"window-{id}-1080.png");
        Assert.Equal(windows[0].Bitmap.Width, OverlayHost.RenderToBitmap(layout, OverlayTheme.Default, values, 1080).Width);

        // the second window off: no picture for it, its cells back in the main window
        layout.SetSecondWindow(false);
        Assert.Single(OverlayHost.RenderWindowsToBitmaps(layout, OverlayTheme.Default, values, 1080));
        Assert.Equal(10, layout.Cells.Count);
    }

    [Fact]
    public void The_grid_geometry_matches_the_picture_and_its_cells()
    {
        var layout = NineCells();
        layout.Normalize();
        foreach (var arrangement in Enum.GetValues<OverlayArrangement>())
        {
            layout.Style.Arrangement = arrangement;
            var (bmp, grid) = OverlayHost.RenderWindowWithGrid(layout, OverlayTheme.Default, Values(), OverlayLayout.MainWindowId, 1.5);
            Assert.Equal((bmp.Width, bmp.Height), (grid.Width, grid.Height));
            Assert.Equal(3, grid.Columns);
            Assert.Equal(3, grid.Rows);
            Assert.Equal(9, grid.Cells.Count);
            Assert.Equal(layout.Cells.Select(c => (c.Id, c.Row, c.Column)), grid.Cells.Select(c => (c.ItemId, c.Row, c.Column)));
            for (var c = 0; c < 3; c++)
            {
                Assert.True(grid.ColumnLeft[c] >= 0 && grid.ColumnRight[c] <= grid.Width && grid.ColumnLeft[c] < grid.ColumnRight[c]);
                if (c > 0) Assert.True(grid.ColumnLeft[c] > grid.ColumnRight[c - 1]);
            }
            for (var r = 0; r < 3; r++)
            {
                Assert.True(grid.RowTop[r] >= 0 && grid.RowBottom[r] <= grid.Height && grid.RowTop[r] < grid.RowBottom[r]);
                if (r > 0) Assert.True(grid.RowTop[r] >= grid.RowBottom[r - 1]);
            }
            Save(bmp, $"grid-3x3-{arrangement}-board.png");
        }
        // an empty window: no cells, no columns
        var (_, empty) = OverlayHost.RenderWindowWithGrid(new OverlayLayout(), OverlayTheme.Default, [], OverlayLayout.MainWindowId, 1);
        Assert.Empty(empty.Cells);
        Assert.Equal(0, empty.Columns);
    }

    [Fact]
    public void Grids_render_to_png()
    {
        var values = Values();
        foreach (var (name, make) in new (string, Func<OverlayLayout>)[] { ("3x3", NineCells), ("rows", ByComponent) })
        {
            foreach (var arrangement in Enum.GetValues<OverlayArrangement>())
            {
                var layout = make();
                layout.Style.Arrangement = arrangement;
                foreach (var h in new[] { 1080, 2160 })
                {
                    using var s = OverlayRenderTests.Build(layout, values, h);
                    var bmp = Offscreen.Render(s, false, GameBackdrop with { Margin = h / 45 });
                    Save(bmp, $"grid-{name}-{arrangement}-{h}-ingame.png");
                    Assert.True(bmp.Width > 40 && bmp.Height > 20);
                }
            }
        }
        foreach (var (name, grouping, arrangement) in new[]
                 {
                     ("list", OverlayGrouping.List, OverlayArrangement.Vertical),
                     ("bycomponent", OverlayGrouping.ByComponent, OverlayArrangement.Vertical),
                     ("oneline", OverlayGrouping.OneLine, OverlayArrangement.Compact),
                 })
        {
            var layout = ByComponent();
            layout.Regroup(grouping);
            layout.Style.Arrangement = arrangement;
            using var s = OverlayRenderTests.Build(layout, values, 1080);
            Save(Offscreen.Render(s, false, GameBackdrop), $"preset-{name}-1080-ingame.png");
        }
        using (var s = OverlayRenderTests.Build(NineCells(), values, 1440, Light))
            Save(Offscreen.Render(s, false, GameBackdrop with { Top = new Rgba(0xE8, 0xEE, 0xF5), Bottom = new Rgba(0x9F, 0xB4, 0xC8) }), "grid-3x3-1440-light-ingame.png");
    }

    [Fact]
    public void Updating_a_grid_does_not_allocate()
    {
        var values = Values();
        foreach (var layout in new[] { ByComponent(), NineCells() })
        {
            using var s = OverlayRenderTests.Build(layout, values, 1080);
            var frame = new float[values.Count];
            for (var i = 0; i < 50; i++) Fill(i);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++) Fill(i);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

            void Fill(int i)
            {
                for (var k = 0; k < frame.Length; k++) frame[k] = values[k].Value + (i * 7 + k * 3) % 23;
                s.Update(frame);
            }
        }
    }
}
