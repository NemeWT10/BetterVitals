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
/// How the items of a row are drawn (every row of every box). The names are the ones of settings schema 1, where the
/// overlay was a flat list: Vertical = one item per line, Horizontal / Compact = all items in one line.
/// </summary>
public enum OverlayArrangement : byte
{
    /// <summary>
    /// Row label in a column on the left, then the row's values side by side: "GPU  66 °C · GPU HS 76 °C · 2856 MHz".
    /// A row with one item is the classic list line "label | value unit".
    /// </summary>
    Vertical,

    /// <summary>Every item with its label above the value, side by side, dividers between.</summary>
    Horizontal,

    /// <summary>Compact cells with dividers: "CPU 45° | GPU 60° | FPS 144".</summary>
    Compact,
}

/// <summary>How Simple mode puts the main box's items into rows. Moving items by hand (Advanced mode) makes it Custom.</summary>
public enum OverlayGrouping : byte
{
    /// <summary>One item per row.</summary>
    List,

    /// <summary>One row per component (CPU, GPU, RAM, FPS…), in the order the components first appear.</summary>
    ByComponent,

    /// <summary>Every item in one row.</summary>
    OneLine,

    /// <summary>Rows arranged by hand; nothing regroups them.</summary>
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

/// <summary>One entry of the overlay: a single sensor, or several of one kind (cores) drawn together.</summary>
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
}

/// <summary>A line of the overlay: one or more items side by side ("GPU  66 °C · GPU HS 76 °C · 2856 MHz").</summary>
public sealed class OverlayRow
{
    /// <summary>Editor identity.</summary>
    public string Id { get; set; } = OverlayLayout.NewId();

    /// <summary>
    /// Row label; null = automatic: the item's own label for a row of one, the component ("CPU", "GPU", "FPS"…) for a row
    /// of one component, else the first item's label. Items whose label equals the row label do not repeat it.
    /// </summary>
    public string? Label { get; set; }

    public List<OverlayItem> Items { get; set; } = [];
}

/// <summary>A detached overlay box: its own window with its own place on screen and its own rows.</summary>
public sealed class OverlayBox
{
    public string Id { get; set; } = OverlayLayout.NewId();

    /// <summary>Name in the editor ("GPU"); null = "Box 2", "Box 3"… Never drawn on the overlay.</summary>
    public string? Name { get; set; }

    public OverlayPlacement Placement { get; set; } = new();

    public List<OverlayRow> Rows { get; set; } = [];
}

/// <summary>One box of a layout: the main one (the layout's own placement and rows) or a detached one.</summary>
public readonly record struct OverlayBoxRef(string Id, OverlayPlacement Placement, List<OverlayRow> Rows)
{
    public bool IsMain => Id == OverlayLayout.MainBoxId;
}

/// <summary>
/// Everything about the on-screen overlay (part of <see cref="Settings.AppSettings"/>). Items sit in rows; the rows of
/// the main box are <see cref="Rows"/> (placed by <see cref="Placement"/>), detached boxes are <see cref="Boxes"/>, each
/// with its own placement. Schema 1 had one flat <see cref="Items"/> list: <see cref="Normalize"/> turns it into rows
/// that draw exactly as before.
/// </summary>
public sealed class OverlayLayout
{
    /// <summary>Id of the main box (the layout's own <see cref="Placement"/> and <see cref="Rows"/>).</summary>
    public const string MainBoxId = "main";

    /// <summary>Boxes including the main one: every box is a window, so a hand-edited file cannot ask for hundreds.</summary>
    public const int MaxBoxes = 8;

    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Shown right now (toggled from the tray).</summary>
    public bool Enabled { get; set; }

    public OverlayVisibility Visibility { get; set; } = OverlayVisibility.Always;

    /// <summary>Invisible to screen capture, streams and recordings (WDA_EXCLUDEFROMCAPTURE). On by default (user decision).</summary>
    public bool HideFromCapture { get; set; } = true;

    /// <summary>The editor is in Advanced mode (Simple keeps everything at defaults except items and corner).</summary>
    public bool Advanced { get; set; }

    /// <summary>The default items (FPS, CPU and GPU temperature) were filled in once; an emptied list stays empty.</summary>
    public bool DefaultsApplied { get; set; }

    /// <summary>Refresh interval of the overlay's values, 250–2000 ms.</summary>
    public int RefreshMs { get; set; } = 1000;

    /// <summary>Where the main box sits.</summary>
    public OverlayPlacement Placement { get; set; } = new();

    public OverlayStyle Style { get; set; } = new();

    /// <summary>How Simple mode groups the main box (new layouts: by component; schema 1 files: from their arrangement).</summary>
    public OverlayGrouping Grouping { get; set; } = OverlayGrouping.ByComponent;

    /// <summary>The main box's rows, top to bottom.</summary>
    public List<OverlayRow> Rows { get; set; } = [];

    /// <summary>Detached boxes (each its own window and placement), at most <see cref="MaxBoxes"/> − 1.</summary>
    public List<OverlayBox> Boxes { get; set; } = [];

    /// <summary>
    /// Flat item list of settings schema 1 / profile v1 (also a short way to build a layout in code). <see cref="Normalize"/>
    /// moves these items into the main box — one row each for <see cref="OverlayArrangement.Vertical"/>, one row for the
    /// others, which draws exactly like the flat list did — and clears it. Never written.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OverlayItem>? Items { get; set; }

    /// <summary>The main box, then the detached ones.</summary>
    public IEnumerable<OverlayBoxRef> AllBoxes()
    {
        yield return new OverlayBoxRef(MainBoxId, Placement, Rows);
        foreach (var b in Boxes) yield return new OverlayBoxRef(b.Id, b.Placement, b.Rows);
    }

    /// <summary>Every item of every box, in order (main box first, row by row).</summary>
    public IEnumerable<OverlayItem> AllItems()
    {
        foreach (var row in Rows)
            foreach (var item in row.Items) yield return item;
        foreach (var box in Boxes)
            foreach (var row in box.Rows)
                foreach (var item in row.Items) yield return item;
        if (Items is { } legacy)
            foreach (var item in legacy) yield return item;
    }

    /// <summary>Nothing to show in any box.</summary>
    [JsonIgnore]
    public bool IsEmpty => !AllItems().Any();

    /// <summary>Repairs nulls, clamps ranges and upgrades the flat schema-1 list after loading a file (hand-edited or from an older version).</summary>
    public void Normalize()
    {
        Placement ??= new OverlayPlacement();
        Style ??= new OverlayStyle();
        Rows ??= [];
        Boxes ??= [];
        // a number in the file that is no known option (hand-edited, or from a newer version) must not reach the renderer
        if (!Enum.IsDefined(Visibility)) Visibility = OverlayVisibility.Always;
        if (!Enum.IsDefined(Style.Arrangement)) Style.Arrangement = OverlayArrangement.Vertical;
        if (!Enum.IsDefined(Grouping)) Grouping = OverlayGrouping.Custom;
        Rows.RemoveAll(r => r is null);
        Boxes.RemoveAll(b => b is null);

        if (Items is { } legacy)
        {
            var fresh = Rows.Count == 0 && Boxes.Count == 0;
            var items = legacy.Where(i => i is not null).ToList();
            if (Style.Arrangement == OverlayArrangement.Vertical)
                foreach (var item in items) Rows.Add(new OverlayRow { Items = [item] });
            else if (items.Count > 0)
                Rows.Add(new OverlayRow { Items = items });
            if (fresh) Grouping = Style.Arrangement == OverlayArrangement.Vertical ? OverlayGrouping.List : OverlayGrouping.OneLine;
            Items = null;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal) { MainBoxId };
        string Unique(string? id)
        {
            if (!string.IsNullOrEmpty(id) && id.Length <= 64 && ids.Add(id)) return id;
            var fresh = NewId();
            ids.Add(fresh);
            return fresh;
        }

        foreach (var box in Boxes)
        {
            box.Id = Unique(box.Id);
            box.Name = Text(box.Name);
            box.Placement ??= new OverlayPlacement();
            box.Rows ??= [];
            box.Rows.RemoveAll(r => r is null);
        }
        // more windows than anyone needs: their rows go to the main box (nothing is lost)
        while (Boxes.Count > MaxBoxes - 1)
        {
            Rows.AddRange(Boxes[^1].Rows);
            Boxes.RemoveAt(Boxes.Count - 1);
        }
        foreach (var box in AllBoxes())
        {
            FixPlacement(box.Placement);
            foreach (var row in box.Rows)
            {
                row.Id = Unique(row.Id);
                row.Label = Text(row.Label);
                row.Items ??= [];
                row.Items.RemoveAll(i => i is null);
                foreach (var item in row.Items)
                {
                    item.Id = Unique(item.Id);
                    FixItem(item);
                }
            }
            // a row without items draws nothing, a detached box without rows is no window
            box.Rows.RemoveAll(r => r.Items.Count == 0);
        }
        Boxes.RemoveAll(b => b.Rows.Count == 0);

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
