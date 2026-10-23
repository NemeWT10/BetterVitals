using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

namespace BetterVitals.UI.Charts;

/// <summary>
/// Text for code-drawn charts with <b>tabular figures</b> (FormattedText cannot ask for them): lines are laid out
/// once with a <see cref="TextFormatter"/>, recorded into frozen drawings and cached by (text, size, weight, color),
/// so a redraw only replays them. The cache is bounded and cleared on theme / DPI changes.
/// </summary>
internal sealed class ChartText
{
    public readonly record struct Run(Drawing Drawing, double Width, double Height, double Baseline);

    private readonly record struct Key(string Text, double Size, bool Bold, Color Color);

    private const int MaxEntries = 600;

    private readonly Dictionary<Key, Run> _cache = [];
    private readonly TextFormatter _formatter = TextFormatter.Create(TextFormattingMode.Ideal);
    private readonly Typeface _regular;
    private readonly Typeface _bold;
    private double _pixelsPerDip = 1;

    public ChartText(FontFamily family)
    {
        _regular = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _bold = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    }

    public double PixelsPerDip
    {
        get => _pixelsPerDip;
        set
        {
            if (Math.Abs(value - _pixelsPerDip) < 1e-6) return;
            _pixelsPerDip = value;
            _cache.Clear();
        }
    }

    public void Clear() => _cache.Clear();

    public int Count => _cache.Count;

    public Run Get(string text, double size, Color color, bool bold = false)
    {
        var key = new Key(text, size, bold, color);
        if (_cache.TryGetValue(key, out var run)) return run;
        if (_cache.Count >= MaxEntries) _cache.Clear();
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var props = new Props(bold ? _bold : _regular, size, brush);
        var source = new Source(text, props);
        var para = new Para(props);
        var group = new DrawingGroup();
        double width, height, baseline;
        using (var line = _formatter.FormatLine(source, 0, 100000, para, null))
        {
            using (var dc = group.Open()) line.Draw(dc, new Point(0, 0), InvertAxes.None);
            width = line.WidthIncludingTrailingWhitespace;
            height = line.Height;
            baseline = line.Baseline;
        }
        group.Freeze();
        run = new Run(group, width, height, baseline);
        _cache[key] = run;
        return run;
    }

    /// <summary>Draws at (x, y) = top-left of the line box.</summary>
    public static void Draw(DrawingContext dc, Run run, double x, double y)
    {
        dc.PushTransform(new TranslateTransform(x, y));
        dc.DrawDrawing(run.Drawing);
        dc.Pop();
    }

    private sealed class Source(string text, TextRunProperties props) : TextSource
    {
        public override TextRun GetTextRun(int textSourceCharacterIndex)
            => textSourceCharacterIndex < text.Length
                ? new TextCharacters(text, textSourceCharacterIndex, text.Length - textSourceCharacterIndex, props)
                : new TextEndOfParagraph(1);

        public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(int textSourceCharacterIndexLimit)
            => new(0, new CultureSpecificCharacterBufferRange(CultureInfo.InvariantCulture, new CharacterBufferRange(string.Empty, 0, 0)));

        public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int textSourceCharacterIndex) => textSourceCharacterIndex;
    }

    private sealed class Props(Typeface typeface, double size, Brush brush) : TextRunProperties
    {
        private static readonly Tabular Typo = new();

        public override Typeface Typeface => typeface;
        public override double FontRenderingEmSize => size;
        public override double FontHintingEmSize => size;
        public override TextDecorationCollection? TextDecorations => null;
        public override Brush ForegroundBrush => brush;
        public override Brush? BackgroundBrush => null;
        public override CultureInfo CultureInfo => CultureInfo.InvariantCulture;
        public override TextEffectCollection? TextEffects => null;
        public override TextRunTypographyProperties TypographyProperties => Typo;
    }

    private sealed class Para(TextRunProperties props) : TextParagraphProperties
    {
        public override FlowDirection FlowDirection => FlowDirection.LeftToRight;
        public override TextAlignment TextAlignment => TextAlignment.Left;
        public override double LineHeight => 0;
        public override bool FirstLineInParagraph => true;
        public override TextRunProperties DefaultTextRunProperties => props;
        public override TextWrapping TextWrapping => TextWrapping.NoWrap;
        public override TextMarkerProperties? TextMarkerProperties => null;
        public override double Indent => 0;
    }

    /// <summary>Default typography except tabular, lining figures (digits that never jump).</summary>
    private sealed class Tabular : TextRunTypographyProperties
    {
        public override bool StandardLigatures => true;
        public override bool ContextualLigatures => true;
        public override bool DiscretionaryLigatures => false;
        public override bool HistoricalLigatures => false;
        public override bool ContextualAlternates => true;
        public override bool HistoricalForms => false;
        public override bool Kerning => true;
        public override bool CapitalSpacing => false;
        public override bool CaseSensitiveForms => false;
        public override bool StylisticSet1 => false;
        public override bool StylisticSet2 => false;
        public override bool StylisticSet3 => false;
        public override bool StylisticSet4 => false;
        public override bool StylisticSet5 => false;
        public override bool StylisticSet6 => false;
        public override bool StylisticSet7 => false;
        public override bool StylisticSet8 => false;
        public override bool StylisticSet9 => false;
        public override bool StylisticSet10 => false;
        public override bool StylisticSet11 => false;
        public override bool StylisticSet12 => false;
        public override bool StylisticSet13 => false;
        public override bool StylisticSet14 => false;
        public override bool StylisticSet15 => false;
        public override bool StylisticSet16 => false;
        public override bool StylisticSet17 => false;
        public override bool StylisticSet18 => false;
        public override bool StylisticSet19 => false;
        public override bool StylisticSet20 => false;
        public override bool SlashedZero => false;
        public override bool MathematicalGreek => false;
        public override bool EastAsianExpertForms => false;
        public override FontVariants Variants => FontVariants.Normal;
        public override FontCapitals Capitals => FontCapitals.Normal;
        public override FontFraction Fraction => FontFraction.Normal;
        public override FontNumeralStyle NumeralStyle => FontNumeralStyle.Lining;
        public override FontNumeralAlignment NumeralAlignment => FontNumeralAlignment.Tabular;
        public override FontEastAsianWidths EastAsianWidths => FontEastAsianWidths.Normal;
        public override FontEastAsianLanguage EastAsianLanguage => FontEastAsianLanguage.Normal;
        public override int StandardSwashes => 0;
        public override int ContextualSwashes => 0;
        public override int StylisticAlternates => 0;
        public override int AnnotationAlternates => 0;
    }
}
