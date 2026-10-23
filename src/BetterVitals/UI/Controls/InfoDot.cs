using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterVitals.UI.Explain;

namespace BetterVitals.UI.Controls;

/// <summary>
/// A small circled "i" (BetterShot's cue that hovering explains something). Lights up in the accent color with a tiny
/// bounce on hover; the text comes in the app's explanation bubble (<see cref="Explain"/>), not a ToolTip.
/// </summary>
public sealed class InfoDot : Border
{
    /// <summary>Loc key of the explanation.</summary>
    public static readonly DependencyProperty TextKeyProperty = DependencyProperty.Register(
        nameof(TextKey), typeof(string), typeof(InfoDot), new PropertyMetadata("", (d, e) => Explain.Explain.SetKey(d, (string?)e.NewValue)));

    public string TextKey { get => (string)GetValue(TextKeyProperty); set => SetValue(TextKeyProperty, value); }

    private readonly TextBlock _glyph;

    public InfoDot()
    {
        Width = 16;
        Height = 16;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1.25);
        Margin = new Thickness(7, 0, 0, 0);
        VerticalAlignment = VerticalAlignment.Center;
        Cursor = Cursors.Help;
        Background = Brushes.Transparent;
        _glyph = new TextBlock
        {
            Text = "i",
            FontFamily = new FontFamily("Georgia, Segoe UI"),
            FontStyle = FontStyles.Italic,
            FontWeight = FontWeights.Bold,
            FontSize = 10.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0),
        };
        Child = _glyph;
        Idle();
        MouseEnter += (_, _) =>
        {
            SetResourceReference(BorderBrushProperty, "Brush.AccentText");
            _glyph.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentText");
            Motion.Bounce(this, 1.25);
        };
        MouseLeave += (_, _) => Idle();
    }

    private void Idle()
    {
        SetResourceReference(BorderBrushProperty, "Brush.TextFaint");
        _glyph.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
    }
}
