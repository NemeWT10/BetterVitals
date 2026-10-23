using System.Windows;
using System.Windows.Controls;
using BetterVitals.Core.Formatting;
using BetterVitals.Core.Sensors;
using BetterVitals.Localization;
using BetterVitals.Services;
using BetterVitals.UI.Controls;

namespace BetterVitals.UI.Dev;

/// <summary>The style gallery (snapshots only): base controls, sensor rows with the explanation hook, theme swatches.</summary>
public partial class GalleryPage : UserControl
{
    public const string Id = "dev-gallery";

    public GalleryPage()
    {
        InitializeComponent();
        Content = null;
        Content = Panel.PageLayout.Scroll(Body);
        var hub = AppServices.Hub;
        foreach (var s in hub.Sensors.Take(8)) Rows.Items.Add(Row(s, hub.Latest[s.Index]));
        Rows.SelectedIndex = 1;
        for (var i = 0; i < 8; i++) Swatches.Children.Add(Swatch("Brush.Series." + i));
        foreach (var kind in Enum.GetValues<HardwareKind>()) Swatches.Children.Add(Swatch("Brush.Cat." + kind));
        foreach (var heat in new[] { "Brush.Heat.Normal", "Brush.Heat.Warm", "Brush.Heat.Hot" }) Swatches.Children.Add(Swatch(heat));
    }

    private static FrameworkElement Row(SensorDescriptor s, float value)
    {
        var row = new DockPanel();
        var tile = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8) };
        Res.SetBackground(tile, "Brush.CatSoft." + s.HardwareKind);
        var icon = new Icon { Size = 16 };
        Res.SetIcon(icon, SensorPresentation.IconKey(s.Kind));
        Res.SetForeground(icon, "Brush.Cat." + s.HardwareKind);
        tile.Child = icon;
        row.Children.Add(tile);

        var toggle = new CheckBox { IsChecked = s.Index % 3 != 2, Margin = new Thickness(18, 0, 0, 0) };
        toggle.SetResourceReference(StyleProperty, "Switch");
        DockPanel.SetDock(toggle, Dock.Right);
        row.Children.Add(toggle);

        var text = ValueFormat.ToText(s.Kind, value, Loc.I.Culture, out var unit, baseUnit: s.Unit);
        var val = new TextBlock { Text = $"{text} {unit}", Width = 110, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
        val.SetResourceReference(StyleProperty, "Text.Value");
        val.FontSize = 14;
        Res.SetForeground(val, SensorPresentation.HeatBrushKey(SensorPresentation.Heat(s, value)));
        DockPanel.SetDock(val, Dock.Right);
        row.Children.Add(val);

        var names = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = s.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Left };
        Explain.Explain.SetSensor(name, s);
        names.Children.Add(name);
        var family = new TextBlock { Text = $"{SensorPresentation.FamilyLabel(s.HardwareKind)} · {SensorPresentation.KindLabel(s.Kind)}", FontSize = 11.5 };
        family.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
        names.Children.Add(family);
        row.Children.Add(names);
        return row;
    }

    private static FrameworkElement Swatch(string key)
    {
        var b = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 6, 6) };
        Res.SetBackground(b, key);
        return b;
    }
}
