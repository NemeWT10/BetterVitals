using System.Windows;

namespace BetterVitals.UI.Pages.Overview;

/// <summary>
/// Cards in as many equal columns as fit (≥ <see cref="MinColumnWidth"/>); a card marked <see cref="IsWideProperty"/>
/// (expanded) takes a whole row. Cards of one row share its height, so the grid stays calm and aligned.
/// </summary>
public sealed class CardGrid : System.Windows.Controls.Panel
{
    public static readonly DependencyProperty IsWideProperty = DependencyProperty.RegisterAttached(
        "IsWide", typeof(bool), typeof(CardGrid), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static bool GetIsWide(DependencyObject d) => (bool)d.GetValue(IsWideProperty);

    public static void SetIsWide(DependencyObject d, bool value) => d.SetValue(IsWideProperty, value);

    public double MinColumnWidth { get; set; } = 340;

    public double Gap { get; set; } = 14;

    private readonly List<(UIElement Child, Rect Slot)> _slots = [];

    /// <summary>Columns for a width (at least one).</summary>
    public int ColumnsFor(double width)
        => double.IsInfinity(width) ? 2 : Math.Max(1, (int)Math.Floor((width + Gap) / (MinColumnWidth + Gap)));

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? MinColumnWidth * 2 + Gap : available.Width;
        return Layout(width, measure: true);
    }

    protected override Size ArrangeOverride(Size final)
    {
        Layout(final.Width, measure: false);
        foreach (var (child, slot) in _slots) child.Arrange(slot);
        return final;
    }

    private Size Layout(double width, bool measure)
    {
        _slots.Clear();
        var cols = ColumnsFor(width);
        var colWidth = (width - Gap * (cols - 1)) / cols;
        double y = 0;
        var row = new List<UIElement>(cols);
        double rowHeight = 0;

        void FlushRow(bool stretch = false)
        {
            if (row.Count == 0) return;
            // a row cut short by an expanded card shares the full width, instead of leaving a hole next to it
            var w = colWidth;
            if (stretch && row.Count < cols)
            {
                w = (width - Gap * (row.Count - 1)) / row.Count;
                rowHeight = 0;
                foreach (var c in row)
                {
                    if (measure) c.Measure(new Size(w, double.PositiveInfinity));
                    rowHeight = Math.Max(rowHeight, c.DesiredSize.Height);
                }
            }
            for (var i = 0; i < row.Count; i++) _slots.Add((row[i], new Rect(i * (w + Gap), y, w, rowHeight)));
            y += rowHeight + Gap;
            row.Clear();
            rowHeight = 0;
        }

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            if (GetIsWide(child))
            {
                FlushRow(stretch: true);
                if (measure) child.Measure(new Size(width, double.PositiveInfinity));
                _slots.Add((child, new Rect(0, y, width, child.DesiredSize.Height)));
                y += child.DesiredSize.Height + Gap;
                continue;
            }
            if (measure) child.Measure(new Size(colWidth, double.PositiveInfinity));
            row.Add(child);
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (row.Count == cols) FlushRow();
        }
        FlushRow();
        return new Size(width, Math.Max(0, y - Gap));
    }
}
