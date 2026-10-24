using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using BetterVitals.Core.Catalog;
using BetterVitals.Localization;
using BetterVitals.UI.Controls;

namespace BetterVitals.UI.Pages.Shared;

/// <summary>Small builders shared by the Overview and Sensors pages (code-built views, theme-following brushes).</summary>
internal static class Ui
{
    [ThreadStatic]
    private static ResourceDictionary? _theme;

    /// <summary>PagesTheme.xaml, loaded once per UI thread (merge into a page's Resources).</summary>
    public static ResourceDictionary Theme
        => _theme ??= (ResourceDictionary)Application.LoadComponent(new Uri("/BetterVitals;component/UI/Pages/Shared/PagesTheme.xaml", UriKind.Relative));

    /// <summary>A live binding to a Loc key.</summary>
    public static Binding Tr(string key) => new($"[{key}]") { Source = Loc.I, Mode = BindingMode.OneWay };

    public static TextBlock Text(double size = 13, string brush = "Brush.Text", FontWeight? weight = null, string? key = null)
    {
        var t = new TextBlock { FontSize = size, FontWeight = weight ?? FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        if (key is not null) t.SetBinding(TextBlock.TextProperty, Tr(key));
        return t;
    }

    /// <summary>A numeric text with tabular figures (values never jump).</summary>
    public static TextBlock Number(double size = 13, string brush = "Brush.Text", FontWeight? weight = null)
    {
        var t = Text(size, brush, weight);
        t.SetValue(System.Windows.Documents.Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular);
        return t;
    }

    /// <summary>
    /// A live numeric value (tabular figures) that changes every second: looks like <see cref="Number"/>, but each
    /// distinct text is laid out only once (<see cref="LiveText"/>).
    /// </summary>
    public static LiveText LiveNumber(double size = 13, string brush = "Brush.Text", FontWeight? weight = null)
    {
        var t = new LiveText { FontSize = size, FontWeight = weight ?? FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(LiveText.ForegroundProperty, brush);
        t.SetValue(System.Windows.Documents.Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular);
        return t;
    }

    public static Icon Glyph(string iconKey, double size = 16, string? brush = null)
    {
        var i = new Icon { Size = size };
        i.SetResourceReference(Icon.DataProperty, iconKey);
        if (brush is not null) i.SetResourceReference(Icon.ForegroundProperty, brush);
        return i;
    }

    /// <summary>A rounded tile with a component icon (soft family color behind, family color line).</summary>
    public static Border IconTile(string iconKey, string brushKey, string softKey, double size = 36, double icon = 19)
    {
        var tile = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.3), VerticalAlignment = VerticalAlignment.Center };
        tile.SetResourceReference(Border.BackgroundProperty, softKey);
        tile.Child = Glyph(iconKey, icon, brushKey);
        return tile;
    }

    public static Style Style(string key) => (Style)Theme[key];

    /// <summary>Friendly parameter name ("Temperatura CPU"): the app's Loc table when someone registered it, else the catalog's own pair.</summary>
    public static string Friendly(string locKey)
    {
        if (Loc.Has(locKey)) return Loc.T(locKey);
        return FriendlyCatalog.Texts.TryGetValue(locKey, out var t) ? (Loc.I.IsPolish ? t.Pl : t.En) : locKey;
    }

    /// <summary>"1,7 MB" / "61 MB" / "820 KB" in the UI culture (decimal units, like Explorer's rounding).</summary>
    public static string Bytes(long bytes, CultureInfo culture)
    {
        double v = Math.Max(0, bytes);
        string unit;
        if (v >= 1e9) { v /= 1e9; unit = "GB"; }
        else if (v >= 1e6) { v /= 1e6; unit = "MB"; }
        else if (v >= 1e3) { v /= 1e3; unit = "KB"; }
        else return string.Create(culture, $"{v:0} B");
        var format = v >= 100 ? "0" : v >= 10 ? "0" : "0.0";
        return v.ToString(format, culture) + " " + unit;
    }
}
