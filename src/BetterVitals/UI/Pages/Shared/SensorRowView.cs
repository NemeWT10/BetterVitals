using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using BetterVitals.Localization;
using BetterVitals.UI.Controls;

namespace BetterVitals.UI.Pages.Shared;

/// <summary>Which value columns a sensor row shows.</summary>
public enum SensorRowLayout
{
    /// <summary>Expanded Overview card: Now · Min · Max · Avg · Historia · Nakładka.</summary>
    Stats,

    /// <summary>Sensors page: sparkline · Now · Min / Max · Historia · Nakładka.</summary>
    Live,
}

/// <summary>Column widths shared by rows, their header and section headers, so everything lines up.</summary>
public static class RowColumns
{
    public const double Spark = 76;
    public const double Now = 112;
    public const double Stat = 78;
    public const double MinMax = 150;
    public const double Toggle = 64;
    public const double RowHeight = 36;
    public const double SidePadding = 10;

    /// <summary>Columns after the name (star) column.</summary>
    public static double[] Widths(SensorRowLayout layout) => layout == SensorRowLayout.Stats
        ? [Now, Stat, Stat, Stat, Toggle, Toggle]
        : [Spark, Now, MinMax, Toggle, Toggle];

    public static Grid Grid(SensorRowLayout layout)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
        foreach (var w in Widths(layout)) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        return g;
    }
}

/// <summary>A ToggleButton whose IsChecked only follows its binding: a click asks the model, settings answer.</summary>
public sealed class StateToggle : ToggleButton
{
    protected override void OnToggle()
    {
    }
}

/// <summary>
/// A name followed by a badge: the badge keeps its size and sits right after the name; the name trims when both do
/// not fit (a StackPanel would let the name overflow instead).
/// </summary>
public sealed class NameNotePanel : System.Windows.Controls.Panel
{
    protected override Size MeasureOverride(Size available)
    {
        if (InternalChildren.Count == 0) return default;
        var badge = InternalChildren.Count > 1 ? InternalChildren[1] : null;
        var badgeSize = default(Size);
        if (badge is not null)
        {
            badge.Measure(available);
            badgeSize = badge.DesiredSize;
        }
        var name = InternalChildren[0];
        name.Measure(new Size(Math.Max(0, available.Width - badgeSize.Width), available.Height));
        return new Size(name.DesiredSize.Width + badgeSize.Width, Math.Max(name.DesiredSize.Height, badgeSize.Height));
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (InternalChildren.Count == 0) return final;
        var badge = InternalChildren.Count > 1 ? InternalChildren[1] : null;
        var badgeWidth = badge?.DesiredSize.Width ?? 0;
        var name = InternalChildren[0];
        var nameWidth = Math.Min(name.DesiredSize.Width, Math.Max(0, final.Width - badgeWidth));
        name.Arrange(new Rect(0, (final.Height - name.DesiredSize.Height) / 2, nameWidth, name.DesiredSize.Height));
        badge?.Arrange(new Rect(nameWidth, (final.Height - badge.DesiredSize.Height) / 2, badgeWidth, badge.DesiredSize.Height));
        return final;
    }
}

/// <summary>Null / empty → Collapsed.</summary>
internal sealed class VisibleWhenSet : IValueConverter
{
    public static VisibleWhenSet I { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null or "" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// The sensor row used everywhere (DataContext = <see cref="SensorRowModel"/>): verbatim name with the explanation
/// bubble and the sensor's note, live value in its heat color, statistics since start, the Historia / Nakładka
/// switches and the context menu (Record · Show on overlay · Pin in flyout · Show in history). Recyclable: it only
/// binds, and counts itself as a view of its model while loaded (the page refreshes shown rows only).
/// </summary>
public class SensorRowView : Border
{
    private SensorRowModel? _counted;

    public SensorRowView() : this(SensorRowLayout.Stats)
    {
    }

    public SensorRowView(SensorRowLayout layout)
    {
        Layout = layout;
        Height = RowColumns.RowHeight;
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(RowColumns.SidePadding, 0, 0, 0);
        Background = System.Windows.Media.Brushes.Transparent;
        Resources.MergedDictionaries.Add(Ui.Theme);

        var grid = RowColumns.Grid(layout);

        // name + note
        var nameCell = new NameNotePanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var name = Ui.Text(13);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(SensorRowModel.Name)) { Mode = BindingMode.OneTime });
        name.SetBinding(Explain.Explain.SensorProperty, new Binding(nameof(SensorRowModel.Sensor)) { Mode = BindingMode.OneWay });
        name.Cursor = Cursors.Help;
        nameCell.Children.Add(name);
        var note = new Border { Margin = new Thickness(8, 0, 0, 0), MaxWidth = 210, Style = Ui.Style("Bv.Badge"), Tag = "warn" };
        var noteText = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        noteText.SetBinding(TextBlock.TextProperty, new Binding(nameof(SensorRowModel.Note)));
        note.Child = noteText;
        note.SetBinding(VisibilityProperty, new Binding(nameof(SensorRowModel.Note)) { Converter = VisibleWhenSet.I });
        nameCell.Children.Add(note);
        grid.Children.Add(nameCell);

        var col = 1;
        if (layout == SensorRowLayout.Live)
        {
            var spark = new Sparkline { Height = 20, Margin = new Thickness(4, 0, 14, 0), StrokeThickness = 1.4 };
            spark.SetBinding(Sparkline.SeriesProperty, new Binding(nameof(SensorRowModel.Series)) { Mode = BindingMode.OneWay });
            spark.SetBinding(Res.StrokeProperty, new Binding(nameof(SensorRowModel.CategoryBrushKey)) { Mode = BindingMode.OneWay });
            Grid.SetColumn(spark, col++);
            grid.Children.Add(spark);
        }

        // now + unit
        var nowCell = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var now = Ui.Number(13.5, "Brush.Text", FontWeights.SemiBold);
        now.SetBinding(TextBlock.TextProperty, new Binding(nameof(SensorRowModel.Now)));
        now.SetBinding(Res.ForegroundProperty, new Binding(nameof(SensorRowModel.NowBrushKey)));
        nowCell.Children.Add(now);
        var unit = Ui.Text(11.5, "Brush.TextDim");
        unit.Margin = new Thickness(4, 1, 0, 0);
        unit.Width = 38;
        unit.SetBinding(TextBlock.TextProperty, new Binding(nameof(SensorRowModel.Unit)));
        nowCell.Children.Add(unit);
        Grid.SetColumn(nowCell, col++);
        grid.Children.Add(nowCell);

        if (layout == SensorRowLayout.Stats)
        {
            foreach (var p in new[] { nameof(SensorRowModel.Min), nameof(SensorRowModel.Max), nameof(SensorRowModel.Avg) })
            {
                var t = Ui.Number(12.5, "Brush.TextDim");
                t.HorizontalAlignment = HorizontalAlignment.Right;
                t.Margin = new Thickness(0, 0, 10, 0);
                t.SetBinding(TextBlock.TextProperty, new Binding(p));
                Grid.SetColumn(t, col++);
                grid.Children.Add(t);
            }
        }
        else
        {
            var t = Ui.Number(12.5, "Brush.TextDim");
            t.HorizontalAlignment = HorizontalAlignment.Right;
            t.Margin = new Thickness(0, 0, 10, 0);
            t.SetBinding(TextBlock.TextProperty, new Binding(nameof(SensorRowModel.MinMax)));
            Grid.SetColumn(t, col++);
            grid.Children.Add(t);
        }

        var history = Toggle("Icon.Record", nameof(SensorRowModel.IsRecorded), nameof(SensorRowModel.HistoryTip), m => m.ToggleRecorded());
        history.SetBinding(OpacityProperty, new Binding(nameof(SensorRowModel.HistoryOpacity)));
        Grid.SetColumn(history, col++);
        grid.Children.Add(history);
        var overlay = Toggle("Icon.Overlay", nameof(SensorRowModel.IsOnOverlay), nameof(SensorRowModel.OverlayTip), m => m.ToggleOverlay());
        Grid.SetColumn(overlay, col);
        grid.Children.Add(overlay);

        Child = grid;

        MouseEnter += (_, _) => SetResourceReference(BackgroundProperty, "Brush.SurfaceHover");
        MouseLeave += (_, _) => Background = System.Windows.Media.Brushes.Transparent;
        ContextMenuOpening += OnContextMenuOpening;
        Loaded += (_, _) => Count(Model);
        Unloaded += (_, _) => Count(null);
        DataContextChanged += (_, _) =>
        {
            if (IsLoaded) Count(Model);
        };
    }

    public SensorRowLayout Layout { get; }

    public SensorRowModel? Model => DataContext as SensorRowModel;

    private StateToggle Toggle(string icon, string isChecked, string tip, Action<SensorRowModel> click)
    {
        var t = new StateToggle { Style = Ui.Style("Bv.ColumnToggle"), Content = Ui.Glyph(icon, 15) };
        t.SetBinding(ToggleButton.IsCheckedProperty, new Binding(isChecked) { Mode = BindingMode.OneWay });
        t.SetBinding(ToolTipProperty, new Binding(tip));
        t.Click += (_, _) =>
        {
            if (Model is { } m) click(m);
        };
        return t;
    }

    private void Count(SensorRowModel? model)
    {
        if (ReferenceEquals(_counted, model)) return;
        if (_counted is not null) _counted.Views--;
        _counted = model;
        if (model is not null) model.Views++;
    }

    // ───────────── context menu ─────────────

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        if (Model is not { } m) return;
        var menu = BuildMenu(m);
        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>Record · Show on overlay · Pin in flyout | Show in history (built when it opens, so it is always current).</summary>
    internal static ContextMenu BuildMenu(SensorRowModel m)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("sensors.menu.record", "Icon.Record", m.IsRecorded, m.ToggleRecorded));
        menu.Items.Add(Item("sensors.menu.overlay", "Icon.Overlay", m.IsOnOverlay, m.ToggleOverlay));
        menu.Items.Add(Item("sensors.menu.pin", "Icon.Pin", m.IsPinned, m.TogglePinned));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("sensors.menu.history", "Icon.Chart", null, m.ShowInHistory));
        return menu;
    }

    private static MenuItem Item(string key, string icon, bool? check, Action click)
    {
        var item = new MenuItem { Header = Loc.T(key), Icon = Ui.Glyph(icon, 15), IsCheckable = false, IsChecked = check == true };
        item.Click += (_, _) => click();
        return item;
    }
}

/// <summary>A row view for the Sensors page's virtualized list (the item template needs a parameterless type).</summary>
public sealed class LiveSensorRowView : SensorRowView
{
    public LiveSensorRowView() : base(SensorRowLayout.Live)
    {
    }
}

/// <summary>The column titles above rows: Now · Min · Max · Avg (or Min / Max) · History · Overlay.</summary>
public sealed class SensorColumnsHeader : Border
{
    public SensorColumnsHeader(SensorRowLayout layout, string? firstKey = null)
    {
        Padding = new Thickness(RowColumns.SidePadding, 0, 0, 0);
        Height = 26;
        var grid = RowColumns.Grid(layout);
        if (firstKey is not null) grid.Children.Add(Label(firstKey, HorizontalAlignment.Left, 0));
        string[] keys = layout == SensorRowLayout.Stats
            ? ["sensors.col.now", "sensors.col.min", "sensors.col.max", "sensors.col.avg", "sensors.col.history", "sensors.col.overlay"]
            : ["", "sensors.col.now", "sensors.col.minMax", "sensors.col.history", "sensors.col.overlay"];
        for (var i = 0; i < keys.Length; i++)
        {
            if (keys[i].Length == 0) continue;
            var toggleColumn = i >= keys.Length - 2;
            var right = !toggleColumn;
            var label = Label(keys[i], toggleColumn ? HorizontalAlignment.Center : HorizontalAlignment.Right, i + 1);
            // the value column's text ends where the number ends (before the unit)
            if (keys[i] == "sensors.col.now") label.Margin = new Thickness(0, 0, 48, 0);
            else if (right) label.Margin = new Thickness(0, 0, 10, 0);
            grid.Children.Add(label);
        }
        Child = grid;
    }

    private static TextBlock Label(string key, HorizontalAlignment align, int column)
    {
        var t = Ui.Text(11.5, "Brush.TextFaint", FontWeights.SemiBold, key);
        t.HorizontalAlignment = align;
        t.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(t, column);
        return t;
    }
}
