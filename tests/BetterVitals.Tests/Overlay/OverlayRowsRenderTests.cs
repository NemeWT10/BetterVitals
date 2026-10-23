using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Overlay;
using BetterVitals.Overlay.Rendering;
using Xunit;
using static BetterVitals.Tests.Overlay.OverlayFixtures;

namespace BetterVitals.Tests.Overlay;

/// <summary>How rows are measured and drawn: a label column, aligned values, small item labels, boxes of their own. PNGs in tests/output/overlay.</summary>
public class OverlayRowsRenderTests
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

    private static OverlayRow Row(params OverlayItem[] items) => new() { Items = [.. items] };

    /// <summary>What "Rows by component" makes of FPS, CPU, GPU and RAM values.</summary>
    internal static OverlayLayout ByComponent() => new()
    {
        Grouping = OverlayGrouping.ByComponent,
        Rows =
        [
            Row(new OverlayItem { SensorIds = [FpsSensor.Id], ShowGraph = true }, Item(Low1)),
            Row(Item(CpuTemp), new OverlayItem { SensorIds = [.. Cores.Select(c => c.Id)] }, Item(CpuLoad)),
            Row(Item(GpuTemp), Item(HotSpot), Item(GpuClock), Item(GpuLoad)),
            Row(Item(Ram)),
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

    [Fact]
    public void Rows_keep_their_values_aligned_and_never_overlap()
    {
        using var s = OverlayRenderTests.Build(ByComponent(), Values(), 1080);
        // the first values share their right edge, the second values their left edge (a sparkline only moves its own row's)
        Assert.Single(s.Rows.Select(r => r.Items[0].ValueRight).Distinct());
        Assert.Single(s.Rows.Where(r => r.Items.Count > 1 && r.Items[0].Spark is null).Select(r => r.Items[1].TagX).Distinct());
        Assert.True(s.Rows[0].Items[1].TagX > s.Rows[1].Items[1].TagX, "after the FPS sparkline");
        foreach (var row in s.Rows)
        {
            Assert.Equal(row.Items.Count - 1, row.Dots.Count);
            for (var k = 1; k < row.Items.Count; k++)
            {
                var prev = row.Items[k - 1];
                var it = row.Items[k];
                Assert.True(it.TagX >= prev.CellEnd + 8, $"cell {k} starts after the previous one ends");
                Assert.InRange(row.Dots[k - 1], prev.CellEnd, it.TagX); // the dot sits between them
                Assert.True(it.ValueRight - it.Main.Width >= it.TagX + (it.Tag?.Width ?? 0), "the value starts after its label");
            }
            Assert.True(row.Items[^1].CellEnd < s.Width, "inside the panel");
            Assert.Equal(row.Baseline, row.Items[^1].Baseline); // one baseline per row
        }
        // rows stack without overlapping
        for (var r = 1; r < s.Rows.Count; r++) Assert.True(s.Rows[r].Top >= s.Rows[r - 1].Top + s.Rows[r - 1].Height);
    }

    [Fact]
    public void Rows_by_component_are_shorter_than_the_list_and_scale_with_the_monitor()
    {
        var values = Values();
        using var rows = OverlayRenderTests.Build(ByComponent(), values, 1080);
        var list = ByComponent();
        list.Regroup(OverlayGrouping.List, "l");
        using var lines = OverlayRenderTests.Build(list, values, 1080);
        Assert.Equal(10, lines.Rows.Count);
        Assert.True(rows.Height < lines.Height * 0.5, $"{rows.Height} vs {lines.Height}");
        using var uhd = OverlayRenderTests.Build(ByComponent(), values, 2160);
        Assert.InRange(uhd.Width / (double)rows.Width, 1.85, 2.15);
        Assert.InRange(uhd.Height / (double)rows.Height, 1.85, 2.15);
    }

    [Fact]
    public void A_row_name_of_its_own_replaces_the_automatic_one()
    {
        var layout = ByComponent();
        layout.Rows[2].Label = "Graphics";
        layout.Rows[3].Label = "Memory"; // a row of one: its own name, and the item label next to the value
        using var s = OverlayRenderTests.Build(layout, Values(), 1080);
        Assert.Equal("Graphics", s.Rows[2].Label!.Text);
        Assert.Equal(["GPU", "GPU HS", "GPU", "GPU"], s.Rows[2].Items.Select(i => i.Tag?.Text));
        Assert.Equal("Memory", s.Rows[3].Label!.Text);
        Assert.Equal("RAM", s.Rows[3].Items[0].Tag?.Text);
    }

    [Fact]
    public void A_mixed_row_takes_its_first_items_label()
    {
        var layout = new OverlayLayout { Rows = [Row(Item(FpsSensor), Item(CpuTemp), Item(GpuTemp))] };
        using var s = OverlayRenderTests.Build(layout, Values(), 1080);
        Assert.Equal("FPS", s.Rows[0].Label!.Text);
        Assert.Equal([null, "CPU", "GPU"], s.Rows[0].Items.Select(i => i.Tag?.Text));
    }

    [Fact]
    public void Every_box_renders_only_its_own_rows()
    {
        var layout = ByComponent();
        layout.Normalize();
        layout.MoveRowToNewBox(layout.Rows[2].Id, "gpu-box", new OverlayPlacement { Anchor = OverlayAnchor.TopRight });
        layout.MoveItemToNewBox(layout.Rows[0].Items[0].Id, "fps-box", "fps-row", new OverlayPlacement { Anchor = OverlayAnchor.BottomLeft });
        var values = Values();
        var boxes = OverlayHost.RenderBoxesToBitmaps(layout, OverlayTheme.Default, values, 1080);
        Assert.Equal([OverlayLayout.MainBoxId, "gpu-box", "fps-box"], boxes.Select(b => b.BoxId));
        using var main = OverlayRenderTests.Build(layout, values, 1080);
        using var gpu = OverlayRenderTests.Build(layout, values, 1080, boxId: "gpu-box");
        using var fps = OverlayRenderTests.Build(layout, values, 1080, boxId: "fps-box");
        Assert.Equal(3, main.Rows.Count); // 1% low, CPU, RAM
        Assert.Equal("1% low", main.Rows[0].Label!.Text);
        Assert.Equal(["GPU"], gpu.Rows.Select(r => r.Label!.Text));
        Assert.Equal(["FPS"], fps.Rows.Select(r => r.Label!.Text));
        Assert.Equal((boxes[1].Bitmap.Width, boxes[1].Bitmap.Height), (gpu.Width, gpu.Height));
        foreach (var (id, bmp) in boxes) Save(bmp, $"box-{id}-1080.png");
        Assert.Equal(boxes[0].Bitmap.Width, OverlayHost.RenderToBitmap(layout, OverlayTheme.Default, values, 1080).Width);
    }

    [Fact]
    public void Arrangements_render_to_png()
    {
        var values = Values();
        foreach (var (name, grouping, arrangement) in new[]
                 {
                     ("list", OverlayGrouping.List, OverlayArrangement.Vertical),
                     ("rows", OverlayGrouping.ByComponent, OverlayArrangement.Vertical),
                     ("oneline", OverlayGrouping.OneLine, OverlayArrangement.Compact),
                     ("rows-above", OverlayGrouping.ByComponent, OverlayArrangement.Horizontal),
                     ("rows-compact", OverlayGrouping.ByComponent, OverlayArrangement.Compact),
                 })
        {
            var layout = ByComponent();
            layout.Regroup(grouping, "g");
            layout.Style.Arrangement = arrangement;
            foreach (var h in new[] { 1080, 2160 })
            {
                using var s = OverlayRenderTests.Build(layout, values, h);
                var bmp = Offscreen.Render(s, false, GameBackdrop with { Margin = h / 45 });
                Save(bmp, $"arrange-{name}-{h}-ingame.png");
                Assert.True(bmp.Width > 40 && bmp.Height > 20);
            }
        }
        var light = ByComponent();
        using (var s = OverlayRenderTests.Build(light, values, 1440, Light))
            Save(Offscreen.Render(s, false, GameBackdrop with { Top = new Rgba(0xE8, 0xEE, 0xF5), Bottom = new Rgba(0x9F, 0xB4, 0xC8) }), "arrange-rows-1440-light-ingame.png");
    }

    [Fact]
    public void Updating_rows_does_not_allocate()
    {
        var values = Values();
        using var s = OverlayRenderTests.Build(ByComponent(), values, 1080);
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
