using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterVitals.Core;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Localization;
using BetterVitals.Overlay;
using BetterVitals.Overlay.Fonts;
using BetterVitals.UI.Themes;

namespace BetterVitals.UI.Pages.OverlayEditor;

/// <summary>
/// Font samples IN each overlay font. WPF cannot load the fonts embedded in BetterVitals.Overlay, so each sample is a
/// tiny overlay rendered by the overlay renderer itself (<see cref="OverlayHost.RenderToBitmap"/>, worker thread) —
/// exactly what the overlay will draw. Cached per family + theme for the app's lifetime (a few KB each).
/// </summary>
internal static class FontSamples
{
    /// <summary>Rendered at 1.5× (crisp up to 150 % display scaling), shown at 1×.</summary>
    public const double Oversample = 1.5;

    private static readonly Dictionary<string, BitmapSource> Cache = new(StringComparer.Ordinal);
    private static readonly SensorDescriptor Sample = new()
    {
        Id = "cpu/sample/0/temperature/sample",
        HardwareId = "cpu/sample/0",
        Name = "Sample",
        Kind = SensorKind.Temperature,
        HardwareKind = HardwareKind.Cpu,
    };

    private static int _pending;

    /// <summary>Samples still rendering (tests wait for them).</summary>
    public static int Pending => Volatile.Read(ref _pending);

    /// <summary>Sets <paramref name="image"/>'s source now (cached) or when the render is done.</summary>
    public static void Load(string family, Image image)
    {
        var theme = ThemeManager.I.ResolveOverlayTheme();
        var key = family + "|" + ThemeManager.I.Current.Id;
        if (Cache.TryGetValue(key, out var cached))
        {
            Show(image, cached);
            return;
        }
        var layout = new OverlayLayout
        {
            Style = new OverlayStyle
            {
                FontFamily = family,
                BackgroundOpacity = 0,
                Border = false,
                Padding = 1,
                AutoScale = true,
            },
            Rows = [new OverlayRow { Items = [new OverlayItem { SensorIds = [Sample.Id], Label = family, Thresholds = [] }] }],
        };
        var values = new[] { new PreviewSensor(Sample, 54) };
        var culture = Loc.I.Culture;
        var dispatcher = image.Dispatcher;
        Interlocked.Increment(ref _pending);
        Task.Run(() =>
        {
            try
            {
                var bmp = OverlayHost.RenderToBitmap(layout, theme, values, (int)(OverlayPlacementMath.ReferenceHeight * Oversample), culture);
                var src = BitmapSource.Create(bmp.Width, bmp.Height, 96 * Oversample, 96 * Oversample, PixelFormats.Pbgra32, null, bmp.Pixels, bmp.Stride);
                src.Freeze();
                return src;
            }
            catch (Exception ex)
            {
                Log.Warn($"overlay editor: font sample '{family}' failed: {ex.Message}");
                return null;
            }
        }).ContinueWith(t =>
        {
            Interlocked.Decrement(ref _pending);
            dispatcher.BeginInvoke(() =>
            {
                if (t.Result is not { } src) return;
                Cache[key] = src;
                Show(image, src);
            });
        }, TaskScheduler.Default);
    }

    private static void Show(Image image, BitmapSource src)
    {
        // natural size at 96 DPI is pixels / 1.5; a narrow column shrinks it, never stretches it
        image.Source = src;
    }
}

/// <summary>Advanced mode, the overlay's global look.</summary>
internal sealed class StyleSection : StackPanel
{
    private readonly EditorContext _ctx;
    private readonly List<(string Family, RadioButton Button, Image Image)> _fonts = [];

    public StyleSection(EditorContext ctx)
    {
        _ctx = ctx;
        var style = ctx.Layout.Style;

        // ── font list, each sample drawn in its own font ──
        var fonts = new UniformGrid { Columns = 2 };
        var group = Ui.NewGroup();
        foreach (var family in BundledFonts.AllFamilies)
        {
            var image = new Image { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 18, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var content = new StackPanel();
            content.Children.Add(image);
            var sub = new TextBlock { FontSize = 11, Margin = new Thickness(0, 3, 0, 0) };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
            sub.SetBinding(TextBlock.TextProperty, Ui.Tr(BundledFonts.Find(family) is null ? "ovl.font.system" : "ovl.font.bundled"));
            content.Children.Add(sub);
            var rb = new RadioButton { GroupName = group, Content = content, IsChecked = string.Equals(style.FontFamily, family, StringComparison.OrdinalIgnoreCase) };
            rb.SetResourceReference(StyleProperty, "OvEd.Option");
            System.Windows.Automation.AutomationProperties.SetName(rb, family);
            var f = family;
            rb.Checked += (_, _) => _ctx.Model.Edit(l => l.Style.FontFamily = f);
            _fonts.Add((family, rb, image));
            fonts.Children.Add(rb);
        }
        Children.Add(Ui.Stacked("ovl.style.font", fonts, top: 0));
        var valueFont = ItemEditor.FontCombo(ctx, style.ValueFontFamily, v => _ctx.Model.Edit(l => l.Style.ValueFontFamily = v), "ovl.style.valueFont.same");
        Children.Add(Ui.Line("ovl.style.valueFont", valueFont, top: 4));

        // ── size ──
        Children.Add(Ui.Divider());
        var scale = new SliderLine(50, 200, style.ScalePercent, 5, v => v.ToString("0", Loc.I.Culture) + " %", v => _ctx.Defer(l => l.Style.ScalePercent = v));
        Children.Add(Ui.Line("ovl.style.auto", Ui.Switch(style.AutoScale, v => _ctx.Model.Edit(l => l.Style.AutoScale = v)), "ovl.style.auto.hint"));
        Children.Add(Ui.Line("ovl.style.size", scale, "ovl.style.size.hint"));
        var arrangement = new SegmentBar<OverlayArrangement>(
            [(OverlayArrangement.Vertical, "ovl.arr.vertical"), (OverlayArrangement.Horizontal, "ovl.arr.horizontal"), (OverlayArrangement.Compact, "ovl.arr.compact")],
            style.Arrangement, v => _ctx.Model.Edit(l => l.Style.Arrangement = v), fontSize: 12);
        Children.Add(Ui.Above("ovl.style.arrangement", arrangement, "ovl.style.arrangement.hint"));

        // ── background and frame ──
        Children.Add(Ui.Divider());
        var bg = ColorPopover.ColorButton(ctx, () => _ctx.Layout.Style.BackgroundColor, v => _ctx.Model.Edit(l => l.Style.BackgroundColor = v), themeColor: () => ThemeManager.I.Current.Bg);
        Children.Add(Ui.Line("ovl.style.background", bg));
        Children.Add(Ui.Line("ovl.style.opacity", new SliderLine(0, 100, Math.Round(style.BackgroundOpacity * 100), 5, v => v.ToString("0", Loc.I.Culture) + " %", v => _ctx.Defer(l => l.Style.BackgroundOpacity = v / 100))));
        Children.Add(Ui.Line("ovl.style.radius", new SliderLine(0, 24, style.CornerRadius, 1, Px, v => _ctx.Defer(l => l.Style.CornerRadius = v))));
        Children.Add(Ui.Line("ovl.style.padding", new SliderLine(0, 30, style.Padding, 1, Px, v => _ctx.Defer(l => l.Style.Padding = v))));
        Children.Add(Ui.Line("ovl.style.spacing", new SliderLine(0, 20, style.Spacing, 1, Px, v => _ctx.Defer(l => l.Style.Spacing = v))));
        Children.Add(Ui.Line("ovl.style.border", Ui.Switch(style.Border, v => _ctx.Model.Edit(l => l.Style.Border = v))));
        Children.Add(Ui.Line("ovl.style.shadow", Ui.Switch(style.TextShadow, v => _ctx.Model.Edit(l => l.Style.TextShadow = v)), "ovl.style.shadow.hint"));
        Children.Add(Ui.Line("ovl.style.units", Ui.Switch(style.ShowUnits, v => _ctx.Model.Edit(l => l.Style.ShowUnits = v))));

        // ── colors for every item (an item's own color wins) ──
        Children.Add(Ui.Divider());
        Children.Add(Ui.Line("ovl.style.labelColor", ColorPopover.ColorButton(ctx, () => _ctx.Layout.Style.LabelColor, v => _ctx.Model.Edit(l => l.Style.LabelColor = v), themeColor: () => ThemeManager.I.Current.Accent), "ovl.style.labelColor.hint"));
        Children.Add(Ui.Line("ovl.style.valueColor", ColorPopover.ColorButton(ctx, () => _ctx.Layout.Style.ValueColor, v => _ctx.Model.Edit(l => l.Style.ValueColor = v), themeColor: () => ThemeManager.I.Current.Text)));
        Children.Add(Ui.Line("ovl.style.unitColor", ColorPopover.ColorButton(ctx, () => _ctx.Layout.Style.UnitColor, v => _ctx.Model.Edit(l => l.Style.UnitColor = v), themeColor: () => ThemeManager.I.Current.TextDim)));

        LoadSamples();
    }

    /// <summary>(Re)renders the font samples (theme or language changed).</summary>
    public void LoadSamples()
    {
        foreach (var (family, _, image) in _fonts) FontSamples.Load(family, image);
    }

    /// <summary>Font samples still rendering (tests wait for them).</summary>
    public bool SamplesReady => _fonts.All(f => f.Image.Source is not null);

    private static string Px(double v) => v.ToString("0", CultureInfo.CurrentCulture) + " px";
}
