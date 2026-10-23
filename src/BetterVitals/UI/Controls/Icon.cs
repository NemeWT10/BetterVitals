using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace BetterVitals.UI.Controls;

/// <summary>
/// Line icon: a 24×24 geometry from Icons.xaml ({StaticResource Icon.Cpu}) scaled to the element's size and stroked with
/// the inherited Foreground (follows button states and themes). <see cref="StrokeThickness"/> is in device-independent
/// pixels, so every icon reads as the same ~1.5 px line whatever its size.
/// </summary>
public sealed class Icon : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(Icon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(Icon), new FrameworkPropertyMetadata(Brushes.Gray,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsFilledProperty = DependencyProperty.Register(
        nameof(IsFilled), typeof(bool), typeof(Icon),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(18.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }

    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// <summary>Line width in DIP (default 1.5).</summary>
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    /// <summary>Fill the geometry instead of stroking it (brand marks, dots).</summary>
    public bool IsFilled { get => (bool)GetValue(IsFilledProperty); set => SetValue(IsFilledProperty, value); }

    /// <summary>Square size in DIP when Width / Height are not set (default 18).</summary>
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    static Icon()
    {
        // defaults as metadata, not local values: a template or style must still be able to set them
        IsHitTestVisibleProperty.OverrideMetadata(typeof(Icon), new UIPropertyMetadata(false));
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(true));
        VerticalAlignmentProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(VerticalAlignment.Center));
        HorizontalAlignmentProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(HorizontalAlignment.Center));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsNaN(Width) ? Size : Width;
        var h = double.IsNaN(Height) ? Size : Height;
        return new Size(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Data is not { } geometry) return;
        var s = Math.Min(ActualWidth, ActualHeight) / 24.0;
        if (s <= 0) return;
        dc.PushTransform(new TranslateTransform((ActualWidth - 24 * s) / 2, (ActualHeight - 24 * s) / 2));
        dc.PushTransform(new ScaleTransform(s, s));
        if (IsFilled)
        {
            dc.DrawGeometry(Foreground, null, geometry);
        }
        else
        {
            var pen = new Pen(Foreground, StrokeThickness / s)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            dc.DrawGeometry(null, pen, geometry);
        }
        dc.Pop();
        dc.Pop();
    }
}
