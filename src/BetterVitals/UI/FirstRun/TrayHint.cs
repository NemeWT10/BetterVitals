using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BetterVitals.UI.Panel;

namespace BetterVitals.UI.Onboarding;

/// <summary>
/// A drawn (never captured) Windows 11 taskbar corner: the "^" overflow button, the hidden-icons flyout with the
/// BetterVitals icon, and an arrow showing the drag onto the taskbar, with numbered steps beside it. Theme colors only;
/// scales with its width. Used on the last first-run screen and in Settings ▸ Startup.
/// </summary>
internal sealed class TrayHint : Grid
{
    private const double W = 296;
    private const double H = 172;

    public TrayHint(bool withSteps = true)
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (withSteps) ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var art = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 12, 14, 12), BorderThickness = new Thickness(1) };
        art.SetResourceReference(Border.BackgroundProperty, "Brush.BgAlt");
        art.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        art.Child = new Viewbox { Child = Drawing(), Stretch = Stretch.Uniform, MaxHeight = 190 };
        Children.Add(art);

        if (!withSteps) return;
        var steps = new StackPanel { Margin = new Thickness(20, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        steps.Children.Add(Step("1", "firstrun.tray.step1"));
        steps.Children.Add(Step("2", "firstrun.tray.step2"));
        var alt = SettingsUi.Text("firstrun.tray.step3", 12, "Brush.TextFaint");
        alt.Margin = new Thickness(30, 4, 0, 0);
        steps.Children.Add(alt);
        SetColumn(steps, 1);
        Children.Add(steps);
    }

    private static FrameworkElement Step(string n, string key)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var badge = Badge(n);
        badge.VerticalAlignment = VerticalAlignment.Top;
        badge.Margin = new Thickness(0, 0, 10, 0);
        dock.Children.Add(badge);
        dock.Children.Add(SettingsUi.Text(key, 13, "Brush.Text"));
        return dock;
    }

    private static Border Badge(string n)
    {
        var b = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10) };
        b.SetResourceReference(Border.BackgroundProperty, "Brush.AccentFill");
        var t = new TextBlock { Text = n, FontSize = 11, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.OnAccent");
        b.Child = t;
        return b;
    }

    private static Canvas Drawing()
    {
        var c = new Canvas { Width = W, Height = H };

        // a floating taskbar along the bottom: a few app icons, then the tray corner
        c.Children.Add(Rect(0, 124, W, 40, 9, "Brush.Surface", "Brush.Border"));
        for (var i = 0; i < 3; i++)
            c.Children.Add(Rect(12 + i * 30, 133, 22, 22, 6, i == 0 ? "Brush.AccentSoft" : "Brush.SurfaceHover", "Brush.Border"));

        // "^" button, highlighted
        c.Children.Add(Rect(146, 131, 26, 26, 7, "Brush.AccentSoft", "Brush.Accent", 1.5));
        c.Children.Add(Line("M 153.5,147 L 159,141.5 L 164.5,147", "Brush.Text", 1.8));

        // the place the icon lands after the drag
        var slot = Rect(178, 132, 24, 24, 7, null, "Brush.AccentText", 1.4);
        slot.StrokeDashArray = [2.5, 2];
        c.Children.Add(slot);
        var landed = Logo(18);
        Place(landed, 181, 135);
        landed.Opacity = 0.55;
        c.Children.Add(landed);

        // network, volume, clock
        c.Children.Add(Line("M 213,141 A 9,9 0 0 1 227,141 M 216,144.5 A 5,5 0 0 1 224,144.5 M 219.5,148 L 220.5,148", "Brush.TextDim", 1.5));
        c.Children.Add(Line("M 235,141.5 H 238 L 242,138 V 150 L 238,146.5 H 235 Z M 245,141 A 4,4 0 0 1 245,147", "Brush.TextDim", 1.4));
        var time = new TextBlock { Text = "12:34", FontSize = 11, FontWeight = FontWeights.SemiBold };
        time.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        Place(time, 254, 136);
        c.Children.Add(time);

        // the hidden-icons flyout above the "^" button
        var flyout = Rect(92, 14, 132, 96, 12, "Brush.PanelSolid", "Brush.PanelBorder");
        flyout.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.3 };
        c.Children.Add(flyout);
        double[] xs = [108, 146, 184];
        double[] ys = [30, 68];
        foreach (var y in ys)
        {
            foreach (var x in xs)
            {
                if (x == 146 && y == 30) continue;
                c.Children.Add(Rect(x, y, 22, 22, 6, "Brush.SurfaceHover", "Brush.Border"));
            }
        }
        // ours, ringed
        c.Children.Add(Rect(142, 26, 30, 30, 9, "Brush.AccentSoft", "Brush.Accent", 1.6));
        var logo = Logo(22);
        Place(logo, 146, 30);
        c.Children.Add(logo);

        // the drag: from the flyout to the taskbar slot
        var arrow = Line("M 173,42 C 238,38 246,96 195,127", "Brush.AccentText", 1.8);
        arrow.StrokeDashArray = [3, 2.5];
        c.Children.Add(arrow);
        c.Children.Add(Line("M 192.5,118.5 L 194.5,127.5 L 203.5,126.5", "Brush.AccentText", 1.8));

        // step numbers
        var one = Badge("1");
        Place(one, 122, 134);
        c.Children.Add(one);
        var two = Badge("2");
        Place(two, 238, 70);
        c.Children.Add(two);
        return c;
    }

    private static Image Logo(double size)
    {
        var img = new Image { Width = size, Height = size };
        img.SetResourceReference(Image.SourceProperty, "Logo.Image");
        return img;
    }

    private static Rectangle Rect(double x, double y, double w, double h, double r, string? fill, string? stroke, double thickness = 1)
    {
        var rect = new Rectangle { Width = w, Height = h, RadiusX = r, RadiusY = r, StrokeThickness = thickness };
        if (fill is not null) rect.SetResourceReference(Shape.FillProperty, fill);
        if (stroke is not null) rect.SetResourceReference(Shape.StrokeProperty, stroke);
        Place(rect, x, y);
        return rect;
    }

    private static Path Line(string data, string brush, double thickness)
    {
        var p = new Path
        {
            Data = Geometry.Parse(data),
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        p.SetResourceReference(Shape.StrokeProperty, brush);
        return p;
    }

    private static void Place(UIElement e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
    }
}
