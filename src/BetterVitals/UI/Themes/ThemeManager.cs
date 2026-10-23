using System.Windows;
using System.Windows.Media;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using Microsoft.Win32;

namespace BetterVitals.UI.Themes;

/// <summary>
/// Turns a palette into one ResourceDictionary of Color.* / Brush.* keys and swaps it live; only the palette changes.
/// XAML uses {DynamicResource Brush.X}; code that paints by itself listens to <see cref="Changed"/> (attach in Loaded,
/// detach in Unloaded). Keys:
/// <list type="bullet">
/// <item>base: Accent, Accent2, AccentSoft, AccentGlow, AccentText, OnAccent, AccentGradient(H), AccentFill, Bg, BgAlt, Surface(Hover|Pressed),
/// Border(Strong), Text(Dim|Faint), Panel*, PanelSolid, Dim, Success / Danger / Warning / Info (+Soft), ToggleOff, WindowBg, Chrome, Layer, LayerBorder;</item>
/// <item>charts: Series.0 … Series.7, Chart.Grid, Chart.Axis, Chart.Cursor;</item>
/// <item>components: Cat.&lt;HardwareKind&gt; and CatSoft.&lt;HardwareKind&gt; (e.g. Brush.Cat.Gpu);</item>
/// <item>heat scale: Heat.Normal / Heat.Warm / Heat.Hot (= Success / Warning / Danger).</item>
/// </list>
/// </summary>
public sealed class ThemeManager
{
    public static ThemeManager I { get; } = new();

    private ResourceDictionary? _dictionary;
    private bool _micaRequested = true;

    public ThemePalette Current { get; private set; } = Palettes.Aurora;

    /// <summary>True when the panel window is drawn over the Mica backdrop.</summary>
    public bool MicaActive { get; private set; }

    /// <summary>The setting value the current palette was resolved from ("system" or an id).</summary>
    public string Setting { get; private set; } = "system";

    /// <summary>After the palette changed (UI thread).</summary>
    public static event Action? Changed;

    public void Apply(string? setting, bool mica)
    {
        Setting = string.IsNullOrWhiteSpace(setting) ? "system" : setting;
        _micaRequested = mica;
        var palette = Resolve(Setting);
        var micaActive = mica && WindowEffects.IsBackdropSupported;
        if (_dictionary is not null && palette == Current && micaActive == MicaActive) return;

        Current = palette;
        MicaActive = micaActive;
        var dict = Build(palette, micaActive);
        if (Application.Current is { } app)
        {
            var merged = app.Resources.MergedDictionaries;
            var index = _dictionary is null ? -1 : merged.IndexOf(_dictionary);
            if (index >= 0) merged[index] = dict;
            else merged.Insert(0, dict);
        }
        _dictionary = dict;
        Changed?.Invoke();
    }

    /// <summary>Re-evaluates "system" after Windows switched between light and dark.</summary>
    public void OnSystemThemeChanged()
    {
        if (Setting == "system") Apply(Setting, _micaRequested);
    }

    /// <summary>"system" → Aurora for dark Windows apps, Daylight for light; otherwise the id (unknown → Aurora).</summary>
    public static ThemePalette Resolve(string? setting)
        => setting is null or "system" ? (SystemUsesLightTheme() ? Palettes.Daylight : Palettes.Aurora) : Palettes.ById(setting);

    public static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Overlay colors of the active theme (handed to the native renderer, which knows nothing about WPF).</summary>
    public OverlayTheme ResolveOverlayTheme() => ToOverlayTheme(Current);

    public static OverlayTheme ToOverlayTheme(ThemePalette p) => new()
    {
        Background = Rgb(p.Bg),
        Border = Rgb(p.PanelBorder),
        Text = Rgb(p.Text),
        TextDim = Rgb(p.TextDim),
        Warning = Rgb(p.Warning),
        Danger = Rgb(p.Danger),
        Category = p.Category.ToDictionary(kv => kv.Key, kv => Rgb(kv.Value)),
    };

    private static Rgba Rgb(Color c) => new(c.R, c.G, c.B, c.A);

    /// <summary>Component color of the active theme (code-drawn visuals; XAML uses Brush.Cat.&lt;Kind&gt;).</summary>
    public Color CategoryColor(HardwareKind kind) => Current.Category.TryGetValue(kind, out var c) ? c : Current.TextDim;

    /// <summary>Chart series color i (wraps after 8).</summary>
    public Color SeriesColor(int i) => Current.Series[((i % Current.Series.Length) + Current.Series.Length) % Current.Series.Length];

    internal static ResourceDictionary Build(ThemePalette p, bool mica)
    {
        var d = new ResourceDictionary();

        void C(string name, Color c)
        {
            d["Color." + name] = c;
            var b = new SolidColorBrush(c);
            b.Freeze();
            d["Brush." + name] = b;
        }

        d["Color.Glow"] = p.Glow;
        d["Color.Shadow"] = p.Shadow;
        d["Theme.IsDark"] = p.IsDark;
        d["Shadow.Opacity"] = p.IsDark ? 0.45 : 0.16;

        C("Accent", p.Accent);
        C("Accent2", p.Accent2);
        C("AccentSoft", p.AccentSoft);
        C("AccentGlow", p.AccentGlow);
        C("AccentText", p.AccentText);
        C("OnAccent", p.OnAccent);
        d["Brush.AccentGradient"] = Gradient(p.AccentGradient, new Point(0, 0), new Point(1, 1));
        d["Brush.AccentGradientH"] = Gradient(p.AccentGradient, new Point(0, 0.5), new Point(1, 0.5));
        // the same gradient for surfaces that carry OnAccent text (buttons, selected segments): every stop stays readable
        d["Brush.AccentFill"] = Gradient(FillStops(p), new Point(0, 0), new Point(1, 1));

        C("Bg", p.Bg);
        C("BgAlt", p.BgAlt);
        C("Surface", p.Surface);
        C("SurfaceHover", p.SurfaceHover);
        C("SurfacePressed", p.SurfacePressed);
        C("Border", p.Border);
        C("BorderStrong", p.BorderStrong);
        C("Text", p.Text);
        C("TextDim", p.TextDim);
        C("TextFaint", p.TextFaint);

        C("Panel", p.Panel);
        C("PanelBorder", p.PanelBorder);
        C("PanelText", p.PanelText);
        C("PanelTextDim", p.PanelTextDim);
        C("PanelHover", p.PanelHover);
        C("PanelPressed", p.PanelPressed);
        C("PanelSeparator", p.PanelSeparator);
        // the panel color made opaque over the window background (bubbles over busy content stay readable)
        C("PanelSolid", Over(p.Panel, p.Bg));
        C("Dim", p.Dim);

        C("Success", p.Success);
        C("Danger", p.Danger);
        C("Warning", p.Warning);
        C("Info", p.Info);
        C("SuccessSoft", WithAlpha(p.Success, 0x26));
        C("DangerSoft", WithAlpha(p.Danger, 0x26));
        C("WarningSoft", WithAlpha(p.Warning, 0x26));
        C("InfoSoft", WithAlpha(p.Info, 0x26));
        C("ToggleOff", p.ToggleOff);

        C("Heat.Normal", p.Success);
        C("Heat.Warm", p.Warning);
        C("Heat.Hot", p.Danger);

        C("WindowBg", mica ? Colors.Transparent : p.Bg);
        C("Chrome", mica ? p.ChromeOverMica : p.ChromeSolid);
        // the page layer of the panel (a lighter sheet with a rounded top-left corner, as in Windows 11 Settings)
        C("Layer", p.IsDark ? WithAlpha(Colors.White, mica ? (byte)0x08 : (byte)0x05) : WithAlpha(Colors.White, mica ? (byte)0x90 : (byte)0xB8));
        C("LayerBorder", p.IsDark ? WithAlpha(Colors.White, 0x12) : WithAlpha(p.Shadow, 0x14));
        C("NavHover", p.IsDark ? WithAlpha(Colors.White, 0x0C) : WithAlpha(p.Shadow, 0x0A));
        C("NavSelected", p.IsDark ? WithAlpha(Colors.White, 0x12) : WithAlpha(Colors.White, 0xE6));

        for (var i = 0; i < p.Series.Length; i++) C("Series." + i, p.Series[i]);
        C("Chart.Grid", p.IsDark ? WithAlpha(Colors.White, 0x10) : WithAlpha(p.Shadow, 0x12));
        C("Chart.Axis", p.TextFaint);
        C("Chart.Cursor", WithAlpha(p.Text, 0x66));

        foreach (var kind in Enum.GetValues<HardwareKind>())
        {
            var c = p.Category.TryGetValue(kind, out var v) ? v : p.TextDim;
            C("Cat." + kind, c);
            C("CatSoft." + kind, WithAlpha(c, p.IsDark ? (byte)0x24 : (byte)0x1C));
        }

        return d;
    }

    private static LinearGradientBrush Gradient(Color[] stops, Point start, Point end)
    {
        var g = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        for (var i = 0; i < stops.Length; i++)
            g.GradientStops.Add(new GradientStop(stops[i], stops.Length == 1 ? 0 : (double)i / (stops.Length - 1)));
        g.Freeze();
        return g;
    }

    /// <summary>Accent gradient stops nudged (darker under light text, lighter under dark text) until OnAccent reads at 3.2:1.</summary>
    public static Color[] FillStops(ThemePalette p)
    {
        var toward = Luminance(p.OnAccent) > 0.5 ? Colors.Black : Colors.White;
        return p.AccentGradient.Select(stop =>
        {
            var c = stop;
            for (var t = 0.06; t <= 0.6 && Contrast(p.OnAccent, c) < 3.2; t += 0.06) c = Blend(stop, toward, t);
            return c;
        }).ToArray();
    }

    /// <summary>WCAG relative luminance.</summary>
    public static double Luminance(Color c)
    {
        static double L(byte v)
        {
            var x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
    }

    /// <summary>WCAG contrast ratio of two opaque colors.</summary>
    public static double Contrast(Color a, Color b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>Shadow color of the active theme, for effects built in code (Freezables cannot use DynamicResource).</summary>
    public static Color ShadowColor => I.Current.Shadow;

    /// <summary>A translucent color composited over an opaque one.</summary>
    public static Color Over(Color top, Color bottom)
    {
        var a = top.A / 255.0;
        byte L(byte t, byte b) => (byte)Math.Round(t * a + b * (1 - a));
        return Color.FromRgb(L(top.R, bottom.R), L(top.G, bottom.G), L(top.B, bottom.B));
    }

    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    public static Color Blend(Color a, Color b, double t)
    {
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromArgb(L(a.A, b.A), L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }
}
