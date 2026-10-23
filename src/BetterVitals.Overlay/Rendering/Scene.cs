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

/// <summary>Everything a <see cref="Scene"/> is built from.</summary>
internal sealed record SceneInput(
    OverlayLayout Layout,
    OverlayTheme Theme,
    ISensorLookup Lookup,
    CultureInfo Culture,
    TemperatureUnit TemperatureUnit,
    Func<OverlayItem, string?>? Labels,
    double Scale);

/// <summary>
/// The overlay's content at one scale: resolved items, fonts, measured text and the arranged geometry, plus the
/// per-refresh value pipeline (aggregation, formatting into reusable buffers, thresholds, sparklines). Owns its
/// DirectWrite objects; device-independent (drawn by <see cref="SceneRenderer"/> into any render target).
/// Single-threaded.
/// </summary>
internal sealed unsafe class Scene : IDisposable
{
    private const int SparkCapacity = 40;

    private readonly Dictionary<(string?, int, int), nint> _formats = [];
    private readonly Dictionary<string, TextLine> _units = new(StringComparer.Ordinal);
    private readonly List<TextLine> _lines = [];

    public Scene(SceneInput input)
    {
        Input = input;
        var layout = input.Layout;
        Style = layout.Style ?? new OverlayStyle();
        Theme = input.Theme;
        S = (float)input.Scale;
        Arrangement = Style.Arrangement;
        RefreshMs = Math.Clamp(layout.RefreshMs, 250, 2000);

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

        foreach (var src in layout.Items ?? [])
        {
            if (src is null) continue;
            Items.Add(BuildItem(src));
        }
        Arrange();
    }

    public SceneInput Input { get; }

    public OverlayStyle Style { get; }

    public OverlayTheme Theme { get; }

    public OverlayArrangement Arrangement { get; }

    /// <summary>Scale (px per reference px).</summary>
    public float S { get; }

    public int RefreshMs { get; }

    public List<SceneItem> Items { get; } = [];

    public int Width { get; private set; }

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

    /// <summary>Divider x positions (Horizontal / Compact), px.</summary>
    public List<float> Dividers { get; } = [];

    public float DividerTop { get; private set; }

    public float DividerBottom { get; private set; }

    // ── build ───────────────────────────────────────────────────────────────────────────────────────────────────

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
        var len = FormatValue(item, seed, buf, out var unit);
        item.TemplateW = ValueText.Measure(item.Face, valuePx, buf[..len]);
        if (item.Cells is not null) item.CellTemplateW = ValueText.Measure(item.Face, item.SubPx, buf[..len]);
        var showUnit = src.ShowUnit ?? Style.ShowUnits;
        if (unit.Length == 0 || string.Equals(unit, label, StringComparison.OrdinalIgnoreCase)) showUnit = false;
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
        if (item.Kind == SensorKind.SmallData && item.BaseUnit is null or "MB")
            return ValueFormat.Format(SensorKind.Data, value / 1024f, dest, Input.Culture, out unit, item.Decimals, Input.TemperatureUnit);
        return ValueFormat.Format(item.Kind, value, dest, Input.Culture, out unit, item.Decimals, Input.TemperatureUnit, item.BaseUnit);
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

    private void ArrangeVertical()
    {
        var p = P;
        float labelCol = 0, valueCol = 0, unitCol = 0;
        var graph = false;
        foreach (var it in Items)
        {
            labelCol = MathF.Max(labelCol, it.Label.Width);
            if (it.SubLabels is { } subs)
                foreach (var s in subs) labelCol = MathF.Max(labelCol, s.Width);
            valueCol = MathF.Max(valueCol, BlockWidth(it));
            unitCol = MathF.Max(unitCol, it.ShowUnit ? it.UnitColW : 0);
            graph |= it.Spark is not null;
        }
        var gapLv = MathF.Round(14 * S);
        var valueRight = MathF.Round(p + labelCol + gapLv + valueCol);
        var unitX = valueRight + UnitGap;
        var end = unitCol > 0 ? unitX + unitCol : valueRight;
        var graphX = end + GraphGap;
        if (graph) end = graphX + GraphW;

        var firstRow = Items.Count > 0 ? RowHeight(Items[0].ValuePx) : RowHeight(ValuePx);
        var firstCap = Items.Count > 0 ? CapPx(Items[0], Items[0].ValuePx) : ValuePx * 0.7f;
        var pv = MathF.Round(MathF.Max(3 * S, p - (firstRow - firstCap) / 2));
        var y = pv;
        foreach (var it in Items)
        {
            var h = BlockHeight(it);
            it.Top = y;
            it.Height = h;
            it.LabelX = MathF.Round(p);
            it.ValueRight = valueRight;
            it.UnitX = unitX;
            it.GraphX = graphX;
            it.GraphW = GraphW;
            var rowH = RowHeight(it.ValuePx);
            it.Baseline = MathF.Round(y + (rowH + CapPx(it, it.ValuePx)) / 2);
            if (it.IsMulti && it.Display == MultiValueDisplay.Bars) it.BarsX = valueRight - it.TextColW - MathF.Round(7 * S) - BarsWidth(it);
            if (it.Display == MultiValueDisplay.Grid && it.IsMulti) it.GridX = valueRight - BlockWidth(it);
            y += h + Gap;
        }
        if (Items.Count > 0) y -= Gap;
        Width = (int)MathF.Ceiling(end + p);
        Height = (int)MathF.Ceiling(y + pv);
        if (Items.Count == 0)
        {
            Width = (int)MathF.Ceiling(2 * p + 40 * S);
            Height = (int)MathF.Ceiling(2 * pv + firstRow);
        }
    }

    private void ArrangeHorizontal()
    {
        var p = P;
        var labelLine = MathF.Ceiling(LabelPx * 1.25f);
        var firstRow = Items.Count > 0 ? RowHeight(Items[0].ValuePx) : RowHeight(ValuePx);
        var pv = MathF.Round(MathF.Max(4 * S, p - LabelPx * 0.2f));
        var cellGap = MathF.Round(22 * S);
        var x = MathF.Round(p);
        float maxH = 0;
        var top = pv;
        for (var i = 0; i < Items.Count; i++)
        {
            var it = Items[i];
            var block = BlockWidth(it);
            var valueW = block + UnitWidth(it) + (it.Spark is not null ? GraphGap + GraphW : 0);
            var subW = 0f;
            if (it.SubLabels is { } subs)
                foreach (var s in subs) subW = MathF.Max(subW, s.Width);
            var subColW = subW > 0 ? subW + MathF.Round(8 * S) : 0;
            var w = MathF.Max(it.Label.Width, subColW + valueW);
            it.LabelX = x;
            it.LabelBaseline = MathF.Round(top + labelLine * 0.8f);
            var valueTop = top + labelLine + MathF.Round(2 * S);
            it.Top = valueTop;
            it.Height = BlockHeight(it);
            it.ValueRight = MathF.Round(x + subColW + block);
            it.UnitX = it.ValueRight + UnitGap;
            it.GraphX = it.ValueRight + UnitWidth(it) + GraphGap;
            it.GraphW = GraphW;
            it.SubLabelX = x;
            var rowH = RowHeight(it.ValuePx);
            it.Baseline = MathF.Round(valueTop + (rowH + CapPx(it, it.ValuePx)) / 2);
            if (it.IsMulti && it.Display == MultiValueDisplay.Bars) it.BarsX = x;
            if (it.IsMulti && it.Display == MultiValueDisplay.Grid) it.GridX = x;
            maxH = MathF.Max(maxH, labelLine + MathF.Round(2 * S) + it.Height);
            x += MathF.Ceiling(w);
            if (i < Items.Count - 1)
            {
                Dividers.Add(MathF.Round(x + cellGap / 2) + 0.5f);
                x += cellGap;
            }
        }
        DividerTop = top + MathF.Round(2 * S);
        DividerBottom = top + maxH - MathF.Round(2 * S);
        Width = (int)MathF.Ceiling(x + p);
        Height = (int)MathF.Ceiling(top + MathF.Max(maxH, firstRow) + pv);
    }

    private void ArrangeCompact()
    {
        var p = P;
        var rowH = Items.Count > 0 ? Items.Max(i => RowHeight(i.ValuePx)) : RowHeight(ValuePx);
        var cap = Items.Count > 0 ? CapPx(Items[0], Items[0].ValuePx) : ValuePx * 0.7f;
        var pv = MathF.Round(MathF.Max(3 * S, p * 0.8f - (rowH - cap) / 2));
        var sep = MathF.Round(11 * S);
        var labelGap = MathF.Round(6 * S);
        var x = MathF.Round(p);
        var baseline = MathF.Round(pv + (rowH + cap) / 2);
        for (var i = 0; i < Items.Count; i++)
        {
            var it = Items[i];
            it.Top = pv;
            it.Height = rowH;
            it.LabelX = x;
            it.Baseline = baseline;
            x += it.Label.Width + labelGap;
            if (it.IsMulti)
            {
                it.BarsX = MathF.Round(x);
                x += BarsWidth(it) + MathF.Round(6 * S);
            }
            x += it.TextColW;
            it.ValueRight = MathF.Round(x);
            it.UnitX = it.ValueRight + UnitGap;
            x = it.ValueRight + UnitWidth(it);
            if (it.Spark is not null)
            {
                it.GraphW = MathF.Round(30 * S);
                it.GraphX = x + MathF.Round(7 * S);
                x = it.GraphX + it.GraphW;
            }
            if (i < Items.Count - 1)
            {
                Dividers.Add(MathF.Round(x + sep) + 0.5f);
                x += 2 * sep;
            }
        }
        DividerTop = pv + rowH * 0.22f;
        DividerBottom = pv + rowH * 0.78f;
        Width = (int)MathF.Ceiling(x + p);
        Height = (int)MathF.Ceiling(pv * 2 + rowH);
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

    public TextLine Label = null!;
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
    public float Top, Height, LabelX, LabelBaseline, SubLabelX, Baseline, ValueRight, UnitX, GraphX, GraphW, BarsX, GridX;

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
