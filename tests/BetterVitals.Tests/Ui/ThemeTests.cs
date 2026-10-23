using Xunit;
using System.Windows;
using System.Windows.Media;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.UI.Themes;

namespace BetterVitals.Tests.Ui;

[Collection(UiCollection.Name)]
public sealed class ThemeTests
{
    public static TheoryData<string> Ids => new(Palettes.All.Select(p => p.Id));

    [Fact]
    public void Five_themes_with_the_sibling_names()
        => Assert.Equal(["aurora", "graphite", "daylight", "neon", "sunset"], Palettes.All.Select(p => p.Id));

    [Theory]
    [MemberData(nameof(Ids))]
    public void Every_theme_has_eight_series_and_every_component_color(string id)
    {
        var p = Palettes.ById(id);
        Assert.Equal(8, p.Series.Length);
        Assert.Equal(8, p.Series.Distinct().Count());
        foreach (var kind in Enum.GetValues<HardwareKind>()) Assert.True(p.Category.ContainsKey(kind), $"{id}: no color for {kind}");
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Text_on_accent_fills_stays_readable(string id)
    {
        var p = Palettes.ById(id);
        foreach (var stop in ThemeManager.FillStops(p)) Assert.True(ThemeManager.Contrast(p.OnAccent, stop) >= 3.0, $"{id}: OnAccent on {stop}");
        Assert.True(ThemeManager.Contrast(p.Text, p.Bg) >= 7, $"{id}: text");
        Assert.True(ThemeManager.Contrast(p.TextDim, p.Surface) >= 4.5, $"{id}: dim text on cards");
    }

    [Fact]
    public void Aurora_overlay_theme_matches_the_core_default()
    {
        var resolved = ThemeManager.ToOverlayTheme(Palettes.Aurora);
        var d = OverlayTheme.Default;
        Assert.Equal(d.Background, resolved.Background);
        Assert.Equal(d.Border, resolved.Border);
        Assert.Equal(d.Text, resolved.Text);
        Assert.Equal(d.TextDim, resolved.TextDim);
        Assert.Equal(d.Warning, resolved.Warning);
        Assert.Equal(d.Danger, resolved.Danger);
        foreach (var (kind, color) in d.Category) Assert.Equal(color, resolved.CategoryColor(kind));
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Overlay_theme_carries_every_component(string id)
    {
        var t = ThemeManager.ToOverlayTheme(Palettes.ById(id));
        foreach (var kind in Enum.GetValues<HardwareKind>()) Assert.True(t.Category.ContainsKey(kind));
    }

    [Fact]
    public void Resolve_maps_system_and_unknown_ids()
    {
        Assert.Same(Palettes.Neon, ThemeManager.Resolve("neon"));
        Assert.Same(Palettes.Aurora, ThemeManager.Resolve("nonsense"));
        Assert.Contains(ThemeManager.Resolve("system"), new[] { Palettes.Aurora, Palettes.Daylight });
        Assert.Same(ThemeManager.SystemUsesLightTheme() ? Palettes.Daylight : Palettes.Aurora, ThemeManager.Resolve(null));
    }

    [Fact]
    public void Dictionary_has_the_tokens_pages_use() => Sta.Run(() =>
    {
        var d = ThemeManager.Build(Palettes.Daylight, mica: false);
        string[] keys =
        [
            "Brush.Accent", "Brush.OnAccent", "Brush.AccentFill", "Brush.AccentGradient", "Brush.Bg", "Brush.Surface", "Brush.Text",
            "Brush.TextDim", "Brush.Panel", "Brush.PanelSolid", "Brush.Layer", "Brush.Heat.Normal", "Brush.Heat.Warm", "Brush.Heat.Hot",
            "Brush.Chart.Grid", "Brush.Chart.Axis", "Color.Shadow",
        ];
        foreach (var k in keys) Assert.True(d.Contains(k), k);
        for (var i = 0; i < 8; i++) Assert.IsType<SolidColorBrush>(d["Brush.Series." + i]);
        foreach (var kind in Enum.GetValues<HardwareKind>())
        {
            Assert.True(d.Contains("Brush.Cat." + kind));
            Assert.True(d.Contains("Brush.CatSoft." + kind));
        }
        Assert.Equal(Palettes.Daylight.Warning, ((SolidColorBrush)d["Brush.Heat.Warm"]).Color);
        Assert.True(((Freezable)d["Brush.Text"]).IsFrozen);
    });
}
