using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using BetterVitals.UI.Controls;
using BetterVitals.UI.Pages.Shared;

namespace BetterVitals.UI.Pages.Sensors;

/// <summary>
/// A hardware section header of the Sensors list (DataContext = <see cref="SectionModel"/>): chevron, icon, verbatim name,
/// count, and the switches for the whole group in the Historia / Nakładka columns (partial when only some are on).
/// </summary>
public sealed class SectionHeaderView : Border
{
    public SectionHeaderView()
    {
        Height = 50;
        Padding = new Thickness(RowColumns.SidePadding - 6, 10, 0, 2);
        Background = System.Windows.Media.Brushes.Transparent;
        // no Resources of its own: the styles come from Ui.Style (a merged copy per row cost a dictionary each)
        var grid = RowColumns.Grid(SensorRowLayout.Live);

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Background = System.Windows.Media.Brushes.Transparent };
        var chevron = new Button { Style = Ui.Style("Bv.Expander"), Width = 26, Height = 26, Margin = new Thickness(0, 0, 6, 0) };
        chevron.SetBinding(TagProperty, new Binding(nameof(SectionModel.ExpanderTag)));
        chevron.Click += (_, _) => (DataContext as SectionModel)?.ToggleCollapsed();
        left.Children.Add(chevron);

        var tile = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 10, 0) };
        tile.SetBinding(Res.BackgroundProperty, new Binding(nameof(SectionModel.CategorySoftKey)));
        var icon = new Icon { Size = 16 };
        icon.SetBinding(Res.IconProperty, new Binding(nameof(SectionModel.IconKey)));
        icon.SetBinding(Res.ForegroundProperty, new Binding(nameof(SectionModel.CategoryBrushKey)));
        tile.Child = icon;
        left.Children.Add(tile);

        var name = Ui.Text(14, "Brush.Text", FontWeights.SemiBold);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(SectionModel.Name)) { Mode = BindingMode.OneWay });
        left.Children.Add(name);

        var count = new Border { Margin = new Thickness(10, 0, 0, 0) };
        count.SetResourceReference(StyleProperty, "Tag");
        var countText = new TextBlock();
        countText.SetValue(System.Windows.Documents.Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular);
        countText.SetBinding(TextBlock.TextProperty, new Binding(nameof(SectionModel.CountText)));
        count.Child = countText;
        left.Children.Add(count);
        left.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInside(d, chevron)) return;
            (DataContext as SectionModel)?.ToggleCollapsed();
        };
        Grid.SetColumnSpan(left, 4);
        grid.Children.Add(left);

        var history = Toggle("Icon.Record", nameof(SectionModel.RecordState), nameof(SectionModel.RecordTip), m => m.ToggleRecord());
        history.SetBinding(OpacityProperty, new Binding(nameof(SectionModel.HistoryOpacity)));
        Grid.SetColumn(history, 4);
        grid.Children.Add(history);
        var overlay = Toggle("Icon.Overlay", nameof(SectionModel.OverlayState), nameof(SectionModel.OverlayTip), m => m.ToggleOverlay());
        Grid.SetColumn(overlay, 5);
        grid.Children.Add(overlay);

        // a hairline under the header ties the section together
        var outer = new Grid();
        outer.Children.Add(grid);
        var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6, 0, 0, -2) };
        line.SetResourceReference(BackgroundProperty, "Brush.Border");
        outer.Children.Add(line);
        Child = outer;
    }

    private StateToggle Toggle(string icon, string state, string tip, Action<SectionModel> click)
    {
        var t = new StateToggle { Style = Ui.Style("Bv.ColumnToggle"), Content = Ui.Glyph(icon, 15), IsThreeState = true };
        t.SetBinding(ToggleButton.IsCheckedProperty, new Binding(state) { Mode = BindingMode.OneWay });
        t.SetBinding(ToolTipProperty, new Binding(tip));
        t.Click += (_, _) =>
        {
            if (DataContext is SectionModel m) click(m);
        };
        return t;
    }

    private static bool IsInside(DependencyObject d, DependencyObject ancestor)
    {
        for (var cur = d; cur is not null;
             cur = cur is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (ReferenceEquals(cur, ancestor)) return true;
        return false;
    }
}
