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

public enum OverlayArrangement : byte
{
    /// <summary>One item per row: label | value unit.</summary>
    Vertical,

    /// <summary>Items side by side.</summary>
    Horizontal,

    /// <summary>One line, separated: "CPU 45° | GPU 60° | 144 FPS".</summary>
    Compact,
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

    /// <summary>User label; null = default short label ("CPU", "GPU", "Hot Spot", "FPS"…).</summary>
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

/// <summary>Everything about the on-screen overlay (part of <see cref="Settings.AppSettings"/>).</summary>
public sealed class OverlayLayout
{
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

    public OverlayPlacement Placement { get; set; } = new();

    public OverlayStyle Style { get; set; } = new();

    public List<OverlayItem> Items { get; set; } = [];

    /// <summary>Repairs nulls and clamps ranges after loading a file (hand-edited or from an older version).</summary>
    public void Normalize()
    {
        Placement ??= new OverlayPlacement();
        Style ??= new OverlayStyle();
        Items ??= [];
        Items.RemoveAll(i => i is null);
        // a number in the file that is no known option (hand-edited, or from a newer version) must not reach the renderer
        if (!Enum.IsDefined(Visibility)) Visibility = OverlayVisibility.Always;
        if (!Enum.IsDefined(Placement.Anchor)) Placement.Anchor = OverlayAnchor.TopLeft;
        if (!Enum.IsDefined(Placement.MonitorMode)) Placement.MonitorMode = OverlayMonitorMode.Primary;
        if (!Enum.IsDefined(Style.Arrangement)) Style.Arrangement = OverlayArrangement.Vertical;
        foreach (var item in Items)
        {
            item.SensorIds ??= [];
            item.SensorIds.RemoveAll(string.IsNullOrEmpty);
            if (string.IsNullOrEmpty(item.Id)) item.Id = Guid.NewGuid().ToString("N");
            if (!Enum.IsDefined(item.MultiDisplay)) item.MultiDisplay = MultiValueDisplay.Bars;
            if (!Enum.IsDefined(item.Aggregation)) item.Aggregation = ValueAggregation.Current;
            if (item.Label is { Length: > 64 } label) item.Label = label[..64];
            item.AggregationSeconds = Math.Clamp(item.AggregationSeconds, 1, 120);
            if (item.Decimals is { } d) item.Decimals = Math.Clamp(d, 0, 3);
            if (item.ValueSize is { } v) item.ValueSize = Fit(v, 6, 96, 16);
        }
        RefreshMs = Math.Clamp(RefreshMs, 250, 2000);
        // numbers from a hand-edited file (or a NaN from some calculation) must not blow the window up or off the screen
        Placement.OffsetX = Fit(Placement.OffsetX, -10_000, 10_000, 0);
        Placement.OffsetY = Fit(Placement.OffsetY, -10_000, 10_000, 0);
        Placement.Margin = Fit(Placement.Margin, 0, 1_000, 24);
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

    private static double Fit(double value, double min, double max, double fallback) => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
