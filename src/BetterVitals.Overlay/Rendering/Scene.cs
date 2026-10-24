using System.Globalization;
using BetterVitals.Core.Formatting;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Overlay.Fonts;
using BetterVitals.Overlay.Interop;

namespace BetterVitals.Overlay.Rendering;

/// <summary>Where the scene gets descriptors and value slots from (the live hub or fixed preview values).</summary>
internal interface ISensorLookup
{
    SensorDescriptor? Find(string sensorId);

    /// <summary>Index into the values span passed to <see cref="Scene.Update"/>.</summary>
    int Slot(SensorDescriptor sensor);
}

/// <summary>Everything a <see cref="Scene"/> is built from: one window of a (tidy) layout.</summary>
internal sealed record SceneInput(
    OverlayLayout Layout,
    OverlayTheme Theme,
    ISensorLookup Lookup,
    CultureInfo Culture,
    TemperatureUnit TemperatureUnit,
    Func<OverlayItem, string?>? Labels,
    double Scale,
    string WindowId = OverlayLayout.MainWindowId);

/// <summary>A vertical divider between two cells (Horizontal / Compact), px.</summary>
internal readonly record struct Divider(float X, float Top, float Bottom);

/// <summary>
/// One window of the overlay at one scale: its grid of cells resolved into rows of items, fonts, measured text and the
/// arranged geometry — columns line up like a table (one width per column; label, value, unit and sparkline parts
/// aligned down the column) — plus the per-refresh value pipeline (aggregation, formatting into reusable buffers,
/// thresholds, sparklines). A grid of one column or one row draws exactly like the flat list of settings schema 1.
/// Owns its DirectWrite objects; device-independent (drawn by <see cref="SceneRenderer"/> into any render target).
/// Single-threaded.
/// </summary>
internal sealed unsafe class Scene : IDisposable
{
    private const int SparkCapacity = 40;

    /// <summary>Size of an item's own label inside a row, relative to the label size.</summary>
    private const float TagScale = 0.92f;

    private readonly Dictionary<(string?, int, int), nint> _formats = [];
    private readonly Dictionary<string, TextLine> _units = new(StringComparer.Ordinal);
    private readonly List<TextLine> _lines = [];
    private readonly ColumnWidths _cols;

    public Scene(SceneInput input)
    {
        Input = input;
        var layout = input.Layout;
        Style = layout.Style ?? new OverlayStyle();
        Theme = input.Theme;
        S = (float)input.Scale;
        Arrangement = Style.Arrangement;
        RefreshMs = Math.Clamp(layout.RefreshMs, 250, 2000);
        WindowId = input.WindowId;

        var bg = Rgba.TryParse(Style.BackgroundColor) ?? Theme.Background;
        Background = bg.WithAlpha(bg.A / 255.0 * Math.Clamp(Style.BackgroundOpacity, 0, 1));
        HasBorder = Style.Border;
        BorderColor = Theme.Border;
        BorderWidth = MathF.Max(1, MathF.Round(S));
        Radius = (float)Math.Max(0, Style.CornerRadius) * S;
        TextShadow = Style.TextShadow;
        ShadowColor = Theme.Background;
        UnitColor = Rgba.TryParse(Style.UnitColor) ?? Theme.TextDim;

        LabelPx = (float)Style.LabelSize * S;
        ValuePx = (float)Style.ValueSize * S;
        UnitPx = (float)Style.UnitSize * S;
        ValueFamily = Style.ValueFontFamily ?? Style.FontFamily;

        // the cells row by row, left to right (holes in a row are allowed: a column another row uses)
        SceneRow? row = null;
        foreach (var src in OverlayGrid.Ordered((layout.CellsOf(input.WindowId) ?? []).Where(c => c is not null)))
        {
            if (row is null || row.Index != src.Row)
            {
                row = new SceneRow(src.Row);
                Rows.Add(row);
            }
            var built = BuildItem(src);
            built.Column = Math.Max(0, src.Column);
            row.Items.Add(built);
            Items.Add(built);
            ColumnCount = Math.Max(ColumnCount, built.Column + 1);
        }
        if (Arrangement == OverlayArrangement.Vertical)
            foreach (var r in Rows) LabelRow(r);
        _cols = new ColumnWidths(ColumnCount);
        ColumnLeft = new float[ColumnCount];
        ColumnRight = new float[ColumnCount];
        Arrange();
    }

    public SceneInput Input { get; }

    /// <summary>The window this scene draws.</summary>
    public string WindowId { get; }

    public List<SceneRow> Rows { get; } = [];

    /// <summary>Columns of the grid (the widest row).</summary>
    public int ColumnCount { get; }

    /// <summary>Left edge of each column's content, px (Vertical: column 0 starts with the label column).</summary>
    public float[] ColumnLeft { get; }

    /// <summary>Right edge of each column's content (units and sparklines included), px.</summary>
    public float[] ColumnRight { get; }

    public OverlayStyle Style { get; }

    public OverlayTheme Theme { get; }

    public OverlayArrangement Arrangement { get; }

    /// <summary>Scale (px per reference px).</summary>
    public float S { get; }

    public int RefreshMs { get; }

    public List<SceneItem> Items { get; } = [];

    public int Width { get; private set; }

    /// <summary>Changes whenever the geometry is arranged again (size, columns, dividers): a cached backdrop is stale.</summary>
    public int LayoutVersion { get; private set; }

    public int Height { get; private set; }

    public Rgba Background { get; }

    public bool HasBorder { get; }

    public Rgba BorderColor { get; }

    public float BorderWidth { get; }

    public float Radius { get; }

    public bool TextShadow { get; }

    public Rgba ShadowColor { get; }

    public Rgba UnitColor { get; }

    public float LabelPx { get; }

    public float ValuePx { get; }

    public float UnitPx { get; }

    public string ValueFamily { get; }

    /// <summary>Some sensor ids are not known to the hub yet (rebuild when the inventory changes).</summary>
    public bool HasUnresolved { get; private set; }

    /// <summary>Dividers between cells (Horizontal / Compact), px.</summary>
    public List<Divider> Dividers { get; } = [];

    /// <summary>Diameter of the dot between the values of a row (Vertical), px.</summary>
    public float DotSize => MathF.Max(2, MathF.Round(3 * S));

    // ── build ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Vertical rows: the row label for the label column and, for items whose label differs from it, a small label of
    /// their own in front of the value. The label column belongs to column 0: a row with its first column empty has no
    /// row label, its cells show their own. A row of one item draws exactly like the old list line. The row label is
    /// the first cell's own label when it has one, otherwise the component ("CPU", "GPU", "FPS"…) of a one-component
    /// row, otherwise the first cell's label.
    /// </summary>
    private void LabelRow(SceneRow row)
    {
        var first = row.Items[0];
        row.LabelColor = first.LabelColor;
        if (first.Column != 0)
        {
            foreach (var it in row.Items)
                if (it.SubLabels is null) it.Tag = Line(it.Label.Text, Format(it.Source.FontFamily ?? Style.FontFamily, Style.LabelWeight, LabelPx * TagScale));
            return;
        }
        if (row.Items.Count == 1)
        {
            // the classic line: the item's own label in the label column (rows of a multi-value item: their sub-labels)
            row.Label = first.SubLabels is null ? first.Label : null;
            row.SubLabelsInColumn = first.SubLabels is not null;
            return;
        }
        var custom = string.IsNullOrWhiteSpace(first.Source.Label) ? null : first.Label.Text;
        var text = custom ?? DefaultLabels.ForRow([.. row.Items.Select(i => i.Label.Text)], [.. row.Items.Select(i => i.Hw)]);
        row.Label = string.Equals(text, first.Label.Text, StringComparison.Ordinal) && first.SubLabels is null
            ? first.Label
            : Line(text, Format(first.Source.FontFamily ?? Style.FontFamily, Style.LabelWeight, LabelPx));
        foreach (var it in row.Items)
        {
            if (it.SubLabels is not null || string.Equals(it.Label.Text, text, StringComparison.OrdinalIgnoreCase)) continue;
            it.Tag = Line(it.Label.Text, Format(it.Source.FontFamily ?? Style.FontFamily, Style.LabelWeight, LabelPx * TagScale));
        }
    }

    private SceneItem BuildItem(OverlayItem src)
    {
        var ids = src.SensorIds ?? [];
        var n = Math.Max(1, ids.Count);
        var item = new SceneItem(src, n);
        for (var k = 0; k < n; k++)
        {
            var id = k < ids.Count ? ids[k] : "";
            var d = id.Length > 0 ? Input.Lookup.Find(id) : null;
            if (d is null)
            {
                if (id.Length > 0) HasUnresolved = true;
                d = Synthetic(id);
                item.Slot[k] = -1;
            }
            else
            {
                item.Slot[k] = Input.Lookup.Slot(d);
            }
            item.Desc[k] = d;
            var cap = Math.Clamp((int)Math.Round(src.AggregationSeconds * 1000.0 / RefreshMs), 1, 480);
            if (src.Aggregation != ValueAggregation.Current) item.Agg[k] = new float[cap];
        }

        var first = item.Desc[0];
        item.Kind = first.Kind;
        item.Hw = first.HardwareKind;
        item.BaseUnit = first.Unit.Length > 0 ? first.Unit : null;
        item.Decimals = src.Decimals ?? OverlayDefaults.Decimals(item.Kind);
        item.IsMulti = n > 1;
        item.Display = !item.IsMulti ? MultiValueDisplay.Bars
            : Arrangement == OverlayArrangement.Compact ? MultiValueDisplay.Bars : src.MultiDisplay;

        // label
        var label = src.Label;
        if (string.IsNullOrWhiteSpace(label)) label = Input.Labels?.Invoke(src);
        if (string.IsNullOrWhiteSpace(label))
            label = item.IsMulti ? DefaultLabels.ForGroup(item.Desc!) : DefaultLabels.For(first);
        var family = src.FontFamily ?? Style.FontFamily;
        var labelFmt = Format(family, Style.LabelWeight, LabelPx);
        item.Label = Line(label, labelFmt);
        item.LabelColor = Rgba.TryParse(src.LabelColor) ?? Rgba.TryParse(Style.LabelColor) ?? Theme.CategoryColor(item.Hw);
        item.BaseValueColor = Rgba.TryParse(src.ValueColor) ?? Rgba.TryParse(Style.ValueColor) ?? Theme.Text;
        item.ValueColor = item.BaseValueColor;
        item.Thresholds = Thresholds(src, first);

        // value fonts
        var valueFamily = src.FontFamily ?? ValueFamily;
        var valuePx = (float)(src.ValueSize ?? Style.ValueSize) * S;
        item.ValuePx = valuePx;
        item.Face = FontLibrary.Face(valueFamily, Style.ValueWeight);
        item.Main = new ValueText(item.Face, valuePx);
        item.UnitFormat = Format(valueFamily, 400, UnitPx);

        // sub-values (grid / rows)
        if (item.IsMulti && item.Display != MultiValueDisplay.Bars)
        {
            var subPx = valuePx * (item.Display == MultiValueDisplay.Grid ? 0.74f : 0.86f);
            item.SubPx = subPx;
            item.Cells = new ValueText[n];
            item.CellColors = new Rgba[n];
            for (var k = 0; k < n; k++)
            {
                item.Cells[k] = new ValueText(item.Face, subPx);
                item.CellColors[k] = item.BaseValueColor;
            }
            if (item.Display == MultiValueDisplay.Rows)
            {
                item.SubLabels = new TextLine[n];
                for (var k = 0; k < n; k++)
                {
                    var sub = string.IsNullOrWhiteSpace(src.Label) && Input.Labels?.Invoke(src) is null
                        ? DefaultLabels.For(item.Desc[k])
                        : $"{label} {DefaultLabels.Sub(item.Desc[k], k)}";
                    item.SubLabels[k] = Line(sub, Format(family, Style.LabelWeight, LabelPx * 0.92f));
                }
            }
        }
        if (item.IsMulti) item.BarLevels = new int[n];

        // units: decided by formatting a seed value (also gives the column templates)
        Span<char> buf = stackalloc char[ValueText.Capacity];
        var seed = OverlayDefaults.Seed(item.Kind);
        FormatValue(item, seed, buf, out var unit);
        // Compact line: "64°" — the degree sign joins the value (value font), no separate unit.
        if (Arrangement == OverlayArrangement.Compact && unit is "°C" or "°F") item.DegreeSuffix = true;
        var len = FormatValue(item, seed, buf, out unit);
        item.TemplateW = ValueText.Measure(item.Face, valuePx, buf[..len]);
        if (item.Cells is not null) item.CellTemplateW = ValueText.Measure(item.Face, item.SubPx, buf[..len]);
        var showUnit = src.ShowUnit ?? Style.ShowUnits;
        if (unit.Length == 0 || item.DegreeSuffix || string.Equals(unit, label, StringComparison.OrdinalIgnoreCase)) showUnit = false;
        item.ShowUnit = showUnit;
        SetUnit(item, unit);
        item.UnitColW = item.UnitLine?.Width ?? 0;

        if (src.ShowGraph) item.Spark = new float[SparkCapacity];
        item.Main.Set("—");
        if (item.Cells is not null)
            foreach (var c in item.Cells) c.Set("—");
        return item;
    }

    private static SensorDescriptor Synthetic(string id)
    {
        DefaultLabels.TryParseId(id, out var hw, out var kind, out var name);
        return new SensorDescriptor
        {
            Id = id.Length > 0 ? id : "missing",
            HardwareId = "",
            Name = name.Length > 0 ? name : "—",
            Kind = kind,
            HardwareKind = hw,
        };
    }

    private (float Value, Rgba Color)[] Thresholds(OverlayItem src, SensorDescriptor first)
    {
        if (src.Thresholds is { } custom)
        {
            return custom
                .Where(t => t is not null && Rgba.TryParse(t.Color) is not null)
                .OrderBy(t => t.Value)
                .Select(t => (t.Value, Rgba.TryParse(t.Color)!.Value))
                .ToArray();
        }
        return DefaultThresholds.For(first) is { } d ? [(d.Warning, Theme.Warning), (d.Danger, Theme.Danger)] : [];
    }

    private void* Format(string? family, int weight, float px)
    {
        var key = (family, weight >= 500 ? 600 : 400, (int)MathF.Round(px * 100));
        if (_formats.TryGetValue(key, out var f)) return (void*)f;
        var fmt = FontLibrary.CreateTextFormat(family, weight, px);
        _formats[key] = (nint)fmt;
        return fmt;
    }

    private TextLine Line(string text, void* format)
    {
        var line = new TextLine(text, format);
        _lines.Add(line);
        return line;
    }

    private void SetUnit(SceneItem item, string unit)
    {
        if (Arrangement == OverlayArrangement.Compact && unit is "°C" or "°F") unit = "°";
        item.Unit = unit;
        if (!item.ShowUnit || unit.Length == 0)
        {
            item.UnitLine = null;
            return;
        }
        var key = unit;
        if (!_units.TryGetValue(key, out var line))
        {
            line = new TextLine(unit, item.UnitFormat);
            _units[key] = line;
        }
        item.UnitLine = line;
    }

    private int FormatValue(SceneItem item, float value, Span<char> dest, out string unit)
    {
        int n;
        if (item.Kind == SensorKind.SmallData && item.BaseUnit is null or "MB")
            n = ValueFormat.Format(SensorKind.Data, value / 1024f, dest, Input.Culture, out unit, item.Decimals, Input.TemperatureUnit);
        else
            n = ValueFormat.Format(item.Kind, value, dest, Input.Culture, out unit, item.Decimals, Input.TemperatureUnit, item.BaseUnit);
        if (item.DegreeSuffix && n > 0 && n < dest.Length && dest[0] != '—') dest[n++] = '°';
        return n;
    }

    // ── per refresh ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Takes the latest values (indexed by sensor slot) and refreshes text, colors, bars, sparklines.
    /// Returns true when something visible changed. <see cref="Width"/> / <see cref="Height"/> may grow (never
    /// shrink) when a value needs more room. Allocation-free.
    /// </summary>
    public bool Update(ReadOnlySpan<float> values)
    {
        var dirty = false;
        var grow = false;
        Span<char> buf = stackalloc char[ValueText.Capacity];
        foreach (var item in Items)
        {
            var n = item.Slot.Length;
            var max = float.NaN;
            for (var k = 0; k < n; k++)
            {
                var slot = item.Slot[k];
                var raw = (uint)slot < (uint)values.Length ? values[slot] : float.NaN;
                var v = item.Aggregate(k, raw);
                item.Cur[k] = v;
                if (!float.IsNaN(v) && (float.IsNaN(max) || v > max)) max = v;
            }

            var main = item.IsMulti ? max : item.Cur[0];
            var len = FormatValue(item, main, buf, out var unit);
            if (item.Main.Set(buf[..len])) dirty = true;
            if (!ReferenceEquals(unit, item.Unit) && !(item.Unit == "°" && unit is "°C" or "°F"))
            {
                SetUnit(item, unit);
                dirty = true;
            }
            var color = Pick(item, main);
            if (color != item.ValueColor)
            {
                item.ValueColor = color;
                dirty = true;
            }
            if (item.Main.Width > item.TextColW + 0.01f) grow = true;
            if ((item.UnitLine?.Width ?? 0) > item.UnitColW + 0.01f) grow = true;

            if (item.IsMulti)
            {
                if (!float.IsNaN(max) && max > item.RunMax) item.RunMax = max;
                if (item.Cells is { } cells)
                {
                    for (var k = 0; k < n; k++)
                    {
                        len = FormatValue(item, item.Cur[k], buf, out _);
                        if (cells[k].Set(buf[..len])) dirty = true;
                        if (cells[k].Width > item.CellColW + 0.01f) grow = true;
                        var cc = Pick(item, item.Cur[k]);
                        if (cc != item.CellColors![k])
                        {
                            item.CellColors[k] = cc;
                            dirty = true;
                        }
                    }
                }
                else
                {
                    for (var k = 0; k < n; k++)
                    {
                        var level = (int)MathF.Round(BarFraction(item, item.Cur[k]) * 64);
                        if (level != item.BarLevels![k])
                        {
                            item.BarLevels[k] = level;
                            dirty = true;
                        }
                    }
                }
            }

            if (item.Spark is { } spark)
            {
                spark[item.SparkHead] = main;
                item.SparkHead = (item.SparkHead + 1) % spark.Length;
                if (item.SparkCount < spark.Length) item.SparkCount++;
                dirty = true;
            }
        }
        if (grow)
        {
            Arrange();
            dirty = true;
        }
        return dirty;
    }

    /// <summary>Color for a value: base color, then thresholds ascending (last match wins).</summary>
    public static Rgba Pick(SceneItem item, float v)
    {
        var c = item.BaseValueColor;
        if (float.IsNaN(v)) return c;
        foreach (var (value, color) in item.Thresholds)
            if (v >= value) c = color;
        return c;
    }

    /// <summary>0–1 bar height for a value of a multi-value item.</summary>
    public static float BarFraction(SceneItem item, float v)
    {
        if (float.IsNaN(v)) return 0;
        var (min, maxOpt) = OverlayDefaults.BarRange(item.Kind);
        var max = maxOpt ?? MathF.Max(item.RunMax, item.Thresholds.Length > 0 ? item.Thresholds[^1].Value : 0);
        if (item.Kind == SensorKind.Temperature && maxOpt is null) max = MathF.Max(max, 100);
        if (max <= min) return v > min ? 1 : 0;
        return Math.Clamp((v - min) / (max - min), 0, 1);
    }

    // ── geometry ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Row height for a value size.</summary>
    public static float RowHeight(float valuePx) => MathF.Ceiling(valuePx * 1.375f);

    public float CapPx(SceneItem item, float px) => item.Face.Px(item.Face.CapHeight, px);

    private float P => (float)Math.Max(0, Style.Padding) * S;

    private float Gap => (float)Math.Max(0, Style.Spacing) * S;

    /// <summary>(Re)computes positions and the panel size. Column widths only grow.</summary>
    private void Arrange()
    {
        foreach (var it in Items)
        {
            it.TextColW = MathF.Max(it.TextColW, MathF.Max(it.TemplateW, it.Main.Width));
            it.UnitColW = MathF.Max(it.UnitColW, it.UnitLine?.Width ?? 0);
            if (it.Cells is { } cells)
            {
                var w = it.CellTemplateW;
                foreach (var c in cells) w = MathF.Max(w, c.Width);
                it.CellColW = MathF.Max(it.CellColW, w);
            }
        }
        Dividers.Clear();
        switch (Arrangement)
        {
            case OverlayArrangement.Horizontal:
                ArrangeHorizontal();
                break;
            case OverlayArrangement.Compact:
                ArrangeCompact();
                break;
            default:
                ArrangeVertical();
                break;
        }
        LayoutVersion++;
    }

    private float BarW => MathF.Max(2, MathF.Round(3 * S));

    private float BarGap => MathF.Max(1, MathF.Round(2 * S));

    private float GraphW => MathF.Round(44 * S);

    private float GraphGap => MathF.Round(9 * S);

    private float UnitGap => MathF.Round(3 * S);

    /// <summary>Width of the value block (bars / grid / rows / single), without the unit.</summary>
    private float BlockWidth(SceneItem it)
    {
        if (!it.IsMulti) return it.TextColW;
        return it.Display switch
        {
            MultiValueDisplay.Grid => GridCols(it) * (it.CellColW + CellGap) - CellGap,
            MultiValueDisplay.Rows => it.CellColW,
            _ => BarsWidth(it) + MathF.Round(7 * S) + it.TextColW,
        };
    }

    private float CellGap => MathF.Round(7 * S);

    private float BarsWidth(SceneItem it) => it.Slot.Length * BarW + (it.Slot.Length - 1) * BarGap;

    public static int GridCols(SceneItem it)
    {
        var n = it.Slot.Length;
        if (n <= 4) return n;
        var rows = (n + 7) / 8;
        return (n + rows - 1) / rows;
    }

    private float BlockHeight(SceneItem it)
    {
        var rowH = RowHeight(it.ValuePx);
        if (!it.IsMulti) return rowH;
        return it.Display switch
        {
            MultiValueDisplay.Grid => (it.Slot.Length + GridCols(it) - 1) / GridCols(it) * RowHeight(it.SubPx),
            MultiValueDisplay.Rows => it.Slot.Length * RowHeight(it.SubPx),
            _ => rowH,
        };
    }

    private float UnitWidth(SceneItem it) => it.ShowUnit && it.UnitColW > 0 ? UnitGap + it.UnitColW : 0;

    /// <summary>Width of an item's own label in front of its value (Vertical rows), with its gap; 0 without one.</summary>
    private float TagWidth(SceneItem it, SceneRow row)
    {
        float w = 0;
        if (it.Tag is { } tag) w = tag.Width;
        else if (it.SubLabels is { } subs && !row.SubLabelsInColumn)
            foreach (var s in subs) w = MathF.Max(w, s.Width);
        return w > 0 ? w + TagGap : 0;
    }

    private float TagGap => MathF.Round(5 * S);

    private float CellGap2 => MathF.Round(20 * S);

    /// <summary>
    /// Label beside the value. Column 0 is the old list: the label column (row labels), item labels, right-aligned
    /// numbers, units, sparklines — shared by all rows. Every further column is the same: its own small labels on the
    /// left, the numbers right-aligned on one edge, then the units and sparklines, as wide as its widest cell. The
    /// sparkline of a row's last cell runs on into the empty space beside it instead of widening its column. A grid of
    /// one column is exactly the old list, a grid of one row the old one-line row.
    /// </summary>
    private void ArrangeVertical()
    {
        var p = P;
        var cols = _cols;
        cols.Clear();
        float labelCol = 0, tagCol = 0, valueCol = 0, unitCol = 0;
        var graph = false;
        foreach (var row in Rows)
        {
            if (row.Label is { } label) labelCol = MathF.Max(labelCol, label.Width);
            for (var k = 0; k < row.Items.Count; k++)
            {
                var it = row.Items[k];
                var inside = it.Spark is not null && k < row.Items.Count - 1; // a sparkline with a cell after it
                if (it.Column == 0)
                {
                    if (row.SubLabelsInColumn)
                    {
                        // as the list always did: the item's own label (not drawn) still widens the column
                        labelCol = MathF.Max(labelCol, it.Label.Width);
                        foreach (var s in it.SubLabels!) labelCol = MathF.Max(labelCol, s.Width);
                    }
                    tagCol = MathF.Max(tagCol, TagWidth(it, row));
                    valueCol = MathF.Max(valueCol, BlockWidth(it));
                    unitCol = MathF.Max(unitCol, it.ShowUnit ? it.UnitColW : 0);
                    graph |= inside;
                    continue;
                }
                var c = it.Column;
                cols.Tag[c] = MathF.Max(cols.Tag[c], TagWidth(it, row));
                cols.Block[c] = MathF.Max(cols.Block[c], BlockWidth(it));
                cols.Unit[c] = MathF.Max(cols.Unit[c], UnitWidth(it));
                cols.Graph[c] |= inside;
            }
        }
        var gapLv = labelCol > 0 ? MathF.Round(14 * S) : 0;
        var tagX = MathF.Round(p + labelCol + gapLv);
        var valueRight = MathF.Round(p + labelCol + gapLv + tagCol + valueCol);
        var unitX = valueRight + UnitGap;
        var end = unitCol > 0 ? unitX + unitCol : valueRight;
        var graphX = end + GraphGap;
        var right = graph ? graphX + GraphW : end;
        if (ColumnCount > 0)
        {
            ColumnLeft[0] = MathF.Round(p);
            ColumnRight[0] = right;
        }
        // further columns start one gap after the previous one ends (its widest cell, sparklines included)
        for (var c = 1; c < ColumnCount; c++)
        {
            var x = ColumnRight[c - 1] + CellGap2;
            cols.X[c] = x;
            cols.ValueRight[c] = MathF.Round(x + cols.Tag[c] + cols.Block[c]);
            var cellEnd = cols.ValueRight[c] + cols.Unit[c];
            cols.GraphX[c] = cellEnd + GraphGap;
            ColumnLeft[c] = x;
            ColumnRight[c] = cols.Graph[c] ? cols.GraphX[c] + GraphW : cellEnd;
            right = MathF.Max(right, ColumnRight[c]);
        }

        var firstItem = Rows.Count > 0 ? Rows[0].Items[0] : null;
        var firstRow = firstItem is not null ? RowHeight(firstItem.ValuePx) : RowHeight(ValuePx);
        var firstCap = firstItem is not null ? CapPx(firstItem, firstItem.ValuePx) : ValuePx * 0.7f;
        var pv = MathF.Round(MathF.Max(3 * S, p - (firstRow - firstCap) / 2));
        var y = pv;
        foreach (var row in Rows)
        {
            // the row's baseline comes from its largest value (one size in a normal row)
            var big = row.Items[0];
            foreach (var it in row.Items)
                if (it.ValuePx > big.ValuePx) big = it;
            var h = 0f;
            foreach (var it in row.Items) h = MathF.Max(h, BlockHeight(it));
            var baseline = MathF.Round(y + (RowHeight(big.ValuePx) + CapPx(big, big.ValuePx)) / 2);
            row.Top = y;
            row.Height = h;
            row.Baseline = baseline;
            row.LabelX = MathF.Round(p);
            row.Dots.Clear();

            for (var k = 0; k < row.Items.Count; k++)
            {
                var it = row.Items[k];
                var c = it.Column;
                it.Top = y;
                it.Height = BlockHeight(it);
                it.Baseline = baseline;
                it.LabelX = MathF.Round(p);
                it.GraphW = GraphW;
                if (c == 0)
                {
                    it.TagX = tagX;
                    it.ValueRight = valueRight;
                    it.UnitX = unitX;
                    it.GraphX = graphX;
                    it.CellEnd = it.Spark is not null ? graphX + GraphW : end;
                }
                else
                {
                    // a dot between this value and the one before it in the row
                    if (k > 0) row.Dots.Add(MathF.Round(cols.X[c] - CellGap2 / 2));
                    it.TagX = cols.X[c];
                    it.ValueRight = cols.ValueRight[c];
                    it.UnitX = it.ValueRight + UnitGap;
                    it.GraphX = cols.GraphX[c];
                    it.CellEnd = it.Spark is not null ? it.GraphX + GraphW : it.ValueRight + UnitWidth(it);
                }
                if (it.IsMulti && it.Display == MultiValueDisplay.Bars) it.BarsX = it.ValueRight - it.TextColW - MathF.Round(7 * S) - BarsWidth(it);
                if (it.Display == MultiValueDisplay.Grid && it.IsMulti) it.GridX = it.ValueRight - BlockWidth(it);
                right = MathF.Max(right, it.CellEnd);
            }
            y += h + Gap;
        }
        if (Rows.Count > 0) y -= Gap;
        Width = (int)MathF.Ceiling(right + p);
        Height = (int)MathF.Ceiling(y + pv);
        if (Rows.Count == 0)
        {
            Width = (int)MathF.Ceiling(2 * p + 40 * S);
            Height = (int)MathF.Ceiling(2 * pv + firstRow);
        }
    }

    /// <summary>Every cell with its label above the value; columns as wide as their widest cell, one band per row.</summary>
    private void ArrangeHorizontal()
    {
        var p = P;
        var cols = _cols;
        cols.Clear();
        var labelLine = MathF.Ceiling(LabelPx * 1.25f);
        var pv = MathF.Round(MathF.Max(4 * S, p - LabelPx * 0.2f));
        var cellGap = MathF.Round(22 * S);
        var rowGap = Gap + MathF.Round(6 * S);
        foreach (var row in Rows)
            foreach (var it in row.Items)
            {
                var c = it.Column;
                var subW = 0f;
                if (it.SubLabels is { } subs)
                    foreach (var s in subs) subW = MathF.Max(subW, s.Width);
                cols.Tag[c] = MathF.Max(cols.Tag[c], subW > 0 ? subW + MathF.Round(8 * S) : 0); // sub-label column
                cols.Block[c] = MathF.Max(cols.Block[c], BlockWidth(it));
                cols.Unit[c] = MathF.Max(cols.Unit[c], UnitWidth(it));
                cols.Graph[c] |= it.Spark is not null;
                cols.Label[c] = MathF.Max(cols.Label[c], it.Label.Width);
            }
        var x = MathF.Round(p);
        var right = x;
        for (var c = 0; c < ColumnCount; c++)
        {
            var valueW = cols.Block[c] + cols.Unit[c] + (cols.Graph[c] ? GraphGap + GraphW : 0);
            var w = MathF.Max(cols.Label[c], cols.Tag[c] + valueW);
            cols.X[c] = x;
            cols.ValueRight[c] = MathF.Round(x + cols.Tag[c] + cols.Block[c]);
            cols.GraphX[c] = cols.ValueRight[c] + cols.Unit[c] + GraphGap;
            ColumnLeft[c] = x;
            x += MathF.Ceiling(w);
            ColumnRight[c] = x;
            right = x;
            x += cellGap;
        }

        var top = pv;
        foreach (var row in Rows)
        {
            float maxH = 0;
            for (var i = 0; i < row.Items.Count; i++)
            {
                var it = row.Items[i];
                var c = it.Column;
                var cx = cols.X[c];
                it.LabelX = cx;
                it.LabelBaseline = MathF.Round(top + labelLine * 0.8f);
                var valueTop = top + labelLine + MathF.Round(2 * S);
                it.Top = valueTop;
                it.Height = BlockHeight(it);
                it.ValueRight = cols.ValueRight[c];
                it.UnitX = it.ValueRight + UnitGap;
                it.GraphX = cols.GraphX[c];
                it.GraphW = GraphW;
                it.SubLabelX = cx;
                var rowH = RowHeight(it.ValuePx);
                it.Baseline = MathF.Round(valueTop + (rowH + CapPx(it, it.ValuePx)) / 2);
                // bars / grid hug their numbers (a narrower block in a wide column starts further right)
                var inset = cols.Tag[c] + cols.Block[c] - BlockWidth(it);
                if (it.IsMulti && it.Display == MultiValueDisplay.Bars) it.BarsX = cx + inset;
                if (it.IsMulti && it.Display == MultiValueDisplay.Grid) it.GridX = cx + inset;
                maxH = MathF.Max(maxH, labelLine + MathF.Round(2 * S) + it.Height);
                // a divider between this cell and the one before it in the row
                if (i > 0) row.DividerXs.Add(MathF.Round(ColumnRight[c - 1] + cellGap / 2) + 0.5f);
            }
            foreach (var d in row.DividerXs) Dividers.Add(new Divider(d, top + MathF.Round(2 * S), top + maxH - MathF.Round(2 * S)));
            row.DividerXs.Clear();
            row.Top = top;
            row.Height = MathF.Max(maxH, RowHeight(row.Items[0].ValuePx));
            top += row.Height + rowGap;
        }
        if (Rows.Count > 0) top -= rowGap;
        else top += RowHeight(ValuePx);
        Width = (int)MathF.Ceiling(right + p);
        Height = (int)MathF.Ceiling(top + pv);
    }

    /// <summary>Compact cells "CPU 45° | GPU 60°", one line per row; columns as wide as their widest cell.</summary>
    private void ArrangeCompact()
    {
        var p = P;
        var cols = _cols;
        cols.Clear();
        var sep = MathF.Round(11 * S);
        var labelGap = MathF.Round(6 * S);
        foreach (var row in Rows)
            foreach (var it in row.Items)
            {
                var c = it.Column;
                cols.Label[c] = MathF.Max(cols.Label[c], it.Label.Width);
                if (it.IsMulti) cols.Tag[c] = MathF.Max(cols.Tag[c], BarsWidth(it) + MathF.Round(6 * S)); // bars
                cols.Block[c] = MathF.Max(cols.Block[c], it.TextColW);
                cols.Unit[c] = MathF.Max(cols.Unit[c], UnitWidth(it));
                cols.Graph[c] |= it.Spark is not null;
            }
        var x = MathF.Round(p);
        var right = x;
        for (var c = 0; c < ColumnCount; c++)
        {
            cols.X[c] = x;
            ColumnLeft[c] = x;
            // label, bars, number (same order of additions as one cell: one row draws exactly as before)
            var at = x + (cols.Label[c] + labelGap);
            cols.GraphX[c] = at; // where the bars of the widest bar block start
            at += cols.Tag[c];
            at += cols.Block[c];
            cols.ValueRight[c] = MathF.Round(at);
            at = cols.ValueRight[c] + cols.Unit[c];
            if (cols.Graph[c]) at = at + MathF.Round(7 * S) + MathF.Round(30 * S);
            ColumnRight[c] = at;
            right = at;
            x = at + 2 * sep;
        }

        float RowH(SceneRow r)
        {
            var h = 0f;
            foreach (var i in r.Items) h = MathF.Max(h, RowHeight(i.ValuePx));
            return h;
        }
        var rowH0 = Rows.Count > 0 ? RowH(Rows[0]) : RowHeight(ValuePx);
        var cap0 = Rows.Count > 0 ? CapPx(Rows[0].Items[0], Rows[0].Items[0].ValuePx) : ValuePx * 0.7f;
        var pv = MathF.Round(MathF.Max(3 * S, p * 0.8f - (rowH0 - cap0) / 2));
        var top = pv;
        foreach (var row in Rows)
        {
            var rowH = RowH(row);
            var cap = CapPx(row.Items[0], row.Items[0].ValuePx);
            var baseline = MathF.Round(top + (rowH + cap) / 2);
            for (var i = 0; i < row.Items.Count; i++)
            {
                var it = row.Items[i];
                var c = it.Column;
                it.Top = top;
                it.Height = rowH;
                it.LabelX = cols.X[c];
                it.Baseline = baseline;
                if (it.IsMulti) it.BarsX = MathF.Round(cols.GraphX[c] + (cols.Tag[c] - (BarsWidth(it) + MathF.Round(6 * S))));
                it.ValueRight = cols.ValueRight[c];
                it.UnitX = it.ValueRight + UnitGap;
                if (it.Spark is not null)
                {
                    it.GraphW = MathF.Round(30 * S);
                    it.GraphX = it.ValueRight + cols.Unit[c] + MathF.Round(7 * S);
                }
                // a divider between this cell and the one before it in the row
                if (i > 0) Dividers.Add(new Divider(MathF.Round(ColumnRight[c - 1] + sep) + 0.5f, top + rowH * 0.22f, top + rowH * 0.78f));
            }
            row.Top = top;
            row.Height = rowH;
            row.Baseline = baseline;
            top += rowH + Gap;
        }
        if (Rows.Count > 0) top -= Gap;
        else top += rowH0;
        Width = (int)MathF.Ceiling(right + p);
        Height = (int)MathF.Ceiling(top + pv);
    }

    /// <summary>Per-column scratch widths and positions of <see cref="Arrange"/> (allocated once per scene).</summary>
    private sealed class ColumnWidths(int n)
    {
        public readonly float[] Label = new float[n], Tag = new float[n], Block = new float[n], Unit = new float[n];
        public readonly float[] X = new float[n], ValueRight = new float[n], GraphX = new float[n];
        public readonly bool[] Graph = new bool[n];

        public void Clear()
        {
            Array.Clear(Label);
            Array.Clear(Tag);
            Array.Clear(Block);
            Array.Clear(Unit);
            Array.Clear(X);
            Array.Clear(ValueRight);
            Array.Clear(GraphX);
            Array.Clear(Graph);
        }
    }

    /// <summary>The grid as arranged now: column and row extents, the cell of every item (px).</summary>
    public OverlayGridGeometry Geometry()
    {
        var rows = Rows.Count == 0 ? 0 : Rows.Max(r => r.Index) + 1;
        var top = new float[rows];
        var bottom = new float[rows];
        foreach (var r in Rows)
        {
            top[r.Index] = r.Top;
            bottom[r.Index] = r.Top + r.Height;
        }
        var cells = new List<OverlayGridCell>(Items.Count);
        foreach (var r in Rows)
            foreach (var it in r.Items) cells.Add(new OverlayGridCell(it.Source.Id, r.Index, it.Column, MathF.Max(it.CellEnd, ColumnRight[it.Column])));
        return new OverlayGridGeometry(Width, Height, [.. ColumnLeft], [.. ColumnRight], top, bottom, cells);
    }

    public void Dispose()
    {
        foreach (var l in _lines) l.Dispose();
        foreach (var l in _units.Values) l.Dispose();
        _lines.Clear();
        _units.Clear();
        foreach (var f in _formats.Values)
        {
            var p = (void*)f;
            Com.Release(ref p);
        }
        _formats.Clear();
    }
}

/// <summary>One row of a <see cref="Scene"/>'s grid: its cells left to right.</summary>
internal sealed class SceneRow(int index)
{
    /// <summary>The grid row.</summary>
    public int Index { get; } = index;

    public List<SceneItem> Items { get; } = [];

    /// <summary>Vertical: what the label column shows (null for a lone multi-value item drawn as rows: its sub-labels go there).</summary>
    public TextLine? Label;

    public Rgba LabelColor;

    /// <summary>A lone multi-value item drawn as rows puts its sub-labels into the label column (the classic list).</summary>
    public bool SubLabelsInColumn;

    public float Top, Height, Baseline, LabelX;

    /// <summary>Vertical: x of the dot between two values.</summary>
    public List<float> Dots { get; } = [];

    /// <summary>Scratch list while arranging.</summary>
    public List<float> DividerXs { get; } = [];
}

/// <summary>One overlay entry inside a <see cref="Scene"/>.</summary>
internal sealed class SceneItem
{
    public SceneItem(OverlayItem src, int sensors)
    {
        Source = src;
        Desc = new SensorDescriptor[sensors];
        Slot = new int[sensors];
        Agg = new float[]?[sensors];
        AggHead = new int[sensors];
        AggCount = new int[sensors];
        Cur = new float[sensors];
    }

    public OverlayItem Source { get; }

    public SensorDescriptor[] Desc { get; }

    public int[] Slot { get; }

    public float[]?[] Agg { get; }

    public int[] AggHead { get; }

    public int[] AggCount { get; }

    /// <summary>Displayed (aggregated) value per sensor.</summary>
    public float[] Cur { get; }

    public SensorKind Kind;
    public HardwareKind Hw;
    public string? BaseUnit;
    public int? Decimals;
    public bool IsMulti;
    public MultiValueDisplay Display;
    public bool ShowUnit;
    public bool DegreeSuffix;

    public TextLine Label = null!;

    /// <summary>Vertical rows: the item's own label in front of its value, when it differs from the row label.</summary>
    public TextLine? Tag;

    /// <summary>The grid column of the cell.</summary>
    public int Column;

    public TextLine[]? SubLabels;
    public FontFace Face = null!;
    public float ValuePx, SubPx;
    public ValueText Main = null!;
    public ValueText[]? Cells;
    public Rgba[]? CellColors;
    public int[]? BarLevels;
    public unsafe void* UnitFormat;
    public string Unit = "";
    public TextLine? UnitLine;

    public Rgba LabelColor, BaseValueColor, ValueColor;
    public (float Value, Rgba Color)[] Thresholds = [];

    public float[]? Spark;
    public int SparkHead, SparkCount;
    public float RunMax;

    // column templates / widths (grow-only)
    public float TemplateW, CellTemplateW, TextColW, CellColW, UnitColW;

    // geometry (px)
    public float Top, Height, LabelX, LabelBaseline, SubLabelX, Baseline, ValueRight, UnitX, GraphX, GraphW, BarsX, GridX, TagX, CellEnd;

    /// <summary>Pushes a raw reading and returns the displayed value (current / max / average over the window).</summary>
    public float Aggregate(int k, float raw)
    {
        var ring = Agg[k];
        if (ring is null) return raw;
        ring[AggHead[k]] = raw;
        AggHead[k] = (AggHead[k] + 1) % ring.Length;
        if (AggCount[k] < ring.Length) AggCount[k]++;
        var count = AggCount[k];
        float max = float.NaN;
        double sum = 0;
        var valid = 0;
        for (var i = 0; i < count; i++)
        {
            var v = ring[i];
            if (float.IsNaN(v)) continue;
            valid++;
            sum += v;
            if (float.IsNaN(max) || v > max) max = v;
        }
        if (valid == 0) return float.NaN;
        return Source.Aggregation == ValueAggregation.Max ? max : (float)(sum / valid);
    }
}
