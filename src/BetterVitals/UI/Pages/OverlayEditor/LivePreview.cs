using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using BetterVitals.Core;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.Overlay;
using BetterVitals.UI.Themes;

namespace BetterVitals.UI.Pages.OverlayEditor;

/// <summary>A screen size the preview shows; <see cref="IsMine"/> = the monitor the overlay is on.</summary>
internal sealed record PreviewResolution(string Id, int Width, int Height, bool IsMine = false)
{
    public static readonly PreviewResolution P1080 = new("1080p", 1920, 1080);
    public static readonly PreviewResolution P1440 = new("1440p", 2560, 1440);
    public static readonly PreviewResolution P4K = new("4K", 3840, 2160);
    public static readonly PreviewResolution P5K = new("5K", 5120, 2880);

    public static IReadOnlyList<PreviewResolution> Presets { get; } = [P1080, P1440, P4K, P5K];

    public double Aspect => (double)Width / Math.Max(1, Height);
}

/// <summary>Sizes a child to the available width and width / <see cref="Aspect"/> height (the monitor mock).</summary>
internal sealed class AspectBox : Decorator
{
    private double _aspect = 16.0 / 9;

    public double Aspect
    {
        get => _aspect;
        set
        {
            if (Math.Abs(_aspect - value) < 1e-6) return;
            _aspect = value;
            InvalidateMeasure();
        }
    }

    public double MaxBoxWidth { get; set; } = double.PositiveInfinity;

    protected override Size MeasureOverride(Size constraint)
    {
        var w = double.IsInfinity(constraint.Width) ? 360 : constraint.Width;
        w = Math.Min(w, MaxBoxWidth);
        var size = new Size(w, Math.Round(w / _aspect));
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var w = Math.Min(arrangeSize.Width, MaxBoxWidth);
        var h = Math.Round(w / _aspect);
        Child?.Arrange(new Rect((arrangeSize.Width - w) / 2, 0, w, h));
        return new Size(arrangeSize.Width, h);
    }
}

/// <summary>
/// The live preview: a monitor mock with the artificial game scene and the real overlay renderer's output for every window
/// (<see cref="OverlayHost.RenderWindowsToBitmaps"/> at the chosen screen's real height, shown scaled down — so it is
/// truthful about the share of the screen it takes), corner buttons (simple mode), dragging any window with snapping
/// (advanced mode, Shift = free), a resolution switch and a close-up of the main window at reference size. Renders on a
/// worker thread into one reused <see cref="WriteableBitmap"/> per window; only while the page runs it
/// (<see cref="Start"/> / <see cref="Stop"/>).
/// </summary>
internal sealed class LivePreview : StackPanel
{
    private const double MinRenderGapMs = 90;

    /// <summary>One window on the mock: its picture and where it was rendered.</summary>
    private sealed class PreviewBox(string id, Rectangle rect, ImageBrush brush)
    {
        public string Id { get; } = id;

        public Rectangle Rect { get; } = rect;

        public ImageBrush Brush { get; } = brush;

        public WriteableBitmap? Bitmap;
        public int W, H;
        public double Scale = 1;
        public int RenderedHeight;
    }

    private readonly EditorContext _ctx;
    private readonly AspectBox _box;
    private readonly Grid _screen;
    private readonly GameScene _scene;
    private readonly Canvas _layer;
    private readonly Rectangle _outline;
    private readonly Rectangle _guideX;
    private readonly Rectangle _guideY;
    private readonly ImageBrush _closeBrush;
    private readonly List<PreviewBox> _boxes = [];
    private readonly Dictionary<OverlayAnchor, RadioButton> _corners = [];
    private readonly Grid _cornerLayer;
    private readonly Border _emptyPill;
    private readonly SegmentBar<string> _resolutionBar;
    private readonly StackPanel _resolutionHost;
    private readonly TextBlock _caption;
    private readonly TextBlock _scaleNote;
    private readonly TextBlock _exampleNote;
    private readonly TextBlock _dragHint;
    private readonly Border _close;
    private readonly GameScene _closeScene;
    private readonly Rectangle _closeOverlay;
    private readonly Canvas _closeLayer;
    private readonly TextBlock _closeEmpty;
    private readonly TextBlock _closeTitle;
    private readonly DispatcherTimer _again;

    private bool _active;
    private bool _busy;
    private bool _pending;
    private DateTime _lastRender;
    private bool _failedLogged;
    private IReadOnlyList<OverlayMonitor> _monitors = [];
    private PreviewResolution? _mine;
    private PreviewResolution _selected = PreviewResolution.P1080;
    private bool _userPicked;
    private bool _dragEnabled;

    // dragging
    private PreviewBox? _dragging;
    private Point _dragStart;
    private (int X, int Y) _dragOrigin;
    private (int X, int Y) _dragPos;

    public LivePreview(EditorContext ctx)
    {
        _ctx = ctx;
        _again = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(MinRenderGapMs) };
        _again.Tick += (_, _) =>
        {
            _again.Stop();
            Render();
        };

        // ── header ──
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var live = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Shape.FillProperty, "Brush.Success");
        live.Children.Add(dot);
        live.Children.Add(Ui.Text("common.live", "Hint"));
        DockPanel.SetDock(live, Dock.Right);
        head.Children.Add(live);
        var title = Ui.Text("ovl.preview.title", size: 14.5);
        title.FontWeight = FontWeights.SemiBold;
        title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        head.Children.Add(title);
        Children.Add(head);

        // ── monitor mock ──
        _scene = new GameScene();
        _guideX = Guide(vertical: true);
        _guideY = Guide(vertical: false);
        _layer = new Canvas { ClipToBounds = true };
        _layer.Children.Add(_guideX);
        _layer.Children.Add(_guideY);
        _outline = new Rectangle { RadiusX = 3, RadiusY = 3, StrokeThickness = 1.5, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _outline.SetResourceReference(Shape.StrokeProperty, "Brush.Accent2");
        _layer.Children.Add(_outline);

        _cornerLayer = new Grid { Margin = new Thickness(8) };
        var group = Ui.NewGroup();
        foreach (var (anchor, icon, h, v) in new[]
                 {
                     (OverlayAnchor.TopLeft, "Icon.Anchor.TopLeft", HorizontalAlignment.Left, VerticalAlignment.Top),
                     (OverlayAnchor.TopRight, "Icon.Anchor.TopRight", HorizontalAlignment.Right, VerticalAlignment.Top),
                     (OverlayAnchor.BottomLeft, "Icon.Anchor.BottomLeft", HorizontalAlignment.Left, VerticalAlignment.Bottom),
                     (OverlayAnchor.BottomRight, "Icon.Anchor.BottomRight", HorizontalAlignment.Right, VerticalAlignment.Bottom),
                 })
        {
            var rb = new RadioButton { GroupName = group, HorizontalAlignment = h, VerticalAlignment = v, Content = Ui.InheritIcon(icon, 16, 1.6) };
            rb.SetResourceReference(StyleProperty, "OvEd.Corner");
            rb.SetBinding(ToolTipProperty, Ui.Tr("ovl.corner." + anchor));
            AutomationName(rb, "ovl.corner." + anchor);
            var a = anchor;
            rb.Click += (_, _) => CornerPicked?.Invoke(a);
            _corners[anchor] = rb;
            _cornerLayer.Children.Add(rb);
        }

        _emptyPill = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 6, 12, 7),
            Background = new SolidColorBrush(Color.FromArgb(0xB3, 0x10, 0x14, 0x20)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        var emptyText = Ui.Text("ovl.preview.empty", size: 12);
        emptyText.Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        _emptyPill.Child = emptyText;

        _screen = new Grid { ClipToBounds = true };
        _screen.Children.Add(_scene);
        _screen.Children.Add(_layer);
        _screen.Children.Add(_emptyPill);
        _screen.Children.Add(_cornerLayer);
        _screen.SizeChanged += (_, _) => Arrange();
        var screenBorder = new Border { CornerRadius = new CornerRadius(5), Child = _screen, ClipToBounds = true };
        _box = new AspectBox { Child = screenBorder };
        var bezel = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(5),
            Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0B, 0x10)),
            BorderThickness = new Thickness(1),
            Child = _box,
        };
        bezel.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderStrong");
        Children.Add(bezel);
        // a little stand under the bezel
        var stand = new Path
        {
            Data = Geometry.Parse("M0,0 L56,0 L50,10 L6,10 Z"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 0),
        };
        stand.SetResourceReference(Shape.FillProperty, "Brush.BorderStrong");
        Children.Add(stand);
        var foot = new Border { Width = 96, Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Center };
        foot.SetResourceReference(Border.BackgroundProperty, "Brush.BorderStrong");
        Children.Add(foot);

        _dragHint = Ui.Hint("ovl.preview.dragHint", new Thickness(0, 10, 0, 0));
        _dragHint.TextAlignment = TextAlignment.Center;
        _dragHint.Visibility = Visibility.Collapsed;
        Children.Add(_dragHint);

        // ── resolution ──
        _resolutionHost = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        _resolutionBar = new SegmentBar<string>(
            [.. PreviewResolution.Presets.Select(r => (r.Id, "=" + r.Id)), ("mine", "ovl.preview.mine")],
            _selected.Id, OnResolution, literal: false, fontSize: 12);
        _resolutionBar.ButtonOf("mine").Visibility = Visibility.Collapsed;
        _resolutionBar.HorizontalAlignment = HorizontalAlignment.Center;
        _resolutionHost.Children.Add(_resolutionBar);
        _caption = new TextBlock { FontSize = 12, Margin = new Thickness(0, 8, 0, 0), TextAlignment = TextAlignment.Center };
        _caption.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        System.Windows.Documents.Typography.SetNumeralAlignment(_caption, FontNumeralAlignment.Tabular);
        _resolutionHost.Children.Add(_caption);
        _scaleNote = new TextBlock { TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
        _scaleNote.SetResourceReference(StyleProperty, "Hint");
        _resolutionHost.Children.Add(_scaleNote);
        Children.Add(_resolutionHost);

        // ── close-up at reference size ──
        var closeTitle = _closeTitle = Ui.Text("ovl.preview.closeUp", "SectionTitle");
        closeTitle.Margin = new Thickness(2, 18, 0, 8);
        Children.Add(closeTitle);
        _closeScene = new GameScene();
        _closeBrush = new ImageBrush { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_closeBrush, BitmapScalingMode.HighQuality);
        _closeOverlay = new Rectangle { Fill = _closeBrush, IsHitTestVisible = false };
        _closeLayer = new Canvas { IsHitTestVisible = false };
        _closeLayer.Children.Add(_closeOverlay);
        _closeEmpty = Ui.Text("ovl.preview.closeEmpty", "Hint");
        _closeEmpty.Margin = new Thickness(16, 18, 16, 18);
        _closeEmpty.HorizontalAlignment = HorizontalAlignment.Center;
        _closeEmpty.Foreground = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));
        var closeGrid = new Grid { ClipToBounds = true };
        closeGrid.Children.Add(_closeScene);
        closeGrid.Children.Add(_closeLayer);
        closeGrid.Children.Add(_closeEmpty);
        _close = new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = closeGrid, HorizontalAlignment = HorizontalAlignment.Center, BorderThickness = new Thickness(1) };
        _close.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        Children.Add(_close);

        _exampleNote = Ui.Hint("ovl.preview.example", new Thickness(2, 10, 0, 0));
        _exampleNote.Visibility = Visibility.Collapsed;
        Children.Add(_exampleNote);

        SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.PreviousSize.Width - e.NewSize.Width) > 0.5) ArrangeCloseUp();
        };
        Unloaded += (_, _) => EndDrag(commit: false);
    }

    /// <summary>A corner button was clicked (simple mode).</summary>
    public event Action<OverlayAnchor>? CornerPicked;

    /// <summary>Simple mode: corner buttons on the mock.</summary>
    public bool CornersVisible
    {
        get => _cornerLayer.Visibility == Visibility.Visible;
        set => _cornerLayer.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Advanced mode: every window can be dragged on the mock.</summary>
    public bool DragEnabled
    {
        get => _dragEnabled;
        set
        {
            _dragEnabled = value;
            foreach (var b in _boxes) b.Rect.Cursor = value ? Cursors.SizeAll : Cursors.Arrow;
            _dragHint.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>The screen the preview shows.</summary>
    public PreviewResolution Resolution => _selected;

    /// <summary>The main window (or the first one with content): what the close-up shows.</summary>
    private PreviewBox? Lead => _boxes.Find(b => b.Id == OverlayLayout.MainWindowId && b.W > 0) ?? _boxes.Find(b => b.W > 0);

    /// <summary>Size of the last render of the main window (tests).</summary>
    public (int Width, int Height) RenderedSize => Lead is { } b ? (b.W, b.H) : (0, 0);

    /// <summary>Windows on the mock right now and their rendered size (tests).</summary>
    public IReadOnlyList<(string WindowId, int Width, int Height)> RenderedWindows => [.. _boxes.Where(b => b.W > 0).Select(b => (b.Id, b.W, b.H))];

    public int RenderCount { get; private set; }

    public bool IsRendering => _busy || _pending;

    public void Start()
    {
        _active = true;
        RequestRender();
    }

    public void Stop()
    {
        _active = false;
        _again.Stop();
        EndDrag(commit: false);
    }

    /// <summary>Monitors as the overlay sees them; "My monitor" follows the overlay's placement.</summary>
    public void SetMonitors(IReadOnlyList<OverlayMonitor> monitors)
    {
        _monitors = monitors;
        UpdateMine();
    }

    /// <summary>Re-evaluates "My monitor" (the placement's monitor may have changed).</summary>
    public void UpdateMine()
    {
        var m = OverlayPlacementMath.Resolve(_ctx.Layout.Placement, _monitors);
        _mine = m is null ? null : new PreviewResolution("mine", m.Bounds.Width, m.Bounds.Height, IsMine: true);
        var mineButton = _resolutionBar.ButtonOf("mine");
        mineButton.Visibility = _mine is null ? Visibility.Collapsed : Visibility.Visible;
        if (_mine is not null) mineButton.ToolTip = $"{m!.FriendlyName} · {_mine.Width} × {_mine.Height}";
        if (_mine is not null && (!_userPicked || _selected.IsMine)) _selected = _mine;
        else if (_mine is null && _selected.IsMine) _selected = PreviewResolution.P1080;
        _resolutionBar.Set(_selected.Id);
        _box.Aspect = _selected.Aspect;
        _scene.Aspect = _selected.Aspect;
        UpdateCaption();
        RequestRender();
    }

    /// <summary>Chooses the preview's screen size ("1080p", "1440p", "4K", "5K", "mine").</summary>
    public void SelectResolution(string id) => OnResolution(id);

    private void OnResolution(string id)
    {
        _userPicked = true;
        _selected = id == "mine" && _mine is not null ? _mine : PreviewResolution.Presets.FirstOrDefault(r => r.Id == id) ?? PreviewResolution.P1080;
        _resolutionBar.Set(_selected.Id);
        _box.Aspect = _selected.Aspect;
        _scene.Aspect = _selected.Aspect;
        UpdateCaption();
        RequestRender();
    }

    /// <summary>Syncs the corner buttons with the placement.</summary>
    public void SyncCorners()
    {
        // the chosen corner shows the overlay itself (outlined), the other three offer "move here"
        var corner = OverlayEditorModel.NearestCorner(_ctx.Layout.Placement.Anchor);
        foreach (var (a, b) in _corners)
        {
            b.IsChecked = a == corner;
            b.Visibility = a == corner && _ctx.Layout.Cells.Count > 0 ? Visibility.Hidden : Visibility.Visible;
        }
    }

    /// <summary>Asks for a new render (coalesced; at most one in flight, ~11 per second while dragging a slider).</summary>
    public void RequestRender()
    {
        if (!_active) return;
        if (_busy)
        {
            _pending = true;
            return;
        }
        var since = (DateTime.UtcNow - _lastRender).TotalMilliseconds;
        if (since < MinRenderGapMs)
        {
            _pending = true;
            if (!_again.IsEnabled) _again.Start();
            return;
        }
        Render();
    }

    private void Render()
    {
        if (!_active) return;
        _pending = false;
        var layout = LayoutJson.Clone(_ctx.Layout);
        var values = _ctx.Feed.Build(layout);
        var theme = ThemeManager.I.ResolveOverlayTheme();
        var culture = Loc.I.Culture;
        var unit = _ctx.TemperatureUnit;
        var height = _selected.Height;
        _exampleNote.Visibility = _ctx.Feed.UsesExampleValues(layout) ? Visibility.Visible : Visibility.Collapsed;
        UpdateCaption();
        if (layout.IsEmpty)
        {
            ShowEmpty(true);
            _lastRender = DateTime.UtcNow;
            return;
        }
        _busy = true;
        _lastRender = DateTime.UtcNow;
        var dispatcher = Dispatcher;
        Task.Run(() =>
        {
            try
            {
                return (Bitmaps: OverlayHost.RenderWindowsToBitmaps(layout, theme, values, height, culture, unit), Error: (Exception?)null);
            }
            catch (Exception ex)
            {
                return (Bitmaps: (IReadOnlyList<(string WindowId, OverlayBitmap Bitmap)>?)null, Error: ex);
            }
        }).ContinueWith(t =>
        {
            var (bitmaps, error) = t.Result;
            dispatcher.BeginInvoke(() =>
            {
                _busy = false;
                if (bitmaps is not null) Apply(bitmaps, OverlayPlacementMath.Scale(layout.Style, height), height);
                else if (!_failedLogged)
                {
                    _failedLogged = true;
                    Log.Error("overlay editor: preview render failed", error);
                }
                if (_pending) RequestRender();
            });
        }, TaskScheduler.Default);
    }

    private void Apply(IReadOnlyList<(string WindowId, OverlayBitmap Bitmap)> bitmaps, double scale, int monitorHeight)
    {
        // windows that are gone leave the mock
        foreach (var gone in _boxes.Where(b => !bitmaps.Any(x => x.WindowId == b.Id)).ToList())
        {
            if (_dragging == gone) EndDrag(commit: false);
            _layer.Children.Remove(gone.Rect);
            _boxes.Remove(gone);
        }
        foreach (var (id, bmp) in bitmaps)
        {
            if (bmp.Width <= 0 || bmp.Height <= 0) continue;
            var box = _boxes.Find(b => b.Id == id) ?? AddBox(id);
            if (box.Bitmap is null || box.Bitmap.PixelWidth < bmp.Width || box.Bitmap.PixelHeight < bmp.Height)
            {
                // grow-only, with headroom: a slider drag never allocates a new bitmap per frame
                var w = Math.Max(bmp.Width + bmp.Width / 4, box.Bitmap?.PixelWidth ?? 0);
                var h = Math.Max(bmp.Height + bmp.Height / 4, box.Bitmap?.PixelHeight ?? 0);
                box.Bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
                box.Brush.ImageSource = box.Bitmap;
            }
            box.Bitmap.WritePixels(new Int32Rect(0, 0, bmp.Width, bmp.Height), bmp.Pixels, bmp.Stride, 0);
            box.W = bmp.Width;
            box.H = bmp.Height;
            box.Scale = scale;
            box.RenderedHeight = monitorHeight;
            box.Brush.Viewbox = new Rect(0, 0, box.W, box.H);
        }
        // the main window (or the first one) in the close-up
        if (Lead is { } lead)
        {
            _closeBrush.ImageSource = lead.Bitmap;
            _closeBrush.Viewbox = new Rect(0, 0, lead.W, lead.H);
        }
        RenderCount++;
        ShowEmpty(false);
        Arrange();
    }

    private PreviewBox AddBox(string id)
    {
        var brush = new ImageBrush { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
        var rect = new Rectangle { Fill = brush, Cursor = _dragEnabled ? Cursors.SizeAll : Cursors.Arrow, Tag = id };
        var box = new PreviewBox(id, rect, brush);
        rect.MouseLeftButtonDown += (_, e) => OnDragStart(box, e);
        rect.MouseMove += (_, e) => OnDragMove(e);
        rect.MouseLeftButtonUp += (_, e) => OnDragEnd(e);
        rect.LostMouseCapture += (_, _) =>
        {
            if (_dragging == box) EndDrag(commit: false);
        };
        _boxes.Add(box);
        // the main box above the others, all below the outline
        _layer.Children.Insert(_layer.Children.IndexOf(_outline), rect);
        return box;
    }

    private void ShowEmpty(bool empty)
    {
        _emptyPill.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        foreach (var b in _boxes) b.Rect.Visibility = empty || b.W == 0 ? Visibility.Collapsed : Visibility.Visible;
        _closeLayer.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        _closeEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _close.Visibility = _closeTitle.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (empty) _outline.Visibility = Visibility.Collapsed;
        if (empty)
        {
            _close.Width = double.NaN;
            _close.Height = double.NaN;
            _closeScene.Viewport = new Rect(0.25, 0.42, 0.5, 0.2);
        }
    }

    // ───────────── geometry ─────────────

    /// <summary>Monitor pixels → mock DIPs.</summary>
    private double Factor => _screen.ActualWidth / Math.Max(1, _selected.Width);

    private PixelRect Area => new(0, 0, _selected.Width, _selected.Height);

    private OverlayPlacement PlacementOf(PreviewBox b) => _ctx.Layout.PlacementOf(b.Id) ?? _ctx.Layout.Placement;

    private (int X, int Y) PixelPosition(PreviewBox b)
    {
        if (_dragging == b) return _dragPos;
        // the bitmap may be from a different resolution than the one now selected (render pending): scale it
        return OverlayPlacementMath.Position(PlacementOf(b), Area, ScaledW(b), ScaledH(b), CurrentScale);
    }

    private double CurrentScale => OverlayPlacementMath.Scale(_ctx.Layout.Style, _selected.Height);

    private int ScaledW(PreviewBox b) => b.RenderedHeight == _selected.Height ? b.W : (int)Math.Round(b.W * CurrentScale / Math.Max(0.01, b.Scale));

    private int ScaledH(PreviewBox b) => b.RenderedHeight == _selected.Height ? b.H : (int)Math.Round(b.H * CurrentScale / Math.Max(0.01, b.Scale));

    private void Arrange()
    {
        if (_screen.ActualWidth <= 0) return;
        var f = Factor;
        foreach (var b in _boxes)
        {
            if (b.W == 0) continue;
            var (x, y) = PixelPosition(b);
            b.Rect.Width = Math.Max(1, ScaledW(b) * f);
            b.Rect.Height = Math.Max(1, ScaledH(b) * f);
            Canvas.SetLeft(b.Rect, x * f);
            Canvas.SetTop(b.Rect, y * f);
        }
        // simple mode outlines the main window (its corner buttons belong to it)
        var main = _boxes.Find(b => b.Id == OverlayLayout.MainWindowId && b.W > 0);
        _outline.Visibility = CornersVisible && main is not null && main.Rect.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        if (main is not null)
        {
            var (mx, my) = PixelPosition(main);
            _outline.Width = main.Rect.Width + 6;
            _outline.Height = main.Rect.Height + 6;
            Canvas.SetLeft(_outline, mx * f - 3);
            Canvas.SetTop(_outline, my * f - 3);
        }
        ArrangeCloseUp();
    }

    /// <summary>The main window at reference size (its 1080p look) over the part of the scene behind it.</summary>
    private void ArrangeCloseUp()
    {
        if (Lead is not { } lead || _closeEmpty.Visibility == Visibility.Visible) return;
        var s = Math.Max(0.01, lead.Scale);
        var pad = 22 * s;
        var (x, y) = PixelPosition(lead);
        var bw = ScaledW(lead);
        var bh = ScaledH(lead);
        // the window into the scene: the overlay plus a margin, kept inside the monitor
        var vw = Math.Min(_selected.Width, bw + 2 * pad);
        var vh = Math.Min(_selected.Height, bh + 2 * pad);
        var vx = Math.Clamp(x - pad, 0, Math.Max(0, _selected.Width - vw));
        var vy = Math.Clamp(y - pad, 0, Math.Max(0, _selected.Height - vh));
        var maxW = Math.Max(120, ActualWidth - 2);
        var k = 1 / CurrentScale; // reference size: 1 DIP per reference px
        if (vw * k > maxW) k = maxW / vw;
        _close.Width = vw * k + 2;
        _close.Height = vh * k + 2;
        _closeScene.Aspect = _selected.Aspect;
        _closeScene.Viewport = new Rect(vx / _selected.Width, vy / _selected.Height, vw / _selected.Width, vh / _selected.Height);
        _closeOverlay.Width = bw * k;
        _closeOverlay.Height = bh * k;
        Canvas.SetLeft(_closeOverlay, (x - vx) * k);
        Canvas.SetTop(_closeOverlay, (y - vy) * k);
    }

    private void UpdateCaption()
    {
        var scale = CurrentScale;
        var c = Loc.I.Culture;
        _caption.Text = string.Format(c, "{0} × {1}  ·  {2}", _selected.Width, _selected.Height, Loc.T("ovl.preview.scale", scale.ToString("0.##", c)));
        _scaleNote.Text = Loc.T(_ctx.Layout.Style.AutoScale ? "ovl.preview.autoNote" : "ovl.preview.fixedNote");
    }

    // ───────────── dragging (advanced) ─────────────

    private void OnDragStart(PreviewBox box, MouseButtonEventArgs e)
    {
        if (!DragEnabled || box.W == 0 || _dragging is not null) return;
        _dragStart = e.GetPosition(_screen);
        _dragOrigin = PixelPosition(box);
        _dragPos = _dragOrigin;
        _dragging = box;
        box.Rect.CaptureMouse();
        e.Handled = true;
    }

    private void OnDragMove(MouseEventArgs e)
    {
        if (_dragging is not { } box) return;
        var f = Factor;
        var p = e.GetPosition(_screen);
        var x = (int)Math.Round(_dragOrigin.X + (p.X - _dragStart.X) / f);
        var y = (int)Math.Round(_dragOrigin.Y + (p.Y - _dragStart.Y) / f);
        var area = Area;
        int w = ScaledW(box), h = ScaledH(box);
        x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - w));
        y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - h));
        int? gx = null, gy = null;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            var margin = (int)Math.Round(PlacementOf(box).Margin * CurrentScale);
            var threshold = Math.Max(2, (int)Math.Round(8 / f));
            var others = _boxes.Where(o => o != box && o.W > 0).Select(o =>
            {
                var (ox, oy) = PixelPosition(o);
                return new PixelRect(ox, oy, ox + ScaledW(o), oy + ScaledH(o));
            }).ToList();
            var snap = OverlayPlacementMath.Snap(x, y, w, h, area, margin, threshold, others, (int)Math.Round(8 * CurrentScale));
            (x, y, gx, gy) = (snap.X, snap.Y, snap.GuideX, snap.GuideY);
        }
        _dragPos = (x, y);
        ShowGuide(_guideX, gx, f, vertical: true);
        ShowGuide(_guideY, gy, f, vertical: false);
        Arrange();
    }

    private void OnDragEnd(MouseButtonEventArgs e)
    {
        if (_dragging is null) return;
        e.Handled = true;
        EndDrag(commit: true);
    }

    private void EndDrag(bool commit)
    {
        if (_dragging is not { } box) return;
        _dragging = null;
        if (box.Rect.IsMouseCaptured) box.Rect.ReleaseMouseCapture();
        _guideX.Visibility = Visibility.Collapsed;
        _guideY.Visibility = Visibility.Collapsed;
        if (commit && _dragPos != _dragOrigin)
        {
            var placed = OverlayPlacementMath.FromPosition(_dragPos.X, _dragPos.Y, ScaledW(box), ScaledH(box), Area, CurrentScale, PlacementOf(box));
            _ctx.Model.EditPlacement(box.Id, p =>
            {
                p.Anchor = placed.Anchor;
                p.OffsetX = placed.OffsetX;
                p.OffsetY = placed.OffsetY;
            });
            Dragged?.Invoke(box.Id);
        }
        Arrange();
    }

    /// <summary>A window was dropped at a new place (argument: its id; the placement fields re-read).</summary>
    public event Action<string>? Dragged;

    private void ShowGuide(Rectangle guide, int? at, double f, bool vertical)
    {
        if (at is not { } v)
        {
            guide.Visibility = Visibility.Collapsed;
            return;
        }
        guide.Visibility = Visibility.Visible;
        if (vertical)
        {
            guide.Height = _screen.ActualHeight;
            Canvas.SetLeft(guide, v * f);
            Canvas.SetTop(guide, 0);
        }
        else
        {
            guide.Width = _screen.ActualWidth;
            Canvas.SetLeft(guide, 0);
            Canvas.SetTop(guide, v * f);
        }
    }

    private static Rectangle Guide(bool vertical)
    {
        var r = new Rectangle { Visibility = Visibility.Collapsed, IsHitTestVisible = false, Opacity = 0.9 };
        if (vertical) r.Width = 1;
        else r.Height = 1;
        r.SetResourceReference(Shape.FillProperty, "Brush.Accent2");
        return r;
    }

    private static void AutomationName(FrameworkElement e, string key)
        => e.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, Ui.Tr(key));

    internal static string ScaleText(double scale) => scale.ToString("0.##", CultureInfo.InvariantCulture);
}
