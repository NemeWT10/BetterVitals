using System.Windows;
using BetterVitals.Core.Overlay;
using BetterVitals.Localization;
using BetterVitals.Overlay;
using BetterVitals.Services;
using BetterVitals.UI.Pages.OverlayEditor;
using BetterVitals.UI.Themes;
using Xunit;

namespace BetterVitals.Tests.Ui.OverlayEditor;

/// <summary>
/// Rows and boxes in the editor: Simple mode's three arrangements, Advanced mode's drag operations (the same path a
/// mouse drop takes) and their round trip to the settings, and offscreen renders of the page and of the overlay in
/// Aurora and Daylight, PL and EN (tests/output/overlay-editor).
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OverlayRowsEditorTests
{
    private const double PageWidth = 884;

    private static readonly string[] Six = ["fps.fps", "cpu.temp", "gpu.temp", "gpu.hotspot", "cpu.clocks", "gpu.clock"];

    private static void Toggle(EditorFixture f, OverlayEditorModel m, params string[] keys)
    {
        foreach (var key in keys) m.Toggle(f.F(key));
    }

    private static void AssertSaved(EditorFixture f, OverlayEditorModel m) => Assert.Equal(LayoutJson.Text(m.Working), LayoutJson.Text(f.Store.Current.Overlay));

    private static string Shot(EditorFixture f, OverlayEditorPage page, string name, double height, Action<OverlayEditorPage>? before = null)
    {
        var host = Offscreen.Host(page, PageWidth, height);
        page.OnNavigatedTo(null);
        before?.Invoke(page);
        f.Advance(1);
        page.TickNow();
        Offscreen.Layout(host);
        page.Preview.RequestRender();
        Assert.True(Sta.PumpUntil(() => !page.Preview.IsRendering && (page.Model.Working.IsEmpty || page.Preview.RenderCount > 0), 15000), "preview render");
        Sta.PumpUntil(() => FontSamples.Pending == 0, 15000);
        Sta.Pump(60);
        var path = Offscreen.Save(host, name);
        page.OnNavigatedFrom();
        host.Child = null;
        return path;
    }

    [Fact]
    public void Simple_mode_groups_new_items_by_component_by_default_and_switches_arrangement() => Sta.Run(() =>
    {
        using var f = new EditorFixture();
        var m = new OverlayEditorModel(f.Store);
        Assert.Equal(OverlayGrouping.ByComponent, m.Working.Grouping);
        Toggle(f, m, Six);
        var rows = f.Store.Current.Overlay.Rows;
        Assert.Equal(3, rows.Count); // FPS | CPU, core clocks | GPU, hot spot, clock
        Assert.Equal([1, 2, 3], rows.Select(r => r.Items.Count));
        Assert.Equal(OverlayArrangement.Vertical, f.Store.Current.Overlay.Style.Arrangement);

        m.SetGrouping(OverlayGrouping.OneLine);
        Assert.Single(f.Store.Current.Overlay.Rows);
        Assert.Equal(OverlayArrangement.Compact, f.Store.Current.Overlay.Style.Arrangement);
        Assert.False(OverlayEditorModel.HasCustomStyle(f.Store.Current.Overlay)); // the compact look belongs to "one line"
        m.Toggle(f.F("ram.used"));
        Assert.Equal(7, Assert.Single(f.Store.Current.Overlay.Rows).Items.Count);

        m.SetGrouping(OverlayGrouping.List);
        Assert.Equal(7, f.Store.Current.Overlay.Rows.Count);
        Assert.Equal(OverlayArrangement.Vertical, f.Store.Current.Overlay.Style.Arrangement);
        // the order the user clicked survives every switch
        Assert.Equal(m.Working.AllItems().Select(i => i.Id), f.Store.Current.Overlay.AllItems().Select(i => i.Id));
        AssertSaved(f, m);
    });

    [Fact]
    public void Advanced_drag_operations_reach_the_settings_and_make_the_rows_custom() => Sta.Run(() =>
    {
        using var f = new EditorFixture();
        var page = f.Page();
        Toggle(f, page.Model, Six);
        page.Model.SetAdvanced(true);
        page.Model.Reload();
        var tree = page.Advanced!.Tree;
        var l = page.Model.Working;
        var fps = l.Rows[0].Items[0].Id;
        var hot = l.Rows[2].Items[1].Id;
        var cpuRow = l.Rows[1].Id;
        var gpuRow = l.Rows[2].Id;

        // an item into another row, before an item
        Assert.True(tree.DropForTest(false, hot, new LayoutTree.IntoRow(cpuRow, l.Rows[1].Items[0].Id)));
        Assert.Equal(OverlayGrouping.Custom, page.Model.Working.Grouping);
        Assert.Equal(hot, page.Model.Working.FindRow(cpuRow)!.Items[0].Id);
        AssertSaved(f, page.Model);

        // an item into a new row (between two rows)
        Assert.True(tree.DropForTest(false, hot, new LayoutTree.RowSlot(OverlayLayout.MainBoxId, gpuRow)));
        Assert.Equal(4, page.Model.Working.Rows.Count);
        Assert.Equal([hot], page.Model.Working.Rows[2].Items.Select(i => i.Id));

        // an item onto "New box": a box of its own at the next free corner, selected in the Position section
        Assert.True(tree.DropForTest(false, fps, new LayoutTree.NewBox()));
        var box = Assert.Single(page.Model.Working.Boxes);
        Assert.Equal([fps], box.Rows[0].Items.Select(i => i.Id));
        Assert.Equal(OverlayAnchor.TopRight, box.Placement.Anchor);
        Assert.Equal(box.Id, page.Advanced!.SelectedBox);
        AssertSaved(f, page.Model);

        // a whole row into that box, then a row onto "New box"
        Assert.True(page.Advanced!.Tree.DropForTest(true, gpuRow, new LayoutTree.RowSlot(box.Id, null)));
        Assert.Equal(2, page.Model.Working.Boxes[0].Rows.Count);
        Assert.True(page.Advanced!.Tree.DropForTest(true, cpuRow, new LayoutTree.NewBox()));
        Assert.Equal(2, page.Model.Working.Boxes.Count);
        Assert.False(page.Advanced!.Tree.DropForTest(true, cpuRow, new LayoutTree.NewBox())); // already alone in its box: nothing happens
        AssertSaved(f, page.Model);

        // renaming, removing a box (its rows come back), and Simple mode says it is custom
        Assert.True(page.Model.RenameRow(gpuRow, "Graphics"));
        Assert.True(page.Model.RenameBox(box.Id, "GPU"));
        Assert.Equal("GPU", page.Context.BoxTitle(box.Id));
        Assert.Equal("Graphics", page.Context.RowLabel(page.Model.Working.FindRow(gpuRow)!));
        Assert.True(OverlayEditorModel.HasCustomRows(page.Model.Working));
        var items = page.Model.Working.AllItems().Count();
        Assert.True(page.Model.RemoveBox(box.Id));
        Assert.Equal(items, page.Model.Working.AllItems().Count());
        Assert.Contains(page.Model.Working.Rows, r => r.Id == gpuRow);
        AssertSaved(f, page.Model);

        // Simple mode's "Restore default look" brings every box back and groups again
        page.Model.SetGrouping(OverlayGrouping.ByComponent);
        page.Model.ResetLook();
        Assert.Empty(page.Model.Working.Boxes);
        Assert.False(OverlayEditorModel.HasCustomizations(page.Model.Working));
        Assert.Equal(3, page.Model.Working.Rows.Count);
        AssertSaved(f, page.Model);
        page.Dispose();
    });

    [Fact]
    public void Set_on_screen_saves_every_box() => Sta.Run(() =>
    {
        var before = AppServices.Overlay;
        var fake = new FakeOverlay();
        AppServices.Overlay = fake;
        try
        {
            using var f = new EditorFixture("en");
            var page = f.Page();
            Toggle(f, page.Model, Six);
            page.Model.SetAdvanced(true);
            page.Model.Reload();
            var boxId = page.Model.DetachRow(page.Model.Working.Rows[2].Id)!;
            fake.Others[boxId] = new OverlayPlacement { Anchor = OverlayAnchor.MiddleRight, OffsetX = 5, OffsetY = -60, Margin = 24 };
            typeof(AdvancedView).GetMethod("BeginOnScreen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(page.Advanced, null);
            Assert.True(fake.IsEditingOnScreen);
            fake.EndOnScreenEdit(true);
            var o = f.Store.Current.Overlay;
            Assert.Equal(OverlayAnchor.BottomRight, o.Placement.Anchor);
            Assert.Equal(OverlayAnchor.MiddleRight, o.Boxes[0].Placement.Anchor);
            Assert.Equal(-60, o.Boxes[0].Placement.OffsetY);
            page.Dispose();
        }
        finally
        {
            AppServices.Overlay = before;
        }
    });

    [Theory]
    [InlineData("en", "aurora")]
    [InlineData("pl", "daylight")]
    [InlineData("pl", "aurora")]
    [InlineData("en", "daylight")]
    public void Simple_mode_with_rows_renders(string lang, string theme) => Sta.Run(() =>
    {
        using var f = new EditorFixture(lang, theme);
        var page = f.Page();
        Toggle(f, page.Model, Six);
        Shot(f, page, $"rows_simple_{theme}_{lang}", 1700);
        page.Model.SetGrouping(OverlayGrouping.OneLine);
        Shot(f, page, $"rows_simple_oneline_{theme}_{lang}", 900);
        Assert.NotNull(page.Simple);
        page.Dispose();
    });

    [Theory]
    [InlineData("en", "aurora")]
    [InlineData("pl", "daylight")]
    public void Advanced_mode_with_rows_and_a_detached_box_renders(string lang, string theme) => Sta.Run(() =>
    {
        var before = AppServices.Overlay;
        AppServices.Overlay = new FakeOverlay();
        try
        {
            using var f = new EditorFixture(lang, theme);
            var page = f.Page();
            Toggle(f, page.Model, [.. Six, "ram.used"]);
            page.Model.SetAdvanced(true);
            page.Model.Reload();
            var gpu = page.Model.DetachRow(page.Model.Working.Rows[2].Id)!;
            page.Model.RenameBox(gpu, "GPU");
            page.Model.RenameRow(page.Model.Working.Rows[1].Id, lang == "pl" ? "Procesor" : "Processor");
            page.Model.Reload();
            Shot(f, page, $"rows_advanced_{theme}_{lang}", 2300);
            // the preview shows both boxes; the Position section edits the detached one
            Assert.Equal(2, page.Preview.RenderedBoxes.Count);
            page.Advanced!.OnBoxDragged(gpu);
            Assert.Equal(gpu, page.Advanced.SelectedBox);
            Shot(f, page, $"rows_advanced_place_{theme}_{lang}", 4400);
            Assert.DoesNotContain(Loc.MissingKeys, k => k.StartsWith("ovl.", StringComparison.Ordinal));
            page.Dispose();
        }
        finally
        {
            AppServices.Overlay = before;
        }
    });

    [Theory]
    [InlineData("aurora", "en")]
    [InlineData("daylight", "pl")]
    public void The_overlay_renders_list_rows_one_line_and_a_detached_box_in_the_app_themes(string theme, string lang) => Sta.Run(() =>
    {
        using var f = new EditorFixture(lang, theme);
        var m = new OverlayEditorModel(f.Store);
        Toggle(f, m, [.. Six, "ram.used"]);
        var feed = new PreviewFeed(f.Engine);
        for (var i = 0; i < 30; i++)
        {
            f.Advance(1);
            feed.Sample(m.Working.AllItems().SelectMany(x => x.SensorIds));
        }
        var overlayTheme = ThemeManager.I.ResolveOverlayTheme();
        var dir = Offscreen.OutputDir;
        void Render(string name, OverlayLayout layout)
        {
            foreach (var (id, bmp) in OverlayHost.RenderBoxesToBitmaps(layout, overlayTheme, feed.Build(layout), 1080, Loc.I.Culture))
                BetterVitals.Overlay.Rendering.Offscreen.WritePng(bmp, Path.Combine(dir, $"overlay_{name}_{(id == OverlayLayout.MainBoxId ? "main" : "box")}_{theme}_{lang}.png"));
        }
        m.SetGrouping(OverlayGrouping.List);
        Render("list", m.Working);
        m.SetGrouping(OverlayGrouping.ByComponent);
        Render("rows", m.Working);
        m.SetGrouping(OverlayGrouping.OneLine);
        Render("oneline", m.Working);
        m.SetGrouping(OverlayGrouping.ByComponent);
        m.DetachRow(m.Working.Rows[2].Id);
        Render("detached", m.Working);
        Assert.Single(m.Working.Boxes);
    });

    /// <summary>On-screen edit without the real overlay; "Done" reports the main box bottom-right plus <see cref="Others"/>.</summary>
    private sealed class FakeOverlay : IOverlayService
    {
        private Action<IReadOnlyDictionary<string, OverlayPlacement>>? _commit;
        private Action? _cancel;

        public IReadOnlyList<OverlayMonitor> Monitors { get; } =
        [
            new(@"\?\DISPLAY#GSM5BBF#1", @"\.\DISPLAY1", "LG ULTRAGEAR", new PixelRect(0, 0, 2560, 1440), new PixelRect(0, 0, 2560, 1392), true),
        ];

        public Dictionary<string, OverlayPlacement> Others { get; } = [];

        public bool IsEditingOnScreen { get; private set; }

        public void BeginOnScreenEdit(Action<IReadOnlyDictionary<string, OverlayPlacement>> commit, Action cancel)
        {
            IsEditingOnScreen = true;
            _commit = commit;
            _cancel = cancel;
        }

        public void EndOnScreenEdit(bool commit)
        {
            IsEditingOnScreen = false;
            var all = new Dictionary<string, OverlayPlacement>(Others) { [OverlayLayout.MainBoxId] = new() { Anchor = OverlayAnchor.BottomRight, Margin = 24 } };
            if (commit) _commit?.Invoke(all);
            else _cancel?.Invoke();
        }

        public event Action? MonitorsChanged { add { } remove { } }
    }
}
