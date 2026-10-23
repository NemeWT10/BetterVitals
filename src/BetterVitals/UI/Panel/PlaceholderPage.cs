using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using BetterVitals.Localization;
using BetterVitals.UI.Controls;

namespace BetterVitals.UI.Panel;

/// <summary>Stand-in for a page that a later wave delivers: the page header and a calm "on its way" card.</summary>
internal sealed class PlaceholderPage : UserControl
{
    public PlaceholderPage(PageInfo info)
    {
        var root = new StackPanel();

        var title = new TextBlock();
        title.SetResourceReference(StyleProperty, "PageTitle");
        title.SetBinding(TextBlock.TextProperty, Tr(info.TitleKey));
        root.Children.Add(title);
        var subtitleKey = $"page.{info.Id}.subtitle";
        if (Loc.Has(subtitleKey))
        {
            var subtitle = new TextBlock();
            subtitle.SetResourceReference(StyleProperty, "PageSubtitle");
            subtitle.SetBinding(TextBlock.TextProperty, Tr(subtitleKey));
            root.Children.Add(subtitle);
        }

        var card = new Border { Margin = new Thickness(0, 24, 0, 0), Padding = new Thickness(28, 34, 28, 30) };
        card.SetResourceReference(StyleProperty, "Card");
        var body = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

        var badge = new Grid { Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Center };
        var ring = new Border { CornerRadius = new CornerRadius(20), Opacity = 0.9 };
        ring.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSoft");
        badge.Children.Add(ring);
        var glyph = new Icon { Size = 30, StrokeThickness = 1.6 };
        glyph.SetResourceReference(Icon.DataProperty, info.IconKey);
        glyph.SetResourceReference(Icon.ForegroundProperty, "Brush.AccentText");
        badge.Children.Add(glyph);
        body.Children.Add(badge);
        Motion.SetHoverLift(badge, 3);

        var head = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        head.SetBinding(TextBlock.TextProperty, Tr("page.placeholder.title"));
        head.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        body.Children.Add(head);
        var text = new TextBlock { FontSize = 13, Margin = new Thickness(0, 6, 0, 0), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        text.SetBinding(TextBlock.TextProperty, Tr("page.placeholder.body"));
        text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        body.Children.Add(text);

        // a quiet sketch of what is coming: three skeleton rows
        var sketch = new StackPanel { Margin = new Thickness(0, 26, 0, 0), Width = 420 };
        foreach (var (w, o) in new[] { (1.0, 0.9), (0.82, 0.7), (0.64, 0.5) })
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0), Opacity = o };
            var dot = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8) };
            dot.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceHover");
            row.Children.Add(dot);
            var bar = new Border { Height = 10, CornerRadius = new CornerRadius(5), Margin = new Thickness(12, 0, 0, 0), Width = 380 * w - 40, HorizontalAlignment = HorizontalAlignment.Left };
            bar.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceHover");
            row.Children.Add(bar);
            sketch.Children.Add(row);
        }
        body.Children.Add(sketch);

        card.Child = body;
        root.Children.Add(card);
        Content = PageLayout.Scroll(root, 980);
    }

    private static Binding Tr(string key) => new($"[{key}]") { Source = Loc.I, Mode = BindingMode.OneWay };
}
