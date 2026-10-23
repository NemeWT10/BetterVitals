using System.Text.Json.Serialization;

namespace BetterVitals.Core.Overlay;

/// <summary>Nine anchor points of a monitor's work area.</summary>
public enum OverlayAnchor : byte
{
    TopLeft,
    TopCenter,
    TopRight,
    MiddleLeft,
    Center,
    MiddleRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
}

public enum OverlayVisibility : byte
{
    Always,

    /// <summary>Only while a game / full-screen or borderless app is in the foreground.</summary>
    GamesOnly,
}

/// <summary>
/// How every cell of the grid is drawn ("row style" in the editor). The names are the ones of settings schema 1, where
/// the overlay was a flat list: Vertical = one item per line, Horizontal / Compact = all items in one line.
/// </summary>
public enum OverlayArrangement : byte
{
    /// <summary>
    /// Label beside the value. The first column has a label column on the left that names the row ("GPU  66 °C ·
    /// GPU HS 76 °C · 2856 MHz"); a cell further right shows its own small label only where the row label does not say
    /// it. A grid of one column is the classic list "label | value unit".
    /// </summary>
    Vertical,

    /// <summary>Every cell with its label above the value, dividers between the cells of a row.</summary>
    Horizontal,

    /// <summary>Compact cells with dividers: "CPU 45° | GPU 60° | FPS 144".</summary>
    Compact,
}

/// <summary>How Simple mode places the main window's cells in its grid. Arranging cells by hand (Advanced mode) makes it Custom.</summary>
public enum OverlayGrouping : byte
{
    /// <summary>One cell per row: a single column.</summary>
    List,

    /// <summary>One row per component (CPU, GPU, RAM, FPS…), in the order the components first appear.</summary>
    ByComponent,

    /// <summary>Every cell in one row.</summary>
    OneLine,

    /// <summary>A grid arranged by hand; nothing regroups it.</summary>
    Custom,
}

/// <summary>How an item with several sensors (e.g. 16 core clocks) is drawn.</summary>
public enum MultiValueDisplay : byte
{
    /// <summary>Tiny bars, color by value, plus the max value as text.</summary>
    Bars,

    /// <summary>A compact grid of numbers.</summary>
    Grid,

    /// <summary>One row per sensor.</summary>
    Rows,
}

public enum ValueAggregation : byte
{
    /// <summary>The latest reading.</summary>
    Current,

    /// <summary>Highest over the last <see cref="OverlayItem.AggregationSeconds"/>.</summary>
    Max,

    /// <summary>Average over the last <see cref="OverlayItem.AggregationSeconds"/>.</summary>
    Average,
}

public enum OverlayMonitorMode : byte
{
    Primary,

    /// <summary>The monitor named by <see cref="OverlayPlacement.MonitorKey"/>; primary while it is missing.</summary>
    Specific,

    /// <summary>The monitor the foreground game is on.</summary>
    FollowGame,
}

/// <summary>
/// Where the overlay sits. Offsets are in "reference pixels" of a 1080 px tall monitor and scale like the overlay,
/// so the overlay lands in the same place on 1080p, 1440p, 4K, 5K. Positive offsets point inward from the anchor's
/// edges; for center anchors positive = right / down. A missing monitor falls back to the primary one (same anchor)
/// and the overlay returns when the monitor comes back.
/// </summary>
public sealed class OverlayPlacement
{
    public OverlayMonitorMode MonitorMode { get; set; } = OverlayMonitorMode.Primary;

    /// <summary>Stable monitor key (device interface path / EDID-based id) for <see cref="OverlayMonitorMode.Specific"/>.</summary>
    public string? MonitorKey { get; set; }

    public OverlayAnchor Anchor { get; set; } = OverlayAnchor.TopLeft;

    public double OffsetX { get; set; }

    public double OffsetY { get; set; }

    /// <summary>Gap between the anchor edge and the overlay, reference px.</summary>
    public double Margin { get; set; } = 24;
}

/// <summary>
/// Global look. Sizes are reference px for a 1080 px tall monitor (see <see cref="AutoScale"/>). Null colors follow
/// the active theme (see <see cref="OverlayTheme"/>). Colors are "#RRGGBB" or "#AARRGGBB".
/// </summary>
public sealed class OverlayStyle
{
    /// <summary>Bundled: "Inter" (default), "Geist", "Space Grotesk", "JetBrains Mono", "Rajdhani", "Orbitron"; system: "Segoe UI Variable", "Bahnschrift".</summary>
    public string FontFamily { get; set; } = "Inter";

    /// <summary>Separate font for values; null = <see cref="FontFamily"/>.</summary>
    public string? ValueFontFamily { get; set; }

    /// <summary>Scale = monitor height / 1080, independent of Windows display scaling (gamers often run 100 % on 4K).</summary>
    public bool AutoScale { get; set; } = true;

    /// <summary>User multiplier on top of the automatic scale, 50–200 %.</summary>
    public double ScalePercent { get; set; } = 100;

    public OverlayArrangement Arrangement { get; set; } = OverlayArrangement.Vertical;

    public double LabelSize { get; set; } = 12;

    public double ValueSize { get; set; } = 16;

    public double UnitSize { get; set; } = 11;

    public int LabelWeight { get; set; } = 600;

    public int ValueWeight { get; set; } = 600;

    public string? BackgroundColor { get; set; }

    /// <summary>0–1.</summary>
    public double BackgroundOpacity { get; set; } = 0.70;

    public double CornerRadius { get; set; } = 10;

    public double Padding { get; set; } = 11;

    /// <summary>Space between rows / items, reference px.</summary>
    public double Spacing { get; set; } = 4;

    public bool Border { get; set; } = true;

    /// <summary>Soft shadow / outline behind text (readable without a background).</summary>
    public bool TextShadow { get; set; }

    public bool ShowUnits { get; set; } = true;

    public string? LabelColor { get; set; }

    public string? ValueColor { get; set; }

    public string? UnitColor { get; set; }
}

/// <summary>Value ≥ <see cref="Value"/> → <see cref="Color"/> (thresholds sorted ascending, last match wins).</summary>
public sealed class OverlayThreshold
{
    public float Value { get; set; }

    public string Color { get; set; } = "#FBBF24";
}

/// <summary>
/// One cell of the overlay: a single sensor, or several of one kind (cores) drawn together, at <see cref="Row"/> /
/// <see cref="Column"/> of its window's grid.
/// </summary>
public sealed class OverlayItem
{
    /// <summary>Editor identity (stable while editing, reordering).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Sensor ids; one for a normal item, several for a multi-value item.</summary>
    public List<string> SensorIds { get; set; } = [];

    /// <summary>User label; null = default short label ("CPU", "GPU", "GPU HS", "FPS"…).</summary>
    public string? Label { get; set; }

    public string? LabelColor { get; set; }

    /// <summary>Static value color; thresholds (or the defaults per sensor) recolor it.</summary>
    public string? ValueColor { get; set; }

    public int? Decimals { get; set; }

    public bool? ShowUnit { get; set; }

    /// <summary>Tiny sparkline next to the value.</summary>
    public bool ShowGraph { get; set; }

    public MultiValueDisplay MultiDisplay { get; set; } = MultiValueDisplay.Bars;

    public ValueAggregation Aggregation { get; set; } = ValueAggregation.Current;

    public int AggregationSeconds { get; set; } = 5;

    /// <summary>Null = default thresholds for the sensor (e.g. CPU 80 / 90 °C); empty list = never recolor.</summary>
    public List<OverlayThreshold>? Thresholds { get; set; }

    public string? FontFamily { get; set; }

    public double? ValueSize { get; set; }

    /// <summary>Grid row inside its window, 0 = top (cells of <see cref="OverlayLayout.Cells"/> / <see cref="OverlaySecondWindow.Cells"/>).</summary>
    public int Row { get; set; }

    /// <summary>Grid column inside its window, 0 = left.</summary>
    public int Column { get; set; }
}

/// <summary>A row of settings schema 2 (read only: <see cref="OverlayLayout.Normalize"/> turns it into a row of cells).</summary>
public sealed class OverlayRow
{
    public string? Id { get; set; }

    /// <summary>The row's own name; it becomes the label of its first item when that has none.</summary>
    public string? Label { get; set; }

    public List<OverlayItem> Items { get; set; } = [];
}

/// <summary>A detached box of settings schema 2 (read only: the first one becomes the second window, the others join it).</summary>
public sealed class OverlayBox
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public OverlayPlacement? Placement { get; set; }

    public List<OverlayRow> Rows { get; set; } = [];
}

/// <summary>The optional second overlay window: its own place on the screen and its own grid of cells.</summary>
public sealed class OverlaySecondWindow
{
    /// <summary>
    /// The window is on (the editor shows its board). Turned off, its cells go back to the main window
    /// (<see cref="OverlayLayout.Normalize"/>); on but without cells, it draws nothing.
    /// </summary>
    public bool Enabled { get; set; }

    public OverlayPlacement Placement { get; set; } = new() { Anchor = OverlayAnchor.TopRight };

    /// <summary>Cells in reading order (row by row, left to right).</summary>
    public List<OverlayItem> Cells { get; set; } = [];
}

/// <summary>One overlay window of a layout: the main one (the layout's own placement and cells) or the second one.</summary>
public readonly record struct OverlayWindowRef(string Id, OverlayPlacement Placement, List<OverlayItem> Cells)
{
    public bool IsMain => Id == OverlayLayout.MainWindowId;
}

/// <summary>
/// Everything about the on-screen overlay (part of <see cref="Settings.AppSettings"/>). Every item is a cell of a grid
/// (<see cref="OverlayItem.Row"/>, <see cref="OverlayItem.Column"/>) inside one of at most two windows: the main one
/// (<see cref="Cells"/>, placed by <see cref="Placement"/>) and the optional <see cref="Second"/> one with its own
/// placement. Columns line up like a table. Older shapes — the flat <see cref="Items"/> list of schema 1 and the
/// <see cref="Rows"/> / <see cref="Boxes"/> of schema 2 — are read and turned into cells by <see cref="Normalize"/>, so
/// that they draw as before.
/// </summary>
public sealed class OverlayLayout
{
    /// <summary>Id of the main window (the layout's own <see cref="Placement"/> and <see cref="Cells"/>).</summary>
    public const string MainWindowId = "main";

    /// <summary>Id of the optional second window (<see cref="Second"/>).</summary>
    public const string SecondWindowId = "second";

    /// <summary>The main window and the second one.</summary>
    public const int MaxWindows = 2;

    /// <summary>Row / column indices beyond this are clamped (a hand-edited file).</summary>
    public const int MaxIndex = 4096;

    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Shown right now (toggled from the tray).</summary>
    public bool Enabled { get; set; }

    public OverlayVisibility Visibility { get; set; } = OverlayVisibility.Always;

    /// <summary>Invisible to screen capture, streams and recordings (WDA_EXCLUDEFROMCAPTURE). On by default (user decision).</summary>
    public bool HideFromCapture { get; set; } = true;

    /// <summary>The editor is in Advanced mode (Simple keeps everything at defaults except items and corner).</summary>
    public bool Advanced { get; set; }

    /// <summary>The default items (FPS, CPU and GPU temperature) were filled in once; an emptied overlay stays empty.</summary>
    public bool DefaultsApplied { get; set; }

    /// <summary>Refresh interval of the overlay's values, 250–2000 ms.</summary>
    public int RefreshMs { get; set; } = 1000;

    /// <summary>Where the main window sits.</summary>
    public OverlayPlacement Placement { get; set; } = new();

    public OverlayStyle Style { get; set; } = new();

    /// <summary>How Simple mode places the main window's cells (new layouts: by component; schema 1 files: from their arrangement).</summary>
    public OverlayGrouping Grouping { get; set; } = OverlayGrouping.ByComponent;

    /// <summary>The main window's cells in reading order (row by row, left to right).</summary>
    public List<OverlayItem> Cells { get; set; } = [];

    /// <summary>The optional second window.</summary>
    public OverlaySecondWindow Second { get; set; } = new();

    /// <summary>Flat item list of settings schema 1 / profile v1 (also a short way to build a layout in code); never written.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OverlayItem>? Items { get; set; }

    /// <summary>The main box's rows of settings schema 2 / profile v2; never written.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OverlayRow>? Rows { get; set; }

    /// <summary>Detached boxes of settings schema 2 / profile v2; never written.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OverlayBox>? Boxes { get; set; }

    /// <summary>The main window, then the second one while it is on.</summary>
    public IEnumerable<OverlayWindowRef> Windows()
    {
        yield return new OverlayWindowRef(MainWindowId, Placement, Cells);
        if (Second is { Enabled: true } second) yield return new OverlayWindowRef(SecondWindowId, second.Placement, second.Cells);
    }

    /// <summary>Every item of every window in reading order (main window first), plus any not normalized yet.</summary>
    public IEnumerable<OverlayItem> AllItems()
    {
        foreach (var item in Cells ?? []) yield return item;
        foreach (var item in Second?.Cells ?? []) yield return item;
        foreach (var item in Items ?? []) yield return item;
        foreach (var row in Rows ?? [])
            foreach (var item in row?.Items ?? []) yield return item;
        foreach (var box in Boxes ?? [])
            foreach (var row in box?.Rows ?? [])
                foreach (var item in row?.Items ?? []) yield return item;
    }

    /// <summary>Nothing to show in any window.</summary>
    [JsonIgnore]
    public bool IsEmpty => !AllItems().Any(i => i is not null);

    /// <summary>
    /// Repairs a layout after loading a file (hand-edited or from an older version): nulls, unknown enum values, empty /
    /// duplicate / over-long editor ids, cells without sensors, negative or absurd grid indices, two cells on one
    /// position (the later one moves right), empty rows and columns (they collapse), a second window that is off but
    /// still holds cells (they go back to the main window), absurd sizes and placements. Upgrades schema 1 (flat list)
    /// and schema 2 (rows and boxes) to cells that draw as before.
    /// </summary>
    public void Normalize()
    {
        Placement ??= new OverlayPlacement();
        Style ??= new OverlayStyle();
        Cells ??= [];
        Second ??= new OverlaySecondWindow();
        Second.Placement ??= new OverlayPlacement { Anchor = OverlayAnchor.TopRight };
        Second.Cells ??= [];
        // a number in the file that is no known option (hand-edited, or from a newer version) must not reach the renderer
        if (!Enum.IsDefined(Visibility)) Visibility = OverlayVisibility.Always;
        if (!Enum.IsDefined(Style.Arrangement)) Style.Arrangement = OverlayArrangement.Vertical;
        if (!Enum.IsDefined(Grouping)) Grouping = OverlayGrouping.Custom;
        Cells.RemoveAll(c => c is null);
        Second.Cells.RemoveAll(c => c is null);

        var fresh = Cells.Count == 0 && Second.Cells.Count == 0 && (Rows ?? []).Count == 0 && (Boxes ?? []).Count == 0;
        // schema 2: the main box's rows, then the detached boxes (the first one is the second window, the others join it)
        if (Rows is { } rows)
        {
            AppendRows(Cells, rows);
            Rows = null;
        }
        if (Boxes is { } boxes)
        {
            foreach (var box in boxes)
            {
                if (box?.Rows is not { } boxRows || !boxRows.Exists(r => r?.Items is { Count: > 0 })) continue;
                if (!Second.Enabled && Second.Cells.Count == 0)
                {
                    Second.Enabled = true;
                    Second.Placement = box.Placement ?? new OverlayPlacement { Anchor = OverlayAnchor.TopRight };
                }
                AppendRows(Second.Cells, boxRows);
            }
            Boxes = null;
        }
        // schema 1: one cell per row for the vertical arrangement, one row of all cells otherwise (draws as the flat list did)
        if (Items is { } legacy)
        {
            var items = legacy.Where(i => i is not null).ToList();
            var vertical = Style.Arrangement == OverlayArrangement.Vertical;
            var row = NextRow(Cells);
            for (var i = 0; i < items.Count; i++)
            {
                items[i].Row = vertical ? row + i : row;
                items[i].Column = vertical ? 0 : i;
                Cells.Add(items[i]);
            }
            if (fresh) Grouping = vertical ? OverlayGrouping.List : OverlayGrouping.OneLine;
            Items = null;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        FixCells(Cells, ids);
        FixCells(Second.Cells, ids);
        // a second window that is off keeps nothing: its cells go back to the main window, below the main window's rows
        if (!Second.Enabled && Second.Cells.Count > 0)
        {
            OverlayGrid.Append(Cells, Second.Cells);
            Second.Cells.Clear();
        }
        OverlayGrid.Tidy(Cells);
        OverlayGrid.Tidy(Second.Cells);
        FixPlacement(Placement);
        FixPlacement(Second.Placement);

        RefreshMs = Math.Clamp(RefreshMs, 250, 2000);
        Style.ScalePercent = Fit(Style.ScalePercent, 50, 200, 100);
        Style.BackgroundOpacity = Fit(Style.BackgroundOpacity, 0, 1, 0.7);
        Style.LabelSize = Fit(Style.LabelSize, 6, 72, 12);
        Style.ValueSize = Fit(Style.ValueSize, 6, 96, 16);
        Style.UnitSize = Fit(Style.UnitSize, 6, 72, 11);
        Style.Padding = Fit(Style.Padding, 0, 100, 11);
        Style.Spacing = Fit(Style.Spacing, 0, 100, 4);
        Style.CornerRadius = Fit(Style.CornerRadius, 0, 100, 10);
        Style.FontFamily = string.IsNullOrWhiteSpace(Style.FontFamily) ? "Inter" : Style.FontFamily;
    }

    /// <summary>Schema 2 rows below the cells already there: row r, item k → cell (r, k). A row's own name labels its first item.</summary>
    private static void AppendRows(List<OverlayItem> cells, List<OverlayRow> rows)
    {
        var r = NextRow(cells);
        foreach (var row in rows)
        {
            if (row?.Items is not { } items) continue;
            items.RemoveAll(i => i is null);
            if (items.Count == 0) continue;
            if (Text(row.Label) is { } name && string.IsNullOrWhiteSpace(items[0].Label)) items[0].Label = name;
            for (var k = 0; k < items.Count; k++)
            {
                items[k].Row = r;
                items[k].Column = k;
                cells.Add(items[k]);
            }
            r++;
        }
    }

    private static int NextRow(List<OverlayItem> cells) => cells.Count == 0 ? 0 : Math.Clamp(cells.Max(c => c.Row), -1, MaxIndex - 1) + 1;

    private static void FixCells(List<OverlayItem> cells, HashSet<string> ids)
    {
        foreach (var item in cells)
        {
            item.Id = !string.IsNullOrEmpty(item.Id) && item.Id.Length <= 64 && ids.Add(item.Id) ? item.Id : Fresh(ids);
            FixItem(item);
        }
        // a cell without a sensor draws nothing useful
        cells.RemoveAll(c => c.SensorIds.Count == 0);
    }

    private static string Fresh(HashSet<string> ids)
    {
        var id = NewId();
        ids.Add(id);
        return id;
    }

    private static void FixPlacement(OverlayPlacement p)
    {
        if (!Enum.IsDefined(p.Anchor)) p.Anchor = OverlayAnchor.TopLeft;
        if (!Enum.IsDefined(p.MonitorMode)) p.MonitorMode = OverlayMonitorMode.Primary;
        // numbers from a hand-edited file (or a NaN from some calculation) must not blow the window up or off the screen
        p.OffsetX = Fit(p.OffsetX, -10_000, 10_000, 0);
        p.OffsetY = Fit(p.OffsetY, -10_000, 10_000, 0);
        p.Margin = Fit(p.Margin, 0, 1_000, 24);
    }

    private static void FixItem(OverlayItem item)
    {
        item.SensorIds ??= [];
        item.SensorIds.RemoveAll(string.IsNullOrEmpty);
        if (!Enum.IsDefined(item.MultiDisplay)) item.MultiDisplay = MultiValueDisplay.Bars;
        if (!Enum.IsDefined(item.Aggregation)) item.Aggregation = ValueAggregation.Current;
        if (item.Label is { Length: > 64 } label) item.Label = label[..64];
        item.AggregationSeconds = Math.Clamp(item.AggregationSeconds, 1, 120);
        if (item.Decimals is { } d) item.Decimals = Math.Clamp(d, 0, 3);
        if (item.ValueSize is { } v) item.ValueSize = Fit(v, 6, 96, 16);
        item.Thresholds?.RemoveAll(t => t is null);
    }

    /// <summary>A user name / label: trimmed, at most 64 characters, blank = null (automatic).</summary>
    private static string? Text(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length > 64 ? s[..64] : s;
    }

    private static double Fit(double value, double min, double max, double fallback) => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

