using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Localization;
using BetterVitals.Overlay;
using BetterVitals.Overlay.Fonts;
using BetterVitals.UI.Themes;

namespace BetterVitals.UI.Pages.OverlayEditor;

/// <summary>
/// Everything about one overlay item (advanced mode): label, label / value colors, decimals, unit, sparkline, multi
/// display, aggregation, thresholds, font and size. Edits go to the item by id; typing and sliders are deferred.
/// </summary>
internal sealed class ItemEditor : StackPanel
{
    private readonly EditorContext _ctx;
    private readonly string _id;
    private readonly Action _titleChanged;

    public ItemEditor(EditorContext ctx, OverlayItem item, Action titleChanged)
    {
        _ctx = ctx;
        _id = item.Id;
        _titleChanged = titleChanged;
        var sensors = ctx.Sensors(item);
        var first = sensors.FirstOrDefault();
        Margin = new Thickness(12, 4, 12, 14);

        // label
        var label = new TextBox { Text = item.Label ?? "", Tag = ctx.DefaultLabel(item), Width = 210, MaxLength = 40 };
        label.SetResourceReference(StyleProperty, "OvEd.SmallField");
        label.TextChanged += (_, _) =>
        {
            var text = label.Text.Trim();
            Defer(i => i.Label = text.Length == 0 ? null : text);
            _titleChanged();
        };
        Children.Add(Ui.Line("ovl.item.label", label, "ovl.item.label.hint", top: 4));

        // colors
        var labelColor = ColorPopover.ColorButton(ctx, () => Item?.LabelColor, v => Edit(i => i.LabelColor = v), themeColor: () => CategoryColor(first));
        Children.Add(Ui.Line("ovl.item.labelColor", labelColor));
        var valueColor = ColorPopover.ColorButton(ctx, () => Item?.ValueColor, v => Edit(i => i.ValueColor = v), themeColor: () => ThemeManager.I.Current.Text);
        Children.Add(Ui.Line("ovl.item.valueColor", valueColor, "ovl.item.valueColor.hint"));

        // decimals / unit / sparkline
        var decimals = new SegmentBar<int>(
            [(-1, "ovl.auto"), (0, "=0"), (1, "=1"), (2, "=2"), (3, "=3")],
            item.Decimals ?? -1, v => Edit(i => i.Decimals = v < 0 ? null : v), fontSize: 12);
        Children.Add(Ui.Line("ovl.item.decimals", decimals));
        var unit = new SegmentBar<int>(
            [(0, "ovl.item.unit.global"), (1, "ovl.item.unit.show"), (2, "ovl.item.unit.hide")],
            item.ShowUnit switch { null => 0, true => 1, false => 2 }, v => Edit(i => i.ShowUnit = v switch { 1 => true, 2 => false, _ => null }), fontSize: 12);
        Children.Add(Ui.Line("ovl.item.unit", unit));
        Children.Add(Ui.Line("ovl.item.graph", Ui.Switch(item.ShowGraph, v => Edit(i => i.ShowGraph = v)), "ovl.item.graph.hint"));

        // several values of one kind
        if (item.SensorIds.Count > 1)
        {
            var multi = new SegmentBar<MultiValueDisplay>(
                [(MultiValueDisplay.Bars, "ovl.multi.bars"), (MultiValueDisplay.Grid, "ovl.multi.grid"), (MultiValueDisplay.Rows, "ovl.multi.rows")],
                item.MultiDisplay, v => Edit(i => i.MultiDisplay = v), fontSize: 12);
            Children.Add(Ui.Line("ovl.item.multi", multi, "ovl.item.multi.hint"));
        }

        // aggregation
        var seconds = new SliderLine(1, 60, Math.Clamp(item.AggregationSeconds, 1, 60), 1,
            v => Loc.T("ovl.seconds", v.ToString("0", Loc.I.Culture)), v => Defer(i => i.AggregationSeconds = (int)v), 150);
        var secondsLine = Ui.Line("ovl.item.window", seconds);
        secondsLine.Visibility = item.Aggregation == ValueAggregation.Current ? Visibility.Collapsed : Visibility.Visible;
        var aggregation = new SegmentBar<ValueAggregation>(
            [(ValueAggregation.Current, "ovl.agg.current"), (ValueAggregation.Max, "ovl.agg.max"), (ValueAggregation.Average, "ovl.agg.average")],
            item.Aggregation, v =>
            {
                Edit(i => i.Aggregation = v);
                secondsLine.Visibility = v == ValueAggregation.Current ? Visibility.Collapsed : Visibility.Visible;
            }, fontSize: 12);
        Children.Add(Ui.Line("ovl.item.aggregation", aggregation));
        Children.Add(secondsLine);

        // thresholds
        Children.Add(Ui.Divider(14, 2));
        Children.Add(new ThresholdsEditor(ctx, _id, first));

        // font and size
        Children.Add(Ui.Divider(12, 2));
        var font = FontCombo(ctx, item.FontFamily, v => Edit(i => i.FontFamily = v));
        Children.Add(Ui.Line("ovl.item.font", font));
        var size = new ComboBox { MinWidth = 120 };
        size.SetResourceReference(StyleProperty, "Combo");
        size.Items.Add(new Choice<double?>(null, Loc.T("ovl.auto")));
        foreach (var px in new double[] { 10, 12, 14, 16, 18, 20, 24, 28, 32, 40, 48 })
            size.Items.Add(new Choice<double?>(px, px.ToString(Loc.I.Culture) + " px"));
        size.SelectedItem = size.Items.OfType<Choice<double?>>().FirstOrDefault(c => c.Value == item.ValueSize) ?? size.Items[0];
        size.SelectionChanged += (_, _) =>
        {
            if (size.SelectedItem is Choice<double?> c) Edit(i => i.ValueSize = c.Value);
        };
        Children.Add(Ui.Line("ovl.item.size", size, "ovl.item.size.hint"));
    }

    private OverlayItem? Item => _ctx.Layout.Items.Find(i => i.Id == _id);

    private void Edit(Action<OverlayItem> change) => _ctx.Model.EditItem(_id, change);

    private void Defer(Action<OverlayItem> change) => _ctx.Defer(l =>
    {
        var item = l.Items.Find(i => i.Id == _id);
        if (item is not null) change(item);
    });

    private static Color CategoryColor(SensorDescriptor? s) => s is null ? ThemeManager.I.Current.TextDim : ThemeManager.I.CategoryColor(s.HardwareKind);

    /// <summary>"Like the overlay" + every family (bundled first, then system).</summary>
    public static ComboBox FontCombo(EditorContext ctx, string? current, Action<string?> changed, string inheritKey = "ovl.item.font.global")
    {
        var combo = new ComboBox { MinWidth = 180 };
        combo.SetResourceReference(StyleProperty, "Combo");
        combo.Items.Add(new Choice<string?>(null, Loc.T(inheritKey)));
        foreach (var f in BundledFonts.AllFamilies) combo.Items.Add(new Choice<string?>(f, f));
        combo.SelectedItem = combo.Items.OfType<Choice<string?>>().FirstOrDefault(c => string.Equals(c.Value, current, StringComparison.OrdinalIgnoreCase)) ?? combo.Items[0];
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is Choice<string?> c) changed(c.Value);
        };
        return combo;
    }
}

/// <summary>An option of a combo box: a value and its (already localized or verbatim) text.</summary>
internal sealed record Choice<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// Value → color rows of an item: the sensor's defaults (e.g. CPU 80 / 90 °C), own rows (value field in the user's
/// unit, color, remove; "Add"), or off. Rows are kept sorted when a value is committed.
/// </summary>
internal sealed class ThresholdsEditor : StackPanel
{
    private readonly EditorContext _ctx;
    private readonly string _id;
    private readonly SensorDescriptor? _sensor;
    private readonly StackPanel _body = new();
    private readonly SegmentBar<ThresholdMode> _mode;

    public ThresholdsEditor(EditorContext ctx, string itemId, SensorDescriptor? sensor)
    {
        _ctx = ctx;
        _id = itemId;
        _sensor = sensor;
        var item = Item!;
        _mode = new SegmentBar<ThresholdMode>(
            [(ThresholdMode.Default, "ovl.thr.default"), (ThresholdMode.Custom, "ovl.thr.custom"), (ThresholdMode.Off, "ovl.thr.off")],
            ThresholdEditing.Mode(item), OnMode, fontSize: 12);
        Children.Add(Ui.Line("ovl.thr.title", _mode, "ovl.thr.hint", top: 10));
        _body.Margin = new Thickness(0, 10, 0, 0);
        Children.Add(_body);
        Build();
    }

    private OverlayItem? Item => _ctx.Layout.Items.Find(i => i.Id == _id);

    private void OnMode(ThresholdMode mode)
    {
        var theme = _ctx.Theme;
        var rows = mode switch
        {
            ThresholdMode.Default => null,
            ThresholdMode.Off => new List<OverlayThreshold>(),
            _ => ThresholdEditing.Seed(_sensor, theme),
        };
        Set(rows);
        Build();
    }

    private void Set(List<OverlayThreshold>? rows)
    {
        var copy = rows is null ? null : ThresholdEditing.Copy(rows);
        _ctx.Model.EditItem(_id, i => i.Thresholds = copy is null ? null : ThresholdEditing.Copy(copy));
    }

    private string Unit => _sensor is null ? "" : _ctx.FormatParts(_sensor, 0).Unit;

    private void Build()
    {
        _body.Children.Clear();
        var item = Item;
        if (item is null) return;
        var theme = _ctx.Theme;
        var kind = _sensor?.Kind ?? SensorKind.Factor;
        switch (ThresholdEditing.Mode(item))
        {
            case ThresholdMode.Default:
            {
                var defaults = ThresholdEditing.Defaults(_sensor, theme);
                if (defaults.Count == 0)
                {
                    _body.Children.Add(Ui.Hint("ovl.thr.none", new Thickness(0)));
                    break;
                }
                var row = new WrapPanel();
                foreach (var t in defaults) row.Children.Add(Pill(t, kind));
                _body.Children.Add(row);
                break;
            }
            case ThresholdMode.Off:
                _body.Children.Add(Ui.Hint("ovl.thr.offHint", new Thickness(0)));
                break;
            default:
            {
                var rows = item.Thresholds!;
                for (var i = 0; i < rows.Count; i++) _body.Children.Add(Row(rows, i, kind));
                var add = Ui.Button("GhostButton", "ovl.thr.add", "Icon.Plus", (_, _) =>
                {
                    Set(ThresholdEditing.Add(Item?.Thresholds ?? [], _ctx.Theme));
                    Build();
                });
                add.HorizontalAlignment = HorizontalAlignment.Left;
                add.Margin = new Thickness(0, 2, 0, 0);
                _body.Children.Add(add);
                break;
            }
        }
    }

    private Border Pill(OverlayThreshold t, SensorKind kind)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var dot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 6, 0), Background = new SolidColorBrush(ColorText.ToColor(t.Color, Colors.Gray)) };
        row.Children.Add(dot);
        var text = new TextBlock { Text = "≥ " + Number(ThresholdEditing.ToDisplay(t.Value, kind, _ctx.TemperatureUnit)) + " " + Unit, FontSize = 12 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        row.Children.Add(text);
        var pill = new Border { Child = row, Margin = new Thickness(0, 0, 8, 6) };
        pill.SetResourceReference(StyleProperty, "Chip");
        return pill;
    }

    private DockPanel Row(List<OverlayThreshold> rows, int index, SensorKind kind)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = false };
        var from = Ui.Text("ovl.thr.from", size: 12.5);
        from.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        from.VerticalAlignment = VerticalAlignment.Center;
        from.Width = 36;
        dock.Children.Add(from);
        var field = new TextBox { Width = 74, Text = Number(ThresholdEditing.ToDisplay(rows[index].Value, kind, _ctx.TemperatureUnit)) };
        field.SetResourceReference(StyleProperty, "OvEd.SmallField");
        System.Windows.Automation.AutomationProperties.SetName(field, Loc.T("ovl.thr.value"));
        void Commit()
        {
            if (!TryNumber(field.Text, out var v))
            {
                field.SetResourceReference(Control.ForegroundProperty, "Brush.Danger");
                return;
            }
            field.SetResourceReference(Control.ForegroundProperty, "Brush.Text");
            var current = Item?.Thresholds ?? [];
            var stored = ThresholdEditing.FromDisplay(v, kind, _ctx.TemperatureUnit);
            if (index < current.Count && Math.Abs(current[index].Value - stored) < 1e-4) return;
            Set(ThresholdEditing.Sorted(ThresholdEditing.SetValue(current, index, stored)));
            Build();
        }
        field.LostKeyboardFocus += (_, _) => Commit();
        field.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Commit();
            }
        };
        dock.Children.Add(field);
        var unit = new TextBlock { Text = Unit, Margin = new Thickness(6, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5, MinWidth = 26 };
        unit.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        dock.Children.Add(unit);
        var color = ColorPopover.ColorButton(_ctx, () => Item?.Thresholds is { } t && index < t.Count ? t[index].Color : null, v =>
        {
            if (v is null) return;
            Set(ThresholdEditing.SetColor(Item?.Thresholds ?? [], index, v));
        }, allowTheme: false);
        dock.Children.Add(color);
        var remove = Ui.IconButton("Icon.Trash", "ovl.thr.remove", (_, _) =>
        {
            Set(ThresholdEditing.Remove(Item?.Thresholds ?? [], index));
            Build();
        }, 15);
        remove.Margin = new Thickness(6, 0, 0, 0);
        dock.Children.Add(remove);
        return dock;
    }

    private static string Number(double v) => v.ToString(Math.Abs(v % 1) < 1e-6 ? "0" : "0.##", Loc.I.Culture);

    /// <summary>Accepts the UI culture's decimal separator and the invariant one.</summary>
    internal static bool TryNumber(string text, out double value)
        => double.TryParse(text.Trim(), NumberStyles.Float, Loc.I.Culture, out value) && double.IsFinite(value)
           || double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
}
