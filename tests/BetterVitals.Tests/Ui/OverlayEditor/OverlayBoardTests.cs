using System.Windows;
using System.Windows.Input;
using BetterVitals.Core.Overlay;
using BetterVitals.Localization;
using BetterVitals.Overlay;
using BetterVitals.Services;
using BetterVitals.UI.Pages.OverlayEditor;
using BetterVitals.UI.Themes;
using Xunit;

namespace BetterVitals.Tests.Ui.OverlayEditor;

/// <summary>
/// The layout board in the editor: its drop rules, the drags it applies (the same path a mouse drop takes) and their
/// round trip to the settings, the second window, the keyboard, removing with Undo, Simple mode's presets, and offscreen
/// renders of the page (also mid-drag) and of the overlay in Aurora and Daylight, PL and EN (tests/output/overlay-editor).
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OverlayBoardTests
{
    private const double PageWidth = 884;

    private static readonly string[] Six = ["fps.fps", "cpu.temp", "gpu.temp", "gpu.hotspot", "cpu.clocks", "gpu.clock"];

    private static void Toggle(EditorFixture f, OverlayEditorModel m, params string[] keys)
    {
        foreach (var key in keys) m.Toggle(f.F(key));
    }

    private static void AssertSaved(EditorFixture f, OverlayEditorModel m) => Assert.Equal(LayoutJson.Text(m.Working), LayoutJson.Text(f.Store.Current.Overlay));

    private static string Grid(List<OverlayItem> cells, Func<OverlayItem, string> name)
    {
        var rows = OverlayGrid.RowCount(cells);
        var cols = OverlayGrid.ColumnCount(cells);
        return string.Join(" / ", Enumerable.Range(0, rows).Select(r => string.Join(" ", Enumerable.Range(0, cols).Select(c => OverlayGrid.At(cells, r, c) is { } x ? name(x) : "."))));
    }

    private static OverlayItem Cell(OverlayEditorPage page, EditorFixture f, string key) => page.Model.Find(f.F(key))!;

    private static void Settle(OverlayEditorPage page, FrameworkElement host)
    {
        Offscreen.Layout(host);
        page.Preview.RequestRender();
        page.Advanced?.Board.RequestRender();
        Assert.True(Sta.PumpUntil(() => !page.Preview.IsRendering && (page.Model.Working.IsEmpty || page.Preview.RenderCount > 0), 15000), "preview render");
        if (page.Advanced is { } a) Assert.True(Sta.PumpUntil(() => !a.Board.IsRendering && a.Board.RenderCount > 0, 15000), "board render");
        Offscreen.Layout(host);
        Sta.PumpUntil(() => FontSamples.Pending == 0, 15000);
        // the board picks its zoom from the column's width once it has a picture: let that render land too
        if (page.Advanced is { } b) Sta.PumpUntil(() => !b.Board.IsRendering, 15000);
        Sta.Pump(80);
        Offscreen.Layout(host);
    }

    private static (FrameworkElement Host, Action Done) Open(EditorFixture f, OverlayEditorPage page, double height)
    {
        var host = Offscreen.Host(page, PageWidth, height);
        page.OnNavigatedTo(null);
        f.Advance(1);
        page.TickNow();
        Settle(page, host);
        return (host, () =>
        {
            page.OnNavigatedFrom();
            host.Child = null;
        });
    }

    // ───────────── drop rules ─────────────

    /// <summary>
    /// Columns 10–60, 80–130, 150–200 px; rows 5–25, 35–55; cells (0,0) (0,1) (1,0) (1,2) — at 1 px per DIP.
    /// </summary>
    private static OverlayGridGeometry Geometry() => new(220, 60, [10, 80, 150], [60, 130, 200], [5, 35], [25, 55],
        [new("a", 0, 0), new("b", 0, 1), new("c", 1, 0), new("d", 1, 2)]);

    private static BoardTarget Hit(double x, double y, bool external = false)
    {
        var g = Geometry();
        return BoardHitTest.Find(OverlayLayout.MainWindowId, g, 1, new Point(x, y), (r, c) => g.Cells.Any(k => k.Row == r && k.Column == c), external);
    }

    [Fact]
    public void Drop_rules_swap_on_a_cell_insert_between_cells_and_add_rows_and_columns_at_the_edges()
    {
        Assert.Equal((GridDropKind.Cell, 0, 0, true), (Hit(35, 15).Drop.Kind, Hit(35, 15).Drop.Row, Hit(35, 15).Drop.Column, Hit(35, 15).Filled)); // the middle of a cell: swap
        Assert.Equal((GridDropKind.Cell, 0, 2, false), (Hit(175, 15).Drop.Kind, Hit(175, 15).Drop.Row, Hit(175, 15).Drop.Column, Hit(175, 15).Filled)); // a free cell: move there
        Assert.Equal((GridDropKind.InsertInRow, 0, 0), D(Hit(12, 15))); // the left edge of a cell
        Assert.Equal((GridDropKind.InsertInRow, 0, 1), D(Hit(58, 15))); // its right edge
        Assert.Equal((GridDropKind.InsertInRow, 0, 1), D(Hit(70, 15))); // the gap between two cells
        Assert.Equal((GridDropKind.InsertInRow, 0, 0), D(Hit(-8, 15))); // left of the grid
        Assert.Equal((GridDropKind.Cell, 0, 3), D(Hit(240, 15))); // right of the last column: a new column
        Assert.Equal((GridDropKind.NewRow, 0, 0), D(Hit(35, 7))); // the top edge of the first row
        Assert.Equal((GridDropKind.NewRow, 0, 1), D(Hit(100, -10))); // above the grid, in column 1
        Assert.Equal((GridDropKind.NewRow, 1, 0), D(Hit(35, 30))); // between two rows
        Assert.Equal((GridDropKind.NewRow, 2, 2), D(Hit(175, 70))); // below the last row, in column 2
        Assert.Equal((GridDropKind.NewRow, 2, 3), D(Hit(240, 70))); // below and right of everything

        // a sensor from the list never swaps: it makes room beside the cell under it
        Assert.Equal((GridDropKind.InsertInRow, 0, 0), D(Hit(30, 15, external: true)));
        Assert.Equal((GridDropKind.InsertInRow, 0, 1), D(Hit(40, 15, external: true)));
        Assert.Equal((GridDropKind.Cell, 0, 2), D(Hit(175, 15, external: true))); // a free cell takes it

        // the markers: a bar between cells, a line between rows, a box on a cell
        Assert.True(Hit(70, 15).Marker.Width < 4 && Hit(70, 15).Marker.Height > 18);
        Assert.True(Hit(35, 30).Marker.Height < 4 && Hit(35, 30).Marker.Width > 180);
        Assert.Equal(new Rect(10, 30 - 2.5, 50, 5), Hit(35, 30).Slot);
        Assert.True(Hit(35, 15).Marker.Contains(new Rect(10, 5, 50, 20)));

        // an empty window takes the cell as its first
        var empty = new OverlayGridGeometry(60, 30, [], [], [], [], []);
        Assert.Equal((GridDropKind.Cell, 0, 0), D(BoardHitTest.Find(OverlayLayout.SecondWindowId, empty, 1, new Point(5, 5), (_, _) => false, false)));

        // cells reach to the middle of the gaps around them
        var g = Geometry();
        Assert.Equal("a", BoardHitTest.CellAt(g, 1, new Point(68, 28))!.Value.ItemId);
        Assert.Equal("b", BoardHitTest.CellAt(g, 1, new Point(72, 10))!.Value.ItemId);
        Assert.Null(BoardHitTest.CellAt(g, 1, new Point(100, 45))); // a hole
        static (GridDropKind, int, int) D(BoardTarget t) => (t.Drop.Kind, t.Drop.Row, t.Drop.Column);
    }

    // ───────────── drags through the board ─────────────

    [Fact]
    public void Board_drags_reach_the_settings_make_the_grid_custom_and_cross_into_the_second_window() => Sta.Run(() =>
    {
        using var f = new EditorFixture();
        var page = f.Page();
        Toggle(f, page.Model, Six);
        page.Model.SetAdvanced(true);
        page.Model.Reload();
        var board = page.Advanced!.Board;
        string N(OverlayItem i) => f.Friendly.First(x => OverlayEditorModel.Matches(i, x)).Key.Split('.')[1];
        Assert.Equal("fps . . / temp clocks . / temp hotspot clock", Grid(page.Model.Working.Cells, N));

        // swap the hot spot with the FPS
        var hot = Cell(page, f, "gpu.hotspot").Id;
        Assert.True(board.DropForTest(hot, new GridDrop(OverlayLayout.MainWindowId, GridDropKind.Cell, 0, 0)));
        Assert.Equal(OverlayGrouping.Custom, page.Model.Working.Grouping);
        Assert.Equal("hotspot . . / temp clocks . / temp fps clock", Grid(page.Model.Working.Cells, N));
        Assert.Equal(hot, LayoutBoard.Selected); // the dropped cell is selected
        AssertSaved(f, page.Model);

        // insert the GPU clock between the CPU's two cells, then make a row of its own for the FPS
        Assert.True(board.DropForTest(Cell(page, f, "gpu.clock").Id, new GridDrop(OverlayLayout.MainWindowId, GridDropKind.InsertInRow, 1, 1)));
        Assert.Equal("hotspot . . / temp clock clocks / temp fps .", Grid(page.Model.Working.Cells, N));
        Assert.True(board.DropForTest(Cell(page, f, "fps.fps").Id, new GridDrop(OverlayLayout.MainWindowId, GridDropKind.NewRow, 0, 0)));
        Assert.Equal("fps . . / hotspot . . / temp clock clocks / temp . .", Grid(page.Model.Working.Cells, N));
        Assert.False(board.DropForTest(Cell(page, f, "fps.fps").Id, new GridDrop(OverlayLayout.MainWindowId, GridDropKind.NewRow, 0, 0))); // already there
        AssertSaved(f, page.Model);

        // the second window: on, then the GPU cells dragged onto its board
        Assert.True(page.Model.SetSecondWindow(true));
        page.Advanced.Sync();
        Assert.Equal(OverlayAnchor.TopRight, page.Model.Working.Second.Placement.Anchor);
        Assert.True(board.DropForTest(Cell(page, f, "gpu.hotspot").Id, new GridDrop(OverlayLayout.SecondWindowId, GridDropKind.NewRow, 0, 0)));
        Assert.True(board.DropForTest(Cell(page, f, "gpu.clock").Id, new GridDrop(OverlayLayout.SecondWindowId, GridDropKind.InsertInRow, 0, 1)));
        Assert.Equal("hotspot clock", Grid(page.Model.Working.Second.Cells, N));
        Assert.True(OverlayEditorModel.HasCustomizations(page.Model.Working));
        AssertSaved(f, page.Model);

        // off: every cell is back in the main window, nothing lost
        Assert.True(page.Model.SetSecondWindow(false));
        Assert.Equal(6, page.Model.Working.Cells.Count);
        Assert.Empty(page.Model.Working.Second.Cells);
        AssertSaved(f, page.Model);

        // Simple mode's "Restore default look": placed by component again, the plain look
        page.Model.ResetLook();
        Assert.False(OverlayEditorModel.HasCustomizations(page.Model.Working));
        Assert.Equal(OverlayGrouping.ByComponent, page.Model.Working.Grouping);
        Assert.Equal(3, OverlayGrid.RowCount(page.Model.Working.Cells));
        AssertSaved(f, page.Model);
        page.Dispose();
    });

    [Fact]
    public void Keys_move_and_remove_the_selected_cell_and_a_cell_dragged_off_the_board_goes_with_undo() => Sta.Run(() =>
    {
        using var f = new EditorFixture();
        var page = f.Page();
        Toggle(f, page.Model, "fps.fps", "cpu.temp", "gpu.temp", "gpu.hotspot");
        page.Model.SetAdvanced(true);
        page.Model.Reload();
        var board = page.Advanced!.Board;
        string N(OverlayItem i) => f.Friendly.First(x => OverlayEditorModel.Matches(i, x)).Key.Split('.')[1];
        Assert.Equal("fps . / temp . / temp hotspot", Grid(page.Model.Working.Cells, N));

        var fps = Cell(page, f, "fps.fps").Id;
        Assert.True(board.HandleKey(Key.Tab, false)); // Tab: the first cell
        Assert.Equal(fps, LayoutBoard.Selected);
        Assert.True(board.HandleKey(Key.Right, false));
        Assert.Equal(". fps / temp . / temp hotspot", Grid(page.Model.Working.Cells, N));
        Assert.True(board.HandleKey(Key.Down, false)); // into the free cell; the emptied top row goes
        Assert.Equal("temp fps / temp hotspot", Grid(page.Model.Working.Cells, N));
        Assert.True(board.HandleKey(Key.Down, false)); // swaps with the hot spot
        Assert.Equal("temp hotspot / temp fps", Grid(page.Model.Working.Cells, N));
        Assert.Equal(fps, LayoutBoard.Selected); // the moved cell stays selected
        AssertSaved(f, page.Model);
        Assert.False(board.HandleKey(Key.A, false)); // other keys are not the board's

        Assert.True(board.HandleKey(Key.Delete, false));
        Assert.Null(page.Model.Find(f.F("fps.fps")));
        Assert.NotNull(LayoutBoard.Selected); // the next cell is selected
        Assert.Equal(3, f.Store.Current.Overlay.AllItems().Count());
        // Undo in the toast puts it back where it was
        var (text, undo) = page.Toasts[^1];
        Assert.Contains("FPS", text);
        undo!();
        Assert.Equal("temp hotspot / temp fps", Grid(f.Store.Current.Overlay.Cells, N));
        var again = Cell(page, f, "fps.fps").Id;
        Assert.True(page.Model.TryEdit(l => l.RemoveCell(again)));
        board = page.Advanced!.Board; // the undo rebuilt the views

        // dragged off the boards: removed
        var hot = Cell(page, f, "gpu.hotspot").Id;
        Assert.True(board.DragOutForTest(hot));
        Assert.Null(page.Model.Find(f.F("gpu.hotspot")));
        AssertSaved(f, page.Model);

        // a sensor dropped from the list lands where it was dropped
        var id = board.DropSensorForTest(f.F("ram.used").SensorIds, new GridDrop(OverlayLayout.MainWindowId, GridDropKind.NewRow, 0, 0));
        Assert.NotNull(id);
        Assert.Equal(id, page.Model.Working.Cells[0].Id);
        Assert.Equal(id, LayoutBoard.Selected);
        Assert.True(board.HandleKey(Key.Escape, false));
        Assert.Null(LayoutBoard.Selected);
        page.Dispose();
    });

    [Fact]
    public void Set_on_screen_saves_both_windows() => Sta.Run(() =>
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
            page.Model.SetSecondWindow(true);
            page.Model.Reload();
            fake.Others[OverlayLayout.SecondWindowId] = new OverlayPlacement { Anchor = OverlayAnchor.MiddleRight, OffsetX = 5, OffsetY = -60, Margin = 24 };
            typeof(AdvancedView).GetMethod("BeginOnScreen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(page.Advanced, null);
            Assert.True(fake.IsEditingOnScreen);
            fake.EndOnScreenEdit(true);
            var o = f.Store.Current.Overlay;
            Assert.Equal(OverlayAnchor.BottomRight, o.Placement.Anchor);
            Assert.Equal(OverlayAnchor.MiddleRight, o.Second.Placement.Anchor);
            Assert.Equal(-60, o.Second.Placement.OffsetY);
            page.Dispose();
        }
        finally
        {
            AppServices.Overlay = before;
        }
    });

    [Fact]
    public void Simple_mode_presets_place_the_grid_and_switch_the_cell_style() => Sta.Run(() =>
    {
        using var f = new EditorFixture();
        var m = new OverlayEditorModel(f.Store);
        Assert.Equal(OverlayGrouping.ByComponent, m.Working.Grouping);
        Toggle(f, m, Six);
        var o = f.Store.Current.Overlay;
        Assert.Equal(3, OverlayGrid.RowCount(o.Cells)); // FPS | CPU, core clocks | GPU, hot spot, clock
        Assert.Equal([1, 2, 3], Enumerable.Range(0, 3).Select(r => o.Cells.Count(c => c.Row == r)));
        Assert.Equal(OverlayArrangement.Vertical, o.Style.Arrangement);

        m.SetGrouping(OverlayGrouping.OneLine);
        o = f.Store.Current.Overlay;
        Assert.Equal(1, OverlayGrid.RowCount(o.Cells));
        Assert.Equal(OverlayArrangement.Compact, o.Style.Arrangement);
        Assert.False(OverlayEditorModel.HasCustomStyle(o)); // the compact look belongs to "one line"
        m.Toggle(f.F("ram.used"));
        Assert.Equal(7, OverlayGrid.ColumnCount(f.Store.Current.Overlay.Cells));

        m.SetGrouping(OverlayGrouping.List);
        o = f.Store.Current.Overlay;
        Assert.Equal(7, OverlayGrid.RowCount(o.Cells));
        Assert.Equal(1, OverlayGrid.ColumnCount(o.Cells));
        Assert.Equal(OverlayArrangement.Vertical, o.Style.Arrangement);
        // the order the user clicked survives every switch
        Assert.Equal(m.Working.AllItems().Select(i => i.Id), o.AllItems().Select(i => i.Id));
        AssertSaved(f, m);
    });

    // ───────────── renders ─────────────

    [Theory]
    [InlineData("en", "aurora")]
    [InlineData("pl", "daylight")]
    [InlineData("pl", "aurora")]
    [InlineData("en", "daylight")]
    public void Advanced_mode_board_renders_with_two_windows_a_selected_cell_and_mid_drag(string lang, string theme) => Sta.Run(() =>
    {
        var before = AppServices.Overlay;
        AppServices.Overlay = new FakeOverlay();
        try
        {
            using var f = new EditorFixture(lang, theme);
            var page = f.Page();
            Toggle(f, page.Model, [.. Six, "ram.used", "cpu.load"]);
            page.Model.SetAdvanced(true);
            page.Model.Reload();
            LayoutBoard.Selected = Cell(page, f, "gpu.hotspot").Id;

            // one window: the board, the selected cell's editor below it
            var (host, done) = Open(f, page, 2200);
            var board = page.Advanced!.Board;
            var main = Assert.Single(board.Boards);
            Assert.NotNull(main.Grid);
            Assert.Equal(4, main.Grid!.Rows); // FPS | CPU | GPU | RAM
            Assert.True(main.Picture.Width > 150, "a readable size");
            Offscreen.Save(host, $"board_{theme}_{lang}");

            // mid-drag: the FPS over the gap between the CPU's two cells — grid lines, a bar, the ghost
            var g = main.Grid;
            var px = main.Picture.Width / g.Width;
            var between = new Point((g.ColumnRight[0] + g.ColumnLeft[1]) / 2 * px, (g.RowTop[1] + g.RowBottom[1]) / 2 * px);
            var target = board.DragForTest(Cell(page, f, "fps.fps").Id, OverlayLayout.MainWindowId, between);
            Assert.Equal(GridDropKind.InsertInRow, target!.Value.Drop.Kind);
            Assert.Equal((1, 1), (target.Value.Drop.Row, target.Value.Drop.Column));
            Offscreen.Layout(host);
            Offscreen.Save(host, $"board_drag_insert_{theme}_{lang}");
            board.EndDragForTest(drop: false);

            // mid-drag onto another cell: a swap
            var onCell = new Point((g.ColumnLeft[0] + g.ColumnRight[0]) / 2 * px, (g.RowTop[2] + g.RowBottom[2]) / 2 * px);
            target = board.DragForTest(Cell(page, f, "fps.fps").Id, OverlayLayout.MainWindowId, onCell);
            Assert.Equal(GridDropKind.Cell, target!.Value.Drop.Kind);
            Assert.True(target.Value.Filled);
            Offscreen.Layout(host);
            Offscreen.Save(host, $"board_drag_swap_{theme}_{lang}");
            board.EndDragForTest(drop: false);
            done();

            // two windows: the GPU row on the second board
            page.Model.SetSecondWindow(true);
            foreach (var key in new[] { "gpu.temp", "gpu.hotspot", "gpu.clock" })
                page.Model.MoveCell(Cell(page, f, key).Id, new GridDrop(OverlayLayout.SecondWindowId, GridDropKind.InsertInRow, 0, 9));
            (host, done) = Open(f, page, 2400);
            Assert.Equal(2, page.Advanced!.Board.Boards.Count());
            Assert.Equal(2, page.Preview.RenderedWindows.Count);
            Offscreen.Save(host, $"board_two_windows_{theme}_{lang}");

            // mid-drag across: a cell of the main window over the second board's right edge
            var second = page.Advanced.Board.Boards.Last();
            var sg = second.Grid!;
            var spx = second.Picture.Width / sg.Width;
            var right = new Point(sg.ColumnRight[^1] * spx + 16, (sg.RowTop[0] + sg.RowBottom[0]) / 2 * spx);
            target = page.Advanced.Board.DragForTest(Cell(page, f, "cpu.temp").Id, OverlayLayout.SecondWindowId, right);
            Assert.Equal(OverlayLayout.SecondWindowId, target!.Value.Drop.WindowId);
            Offscreen.Layout(host);
            Offscreen.Save(host, $"board_drag_across_{theme}_{lang}");
            page.Advanced.Board.EndDragForTest(drop: true);
            Assert.Equal(4, page.Model.Working.Second.Cells.Count);
            done();
            Assert.DoesNotContain(Loc.MissingKeys, k => k.StartsWith("ovl.", StringComparison.Ordinal));
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
    public void Simple_mode_with_presets_renders(string lang, string theme) => Sta.Run(() =>
    {
        using var f = new EditorFixture(lang, theme);
        var page = f.Page();
        Toggle(f, page.Model, Six);
        var (host, done) = Open(f, page, 1700);
        Offscreen.Save(host, $"grid_simple_{theme}_{lang}");
        done();
        page.Model.SetGrouping(OverlayGrouping.OneLine);
        (host, done) = Open(f, page, 900);
        Offscreen.Save(host, $"grid_simple_oneline_{theme}_{lang}");
        done();
        // a hand-made grid: Simple mode says so and offers the default back
        page.Model.SetGrouping(OverlayGrouping.ByComponent);
        page.Model.MoveCell(page.Model.Working.Cells[^1].Id, new GridDrop(OverlayLayout.MainWindowId, GridDropKind.Cell, 0, 1));
        Assert.True(OverlayEditorModel.HasCustomizations(page.Model.Working));
        (host, done) = Open(f, page, 900);
        Offscreen.Save(host, $"grid_simple_custom_{theme}_{lang}");
        done();
        Assert.NotNull(page.Simple);
        page.Dispose();
    });

    [Theory]
    [InlineData("aurora", "en")]
    [InlineData("daylight", "pl")]
    public void The_overlay_renders_the_presets_a_table_and_two_windows_in_the_app_themes(string theme, string lang) => Sta.Run(() =>
    {
        using var f = new EditorFixture(lang, theme);
        var m = new OverlayEditorModel(f.Store);
        Toggle(f, m, [.. Six, "ram.used", "cpu.load", "gpu.load"]);
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
            foreach (var (id, bmp) in OverlayHost.RenderWindowsToBitmaps(layout, overlayTheme, feed.Build(layout), 1080, Loc.I.Culture))
                BetterVitals.Overlay.Rendering.Offscreen.WritePng(bmp, Path.Combine(dir, $"overlay_{name}_{id}_{theme}_{lang}.png"));
        }
        m.SetGrouping(OverlayGrouping.List);
        Render("list", m.Working);
        m.SetGrouping(OverlayGrouping.ByComponent);
        Render("bycomponent", m.Working);
        m.SetGrouping(OverlayGrouping.OneLine);
        Render("oneline", m.Working);
        m.SetGrouping(OverlayGrouping.ByComponent);
        m.SetSecondWindow(true);
        foreach (var c in m.Working.Cells.Where(c => c.Row == 2).ToList())
            m.MoveCell(c.Id, new GridDrop(OverlayLayout.SecondWindowId, GridDropKind.Cell, 0, c.Column));
        Render("two", m.Working);
        Assert.Equal(2, m.Working.Windows().Count(w => w.Cells.Count > 0));
    });

    /// <summary>On-screen edit without the real overlay; "Done" reports the main window bottom-right plus <see cref="Others"/>.</summary>
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
            var all = new Dictionary<string, OverlayPlacement>(Others) { [OverlayLayout.MainWindowId] = new() { Anchor = OverlayAnchor.BottomRight, Margin = 24 } };
            if (commit) _commit?.Invoke(all);
            else _cancel?.Invoke();
        }

        public event Action? MonitorsChanged { add { } remove { } }
    }
}
