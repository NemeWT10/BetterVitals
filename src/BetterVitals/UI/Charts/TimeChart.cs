using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BetterVitals.Core.History;
using BetterVitals.UI.Themes;

namespace BetterVitals.UI.Charts;

/// <summary>One line of a <see cref="TimeChart"/>: buckets (min / avg / max) in display units, sorted by time.</summary>
public sealed class ChartSeries
{
    public required string Id { get; init; }

    /// <summary>Verbatim sensor name (bubble).</summary>
    public required string Name { get; init; }

    /// <summary>Display unit; series with the same unit share an axis.</summary>
    public required string Unit { get; init; }

    public int Decimals { get; init; } = 1;

    /// <summary>Theme series color slot (Brush.Series.0 … 7).</summary>
    public int ColorIndex { get; init; }

    public bool Visible { get; set; } = true;

    public List<HistoryPoint> Points { get; set; } = [];

    /// <summary>Bucket width in ticks (points sit at the centre of their bucket); 0 = derive from the points.</summary>
    public long Bucket { get; set; }

    internal LiveBucket Live;

    internal long EffectiveBucket => Bucket > 0 ? Bucket : Math.Max(TimeSpan.TicksPerSecond, ChartMath.TypicalStep(CollectionsMarshal.AsSpan(Points)));

    /// <summary>Appends a live sample (merged into the last bucket while it lasts). True when something changed.</summary>
    public bool Append(long utc, float value) => ChartMath.AppendLive(Points, ref Live, utc, value, EffectiveBucket);
}

public enum ViewChangeReason : byte
{
    /// <summary>Wheel zoom or a pan drag.</summary>
    User,

    /// <summary>Set by the page (preset, live follow, jump).</summary>
    Program,
}

/// <summary>
/// The history chart (PROJEKT §6 / §8 / §9): a local-time X axis with smart ticks, one or more series each drawn as a
/// soft min–max envelope plus the average line, gaps never bridged, up to two value axes grouped by unit (a third
/// unit gets its own unlabelled scale), crosshair with a bubble, wheel zoom around the cursor, pan with the right
/// button or Shift+drag, drag-to-select, and smooth transitions (respects <see cref="Motion"/>).
/// <para>Cost: four <see cref="DrawingVisual"/> layers (grid, selection, series, crosshair) redrawn only when their
/// input changed — moving the mouse repaints the crosshair layer only; series geometry is one StreamGeometry per
/// series, decimated to device-pixel columns; no per-point elements, no timers (the render callback runs only while
/// a transition plays).</para>
/// </summary>
public sealed class TimeChart : FrameworkElement
{
    [Flags]
    private enum Layer : byte
    {
        None = 0,
        Grid = 1,
        Selection = 2,
        Series = 4,
        Overlay = 8,
        All = 15,
    }

    private const double TopPad = 22;
    private const double BottomPad = 26;
    private const double AxisFont = 11;
    private const double TransitionMs = 280;
    private const double ScaleMs = 220;

    private readonly VisualCollection _visuals;
    private readonly DrawingVisual _grid = new();
    private readonly DrawingVisual _selection = new();
    private readonly DrawingVisual _series = new();
    private readonly DrawingVisual _overlay = new();
    private readonly ChartText _text;
    private readonly List<(int Start, int End)> _segments = [];
    private readonly List<HistoryPoint> _decimated = [];
    private readonly Dictionary<string, ValueScale> _scaleFrom = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ValueScale> _scaleTo = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ValueScale> _scaleNow = new(StringComparer.Ordinal);
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private IReadOnlyList<ChartSeries> _list = [];
    private AxisSide[] _sides = [];
    private string? _leftUnit, _rightUnit;
    private TimeView _view;
    private TimeView _viewStart;
    private double _viewAnimAt = -1;
    private double _scaleAnimAt = -1;
    private double _fadeAt = -1;
    private bool _rendering;
    private Layer _dirty = Layer.All;
    private bool _scheduled;
    private Rect _plot;
    private Palette _p;
    private bool _paletteLoaded;
    private double _dpi = 1;

    // interaction
    private Point? _mouse;
    private long? _marker;
    private (long From, long To)? _selected;
    private double? _dragFrom;
    private double _dragTo;
    private Point _panFrom;
    private TimeView _panView;
    private bool _panning;

    public TimeChart()
    {
        _visuals = new VisualCollection(this) { _grid, _selection, _series, _overlay };
        _text = new ChartText(new FontFamily("Segoe UI Variable Text, Segoe UI"));
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        Cursor = Cursors.Cross;
        var now = Clock();
        _view = new TimeView(now - TimeSpan.TicksPerHour, now);
        Loaded += (_, _) =>
        {
            ThemeManager.Changed += OnThemeChanged;
            LoadPalette();
            Invalidate(Layer.All);
        };
        Unloaded += (_, _) =>
        {
            ThemeManager.Changed -= OnThemeChanged;
            StopRendering();
        };
    }

    // ───────────── public surface ─────────────

    /// <summary>"Now" in UTC ticks (the window never reaches past it). Tests replace it.</summary>
    public Func<long> Clock { get; set; } = static () => DateTime.UtcNow.Ticks;

    /// <summary>Time zone of the labels (data stays UTC).</summary>
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;

    /// <summary>Number / date culture of the labels (the UI language).</summary>
    public CultureInfo Culture { get; set; } = CultureInfo.CurrentCulture;

    /// <summary>Animate range changes (also requires Motion.I.Enabled and a loaded control).</summary>
    public bool Animations { get; set; } = true;

    /// <summary>The window follows "now": the newest point of each series gets a soft live dot.</summary>
    public bool IsLive
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Invalidate(Layer.Series);
        }
    }

    public IReadOnlyList<ChartSeries> Series => _list;

    /// <summary>The window being shown (the target, when a transition plays).</summary>
    public TimeView View => _view;

    /// <summary>The window drawn right now (mid-transition it is between the old and the new one).</summary>
    public TimeView ShownView => CurrentView();

    /// <summary>The plot area inside the control (axes around it).</summary>
    public Rect PlotRect => _plot;

    /// <summary>The drag selection (UTC ticks), null when there is none.</summary>
    public (long From, long To)? Selection => _selected;

    /// <summary>The pinned crosshair time (from "go to min / max"), null when the crosshair follows the mouse.</summary>
    public long? Marker => _marker;

    /// <summary>The crosshair is visible (mouse over the plot or a pinned marker).</summary>
    public bool CrosshairVisible => CrosshairTime() is not null;

    /// <summary>Duration of the last full redraw in ms (diagnostics, the perf test).</summary>
    public double LastRedrawMs { get; private set; }

    /// <summary>Units that got their own unlabelled scale (a third unit and more).</summary>
    public IReadOnlyList<string> OwnScaleUnits { get; private set; } = [];

    /// <summary>The user zoomed / panned, or the page moved the window.</summary>
    public event Action<TimeView, ViewChangeReason>? ViewChanged;

    /// <summary>A drag selection was made (or cleared with a click / Esc).</summary>
    public event Action<(long From, long To)?>? SelectionChanged;

    /// <summary>Replaces the series (their points may be filled later; call <see cref="DataChanged"/> then).</summary>
    public void SetSeries(IReadOnlyList<ChartSeries> series)
    {
        _list = series;
        var (sides, left, right, own) = ChartMath.AssignAxes(series.Select(s => s.Unit).ToList());
        _sides = sides;
        _leftUnit = left;
        _rightUnit = right;
        OwnScaleUnits = own;
        DataChanged(fade: false);
    }

    /// <summary>Points were replaced: rescale (smoothly) and redraw; <paramref name="fade"/> fades the lines in (new range).</summary>
    public void DataChanged(bool fade)
    {
        RetargetScales(animate: true);
        if (fade && CanAnimate)
        {
            _fadeAt = _clock.Elapsed.TotalMilliseconds;
            _series.Opacity = 0.2;
            StartRendering();
        }
        Invalidate(Layer.All);
    }

    /// <summary>A visibility toggle of a series (chip click).</summary>
    public void VisibilityChanged() => DataChanged(fade: false);

    /// <summary>Live samples were appended: redraw, rescale when they leave the scale.</summary>
    public void Appended()
    {
        RetargetScales(animate: true);
        Invalidate(Layer.Series | Layer.Grid | Layer.Overlay);
    }

    /// <summary>Moves the window (presets, live follow); <paramref name="animate"/> plays the range transition.</summary>
    public void SetView(TimeView view, bool animate)
    {
        if (view.Span <= 0) return;
        if (view == _view && _viewAnimAt < 0) return;
        if (animate && CanAnimate && _plot.Width > 0)
        {
            _viewStart = CurrentView();
            _viewAnimAt = _clock.Elapsed.TotalMilliseconds;
            _view = view;
            StartRendering();
        }
        else
        {
            _viewAnimAt = -1;
            _view = view;
        }
        RetargetScales(animate: true);
        Invalidate(Layer.All);
        ViewChanged?.Invoke(_view, ViewChangeReason.Program);
    }

    /// <summary>Sets / clears the selection from code (raises <see cref="SelectionChanged"/>).</summary>
    public void SetSelection((long From, long To)? range)
    {
        if (range is { } r && r.To <= r.From) range = null;
        if (_selected == range) return;
        _selected = range;
        Invalidate(Layer.Selection);
        SelectionChanged?.Invoke(range);
    }

    /// <summary>Pins the crosshair at a time (stats: "when was the max") until the mouse moves over the chart.</summary>
    public void ShowMarker(long utc)
    {
        _marker = utc;
        Invalidate(Layer.Overlay);
    }

    public void ClearMarker()
    {
        if (_marker is null) return;
        _marker = null;
        Invalidate(Layer.Overlay);
    }

    /// <summary>Redraws everything now (tests, perf measurement).</summary>
    public void RenderNow()
    {
        _dirty = Layer.All;
        Redraw();
    }

    // ───────────── input ─────────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        PointerMove(e.GetPosition(this));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_dragFrom is null && !_panning) PointerLeave();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (CaptureMouse()) e.Handled = true;
        PointerDown(e.GetPosition(this), pan: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        Focus();
        if (CaptureMouse()) e.Handled = true;
        PointerDown(e.GetPosition(this), pan: true);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        PointerUp(e.GetPosition(this));
        ReleaseMouseCapture();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        PointerUp(e.GetPosition(this));
        ReleaseMouseCapture();
        e.Handled = true; // no context menu after a pan
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragFrom is not null || _panning)
        {
            _dragFrom = null;
            _panning = false;
            Cursor = Cursors.Cross;
            Invalidate(Layer.Selection | Layer.Overlay);
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        Wheel(e.GetPosition(this), e.Delta);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _selected is not null)
        {
            SetSelection(null);
            e.Handled = true;
        }
    }

    /// <summary>Mouse moved to <paramref name="p"/> (also driven by tests).</summary>
    internal void PointerMove(Point p)
    {
        _mouse = p;
        _marker = null;
        if (_panning)
        {
            var dx = p.X - _panFrom.X;
            var delta = _plot.Width > 0 ? -(long)(dx / _plot.Width * _panView.Span) : 0;
            var v = ChartMath.Pan(_panView, delta, Clock());
            if (v != _view)
            {
                _viewAnimAt = -1;
                _view = v;
                RetargetScales(animate: true);
                Invalidate(Layer.All);
                ViewChanged?.Invoke(v, ViewChangeReason.User);
            }
            return;
        }
        if (_dragFrom is not null)
        {
            _dragTo = p.X;
            Invalidate(Layer.Selection | Layer.Overlay);
            return;
        }
        Invalidate(Layer.Overlay);
    }

    internal void PointerLeave()
    {
        _mouse = null;
        Invalidate(Layer.Overlay);
    }

    internal void PointerDown(Point p, bool pan)
    {
        _mouse = p;
        _marker = null;
        if (pan)
        {
            _panning = true;
            _panFrom = p;
            _panView = _view;
            Cursor = Cursors.ScrollWE;
            return;
        }
        if (!_plot.Contains(new Point(Math.Clamp(p.X, _plot.Left, _plot.Right), p.Y))) return;
        _dragFrom = p.X;
        _dragTo = p.X;
    }

    internal void PointerUp(Point p)
    {
        if (_panning)
        {
            _panning = false;
            Cursor = Cursors.Cross;
            return;
        }
        if (_dragFrom is not { } x1) return;
        _dragFrom = null;
        var range = ChartMath.SelectionRange(_view, _plot.Left, _plot.Width, x1, p.X);
        if (range is null)
        {
            // a click clears the selection
            if (_selected is not null) SetSelection(null);
            else Invalidate(Layer.Selection);
            return;
        }
        _selected = null; // force the event even for an equal range
        SetSelection(range);
    }

    internal void Wheel(Point p, int delta)
    {
        if (_plot.Width <= 0 || delta == 0) return;
        var anchor = (p.X - _plot.Left) / _plot.Width;
        var factor = Math.Pow(0.8, delta / 120.0);
        var v = ChartMath.ZoomAt(_view, anchor, factor, Clock());
        if (v == _view) return;
        _viewAnimAt = -1;
        _view = v;
        RetargetScales(animate: true);
        Invalidate(Layer.All);
        ViewChanged?.Invoke(v, ViewChangeReason.User);
    }

    // ───────────── layout & hit testing ─────────────

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
        => new Rect(RenderSize).Contains(hitTestParameters.HitPoint) ? new PointHitTestResult(this, hitTestParameters.HitPoint) : null!;

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 300 : availableSize.Height);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        RetargetScales(animate: false);
        Invalidate(Layer.All);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _text.Clear();
        Invalidate(Layer.All);
    }

    // ───────────── animation ─────────────

    private bool CanAnimate => Animations && Motion.I.Enabled && IsLoaded && IsVisible;

    private static double Ease(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

    private TimeView CurrentView()
    {
        if (_viewAnimAt < 0) return _view;
        var t = Ease((_clock.Elapsed.TotalMilliseconds - _viewAnimAt) / TransitionMs);
        if (t >= 1) return _view;
        // interpolate the span geometrically (zooming feels even), the end linearly
        var spanA = (double)_viewStart.Span;
        var spanB = (double)_view.Span;
        var span = spanA * Math.Pow(spanB / spanA, t);
        var to = _viewStart.To + (_view.To - _viewStart.To) * t;
        return new TimeView((long)(to - span), (long)to);
    }

    private void StartRendering()
    {
        if (_rendering) return;
        _rendering = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRendering()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnRendering;
        _viewAnimAt = _scaleAnimAt = _fadeAt = -1;
        _series.Opacity = 1;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        var busy = false;
        var layers = Layer.None;
        if (_viewAnimAt >= 0)
        {
            if (now - _viewAnimAt >= TransitionMs) _viewAnimAt = -1;
            else busy = true;
            layers |= Layer.All;
        }
        if (_scaleAnimAt >= 0)
        {
            if (now - _scaleAnimAt >= ScaleMs) _scaleAnimAt = -1;
            else busy = true;
            layers |= Layer.Grid | Layer.Series | Layer.Overlay;
        }
        if (_fadeAt >= 0)
        {
            var t = Ease((now - _fadeAt) / 220);
            _series.Opacity = 0.2 + 0.8 * t;
            if (t >= 1) _fadeAt = -1;
            else busy = true;
        }
        if (layers != Layer.None || _dirty != Layer.None)
        {
            _dirty |= layers;
            Redraw();
        }
        if (!busy) StopRendering();
    }

    // ───────────── scales ─────────────

    /// <summary>Recomputes where each unit's scale should end up (for the target window); tweens from the shown one.</summary>
    private void RetargetScales(bool animate)
    {
        var height = Math.Max(40, ActualHeight - TopPad - BottomPad);
        var current = new Dictionary<string, ValueScale>(_scaleNow, StringComparer.Ordinal);
        _scaleTo.Clear();
        var extents = new Dictionary<string, (double Lo, double Hi)>(StringComparer.Ordinal);
        foreach (var s in _list)
        {
            if (!s.Visible) continue;
            var (lo, hi) = ChartMath.Extent(CollectionsMarshal.AsSpan(s.Points), _view.From - s.EffectiveBucket, _view.To);
            if (double.IsNaN(lo)) continue;
            extents[s.Unit] = extents.TryGetValue(s.Unit, out var e) ? (Math.Min(e.Lo, lo), Math.Max(e.Hi, hi)) : (lo, hi);
        }
        foreach (var (unit, (lo, hi)) in extents) _scaleTo[unit] = ChartMath.Nice(lo, hi, height, unit);
        // two axes share the grid: the right labels sit on the left axis' lines
        if (_leftUnit is { } lu && _rightUnit is { } ru && extents.TryGetValue(lu, out var le) && extents.TryGetValue(ru, out var re))
            (_scaleTo[lu], _scaleTo[ru]) = ChartMath.Paired(le.Lo, le.Hi, lu, re.Lo, re.Hi, ru, height);

        _scaleFrom.Clear();
        foreach (var (unit, to) in _scaleTo) _scaleFrom[unit] = current.TryGetValue(unit, out var c) ? c : to;
        var changed = _scaleTo.Any(kv => !_scaleFrom.TryGetValue(kv.Key, out var f) || f != kv.Value);
        if (animate && changed && CanAnimate && _scaleFrom.Count > 0)
        {
            _scaleAnimAt = _clock.Elapsed.TotalMilliseconds;
            StartRendering();
        }
        else
        {
            _scaleAnimAt = -1;
        }
        UpdateShownScales();
    }

    private void UpdateShownScales()
    {
        var t = _scaleAnimAt < 0 ? 1 : Ease((_clock.Elapsed.TotalMilliseconds - _scaleAnimAt) / ScaleMs);
        _scaleNow.Clear();
        foreach (var (unit, to) in _scaleTo)
            _scaleNow[unit] = _scaleFrom.TryGetValue(unit, out var from) ? ValueScale.Lerp(from, to, t) : to;
    }

    /// <summary>The scale a unit is drawn with right now (tests).</summary>
    public ValueScale? ScaleOf(string unit) => _scaleNow.TryGetValue(unit, out var s) ? s : null;

    // ───────────── drawing ─────────────

    private void Invalidate(Layer layers)
    {
        _dirty |= layers;
        if (_scheduled || _rendering) return;
        _scheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _scheduled = false;
            Redraw();
        });
    }

    private void OnThemeChanged()
    {
        LoadPalette();
        _text.Clear();
        Invalidate(Layer.All);
    }

    private readonly record struct Palette(
        Color Grid, Color GridStrong, Color Axis, Color Cursor, Color Panel, Color PanelBorder, Color PanelText, Color PanelTextDim,
        Color Accent, Color Bg, bool Dark, Color[] Series);

    private void LoadPalette()
    {
        Color C(string key, Color fallback) => TryFindResource("Color." + key) is Color c ? c : fallback;
        var dark = TryFindResource("Theme.IsDark") is not bool b || b;
        var series = new Color[8];
        for (var i = 0; i < 8; i++) series[i] = C("Series." + i, Color.FromRgb(0x8B, 0x6C, 0xFF));
        var grid = C("Chart.Grid", Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
        _p = new Palette(
            grid,
            Color.FromArgb((byte)Math.Min(255, grid.A * 2 + 8), grid.R, grid.G, grid.B),
            C("Chart.Axis", Color.FromRgb(0x80, 0x88, 0x99)),
            C("Chart.Cursor", Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            C("PanelSolid", Color.FromRgb(0x1A, 0x1D, 0x26)),
            C("PanelBorder", Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            C("PanelText", Colors.White),
            C("PanelTextDim", Color.FromRgb(0xA0, 0xA6, 0xB4)),
            C("Accent", Color.FromRgb(0x8B, 0x6C, 0xFF)),
            C("Bg", Color.FromRgb(0x0F, 0x11, 0x17)),
            dark,
            series);
        _paletteLoaded = true;
    }

    private void Redraw()
    {
        if (!_paletteLoaded) LoadPalette();
        var sw = Stopwatch.StartNew();
        _dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _text.PixelsPerDip = _dpi;
        var view = CurrentView();
        UpdateShownScales();
        var layers = _dirty;
        _dirty = Layer.None;
        // the plot rectangle depends on the axis label widths: grid first
        if (layers.HasFlag(Layer.Grid) || _plot.IsEmpty || _plot.Width <= 0)
        {
            DrawGrid(view);
            layers |= Layer.Selection | Layer.Series | Layer.Overlay;
        }
        if (layers.HasFlag(Layer.Selection)) DrawSelection(view);
        if (layers.HasFlag(Layer.Series)) DrawSeries(view);
        if (layers.HasFlag(Layer.Overlay)) DrawOverlay(view);
        if ((layers & (Layer.Grid | Layer.Series)) != 0) LastRedrawMs = sw.Elapsed.TotalMilliseconds;
    }

    private string FormatValue(double v, int decimals) => v.ToString("F" + Math.Clamp(decimals, 0, 6), Culture);

    private Pen HairPen(Color c)
    {
        var pen = new Pen(new SolidColorBrush(c), 1 / _dpi);
        pen.Freeze();
        return pen;
    }

    /// <summary>A coordinate on the centre of a device pixel (crisp 1 px lines).</summary>
    private double Snap(double v) => (Math.Floor(v * _dpi) + 0.5) / _dpi;

    private void DrawGrid(TimeView view)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        using var dc = _grid.RenderOpen();
        if (w <= 0 || h <= 0)
        {
            _plot = Rect.Empty;
            return;
        }

        var left = _leftUnit is not null && _scaleNow.TryGetValue(_leftUnit, out var ls) ? ls : (ValueScale?)null;
        var right = _rightUnit is not null && _scaleNow.TryGetValue(_rightUnit, out var rs) ? rs : (ValueScale?)null;

        // axis label widths decide the plot rectangle
        double LabelWidth(ValueScale? s, string? unit)
        {
            if (s is not { } sc) return 0;
            var max = unit is null ? 0 : _text.Get(unit, AxisFont, _p.Axis).Width;
            foreach (var t in sc.Ticks()) max = Math.Max(max, _text.Get(FormatValue(t, sc.Decimals), AxisFont, _p.Axis).Width);
            return max;
        }
        var lw = LabelWidth(left, _leftUnit);
        var rw = LabelWidth(right, _rightUnit);
        var plotLeft = left is null ? 14 : Math.Ceiling(lw) + 14;
        var plotRight = w - (right is null ? 14 : Math.Ceiling(rw) + 14);
        _plot = new Rect(plotLeft, TopPad, Math.Max(10, plotRight - plotLeft), Math.Max(10, h - TopPad - BottomPad));

        var grid = HairPen(_p.Grid);
        var strong = HairPen(_p.GridStrong);
        var primary = left ?? right;

        // horizontal grid on the primary axis' ticks
        if (primary is { } ps)
        {
            foreach (var t in ps.Ticks())
            {
                var y = Snap(YOf(ps, t));
                dc.DrawLine(grid, new Point(_plot.Left, y), new Point(_plot.Right, y));
            }
        }
        else
        {
            // no data: a quiet frame of four lines
            for (var i = 0; i <= 4; i++)
            {
                var y = Snap(_plot.Top + _plot.Height * i / 4);
                dc.DrawLine(grid, new Point(_plot.Left, y), new Point(_plot.Right, y));
            }
        }

        // time ticks (label width decides the spacing)
        var sample = _text.Get(view.Span < TimeSpan.TicksPerMinute * 3 ? FormatSampleLong() : FormatSampleShort(), AxisFont, _p.Axis);
        var ticks = TimeTicks.Build(view.From, view.To, _plot.Width, Zone, Culture, Math.Max(64, sample.Width + 30));
        var lastRight = double.MinValue;
        foreach (var tick in ticks)
        {
            var x = Snap(ChartMath.XOf(view, _plot.Left, _plot.Width, tick.Utc));
            if (x < _plot.Left - 0.5 || x > _plot.Right + 0.5) continue;
            dc.DrawLine(tick.Major ? strong : grid, new Point(x, _plot.Top), new Point(x, _plot.Bottom));
            var label = _text.Get(tick.Label, AxisFont, tick.Major ? _p.PanelText : _p.Axis, tick.Major);
            var lx = Math.Clamp(x - label.Width / 2, 2, w - label.Width - 2);
            if (lx < lastRight + 8) continue;
            ChartText.Draw(dc, label, lx, _plot.Bottom + 6);
            lastRight = lx + label.Width;
        }

        // bottom line
        var by = Snap(_plot.Bottom);
        dc.DrawLine(strong, new Point(_plot.Left, by), new Point(_plot.Right, by));

        // value labels + units
        if (left is { } l)
        {
            foreach (var t in l.Ticks())
            {
                var label = _text.Get(FormatValue(t, l.Decimals), AxisFont, _p.Axis);
                ChartText.Draw(dc, label, _plot.Left - 8 - label.Width, YOf(l, t) - label.Height / 2);
            }
            var unit = _text.Get(_leftUnit!, AxisFont, _p.PanelTextDim, bold: true);
            ChartText.Draw(dc, unit, _plot.Left - 8 - unit.Width, 2);
        }
        if (right is { } r)
        {
            foreach (var t in r.Ticks())
            {
                var label = _text.Get(FormatValue(t, r.Decimals), AxisFont, _p.Axis);
                ChartText.Draw(dc, label, _plot.Right + 8, YOf(r, t) - label.Height / 2);
            }
            var unit = _text.Get(_rightUnit!, AxisFont, _p.PanelTextDim, bold: true);
            ChartText.Draw(dc, unit, _plot.Right + 8, 2);
        }
    }

    private string FormatSampleShort() => new DateTime(2026, 10, 23, 23, 58, 0).ToString(TimeTicks.ShortTime(Culture), Culture);

    private string FormatSampleLong() => new DateTime(2026, 10, 23, 23, 58, 58).ToString(TimeTicks.LongTime(Culture), Culture);

    private double YOf(ValueScale s, double v) => s.Span <= 0 ? _plot.Bottom : _plot.Bottom - (v - s.Min) / s.Span * _plot.Height;

    private ValueScale? ScaleFor(ChartSeries s) => _scaleNow.TryGetValue(s.Unit, out var sc) ? sc : null;

    private void DrawSelection(TimeView view)
    {
        using var dc = _selection.RenderOpen();
        if (_plot.Width <= 0) return;
        double a, b;
        if (_dragFrom is { } x1)
        {
            a = Math.Clamp(Math.Min(x1, _dragTo), _plot.Left, _plot.Right);
            b = Math.Clamp(Math.Max(x1, _dragTo), _plot.Left, _plot.Right);
        }
        else if (_selected is { } s)
        {
            a = Math.Clamp(ChartMath.XOf(view, _plot.Left, _plot.Width, s.From), _plot.Left, _plot.Right);
            b = Math.Clamp(ChartMath.XOf(view, _plot.Left, _plot.Width, s.To), _plot.Left, _plot.Right);
        }
        else return;
        if (b - a < 1) return;
        var fill = new SolidColorBrush(Color.FromArgb(_p.Dark ? (byte)0x1E : (byte)0x18, _p.Accent.R, _p.Accent.G, _p.Accent.B));
        fill.Freeze();
        dc.DrawRectangle(fill, null, new Rect(a, _plot.Top, b - a, _plot.Height));
        var edge = HairPen(Color.FromArgb(0xA0, _p.Accent.R, _p.Accent.G, _p.Accent.B));
        dc.DrawLine(edge, new Point(Snap(a), _plot.Top), new Point(Snap(a), _plot.Bottom));
        dc.DrawLine(edge, new Point(Snap(b), _plot.Top), new Point(Snap(b), _plot.Bottom));
        // small handles at the top
        var handle = new SolidColorBrush(_p.Accent);
        handle.Freeze();
        dc.DrawRoundedRectangle(handle, null, new Rect(Snap(a) - 1.5, _plot.Top - 1, 3, 9), 1.5, 1.5);
        dc.DrawRoundedRectangle(handle, null, new Rect(Snap(b) - 1.5, _plot.Top - 1, 3, 9), 1.5, 1.5);
    }

    private void DrawSeries(TimeView view)
    {
        using var dc = _series.RenderOpen();
        if (_plot.Width <= 0 || _list.Count == 0) return;
        dc.PushClip(new RectangleGeometry(new Rect(_plot.Left - 4, _plot.Top - 4, _plot.Width + 8, _plot.Height + 8)));
        for (var si = 0; si < _list.Count; si++)
        {
            var s = _list[si];
            if (!s.Visible || s.Points.Count == 0 || ScaleFor(s) is not { } scale) continue;
            var color = _p.Series[((s.ColorIndex % 8) + 8) % 8];
            var pts = CollectionsMarshal.AsSpan(s.Points);
            var bucket = s.EffectiveBucket;
            var half = bucket / 2;
            var start = Math.Max(0, ChartMath.LowerBound(pts, view.From - bucket * 2) - 1);
            var end = Math.Min(pts.Length, ChartMath.LowerBound(pts, view.To + bucket * 2) + 1);
            if (end <= start) continue;
            var visible = pts[start..end];
            ChartMath.Segments(visible, ChartMath.GapThreshold(bucket), _segments);

            var envelope = new StreamGeometry();
            var line = new StreamGeometry();
            var dots = new List<Point>();
            using (var ec = envelope.Open())
            using (var lc = line.Open())
            {
                foreach (var (a, b) in _segments)
                {
                    ChartMath.Decimate(visible, a, b, view, _plot.Left, _plot.Width, _dpi, half, _decimated);
                    var d = CollectionsMarshal.AsSpan(_decimated);
                    if (d.Length == 1)
                    {
                        dots.Add(new Point(ChartMath.XOf(view, _plot.Left, _plot.Width, d[0].TimeUtcTicks + half), YOf(scale, d[0].Avg)));
                        continue;
                    }
                    var spread = false;
                    for (var i = 0; i < d.Length; i++)
                    {
                        if (d[i].Max - d[i].Min > 0)
                        {
                            spread = true;
                            break;
                        }
                    }
                    if (spread)
                    {
                        ec.BeginFigure(P(view, scale, d[0].TimeUtcTicks + half, d[0].Max), true, true);
                        for (var i = 1; i < d.Length; i++) ec.LineTo(P(view, scale, d[i].TimeUtcTicks + half, d[i].Max), false, false);
                        for (var i = d.Length - 1; i >= 0; i--) ec.LineTo(P(view, scale, d[i].TimeUtcTicks + half, d[i].Min), false, false);
                    }
                    lc.BeginFigure(P(view, scale, d[0].TimeUtcTicks + half, d[0].Avg), false, false);
                    for (var i = 1; i < d.Length; i++) lc.LineTo(P(view, scale, d[i].TimeUtcTicks + half, d[i].Avg), true, true);
                }
            }
            envelope.Freeze();
            line.Freeze();
            var fill = new SolidColorBrush(Color.FromArgb(_p.Dark ? (byte)0x34 : (byte)0x2A, color.R, color.G, color.B));
            fill.Freeze();
            var stroke = new SolidColorBrush(color);
            stroke.Freeze();
            var pen = new Pen(stroke, 1.7) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            dc.DrawGeometry(fill, null, envelope);
            dc.DrawGeometry(null, pen, line);
            foreach (var p in dots) dc.DrawEllipse(stroke, null, p, 2.2, 2.2);

            if (IsLive)
            {
                var last = s.Points[^1];
                var lp = P(view, scale, last.TimeUtcTicks + half, last.Avg);
                if (lp.X >= _plot.Left && lp.X <= _plot.Right + 4)
                {
                    var halo = new SolidColorBrush(Color.FromArgb(0x40, color.R, color.G, color.B));
                    halo.Freeze();
                    dc.DrawEllipse(halo, null, lp, 6.5, 6.5);
                    dc.DrawEllipse(stroke, null, lp, 3, 3);
                }
            }
        }
        dc.Pop();
    }

    private Point P(TimeView v, ValueScale s, long t, double value) => new(ChartMath.XOf(v, _plot.Left, _plot.Width, t), YOf(s, value));

    // ───────────── crosshair & bubble ─────────────

    private long? CrosshairTime()
    {
        if (_marker is { } m) return m;
        if (_mouse is not { } p || _panning || _dragFrom is not null || _plot.Width <= 0) return null;
        if (p.X < _plot.Left || p.X > _plot.Right || p.Y < _plot.Top - 6 || p.Y > _plot.Bottom + 6) return null;
        return ChartMath.TimeAt(CurrentView(), _plot.Left, _plot.Width, p.X);
    }

    /// <summary>The bucket of each visible series under the crosshair (index or -1), for the bubble and tests.</summary>
    public IReadOnlyList<(ChartSeries Series, int Index)> HitTest(long t)
    {
        var view = CurrentView();
        var result = new List<(ChartSeries, int)>();
        var pxTicks = _plot.Width > 0 ? (long)(view.Span / _plot.Width) : 0;
        foreach (var s in _list)
        {
            if (!s.Visible) continue;
            var bucket = s.EffectiveBucket;
            var i = ChartMath.Nearest(CollectionsMarshal.AsSpan(s.Points), t, bucket, Math.Max(bucket, pxTicks * 6));
            result.Add((s, i));
        }
        return result;
    }

    private void DrawOverlay(TimeView view)
    {
        using var dc = _overlay.RenderOpen();
        if (CrosshairTime() is not { } t || _plot.Width <= 0) return;
        var hits = HitTest(t);
        // snap the line to the nearest bucket of the first series that has one
        var snapT = t;
        long bucketOfSnap = 0;
        foreach (var (s, i) in hits)
        {
            if (i < 0) continue;
            bucketOfSnap = s.EffectiveBucket;
            snapT = s.Points[i].TimeUtcTicks + bucketOfSnap / 2;
            break;
        }
        var x = Snap(ChartMath.XOf(view, _plot.Left, _plot.Width, snapT));
        if (x < _plot.Left - 1 || x > _plot.Right + 1) return;
        var pen = HairPen(_p.Cursor);
        dc.DrawLine(pen, new Point(x, _plot.Top), new Point(x, _plot.Bottom));

        var ring = new SolidColorBrush(_p.Bg);
        ring.Freeze();
        var rows = new List<(Color Color, string Name, string Value, string? Range)>();
        foreach (var (s, i) in hits)
        {
            if (i < 0 || ScaleFor(s) is not { } scale) continue;
            var pt = s.Points[i];
            var color = _p.Series[((s.ColorIndex % 8) + 8) % 8];
            var b = new SolidColorBrush(color);
            b.Freeze();
            var y = YOf(scale, pt.Avg);
            dc.DrawEllipse(ring, null, new Point(x, y), 5, 5);
            dc.DrawEllipse(b, null, new Point(x, y), 3.4, 3.4);
            var value = FormatValue(pt.Avg, s.Decimals) + " " + s.Unit;
            string? range = pt.Max > pt.Min ? FormatValue(pt.Min, s.Decimals) + "–" + FormatValue(pt.Max, s.Decimals) : null;
            rows.Add((color, s.Name, value.TrimEnd(), range));
        }
        DrawBubble(dc, x, snapT, bucketOfSnap, rows);
    }

    private void DrawBubble(DrawingContext dc, double x, long centerUtc, long bucket, List<(Color Color, string Name, string Value, string? Range)> rows)
    {
        const double pad = 11, rowH = 19, dot = 8, nameMax = 190;
        var title = _text.Get(BubbleTime(centerUtc, bucket), 11.5, _p.PanelTextDim, bold: true);
        double nameW = 0, valueW = 0, rangeW = 0;
        var names = new List<ChartText.Run>(rows.Count);
        var values = new List<ChartText.Run>(rows.Count);
        var ranges = new List<ChartText.Run?>(rows.Count);
        foreach (var r in rows)
        {
            var n = _text.Get(Fit(r.Name, 12, nameMax), 12, _p.PanelText);
            var v = _text.Get(r.Value, 12, _p.PanelText, bold: true);
            ChartText.Run? g = r.Range is null ? null : _text.Get(r.Range, 11, _p.PanelTextDim);
            names.Add(n);
            values.Add(v);
            ranges.Add(g);
            nameW = Math.Max(nameW, n.Width);
            valueW = Math.Max(valueW, v.Width);
            if (g is { } gr) rangeW = Math.Max(rangeW, gr.Width);
        }
        var width = Math.Max(title.Width, dot + 7 + nameW + 18 + valueW + (rangeW > 0 ? 10 + rangeW : 0)) + pad * 2;
        var height = pad + title.Height + (rows.Count > 0 ? 6 + rows.Count * rowH : 0) + pad - 3;
        var bx = x + 14;
        if (bx + width > ActualWidth - 4) bx = x - 14 - width;
        bx = Math.Max(4, bx);
        var by = _plot.Top + 6;
        var rect = new Rect(Math.Round(bx), Math.Round(by), Math.Ceiling(width), Math.Ceiling(height));

        // soft shadow (two translucent layers, no effect) + the panel
        var shadow1 = new SolidColorBrush(Color.FromArgb(_p.Dark ? (byte)0x30 : (byte)0x14, 0, 0, 0));
        shadow1.Freeze();
        var shadow2 = new SolidColorBrush(Color.FromArgb(_p.Dark ? (byte)0x18 : (byte)0x0A, 0, 0, 0));
        shadow2.Freeze();
        dc.DrawRoundedRectangle(shadow2, null, new Rect(rect.X - 2, rect.Y + 1, rect.Width + 4, rect.Height + 6), 13, 13);
        dc.DrawRoundedRectangle(shadow1, null, new Rect(rect.X - 0.5, rect.Y + 1.5, rect.Width + 1, rect.Height + 2), 11, 11);
        var panel = new SolidColorBrush(_p.Panel);
        panel.Freeze();
        var border = new Pen(new SolidColorBrush(_p.PanelBorder), 1 / _dpi);
        border.Freeze();
        dc.DrawRoundedRectangle(panel, border, new Rect(Snap(rect.X), Snap(rect.Y), rect.Width, rect.Height), 10, 10);

        var cy = rect.Y + pad - 2;
        ChartText.Draw(dc, title, rect.X + pad, cy);
        cy += title.Height + 6;
        for (var i = 0; i < rows.Count; i++)
        {
            var b = new SolidColorBrush(rows[i].Color);
            b.Freeze();
            var rowTop = cy + i * rowH;
            dc.DrawRoundedRectangle(b, null, new Rect(rect.X + pad, rowTop + (rowH - dot) / 2 - 1, dot, dot), 2.5, 2.5);
            ChartText.Draw(dc, names[i], rect.X + pad + dot + 7, rowTop + (rowH - names[i].Height) / 2 - 1);
            var vx = rect.X + pad + dot + 7 + nameW + 18 + valueW - values[i].Width;
            ChartText.Draw(dc, values[i], vx, rowTop + (rowH - values[i].Height) / 2 - 1);
            if (ranges[i] is { } g) ChartText.Draw(dc, g, rect.X + pad + dot + 7 + nameW + 18 + valueW + 10, rowTop + (rowH - g.Height) / 2 - 1);
        }
    }

    /// <summary>Shortens a name with an ellipsis to fit <paramref name="max"/> px.</summary>
    private string Fit(string text, double size, double max)
    {
        if (_text.Get(text, size, _p.PanelText).Width <= max) return text;
        for (var n = text.Length - 1; n > 3; n--)
        {
            var t = text[..n].TrimEnd() + "…";
            if (_text.Get(t, size, _p.PanelText).Width <= max) return t;
        }
        return text;
    }

    /// <summary>"23 paź, 14:05:20" for a sample, "23 paź, 14:05 – 14:06" for a minute bucket, days for wider ones.</summary>
    internal string BubbleTime(long centerUtc, long bucket)
    {
        var start = centerUtc - bucket / 2;
        var a = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(Math.Max(0, start), DateTimeKind.Utc), Zone);
        var day = TimeTicks.DayPattern(Culture);
        if (bucket >= TimeSpan.TicksPerDay)
        {
            var e = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(start + bucket, DateTimeKind.Utc), Zone);
            return a.ToString(day, Culture) + " – " + e.ToString(day, Culture);
        }
        if (bucket >= TimeSpan.TicksPerMinute)
        {
            var e = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(start + bucket, DateTimeKind.Utc), Zone);
            var st = TimeTicks.ShortTime(Culture);
            return a.ToString(day, Culture) + ", " + a.ToString(st, Culture) + " – " + e.ToString(st, Culture);
        }
        var c = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(Math.Max(0, start), DateTimeKind.Utc), Zone);
        return c.ToString(day, Culture) + ", " + c.ToString(TimeTicks.LongTime(Culture), Culture);
    }
}
