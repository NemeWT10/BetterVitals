using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;

namespace BetterVitals.UI.Controls;

/// <summary>
/// A one-line text that changes every second (live values): looks and measures exactly like a <see cref="TextBlock"/>
/// with the same text properties, but each distinct text is laid out once and kept as a frozen drawing, so a value that
/// comes back (temperatures, loads) costs neither a text layout nor its ~10 KB of garbage. A text block lays out its
/// line again on every change. The drawing is made by a real TextBlock (<see cref="Capture"/>), so the pixels are the
/// same; the shared cache is emptied when the app goes to the tray.
/// Supports what live values use: plain text, left-aligned, no wrapping or trimming, inherited font, foreground,
/// numeral alignment, language and text formatting mode.
/// </summary>
public sealed class LiveText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(LiveText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(LiveText));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(LiveText));

    public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(typeof(LiveText));

    public static readonly DependencyProperty FontStyleProperty = TextElement.FontStyleProperty.AddOwner(typeof(LiveText));

    public static readonly DependencyProperty FontStretchProperty = TextElement.FontStretchProperty.AddOwner(typeof(LiveText));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(LiveText));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }

    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }

    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private Entry? _entry;

    /// <summary>Shared by every live text of the UI thread; emptied in the tray (<see cref="ClearCache"/>).</summary>
    [ThreadStatic]
    private static Dictionary<Key, Entry>? _cache;

    [ThreadStatic]
    private static Capture? _capture;

    private const int MaxEntries = 2048;

    /// <summary>Layouts done (cache misses) on this thread, for tests.</summary>
    [ThreadStatic]
    internal static int Layouts;

    private sealed record Entry(Size Size, Drawing? Drawing, double Baseline);

    private readonly record struct Key(
        string Text, FontFamily Family, double Size, FontWeight Weight, FontStyle Style, FontStretch Stretch, Brush? Brush,
        FontNumeralAlignment Numerals, XmlLanguage Language, TextFormattingMode Mode, FlowDirection Flow, double PixelsPerDip);

    /// <summary>Drops every cached text (the windows closed).</summary>
    public static void ClearCache()
    {
        _cache = null;
        _capture = null;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FontFamilyProperty || e.Property == FontSizeProperty || e.Property == FontWeightProperty ||
            e.Property == FontStyleProperty || e.Property == FontStretchProperty || e.Property == ForegroundProperty ||
            e.Property == Typography.NumeralAlignmentProperty || e.Property == LanguageProperty ||
            e.Property == TextOptions.TextFormattingModeProperty || e.Property == FlowDirectionProperty)
        {
            _entry = null;
            InvalidateMeasure();
            InvalidateVisual();
        }
        else if (e.Property == TextProperty)
        {
            _entry = null;
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _entry = null;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => Resolve().Size;

    protected override void OnRender(DrawingContext dc)
    {
        if (Resolve().Drawing is { } d) dc.DrawDrawing(d);
    }

    /// <summary>The baseline (like <see cref="TextBlock.BaselineOffset"/>).</summary>
    public double BaselineOffset => Resolve().Baseline;

    private Entry Resolve()
    {
        if (_entry is { } e) return e;
        var key = new Key(Text ?? "", FontFamily, FontSize, FontWeight, (FontStyle)GetValue(FontStyleProperty), (FontStretch)GetValue(FontStretchProperty),
            Foreground, Typography.GetNumeralAlignment(this), Language, TextOptions.GetTextFormattingMode(this), FlowDirection,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var cache = _cache ??= new Dictionary<Key, Entry>();
        if (!cache.TryGetValue(key, out e))
        {
            if (cache.Count >= MaxEntries) cache.Clear();
            e = (_capture ??= new Capture()).Make(key, this);
            cache[key] = e;
            Layouts++;
        }
        return _entry = e;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new LiveTextAutomationPeer(this);

    private sealed class LiveTextAutomationPeer(LiveText owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(TextBlock);

        protected override string GetNameCore() => ((LiveText)Owner).Text ?? "";

        protected override bool IsControlElementCore() => false;
    }

    /// <summary>A detached TextBlock that lays a text out and draws it into a frozen drawing.</summary>
    private sealed class Capture : TextBlock
    {
        public Entry Make(Key key, DependencyObject source)
        {
            Text = key.Text;
            FontFamily = key.Family;
            FontSize = key.Size;
            FontWeight = key.Weight;
            FontStyle = key.Style;
            FontStretch = key.Stretch;
            Foreground = key.Brush;
            Typography.SetNumeralAlignment(this, key.Numerals);
            Language = key.Language;
            TextOptions.SetTextFormattingMode(this, key.Mode);
            FlowDirection = key.Flow;
            VisualTreeHelper.SetRootDpi(this, VisualTreeHelper.GetDpi((Visual)source));
            Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = DesiredSize;
            Arrange(new Rect(size));
            var group = new DrawingGroup();
            using (var dc = group.Open()) OnRender(dc);
            Drawing? drawing = group.Children.Count == 0 ? null : group;
            if (drawing is not null && drawing.CanFreeze) drawing.Freeze();
            return new Entry(size, drawing, BaselineOffset);
        }
    }
}
