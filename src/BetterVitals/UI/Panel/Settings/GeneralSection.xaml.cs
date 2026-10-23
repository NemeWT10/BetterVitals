using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.UI.Themes;

namespace BetterVitals.UI.Panel;

/// <summary>Settings ▸ General: language, theme, animations, explanations. Every change applies at once (no "Save").</summary>
public partial class GeneralSection : UserControl
{
    private readonly Dictionary<RadioButton, string> _themeIds = new();
    private bool _syncing;

    public GeneralSection()
    {
        InitializeComponent();
        BuildThemeTiles();
        Sync();
        Loaded += (_, _) =>
        {
            if (SettingsStore.I is { } s) s.Changed += OnSettingsChanged;
            Sync();
        };
        Unloaded += (_, _) =>
        {
            if (SettingsStore.I is { } s) s.Changed -= OnSettingsChanged;
        };
    }

    private static GeneralSettings General => SettingsStore.I.Current.General;

    private void BuildThemeTiles()
    {
        Themes.Children.Add(Tile("system", "theme.system", Preview(Palettes.Aurora, Palettes.Daylight)));
        foreach (var p in Palettes.All) Themes.Children.Add(Tile(p.Id, p.NameKey, Preview(p, null)));
    }

    private RadioButton Tile(string id, string nameKey, UIElement preview)
    {
        var tile = new RadioButton { GroupName = "Theme", Content = preview };
        tile.SetResourceReference(StyleProperty, "ThemeTile");
        tile.SetBinding(TagProperty, new Binding($"[{nameKey}]") { Source = Loc.I, Mode = BindingMode.OneWay });
        AutomationPropertiesName(tile, nameKey);
        tile.Checked += Theme_Checked;
        _themeIds[tile] = id;
        return tile;
    }

    private static void AutomationPropertiesName(DependencyObject d, string key)
        => BindingOperations.SetBinding(d, System.Windows.Automation.AutomationProperties.NameProperty, new Binding($"[{key}]") { Source = Loc.I, Mode = BindingMode.OneWay });

    /// <summary>A tiny window in the palette's own colors (the "system" tile shows dark and light halves).</summary>
    private static UIElement Preview(ThemePalette p, ThemePalette? second)
    {
        var grid = new Grid { ClipToBounds = true };
        grid.Children.Add(Mini(p));
        if (second is not null)
        {
            var light = Mini(second);
            light.Clip = new PathGeometry([new PathFigure(new Point(98, 0), [new LineSegment(new Point(98, 64), false), new LineSegment(new Point(34, 64), false)], true)]);
            grid.Children.Add(light);
        }
        return grid;
    }

    private static FrameworkElement Mini(ThemePalette p)
    {
        Brush B(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
        var accent = new LinearGradientBrush(p.AccentGradient[0], p.AccentGradient[^1], 0);
        accent.Freeze();
        var root = new Border { CornerRadius = new CornerRadius(8), Background = B(p.Bg) };
        var canvas = new Canvas { Width = 98, Height = 64 };
        // nav rail stub + content card
        canvas.Children.Add(At(new Border { Width = 15, Height = 4, CornerRadius = new CornerRadius(2), Background = accent }, 7, 9));
        canvas.Children.Add(At(new Border { Width = 12, Height = 3, CornerRadius = new CornerRadius(1.5), Background = B(p.TextFaint) }, 7, 18));
        canvas.Children.Add(At(new Border { Width = 14, Height = 3, CornerRadius = new CornerRadius(1.5), Background = B(p.TextFaint) }, 7, 25));
        var card = new Border { Width = 64, Height = 48, CornerRadius = new CornerRadius(6), Background = B(p.Surface), BorderBrush = B(p.Border), BorderThickness = new Thickness(1) };
        canvas.Children.Add(At(card, 28, 8));
        canvas.Children.Add(At(new Border { Width = 26, Height = 4, CornerRadius = new CornerRadius(2), Background = B(p.Text) }, 34, 14));
        canvas.Children.Add(At(new Border { Width = 40, Height = 3, CornerRadius = new CornerRadius(1.5), Background = B(p.TextDim) }, 34, 22));
        // a sparkline in the first two series colors
        var line = new System.Windows.Shapes.Polyline
        {
            Points = [new(34, 47), new(41, 41), new(48, 44), new(55, 35), new(62, 39), new(69, 31), new(86, 34)],
            Stroke = B(p.Series[0]),
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
        };
        canvas.Children.Add(line);
        canvas.Children.Add(At(new Border { Width = 18, Height = 6, CornerRadius = new CornerRadius(3), Background = accent }, 68, 13));
        root.Child = canvas;
        return root;
    }

    private static UIElement At(UIElement e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        return e;
    }

    private void Sync()
    {
        if (SettingsStore.I is null) return;
        _syncing = true;
        try
        {
            var g = General;
            foreach (var rb in Languages.Children.OfType<RadioButton>()) rb.IsChecked = (string)rb.Tag == (g.Language is "pl" or "en" ? g.Language : "system");
            var theme = Palettes.All.Any(p => p.Id == g.Theme) ? g.Theme : "system";
            foreach (var (rb, id) in _themeIds) rb.IsChecked = id == theme;
            AnimationsSwitch.IsChecked = g.Animations;
            ExplanationsSwitch.IsChecked = g.Explanations;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnSettingsChanged(string section)
    {
        if (section != "general") return;
        if (Dispatcher.CheckAccess()) Sync();
        else Dispatcher.BeginInvoke(Sync);
    }

    private void Language_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton { Tag: string lang } || General.Language == lang) return;
        SettingsStore.I.Mutate("general", s => s.General.Language = lang);
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton rb || !_themeIds.TryGetValue(rb, out var id) || General.Theme == id) return;
        SettingsStore.I.Mutate("general", s => s.General.Theme = id);
    }

    private void Animations_Click(object sender, RoutedEventArgs e)
    {
        var on = AnimationsSwitch.IsChecked == true;
        SettingsStore.I.Mutate("general", s => s.General.Animations = on);
    }

    private void Explanations_Click(object sender, RoutedEventArgs e)
    {
        var on = ExplanationsSwitch.IsChecked == true;
        SettingsStore.I.Mutate("general", s => s.General.Explanations = on);
    }
}
