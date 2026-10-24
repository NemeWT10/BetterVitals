using System.Windows;
using System.Windows.Media;

namespace BetterVitals.UI.Controls;

/// <summary>
/// A tiny live chart: the last <see cref="Capacity"/> values as a smooth line over a soft fade, in <see cref="Stroke"/>.
/// Values are pushed (no timer, no binding churn); one push = one redraw (deferred while scrolled out of view). NaN leaves a
/// gap. Draws nothing until it has two points.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((Sparkline)d)._fill = null));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Optional fixed range; NaN = auto from the visible values (with some headroom).</summary>
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The values to draw (a <see cref="SparkSeries"/>; the view model owns it, the control only reads it).</summary>
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(SparkSeries), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));

    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }

    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public SparkSeries? Series { get => (SparkSeries?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }

    private Brush? _fill;
    private System.Windows.Controls.ScrollViewer? _scroller;
    private bool _stale;

    public Sparkline()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) =>
        {
            _scroller = FindScroller();
            if (_scroller is not null) _scroller.ScrollChanged += OnScrollChanged;
            if (Series is not { } s) return;
            s.Changed -= OnPushed;
            s.Changed += OnPushed;
            if (_stale) OnPushed();
        };
        Unloaded += (_, _) =>
        {
            if (_scroller is not null) _scroller.ScrollChanged -= OnScrollChanged;
            _scroller = null;
            if (Series is { } s) s.Changed -= OnPushed;
        };
    }

    private static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (Sparkline)d;
        if (!self.IsLoaded) return;
        if (e.OldValue is SparkSeries o) o.Changed -= self.OnPushed;
        if (e.NewValue is SparkSeries n) n.Changed += self.OnPushed;
    }

    /// <summary>
    /// A new value: redrawn now when the line is on screen; scrolled out of its scroll viewer (the Overview's lower cards)
    /// it is only marked, and redrawn as it scrolls back in, before that frame is drawn (WPF would redraw it unseen,
    /// ~5 KB of garbage per redraw).
    /// </summary>
    private void OnPushed()
    {
        if (IsOutOfView())
        {
            _stale = true;
            return;
        }
        _stale = false;
        InvalidateVisual();
    }

    private void OnScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (_stale && !IsOutOfView())
        {
            _stale = false;
            InvalidateVisual();
        }
    }

    private bool IsOutOfView()
    {
        if (_scroller is not { } sv || !IsVisible || sv.ActualHeight <= 0) return false;
        try
        {
            var r = TransformToAncestor(sv).TransformBounds(new Rect(RenderSize));
            return r.Bottom < 0 || r.Top > sv.ActualHeight || r.Right < 0 || r.Left > sv.ActualWidth;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private System.Windows.Controls.ScrollViewer? FindScroller()
    {
        for (DependencyObject? d = VisualTreeHelper.GetParent(this); d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.ScrollViewer sv) return sv;
        return null;
    }

    /// <summary>Redraws so far (tests).</summary>
    internal int Renders { get; private set; }

    protected override void OnRender(DrawingContext dc)
    {
        Renders++;
        var s = Series;
        if (s is null || s.Count < 2 || ActualWidth <= 0 || ActualHeight <= 0) return;

        double lo = double.MaxValue, hi = double.MinValue;
        for (var i = 0; i < s.Count; i++)
        {
            var v = s[i];
            if (float.IsNaN(v)) continue;
            lo = Math.Min(lo, v);
            hi = Math.Max(hi, v);
        }
        if (lo == double.MaxValue) return;
        if (!double.IsNaN(Minimum)) lo = Minimum;
        if (!double.IsNaN(Maximum)) hi = Maximum;
        if (hi - lo < 1e-6)
        {
            hi += 1;
            lo -= 1;
        }
        var pad = (hi - lo) * 0.12;
        lo -= pad;
        hi += pad;

        var t = StrokeThickness;
        var w = ActualWidth;
        var h = ActualHeight - t;
        var step = w / (s.Capacity - 1);
        var x0 = w - (s.Count - 1) * step;
        double Y(float v) => t / 2 + h - (v - lo) / (hi - lo) * h;

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            var open = false;
            double lastX = 0;
            for (var i = 0; i < s.Count; i++)
            {
                var v = s[i];
                var x = x0 + i * step;
                if (float.IsNaN(v))
                {
                    if (open) ac.LineTo(new Point(lastX, ActualHeight), false, false);
                    open = false;
                    continue;
                }
                var p = new Point(x, Y(v));
                if (!open)
                {
                    lc.BeginFigure(p, false, false);
                    ac.BeginFigure(new Point(x, ActualHeight), true, true);
                    ac.LineTo(p, false, false);
                    open = true;
                }
                else
                {
                    lc.LineTo(p, true, true);
                    ac.LineTo(p, false, false);
                }
                lastX = x;
            }
            if (open) ac.LineTo(new Point(lastX, ActualHeight), false, false);
        }
        line.Freeze();
        area.Freeze();

        dc.DrawGeometry(_fill ??= FadeOf(Stroke), null, area);
        var pen = new Pen(Stroke, t) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, pen, line);
    }

    private static Brush FadeOf(Brush stroke)
    {
        var c = stroke is SolidColorBrush sc ? sc.Color : Colors.Gray;
        var g = new LinearGradientBrush(Color.FromArgb(0x55, c.R, c.G, c.B), Color.FromArgb(0x00, c.R, c.G, c.B), 90);
        g.Freeze();
        return g;
    }
}

/// <summary>A fixed-size ring of the latest values for a <see cref="Sparkline"/> (UI thread).</summary>
public sealed class SparkSeries
{
    private readonly float[] _ring;
    private int _start;

    public SparkSeries(int capacity = 60) => _ring = new float[Math.Max(2, capacity)];

    public int Capacity => _ring.Length;

    public int Count { get; private set; }

    /// <summary>i = 0 is the oldest value.</summary>
    public float this[int i] => _ring[(_start + i) % _ring.Length];

    /// <summary>A new value arrived (redraws the sparklines that show this series).</summary>
    public event Action? Changed;

    public void Push(float value)
    {
        Append(value);
        Changed?.Invoke();
    }

    /// <summary>Fills in older values at once (one redraw).</summary>
    public void PushRange(IEnumerable<float> values)
    {
        foreach (var v in values) Append(v);
        Changed?.Invoke();
    }

    public void Clear()
    {
        Count = 0;
        _start = 0;
        Changed?.Invoke();
    }

    private void Append(float value)
    {
        if (Count < _ring.Length)
        {
            _ring[(_start + Count) % _ring.Length] = value;
            Count++;
        }
        else
        {
            _ring[_start] = value;
            _start = (_start + 1) % _ring.Length;
        }
    }
}
