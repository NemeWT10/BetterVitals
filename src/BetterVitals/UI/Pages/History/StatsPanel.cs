using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using BetterVitals.Core.History;
using BetterVitals.Core.Sensors;
using BetterVitals.Localization;
using BetterVitals.UI.Controls;

namespace BetterVitals.UI.Pages.History;

/// <summary>One sensor's line in the stats panel (values already in display units).</summary>
public sealed record StatsRow(
    SensorDescriptor? Descriptor,
    string Name,
    string Unit,
    int ColorIndex,
    HistoryModel.DisplayUnit Display,
    RangeStats? Stats,
    bool Loading,
    string? Session = null);

/// <summary>
/// "Min / Średnia / Max" for the visible range or the drag selection: per series the exact values (from the
/// history's stats query) and when the min and max happened — a click on that time moves the chart's crosshair there.
/// </summary>
public sealed class StatsPanel : Border
{
    private readonly TextBlock _title = new() { FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _scope = new() { FontSize = 12.5, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _clear = new() { Margin = new Thickness(8, -3, -4, -3), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hint = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Grid _table = new() { Margin = new Thickness(0, 10, 0, 0) };

    public StatsPanel()
    {
        SetResourceReference(StyleProperty, "Card");
        Padding = new Thickness(18, 14, 18, 10);
        Margin = new Thickness(0);
        var root = new DockPanel();
        var head = new DockPanel { LastChildFill = true };
        _title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        _scope.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
        _clear.SetResourceReference(StyleProperty, "FlatButton");
        _clear.SetBinding(ContentControl.ContentProperty, new Binding("[history.stats.clear]") { Source = Loc.I, Mode = BindingMode.OneWay });
        _clear.Click += (_, _) => ClearSelection?.Invoke();
        DockPanel.SetDock(_title, Dock.Left);
        DockPanel.SetDock(_clear, Dock.Right);
        DockPanel.SetDock(_hint, Dock.Right);
        head.Children.Add(_title);
        head.Children.Add(_clear);
        head.Children.Add(_hint);
        head.Children.Add(_scope);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        root.Children.Add(_table);
        Child = root;
        _hint.SetBinding(TextBlock.TextProperty, new Binding("[history.stats.selectHint]") { Source = Loc.I, Mode = BindingMode.OneWay });
    }

    /// <summary>A min / max time was clicked (UTC ticks).</summary>
    public event Action<long>? GoTo;

    public event Action? ClearSelection;

    /// <summary>The rows shown (tests).</summary>
    public IReadOnlyList<StatsRow> Rows { get; private set; } = [];

    public string ScopeText => _scope.Text;

    public void Update(bool selection, string scope, IReadOnlyList<StatsRow> rows, Func<long, string> moment, CultureInfo culture, Func<int, Brush?> seriesBrush)
    {
        Rows = rows;
        _title.Text = Loc.T(selection ? "history.stats.selection" : "history.stats.visible");
        _scope.Text = scope;
        _clear.Visibility = selection ? Visibility.Visible : Visibility.Collapsed;
        _hint.Visibility = selection ? Visibility.Collapsed : Visibility.Visible;
        _hint.Margin = new Thickness(12, 0, 0, 0);

        _table.Children.Clear();
        _table.RowDefinitions.Clear();
        _table.ColumnDefinitions.Clear();
        _table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star), MinWidth = 150 });
        _table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.8, GridUnitType.Star) });
        _table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string[] heads = ["history.stats.sensor", "history.stats.min", "history.stats.avg", "history.stats.max"];
        for (var c = 0; c < heads.Length; c++)
        {
            var h = new TextBlock { Text = Loc.T(heads[c]), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(c == 0 ? 0 : 0, 0, 0, 6) };
            h.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
            Grid.SetColumn(h, c);
            _table.Children.Add(h);
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            _table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = i + 1;
            var line = new Rectangle { Height = 1, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
            line.SetResourceReference(Shape.FillProperty, "Brush.Border");
            Grid.SetRow(line, row);
            Grid.SetColumnSpan(line, 4);
            _table.Children.Add(line);

            var name = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 8, 7), VerticalAlignment = VerticalAlignment.Center };
            var dot = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.Background = seriesBrush(r.ColorIndex);
            name.Children.Add(dot);
            var text = new StackPanel();
            var n = new TextBlock { Text = r.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220 };
            n.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
            if (r.Descriptor is not null) Explain.Explain.SetSensor(n, r.Descriptor);
            text.Children.Add(n);
            if (r.Session is not null)
            {
                var s = new TextBlock { Text = r.Session, FontSize = 11, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 250 };
                s.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
                text.Children.Add(s);
            }
            name.Children.Add(text);
            Grid.SetRow(name, row);
            _table.Children.Add(name);

            if (r.Stats is { } st)
            {
                AddCell(row, 1, r.Display.Format(r.Display.Apply(st.Min), culture), r.Unit, moment(st.MinTimeUtcTicks), st.MinTimeUtcTicks);
                AddCell(row, 2, r.Display.Format(r.Display.Apply(st.Avg), culture), r.Unit, null, 0);
                AddCell(row, 3, r.Display.Format(r.Display.Apply(st.Max), culture), r.Unit, moment(st.MaxTimeUtcTicks), st.MaxTimeUtcTicks);
            }
            else
            {
                var none = new TextBlock { Text = r.Loading ? "…" : Loc.T("history.stats.none"), FontSize = 12.5, Margin = new Thickness(0, 8, 0, 7), VerticalAlignment = VerticalAlignment.Center };
                none.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
                Grid.SetRow(none, row);
                Grid.SetColumn(none, 1);
                Grid.SetColumnSpan(none, 3);
                _table.Children.Add(none);
            }
        }
    }

    private void AddCell(int row, int column, string value, string unit, string? when, long whenUtc)
    {
        var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 8, 5), VerticalAlignment = VerticalAlignment.Center };
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        var v = new TextBlock { Text = value, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        v.SetResourceReference(StyleProperty, "Text.Value");
        v.FontSize = 16;
        line.Children.Add(v);
        if (unit.Length > 0)
        {
            var u = new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4, 0, 0, 2) };
            u.SetResourceReference(StyleProperty, "Text.Unit");
            u.Margin = new Thickness(4, 0, 0, 2);
            line.Children.Add(u);
        }
        cell.Children.Add(line);
        if (when is not null)
        {
            var b = new Button { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 1, 6, 2), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = Loc.T("history.stats.goto") };
            b.SetResourceReference(StyleProperty, "FlatButton");
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = new Icon { Size = 12, Margin = new Thickness(0, 0, 4, 0) };
            icon.SetResourceReference(Icon.DataProperty, "Icon.Clock");
            content.Children.Add(icon);
            content.Children.Add(new TextBlock { Text = when, FontSize = 11.5, Typography = { NumeralAlignment = FontNumeralAlignment.Tabular } });
            b.Content = content;
            b.Click += (_, _) => GoTo?.Invoke(whenUtc);
            cell.Children.Add(b);
        }
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        _table.Children.Add(cell);
    }
}
