using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using BetterVitals.Core;
using BetterVitals.Core.Overlay;
using BetterVitals.Localization;
using BetterVitals.Overlay;
using BetterVitals.UI.Themes;
using BetterVitals.UI.Toasts;

namespace BetterVitals.UI.Pages.OverlayEditor;

/// <summary>
/// Advanced mode's "On the overlay": a board per overlay window (the main one, and the second one while it is on) that
/// shows the window exactly as the overlay draws it — its live values, at a readable zoom — and on which its cells are
/// arranged with the mouse: drag a cell onto another to swap them, between two cells to insert it into that row, above
/// or below a row for a new row, right of a row for a new column, onto the other board to move it to the other window,
/// off the boards to remove it (Undo in the toast). While dragging, the grid's lines and a marker show what the drop
/// will do; the page scrolls near its edges. A sensor dragged out of the sensor list lands the same way. A click
/// selects a cell (its look is edited below the boards); arrow keys move the selected cell, Delete removes it, Tab walks
/// the cells — only while the board has the keyboard focus, never globally. Mouse capture only, no OLE drag and drop.
/// Every change goes to the model by id and position; the boards are rendered again after it.
/// </summary>
internal sealed class LayoutBoard : StackPanel
{
    private const double DragThreshold = 4;
    private const double ScrollEdge = 36;
    private const double StagePad = 14;
    private const double MinZoom = 0.9;
    private const double MaxZoom = 1.5;
    private const double MinRenderGapMs = 90;

    /// <summary>The selected cell (kept across rebuilds while the app runs).</summary>
    internal static string? Selected;

    private readonly EditorContext _ctx;
    private readonly BoardsPanel _panel;
    private readonly WindowBoard _main;
    private readonly WindowBoard _second;
    private readonly CheckBox _secondSwitch;
    private readonly Button _basics;
    private readonly StackPanel _cellHost = new();
    private readonly DispatcherTimer _again;
    private readonly DispatcherTimer _scroll;
    private string? _cellShown;
    private bool _syncing;

    // rendering
    private bool _active;
    private bool _busy;
    private bool _pending;
    private DateTime _lastRender;
    private double _zoom = 1.25;
    private bool _failedLogged;

    // dragging
    private Press? _press;
    private Drag? _drag;
    private ScrollViewer? _outer;
    private Vector _scrollStep;

    /// <summary>Where the pointer is, relative to an element (the mouse; tests put it anywhere).</summary>
    private Func<IInputElement, Point> _pointer = Mouse.GetPosition;

    /// <summary>The mouse went down on a cell; a drag once it moves far enough.</summary>
    private sealed record Press(WindowBoard Board, OverlayGridCell Cell, Point Start, Point Grab);

    /// <summary>A drag in progress: a cell of a board, or a sensor from the list (<see cref="SensorIds"/>).</summary>
    private sealed class Drag
    {
        public WindowBoard? Source;
        public OverlayGridCell Cell;
        public IReadOnlyList<string>? SensorIds;
        public Point Grab;
        public FrameworkElement? Ghost;
        public FrameworkElement? RemoveBadge;
        public FrameworkElement? RemoveLabel;
        public WindowBoard? Over;
        public BoardTarget? Target;
        public bool Removing;

        public bool External => SensorIds is not null;
    }

    /// <param name="footer">Shown at the end, under the selected cell (the quick arrangement).</param>
    public LayoutBoard(EditorContext ctx, UIElement? footer = null)
    {
        _ctx = ctx;
        _again = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(MinRenderGapMs) };
        _again.Tick += (_, _) =>
        {
            _again.Stop();
            Render();
        };
        _scroll = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
        _scroll.Tick += (_, _) => ScrollTick();

        // ── toolbar: add sensors (the basics while empty) | the second window ──
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = false };
        var second = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var secondIcon = Ui.Icon("OvEd.Icon.Detach", 16, "Brush.AccentText");
        secondIcon.Margin = new Thickness(0, 0, 8, 0);
        secondIcon.VerticalAlignment = VerticalAlignment.Center;
        second.Children.Add(secondIcon);
        var secondTitle = Ui.Text("ovl.second.title", size: 13);
        secondTitle.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        secondTitle.VerticalAlignment = VerticalAlignment.Center;
        secondTitle.Margin = new Thickness(0, 0, 10, 0);
        second.Children.Add(secondTitle);
        _secondSwitch = Ui.Switch(ctx.Layout.Second.Enabled, OnSecond);
        System.Windows.Automation.AutomationProperties.SetName(_secondSwitch, Loc.T("ovl.second.title"));
        System.Windows.Automation.AutomationProperties.SetHelpText(_secondSwitch, Loc.T("ovl.second.hint"));
        second.Children.Add(_secondSwitch);
        second.SetBinding(ToolTipProperty, Ui.Tr("ovl.second.hint"));
        DockPanel.SetDock(second, Dock.Right);
        bar.Children.Add(second);
        var add = Ui.Button("SecondaryButton", "ovl.items.add", "Icon.Plus", (_, _) => _ctx.OpenPicker(true));
        add.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(add, Dock.Left);
        bar.Children.Add(add);
        _basics = Ui.Button("GhostButton", "ovl.items.defaults", "Icon.Sparkle", (_, _) => _ctx.Model.AddDefaults(_ctx.Friendly));
        DockPanel.SetDock(_basics, Dock.Left);
        bar.Children.Add(_basics);
        Children.Add(bar);

        // ── the boards ──
        _main = new WindowBoard(this, OverlayLayout.MainWindowId);
        _second = new WindowBoard(this, OverlayLayout.SecondWindowId);
        _panel = new BoardsPanel { Focusable = true, FocusVisualStyle = null };
        _panel.Children.Add(_main);
        _panel.Children.Add(_second);
        _panel.KeyDown += OnKey;
        _panel.GotKeyboardFocus += (_, _) => UpdateChrome();
        _panel.LostKeyboardFocus += (_, _) => UpdateChrome();
        System.Windows.Automation.AutomationProperties.SetName(_panel, Loc.T("ovl.board.name"));
        System.Windows.Automation.AutomationProperties.SetHelpText(_panel, Loc.T("ovl.board.keys"));
        Children.Add(_panel);
        var hint = Ui.Hint("ovl.board.keys", new Thickness(2, 10, 0, 0));
        hint.TextWrapping = TextWrapping.Wrap;
        Children.Add(hint);

        // ── the selected cell ──
        _cellHost.Margin = new Thickness(0, 16, 0, 0);
        Children.Add(_cellHost);
        if (footer is not null) Children.Add(footer);

        MouseMove += (_, e) => OnPointerMove(e);
        MouseLeftButtonUp += (_, e) => OnPointerUp(e);
        LostMouseCapture += (_, _) => Cancel();
        SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.PreviousSize.Width - e.NewSize.Width) > 0.5) FitZoom();
        };
        Loaded += (_, _) =>
        {
            _ctx.SensorDragStarted = BeginExternal;
            ThemeManager.Changed += OnThemeChanged;
        };
        Unloaded += (_, _) =>
        {
            Cancel();
            Stop();
            if (_ctx.SensorDragStarted == BeginExternal) _ctx.SensorDragStarted = null;
            ThemeManager.Changed -= OnThemeChanged;
        };
        Sync();
    }

    /// <summary>The "Position" of a window was asked for (its placement controls).</summary>
    public event Action<string>? PositionRequested;

    /// <summary>Boards rendered so far (tests).</summary>
    public int RenderCount { get; private set; }

    public bool IsRendering => _busy || _pending;

    /// <summary>The window boards (tests): id, picture size in DIP, its grid.</summary>
    internal IEnumerable<(string WindowId, Size Picture, OverlayGridGeometry? Grid)> Boards
        => Visible().Select(b => (b.WindowId, new Size(b.Picture.Width, b.Picture.Height), b.Grid));

    /// <summary>The board's keyboard target (tests, focus).</summary>
    internal UIElement KeyTarget => _panel;

    private IEnumerable<WindowBoard> Visible() => _second.Visibility == Visibility.Visible ? [_main, _second] : [_main];

    // ───────────── state ─────────────

    /// <summary>Re-reads the layout: second window, captions, selection, the selected cell's editor; renders again.</summary>
    public void Sync()
    {
        _syncing = true;
        try
        {
            var l = _ctx.Layout;
            _secondSwitch.IsChecked = l.Second.Enabled;
            _basics.Visibility = l.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
            var wasHidden = _second.Visibility != Visibility.Visible;
            _second.Visibility = l.Second.Enabled ? Visibility.Visible : Visibility.Collapsed;
            if (wasHidden && l.Second.Enabled && IsLoaded) Motion.Appear(_second, rise: 6);
            _main.Caption(_ctx.PlacementSummary(l.Placement));
            _second.Caption(_ctx.PlacementSummary(l.Second.Placement));
            if (Selected is not null && l.Locate(Selected) is null) Selected = null;
            ShowCell();
        }
        finally
        {
            _syncing = false;
        }
        RequestRender();
    }

    public void Start()
    {
        _active = true;
        RequestRender();
    }

    public void Stop()
    {
        _active = false;
        _again.Stop();
    }

    private void OnThemeChanged()
    {
        _main.Restyle();
        _second.Restyle();
        RequestRender();
    }

    // ───────────── rendering ─────────────

    /// <summary>Renders the boards again (coalesced; at most one in flight; held back while a drag runs).</summary>
    public void RequestRender()
    {
        if (!_active) return;
        if (_busy || _drag is not null)
        {
            _pending = true;
            return;
        }
        if ((DateTime.UtcNow - _lastRender).TotalMilliseconds < MinRenderGapMs)
        {
            _pending = true;
            if (!_again.IsEnabled) _again.Start();
            return;
        }
        Render();
    }

    private double Dpi => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    private void Render()
    {
        if (!_active || _drag is not null) return;
        _pending = false;
        var layout = LayoutJson.Clone(_ctx.Layout);
        var values = _ctx.Feed.Build(layout);
        var theme = ThemeManager.I.ResolveOverlayTheme();
        var culture = Loc.I.Culture;
        var unit = _ctx.TemperatureUnit;
        var dpi = Math.Max(1, Dpi);
        var zoom = _zoom;
        var windows = layout.Windows().Where(w => w.Cells.Count > 0).Select(w => w.Id).ToList();
        _busy = true;
        _lastRender = DateTime.UtcNow;
        var dispatcher = Dispatcher;
        Task.Run(() =>
        {
            try
            {
                var result = new List<(string Id, OverlayBitmap Bitmap, OverlayGridGeometry Grid)>();
                foreach (var id in windows)
                {
                    var (bmp, grid) = OverlayHost.RenderWindowWithGrid(layout, theme, values, id, zoom * dpi, culture, unit);
                    result.Add((id, bmp, grid));
                }
                return (Result: result, Error: (Exception?)null);
            }
            catch (Exception ex)
            {
                return (Result: (List<(string, OverlayBitmap, OverlayGridGeometry)>?)null, Error: ex);
            }
        }).ContinueWith(t =>
        {
            var (result, error) = t.Result;
            dispatcher.BeginInvoke(() =>
            {
                _busy = false;
                if (result is not null) Apply(result, dpi, zoom);
                else if (!_failedLogged)
                {
                    _failedLogged = true;
                    Log.Error("overlay editor: board render failed", error);
                }
                if (_pending) RequestRender();
            });
        }, TaskScheduler.Default);
    }

    private void Apply(List<(string Id, OverlayBitmap Bitmap, OverlayGridGeometry Grid)> result, double dpi, double zoom)
    {
        foreach (var board in new[] { _main, _second })
        {
            var r = result.FirstOrDefault(x => x.Id == board.WindowId);
            if (r.Bitmap is null) board.SetEmpty();
            else board.SetPicture(r.Bitmap, r.Grid, dpi, zoom);
        }
        RenderCount++;
        UpdateChrome();
        FitZoom();
    }

    /// <summary>
    /// A readable zoom: as large as fits the column (0.9×–1.5× the 1080p look, the same for both boards); a board wider
    /// than that at 0.9× scrolls sideways.
    /// </summary>
    private void FitZoom()
    {
        var width = _panel.ActualWidth;
        if (width <= 0) return;
        var widest = Visible().Where(b => b.Grid is not null).Select(b => b.ReferenceWidth).DefaultIfEmpty(0).Max();
        if (widest <= 0) return;
        var available = width - 2 * StagePad - WindowBoard.Chrome;
        var ratio = available / widest;
        var fit = Math.Floor(Math.Clamp(ratio, MinZoom, MaxZoom) * 20) / 20;
        // a step up only with some room to spare: rounding at the new zoom must not bring it straight back
        if (Math.Abs(fit - _zoom) < 0.049 || (fit > _zoom && ratio < fit + 0.03 && fit < MaxZoom)) return;
        _zoom = fit;
        RequestRender();
    }

    // ───────────── selection and the selected cell's editor ─────────────

    private void Select(string? itemId)
    {
        if (Selected == itemId)
        {
            UpdateChrome();
            return;
        }
        Selected = itemId;
        UpdateChrome();
        ShowCell();
    }

    private void ShowCell()
    {
        if (_cellShown == Selected && _cellHost.Children.Count > 0) return;
        _cellShown = Selected;
        _cellHost.Children.Clear();
        if (Selected is null || _ctx.Layout.FindItem(Selected) is not { } item)
        {
            var none = Ui.Hint("ovl.board.cell.none", new Thickness(2, 0, 0, 0));
            none.TextWrapping = TextWrapping.Wrap;
            _cellHost.Children.Add(none);
            return;
        }
        var card = new Border { Padding = new Thickness(14, 12, 14, 4) };
        card.SetResourceReference(StyleProperty, "OvEd.Row");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.AccentGlow");
        var stack = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        var remove = Ui.IconButton("Icon.Trash", "ovl.board.cell.remove", (_, _) => RemoveWithUndo(item.Id), 15);
        DockPanel.SetDock(remove, Dock.Right);
        head.Children.Add(remove);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var caption = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 0, 0, 1) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentText");
        caption.Text = Loc.T("ovl.board.cell.title");
        texts.Children.Add(caption);
        var title = new TextBlock { Text = _ctx.Title(item), FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        texts.Children.Add(title);
        var sensors = _ctx.Sensors(item);
        var names = new TextBlock { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
        names.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
        names.Text = sensors.Count switch
        {
            0 => item.SensorIds.FirstOrDefault() ?? "",
            1 => $"{sensors[0].Name} · {_ctx.Hub.FindHardware(sensors[0].HardwareId)?.Name ?? DefaultLabels.Hardware(sensors[0].HardwareKind)}",
            // the count first, so trimming a long list never eats it
            _ => $"{Loc.Plural("sensors.count", sensors.Count)} · {string.Join(", ", sensors.Take(3).Select(s => s.Name))}{(sensors.Count > 3 ? ", …" : "")}",
        };
        if (sensors.Count > 0) Explain.Explain.SetSensor(names, sensors[0]);
        texts.Children.Add(names);
        head.Children.Add(texts);
        stack.Children.Add(head);
        stack.Children.Add(new ItemEditor(_ctx, item, () => title.Text = _ctx.Title(_ctx.Layout.FindItem(item.Id) ?? item)) { Margin = new Thickness(0, 4, 0, 10) });
        card.Child = stack;
        _cellHost.Children.Add(card);
        if (IsLoaded) Motion.Appear(card, rise: 6, ms: 220);
    }

    /// <summary>Selection, hover and the remove button on the boards.</summary>
    private void UpdateChrome()
    {
        var focused = _panel.IsKeyboardFocusWithin;
        foreach (var b in Visible()) b.ShowSelection(Selected, focused, _drag is null);
    }

    // ───────────── actions ─────────────

    private void OnSecond(bool on)
    {
        if (_syncing) return;
        var before = LayoutJson.Clone(_ctx.Layout);
        var hadCells = _ctx.Layout.Second.Cells.Count > 0;
        if (!_ctx.Model.SetSecondWindow(on)) return;
        if (on) _ctx.Toast(Loc.T("ovl.toast.secondOn"), ToastKind.Success, null, null);
        else if (hadCells) _ctx.Toast(Loc.T("ovl.toast.secondOff"), ToastKind.Info, Loc.T("common.undo"), () => _ctx.Model.Replace(before));
    }

    private void RemoveWithUndo(string itemId)
    {
        if (_ctx.Layout.FindItem(itemId) is not { } item) return;
        var before = LayoutJson.Clone(_ctx.Layout);
        var title = _ctx.Title(item);
        var next = NextCell(itemId, 1) ?? NextCell(itemId, -1);
        if (!_ctx.Model.TryEdit(l => l.RemoveCell(itemId))) return;
        Selected = next;
        ShowCell();
        _ctx.Toast(Loc.T("ovl.toast.removed", title), ToastKind.Info, Loc.T("common.undo"), () => _ctx.Model.Replace(before));
    }

    /// <summary>The cell after / before <paramref name="itemId"/> in reading order (main window, then the second one).</summary>
    private string? NextCell(string? itemId, int dir)
    {
        var all = _ctx.Layout.Windows().SelectMany(w => w.Cells).Select(c => c.Id).ToList();
        if (all.Count == 0) return null;
        var i = itemId is null ? -1 : all.IndexOf(itemId);
        if (i < 0) return dir > 0 ? all[0] : all[^1];
        var j = i + dir;
        return j >= 0 && j < all.Count ? all[j] : null;
    }

    // ───────────── keyboard (only while the board has the focus) ─────────────

    private void OnKey(object sender, KeyEventArgs e)
    {
        // only keys meant for the board itself (not for a text box of the cell editor below it)
        if (e.OriginalSource == _panel && HandleKey(e.Key, (Keyboard.Modifiers & ModifierKeys.Shift) != 0)) e.Handled = true;
    }

    /// <summary>Arrows move the selected cell, Delete removes it, Tab / Shift+Tab walk the cells, Esc cancels a drag or the selection.</summary>
    internal bool HandleKey(Key key, bool shift)
    {
        if (key == Key.Escape)
        {
            if (_drag is not null) Cancel();
            else if (Selected is not null) Select(null);
            else return false;
            return true;
        }
        if (key == Key.Tab)
        {
            var next = NextCell(Selected, shift ? -1 : 1);
            if (next is null) return false; // past the last cell: the focus moves on
            Select(next);
            BringSelectedIntoView();
            return true;
        }
        if (Selected is not { } id || _drag is not null) return false;
        var (rows, cols) = key switch
        {
            Key.Left => (0, -1),
            Key.Right => (0, 1),
            Key.Up => (-1, 0),
            Key.Down => (1, 0),
            _ => (0, 0),
        };
        if (rows != 0 || cols != 0)
        {
            _ctx.Model.MoveBy(id, rows, cols);
            return true;
        }
        if (key is Key.Delete or Key.Back)
        {
            RemoveWithUndo(id);
            return true;
        }
        return false;
    }

    private void BringSelectedIntoView()
    {
        foreach (var b in Visible())
            if (b.SelectionRect() is { } r)
                b.Surface.BringIntoView(r);
    }

    // ───────────── mouse: press, drag, drop ─────────────

    /// <summary>The mouse went down on a board (its surface coordinates).</summary>
    internal void OnBoardPress(WindowBoard board, Point p, MouseButtonEventArgs e)
    {
        if (_drag is not null || board.Grid is not { } g) return;
        if (BoardHitTest.CellAt(g, board.Px, p) is not { } cell)
        {
            _panel.Focus();
            Select(null);
            return;
        }
        _panel.Focus();
        Select(cell.ItemId);
        var rect = BoardHitTest.CellRect(g, board.Px, cell.Row, cell.Column);
        _press = new Press(board, cell, e.GetPosition(this), new Point(p.X - rect.Left, p.Y - rect.Top));
        CaptureMouse();
        e.Handled = true;
    }

    private void OnPointerMove(MouseEventArgs e)
    {
        if (_drag is not null)
        {
            Track();
            return;
        }
        if (_press is not { } press || e.LeftButton != MouseButtonState.Pressed) return;
        if ((e.GetPosition(this) - press.Start).Length < DragThreshold) return;
        _press = null;
        _drag = new Drag { Source = press.Board, Cell = press.Cell, Grab = press.Grab };
        BeginVisuals();
        Track();
    }

    private void OnPointerUp(MouseButtonEventArgs e)
    {
        if (_drag is null)
        {
            if (_press is not null)
            {
                _press = null;
                if (IsMouseCaptured) ReleaseMouseCapture();
            }
            return;
        }
        e.Handled = true;
        DropNow();
    }

    /// <summary>A sensor row of the list started a drag: the board takes the mouse.</summary>
    private void BeginExternal(IReadOnlyList<string> sensorIds, string title)
    {
        if (_drag is not null || sensorIds.Count == 0 || !IsVisible) return;
        _press = null;
        _drag = new Drag { SensorIds = sensorIds, Grab = new Point(14, 14) };
        BeginVisuals(title);
        CaptureMouse();
        Track();
    }

    private void BeginVisuals(string? title = null)
    {
        var d = _drag!;
        _outer = FindScroller(this);
        foreach (var b in Visible()) b.BeginDrag(d.External ? null : d.Source == b ? d.Cell : null);
        d.Ghost = d.External ? SensorGhost(title ?? "") : CellGhost(d.Source!, d.Cell);
        if (d.Ghost is not null && _ctx.DragLayer is { } layer)
        {
            layer.Children.Add(d.Ghost);
            if (Motion.I.Enabled)
                d.Ghost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, d.Ghost.Opacity, TimeSpan.FromMilliseconds(120)) { EasingFunction = Motion.EaseOut });
        }
    }

    /// <summary>The dragged cell's picture, lifted off the board.</summary>
    private FrameworkElement? CellGhost(WindowBoard board, OverlayGridCell cell)
    {
        if (board.Bitmap is null || board.Grid is not { } g) return null;
        var rect = BoardHitTest.CellRect(g, board.Px, cell.Row, cell.Column);
        rect.Inflate(4, 3);
        var px = new Int32Rect(
            (int)Math.Clamp(Math.Floor(rect.Left * board.Px), 0, g.Width - 1),
            (int)Math.Clamp(Math.Floor(rect.Top * board.Px), 0, g.Height - 1),
            0, 0);
        px.Width = (int)Math.Clamp(Math.Ceiling(rect.Width * board.Px), 1, g.Width - px.X);
        px.Height = (int)Math.Clamp(Math.Ceiling(rect.Height * board.Px), 1, g.Height - px.Y);
        BitmapSource snap = board.Bitmap.Clone();
        snap.Freeze();
        var crop = new CroppedBitmap(snap, px);
        crop.Freeze();
        var face = new Border
        {
            Width = px.Width / board.Px,
            Height = px.Height / board.Px,
            CornerRadius = new CornerRadius(7),
            Background = new ImageBrush(crop) { Stretch = Stretch.Fill },
            BorderThickness = new Thickness(1.5),
        };
        face.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
        return Ghost(face);
    }

    /// <summary>A sensor from the list: a pill with its name.</summary>
    private FrameworkElement SensorGhost(string title)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = Ui.Icon("Icon.Plus", 14, "Brush.OnAccent", 2);
        icon.Margin = new Thickness(0, 0, 6, 0);
        row.Children.Add(icon);
        var text = new TextBlock { Text = title, FontSize = 12.5, FontWeight = FontWeights.SemiBold, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.OnAccent");
        row.Children.Add(text);
        var face = new Border { Padding = new Thickness(10, 6, 12, 7), CornerRadius = new CornerRadius(10), Child = row };
        face.SetResourceReference(Border.BackgroundProperty, "Brush.AccentFill");
        return Ghost(face);
    }

    private FrameworkElement Ghost(FrameworkElement face)
    {
        var badge = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -9, -9, 0), Visibility = Visibility.Collapsed };
        badge.SetResourceReference(Border.BackgroundProperty, "Brush.Danger");
        badge.Child = Ui.Icon("Icon.Trash", 12, "Brush.OnAccent", 1.8);
        var label = new Border { Padding = new Thickness(8, 3, 8, 4), CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, -28), Visibility = Visibility.Collapsed };
        label.SetResourceReference(Border.BackgroundProperty, "Brush.Danger");
        var labelText = new TextBlock { Text = Loc.T("ovl.board.drop.remove"), FontSize = 11.5, FontWeight = FontWeights.SemiBold };
        labelText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.OnAccent");
        label.Child = labelText;
        var grid = new Grid { IsHitTestVisible = false, Opacity = 0.84, RenderTransformOrigin = new Point(0.5, 0.5) };
        grid.RenderTransform = new ScaleTransform(1.03, 1.03);
        grid.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 5, Direction = 270, Opacity = 0.35, Color = ThemeManager.ShadowColor };
        grid.Children.Add(face);
        grid.Children.Add(badge);
        grid.Children.Add(label);
        _drag!.RemoveBadge = badge;
        _drag.RemoveLabel = label;
        return grid;
    }

    /// <summary>Where the pointer is now: target, markers, ghost, remove state, scrolling.</summary>
    private void Track()
    {
        if (_drag is not { } d) return;
        WindowBoard? over = null;
        BoardTarget? target = null;
        foreach (var b in Visible())
        {
            var q = _pointer(b.Surface);
            if (!b.StageContains(q)) continue;
            over = b;
            target = b.Target(q, d.External, d.External ? null : d.Source == b ? d.Cell : null);
            break;
        }
        foreach (var b in Visible()) b.ShowTarget(b == over ? target : null);
        d.Over = over;
        d.Target = target;
        // off the boards (not just between them): release removes the cell
        var p = _pointer(this);
        var area = new Rect(_panel.TranslatePoint(new Point(0, 0), this), _panel.RenderSize);
        area.Inflate(10, 10);
        d.Removing = !d.External && target is null && !area.Contains(p);
        if (d.Ghost is { } ghost && _ctx.DragLayer is { } layer)
        {
            var at = _pointer(layer);
            Canvas.SetLeft(ghost, at.X - d.Grab.X);
            Canvas.SetTop(ghost, at.Y - d.Grab.Y);
            if (d.RemoveBadge is { } badge && d.RemoveLabel is { } label)
            {
                badge.Visibility = label.Visibility = d.Removing ? Visibility.Visible : Visibility.Collapsed;
                ghost.Opacity = d.Removing ? 0.7 : 0.84;
            }
        }
        AutoScroll(over);
    }

    private void DropNow()
    {
        var d = _drag!;
        var target = d.Target;
        var removing = d.Removing;
        EndDrag();
        if (target is { } t)
        {
            if (d.External)
            {
                if (_ctx.Model.AddCell(d.SensorIds!, t.Drop) is { } id) Select(id);
            }
            else if (_ctx.Model.MoveCell(d.Cell.ItemId, t.Drop))
            {
                Select(d.Cell.ItemId);
            }
        }
        else if (removing)
        {
            RemoveWithUndo(d.Cell.ItemId);
        }
        if (d.External) _ctx.SensorDragEnded?.Invoke();
        _panel.Focus();
    }

    private void Cancel()
    {
        _press = null;
        if (_drag is null) return;
        var external = _drag.External;
        EndDrag();
        if (external) _ctx.SensorDragEnded?.Invoke();
    }

    private void EndDrag()
    {
        var d = _drag;
        _drag = null;
        _scroll.Stop();
        _scrollStep = default;
        if (d?.Ghost is { } ghost) _ctx.DragLayer?.Children.Remove(ghost);
        foreach (var b in new[] { _main, _second }) b.EndDrag();
        if (IsMouseCaptured) ReleaseMouseCapture();
        UpdateChrome();
        if (_pending) RequestRender();
    }

    /// <summary>Applies a drop as if dragged there (tests: the same path as the mouse, without the mouse).</summary>
    internal bool DropForTest(string itemId, GridDrop drop)
    {
        var before = LayoutJson.Text(_ctx.Layout);
        var slot = _ctx.Layout.Locate(itemId);
        if (slot is null) return false;
        var board = slot.Value.WindowId == OverlayLayout.SecondWindowId ? _second : _main;
        _drag = new Drag { Source = board, Cell = new OverlayGridCell(itemId, slot.Value.Item.Row, slot.Value.Item.Column), Target = new BoardTarget(drop, Rect.Empty, Rect.Empty, false) };
        DropNow();
        return LayoutJson.Text(_ctx.Layout) != before;
    }

    /// <summary>A sensor from the list dropped on a board (tests: the same path as the mouse).</summary>
    internal string? DropSensorForTest(IReadOnlyList<string> sensorIds, GridDrop drop)
    {
        _drag = new Drag { SensorIds = sensorIds, Target = new BoardTarget(drop, Rect.Empty, Rect.Empty, false) };
        DropNow();
        return Selected;
    }

    /// <summary>
    /// Starts dragging a cell and holds the pointer at <paramref name="at"/> (relative to the picture of
    /// <paramref name="windowId"/>'s board): the markers, grid lines and ghost show as they would mid-drag (tests).
    /// </summary>
    internal BoardTarget? DragForTest(string itemId, string windowId, Point at)
    {
        var source = Visible().FirstOrDefault(b => b.Grid?.Cells.Any(c => c.ItemId == itemId) == true);
        var target = windowId == OverlayLayout.SecondWindowId ? _second : _main;
        if (source?.Grid is not { } g) return null;
        var cell = g.Cells.First(c => c.ItemId == itemId);
        _pointer = e => target.Surface.TranslatePoint(at, (UIElement)e);
        _drag = new Drag { Source = source, Cell = cell, Grab = new Point(10, 8) };
        BeginVisuals();
        Track();
        _scroll.Stop();
        return _drag.Target;
    }

    /// <summary>Ends a <see cref="DragForTest"/>: drops it or cancels it.</summary>
    internal void EndDragForTest(bool drop)
    {
        if (drop && _drag is not null) DropNow();
        else Cancel();
        _pointer = Mouse.GetPosition;
    }

    /// <summary>A cell dragged off the boards (tests).</summary>
    internal bool DragOutForTest(string itemId)
    {
        var before = LayoutJson.Text(_ctx.Layout);
        _drag = new Drag { Source = _main, Cell = new OverlayGridCell(itemId, 0, 0), Removing = true };
        DropNow();
        return LayoutJson.Text(_ctx.Layout) != before;
    }

    /// <summary>Selects a cell as a click would (tests).</summary>
    internal void SelectForTest(string? itemId) => Select(itemId);

    // ───────────── auto-scroll ─────────────

    private static ScrollViewer? FindScroller(DependencyObject from)
    {
        for (var d = VisualTreeHelper.GetParent(from); d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is ScrollViewer sv) return sv;
        return null;
    }

    private void AutoScroll(WindowBoard? over)
    {
        double dx = 0, dy = 0;
        if (_outer is { } outer)
        {
            var y = _pointer(outer).Y;
            dy = y < ScrollEdge ? -(ScrollEdge - y) / 2.5 : y > outer.ActualHeight - ScrollEdge ? (y - (outer.ActualHeight - ScrollEdge)) / 2.5 : 0;
        }
        if (over?.Scroller is { ScrollableWidth: > 0 } inner)
        {
            var x = _pointer(inner).X;
            dx = x < ScrollEdge ? -(ScrollEdge - x) / 2.5 : x > inner.ActualWidth - ScrollEdge ? (x - (inner.ActualWidth - ScrollEdge)) / 2.5 : 0;
        }
        _scrollStep = new Vector(dx, dy);
        if (_scrollStep != default && !_scroll.IsEnabled) _scroll.Start();
        else if (_scrollStep == default) _scroll.Stop();
    }

    private void ScrollTick()
    {
        if (_drag is not { } d || _scrollStep == default)
        {
            _scroll.Stop();
            return;
        }
        if (_scrollStep.Y != 0 && _outer is { } outer)
        {
            outer.ScrollToVerticalOffset(outer.VerticalOffset + _scrollStep.Y);
            outer.UpdateLayout();
        }
        if (_scrollStep.X != 0 && d.Over?.Scroller is { } inner)
        {
            inner.ScrollToHorizontalOffset(inner.HorizontalOffset + _scrollStep.X);
            inner.UpdateLayout();
        }
        Track();
    }

    // ───────────── one window's board ─────────────

    /// <summary>
    /// One window: its name, where it sits and a "Position" button, then the stage — the window's picture on a game-like
    /// backdrop, with selection, markers and grid lines drawn over it. Scrolls sideways when wider than the column.
    /// </summary>
    internal sealed class WindowBoard : Border
    {
        /// <summary>Horizontal space the board takes besides its stage (border + padding), DIP.</summary>
        public const double Chrome = 20;

        private readonly LayoutBoard _owner;
        private readonly TextBlock _where;
        private readonly Border _stage;
        private readonly ImageBrush _brush = new() { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        private readonly Canvas _chrome = new() { IsHitTestVisible = false };
        private readonly Border _empty;
        private readonly Rectangle _hover;
        private readonly Rectangle _selection;
        private readonly Button _remove;
        private readonly Rectangle _marker;
        private readonly Rectangle _slot;
        private readonly Border _source;
        private readonly System.Windows.Shapes.Path _lines;
        private OverlayGridCell? _hoverCell;

        public WindowBoard(LayoutBoard owner, string windowId)
        {
            _owner = owner;
            WindowId = windowId;
            Margin = new Thickness(0, 0, 0, 0);
            Padding = new Thickness(9, 9, 9, 9);
            CornerRadius = new CornerRadius(14);
            BorderThickness = new Thickness(1);
            SetResourceReference(BackgroundProperty, "Brush.BgAlt");
            SetResourceReference(BorderBrushProperty, "Brush.Border");

            var root = new DockPanel();
            // caption: icon, name, where it sits | Position
            var head = new DockPanel { Margin = new Thickness(2, 0, 0, 10) };
            var position = Ui.Button("GhostButton", "ovl.board.position", "Icon.Monitor", (_, _) => owner.PositionRequested?.Invoke(windowId));
            position.Padding = new Thickness(8, 4, 10, 4);
            DockPanel.SetDock(position, Dock.Right);
            head.Children.Add(position);
            var badge = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            badge.SetResourceReference(BackgroundProperty, "Brush.AccentSoft");
            badge.Child = Ui.Icon(windowId == OverlayLayout.MainWindowId ? "Icon.Overlay" : "OvEd.Icon.Detach", 14, "Brush.AccentText");
            head.Children.Add(badge);
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = Ui.Text(windowId == OverlayLayout.MainWindowId ? "ovl.window.main" : "ovl.window.second", size: 13);
            title.FontWeight = FontWeights.SemiBold;
            title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
            titles.Children.Add(title);
            _where = new TextBlock { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
            _where.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
            titles.Children.Add(_where);
            head.Children.Add(titles);
            DockPanel.SetDock(head, Dock.Top);
            root.Children.Add(head);

            // the stage
            Picture = new Rectangle { Fill = _brush, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            RenderOptions.SetBitmapScalingMode(_brush, BitmapScalingMode.HighQuality);
            _lines = new System.Windows.Shapes.Path { StrokeThickness = 1, StrokeDashArray = [3, 3], Opacity = 0.55, Visibility = Visibility.Collapsed };
            _lines.SetResourceReference(Shape.StrokeProperty, "Brush.Accent");
            _source = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1.2), Visibility = Visibility.Collapsed, Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x1A, 0x22, 0x33)) };
            _source.SetResourceReference(Border.BorderBrushProperty, "Brush.TextFaint");
            _hover = Outline(1, 0.5);
            _selection = Outline(2, 1);
            _marker = new Rectangle { RadiusX = 6, RadiusY = 6, Visibility = Visibility.Collapsed };
            _marker.SetResourceReference(Shape.StrokeProperty, "Brush.Accent");
            _slot = new Rectangle { RadiusX = 2.5, RadiusY = 2.5, Visibility = Visibility.Collapsed };
            _slot.SetResourceReference(Shape.FillProperty, "Brush.Accent");
            _chrome.Children.Add(_lines);
            _chrome.Children.Add(_source);
            _chrome.Children.Add(_hover);
            _chrome.Children.Add(_selection);
            _chrome.Children.Add(_marker);
            _chrome.Children.Add(_slot);
            _remove = new Button { Width = 22, Height = 22, Visibility = Visibility.Collapsed, Cursor = Cursors.Hand, Focusable = false };
            _remove.SetResourceReference(StyleProperty, "OvEd.CellRemove");
            _remove.SetBinding(ToolTipProperty, Ui.Tr("ovl.board.cell.remove"));
            System.Windows.Automation.AutomationProperties.SetName(_remove, Loc.T("ovl.board.cell.remove"));
            _remove.Click += (_, _) =>
            {
                if (Selected is { } id) owner.RemoveWithUndo(id);
            };
            _empty = new Border { MinWidth = 260, MinHeight = 84, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.2), Visibility = Visibility.Collapsed };
            _empty.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderStrong");
            var emptyText = Ui.Text(windowId == OverlayLayout.MainWindowId ? "ovl.board.empty.main" : "ovl.board.empty.second", size: 12.5, wrap: true);
            emptyText.Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
            emptyText.TextAlignment = TextAlignment.Center;
            emptyText.Margin = new Thickness(18, 10, 18, 10);
            emptyText.MaxWidth = 300;
            emptyText.VerticalAlignment = VerticalAlignment.Center;
            _empty.Child = emptyText;

            Surface = new Grid { Margin = new Thickness(StagePad), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent };
            Surface.Children.Add(Picture);
            Surface.Children.Add(_empty);
            Surface.Children.Add(_chrome);
            var stageGrid = new Grid();
            stageGrid.Children.Add(Surface);
            var removeLayer = new Canvas { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(StagePad) };
            removeLayer.Children.Add(_remove);
            stageGrid.Children.Add(removeLayer);
            _stage = new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = stageGrid, HorizontalAlignment = HorizontalAlignment.Left, Background = Backdrop() };
            _stage.MouseLeftButtonDown += (_, e) => owner.OnBoardPress(this, e.GetPosition(Surface), e);
            _stage.MouseMove += (_, e) => Hover(e.GetPosition(Surface));
            _stage.MouseLeave += (_, _) => Hover(null);
            Scroller = new ScrollViewer { Content = _stage, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
            root.Children.Add(Scroller);
            Child = root;
            SetEmpty();
        }

        public string WindowId { get; }

        /// <summary>The window's picture over the stage (sized in DIP).</summary>
        public Rectangle Picture { get; }

        /// <summary>The picture's coordinate space: everything on the board is measured from its top-left.</summary>
        public Grid Surface { get; }

        public ScrollViewer Scroller { get; }

        public WriteableBitmap? Bitmap { get; private set; }

        public OverlayGridGeometry? Grid { get; private set; }

        /// <summary>Pixels of the picture per DIP.</summary>
        public double Px { get; private set; } = 1;

        /// <summary>The picture's width at the 1080p reference size (to pick a zoom that fits).</summary>
        public double ReferenceWidth { get; private set; }

        /// <summary>A fixed game-like backdrop: the overlay is drawn for games, whatever the app's theme.</summary>
        private static Brush Backdrop()
        {
            var b = new LinearGradientBrush(Color.FromRgb(0x2B, 0x3D, 0x5C), Color.FromRgb(0x8C, 0x6A, 0x52), new Point(0, 0), new Point(1, 1));
            b.Freeze();
            return b;
        }

        private static Rectangle Outline(double thickness, double opacity)
        {
            var r = new Rectangle { RadiusX = 6, RadiusY = 6, StrokeThickness = thickness, Opacity = opacity, Visibility = Visibility.Collapsed };
            r.SetResourceReference(Shape.StrokeProperty, "Brush.Accent");
            return r;
        }

        public void Caption(string where) => _where.Text = where;

        public void Restyle() => _stage.Background = Backdrop();

        public void SetEmpty()
        {
            Grid = null;
            Picture.Visibility = Visibility.Collapsed;
            _empty.Visibility = Visibility.Visible;
            Surface.Width = double.NaN;
            Surface.Height = double.NaN;
            ReferenceWidth = 0;
            Hover(null);
        }

        public void SetPicture(OverlayBitmap bmp, OverlayGridGeometry grid, double dpi, double zoom)
        {
            if (Bitmap is null || Bitmap.PixelWidth < bmp.Width || Bitmap.PixelHeight < bmp.Height)
            {
                // grow-only with headroom: values that change every second never allocate a new bitmap
                var w = Math.Max(bmp.Width + bmp.Width / 4, Bitmap?.PixelWidth ?? 0);
                var h = Math.Max(bmp.Height + bmp.Height / 4, Bitmap?.PixelHeight ?? 0);
                Bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
                _brush.ImageSource = Bitmap;
            }
            Bitmap.WritePixels(new Int32Rect(0, 0, bmp.Width, bmp.Height), bmp.Pixels, bmp.Stride, 0);
            _brush.Viewbox = new Rect(0, 0, bmp.Width, bmp.Height);
            Grid = grid;
            Px = dpi;
            ReferenceWidth = bmp.Width / (dpi * zoom);
            Picture.Width = bmp.Width / dpi;
            Picture.Height = bmp.Height / dpi;
            Surface.Width = Picture.Width;
            Surface.Height = Picture.Height;
            Picture.Visibility = Visibility.Visible;
            _empty.Visibility = Visibility.Collapsed;
            if (_hoverCell is { } h2 && !grid.Cells.Contains(h2)) Hover(null);
        }

        /// <summary>The point (surface coordinates) is on this board's stage, edges included.</summary>
        public bool StageContains(Point q)
        {
            var w = Surface.ActualWidth;
            var h = Surface.ActualHeight;
            return q.X >= -StagePad && q.Y >= -StagePad && q.X <= w + StagePad && q.Y <= h + StagePad;
        }

        public BoardTarget Target(Point q, bool external, OverlayGridCell? dragged)
        {
            if (Grid is not { } g) return new BoardTarget(new GridDrop(WindowId, GridDropKind.Cell, 0, 0), new Rect(Surface.RenderSize), Rect.Empty, false);
            return BoardHitTest.Find(WindowId, g, Px, q, (r, c) => g.Cells.Any(x => x.Row == r && x.Column == c && x != dragged), external);
        }

        private void Hover(Point? q)
        {
            OverlayGridCell? cell = q is { } p && Grid is { } g && _owner._drag is null ? BoardHitTest.CellAt(g, Px, p) : null;
            _hoverCell = cell;
            if (cell is { } c && Grid is { } grid && c.ItemId != Selected)
            {
                Place(_hover, Grow(BoardHitTest.CellRect(grid, Px, c.Row, c.Column), 3));
                _hover.Visibility = Visibility.Visible;
            }
            else
            {
                _hover.Visibility = Visibility.Collapsed;
            }
            Cursor = cell is not null ? Cursors.SizeAll : null;
        }

        public Rect? SelectionRect()
        {
            if (Selected is not { } id || Grid is not { } g || g.Cells.FirstOrDefault(c => c.ItemId == id) is not { ItemId: not null } cell) return null;
            return Grow(BoardHitTest.CellRect(g, Px, cell.Row, cell.Column), 4);
        }

        public void ShowSelection(string? selected, bool focused, bool buttons)
        {
            if (SelectionRect() is { } r)
            {
                Place(_selection, r);
                _selection.Visibility = Visibility.Visible;
                _selection.Opacity = focused ? 1 : 0.75;
                _selection.StrokeDashArray = focused ? null : [4, 2];
                Canvas.SetLeft(_remove, Math.Max(-StagePad + 2, r.Right - 11));
                Canvas.SetTop(_remove, Math.Max(-StagePad + 2, r.Top - 11));
                _remove.Visibility = buttons ? Visibility.Visible : Visibility.Collapsed;
                if (_hoverCell?.ItemId == selected) _hover.Visibility = Visibility.Collapsed;
            }
            else
            {
                _selection.Visibility = Visibility.Collapsed;
                _remove.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>Drag started: grid lines on, the dragged cell's place dimmed (when it is on this board).</summary>
        public void BeginDrag(OverlayGridCell? dragged)
        {
            _hover.Visibility = Visibility.Collapsed;
            _remove.Visibility = Visibility.Collapsed;
            _selection.Visibility = Visibility.Collapsed;
            if (Grid is not { } g) return;
            var lines = new GeometryGroup();
            var top = g.Rows > 0 ? g.RowTop[0] / Px - 4 : 0;
            var bottom = g.Rows > 0 ? g.RowBottom[g.Rows - 1] / Px + 4 : 0;
            var left = g.Columns > 0 ? g.ColumnLeft[0] / Px - 4 : 0;
            var right = g.Columns > 0 ? g.ColumnRight[g.Columns - 1] / Px + 4 : 0;
            for (var c = 0; c < g.Columns - 1; c++)
            {
                var x = Math.Round((g.ColumnRight[c] + g.ColumnLeft[c + 1]) / 2 / Px) + 0.5;
                lines.Children.Add(new LineGeometry(new Point(x, top), new Point(x, bottom)));
            }
            for (var r = 0; r < g.Rows - 1; r++)
            {
                var y = Math.Round((g.RowBottom[r] + g.RowTop[r + 1]) / 2 / Px) + 0.5;
                lines.Children.Add(new LineGeometry(new Point(left, y), new Point(right, y)));
            }
            lines.Freeze();
            _lines.Data = lines;
            _lines.Visibility = Visibility.Visible;
            if (dragged is { } d)
            {
                Place(_source, Grow(BoardHitTest.CellRect(g, Px, d.Row, d.Column), 3));
                _source.Visibility = Visibility.Visible;
            }
            Fade(_lines);
        }

        public void ShowTarget(BoardTarget? t)
        {
            if (t is not { } target)
            {
                _marker.Visibility = _slot.Visibility = Visibility.Collapsed;
                return;
            }
            var drop = target.Drop;
            var m = target.Marker;
            _marker.Visibility = Visibility.Visible;
            _slot.Visibility = Visibility.Collapsed;
            switch (drop.Kind)
            {
                case GridDropKind.Cell:
                    // swap: a filled accent box; a free cell or a new column: a dashed one
                    _marker.StrokeThickness = 2;
                    _marker.StrokeDashArray = target.Filled ? null : [4, 3];
                    _marker.RadiusX = _marker.RadiusY = 6;
                    _marker.SetResourceReference(Shape.FillProperty, "Brush.AccentSoft");
                    _marker.Opacity = target.Filled ? 0.95 : 0.85;
                    break;
                default:
                    // a bar between two cells / a line between two rows
                    _marker.StrokeThickness = 0;
                    _marker.StrokeDashArray = null;
                    _marker.RadiusX = _marker.RadiusY = 1.5;
                    _marker.SetResourceReference(Shape.FillProperty, "Brush.Accent");
                    _marker.Opacity = 1;
                    if (drop.Kind == GridDropKind.NewRow && !target.Slot.IsEmpty)
                    {
                        Place(_slot, OnStage(target.Slot));
                        _slot.Visibility = Visibility.Visible;
                    }
                    break;
            }
            Place(_marker, m.IsEmpty ? new Rect(0, 0, 1, 1) : OnStage(m));
        }

        /// <summary>A marker kept on the stage (a new column right of the picture shows at the stage's edge).</summary>
        private Rect OnStage(Rect r)
        {
            var stage = new Rect(-StagePad + 2, -StagePad + 2, Surface.ActualWidth + 2 * StagePad - 4, Surface.ActualHeight + 2 * StagePad - 4);
            var clipped = Rect.Intersect(r, stage);
            if (!clipped.IsEmpty && clipped.Width >= 3 && clipped.Height >= 2) return clipped;
            // entirely outside: the nearest strip along the stage's edge
            var x = Math.Clamp(r.Left, stage.Left, stage.Right - Math.Min(r.Width, 8));
            var y = Math.Clamp(r.Top, stage.Top, stage.Bottom - Math.Min(r.Height, 8));
            return new Rect(x, y, Math.Min(r.Width, stage.Right - x), Math.Min(r.Height, stage.Bottom - y));
        }

        public void EndDrag()
        {
            _lines.Visibility = Visibility.Collapsed;
            _source.Visibility = Visibility.Collapsed;
            _marker.Visibility = Visibility.Collapsed;
            _slot.Visibility = Visibility.Collapsed;
        }

        private static Rect Grow(Rect r, double by)
        {
            if (r.IsEmpty) return r;
            r.Inflate(by, by);
            return r;
        }

        private static void Place(FrameworkElement e, Rect r)
        {
            Canvas.SetLeft(e, r.Left);
            Canvas.SetTop(e, r.Top);
            e.Width = Math.Max(1, r.Width);
            e.Height = Math.Max(1, r.Height);
        }

        private static void Fade(UIElement e)
        {
            if (!Motion.I.Enabled) return;
            e.BeginAnimation(OpacityProperty, new DoubleAnimation(0, e.Opacity, TimeSpan.FromMilliseconds(140)) { EasingFunction = Motion.EaseOut });
        }
    }

    /// <summary>The boards side by side when both fit, otherwise one under the other (each as wide as the column).</summary>
    private sealed class BoardsPanel : System.Windows.Controls.Panel
    {
        private const double Gap = 14;
        private bool _row;

        protected override Size MeasureOverride(Size available)
        {
            var shown = InternalChildren.OfType<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
            foreach (var c in shown) c.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var total = shown.Sum(c => c.DesiredSize.Width) + Gap * Math.Max(0, shown.Count - 1);
            _row = shown.Count > 1 && (double.IsInfinity(available.Width) || total <= available.Width);
            if (_row) return new Size(total, shown.Max(c => c.DesiredSize.Height));
            var width = double.IsInfinity(available.Width) ? shown.Select(c => c.DesiredSize.Width).DefaultIfEmpty(0).Max() : available.Width;
            double height = 0;
            foreach (var c in shown)
            {
                c.Measure(new Size(width, double.PositiveInfinity));
                height += c.DesiredSize.Height;
            }
            height += Gap * Math.Max(0, shown.Count - 1);
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size final)
        {
            var shown = InternalChildren.OfType<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
            double at = 0;
            foreach (var c in shown)
            {
                if (_row)
                {
                    c.Arrange(new Rect(at, 0, c.DesiredSize.Width, final.Height));
                    at += c.DesiredSize.Width + Gap;
                }
                else
                {
                    c.Arrange(new Rect(0, at, final.Width, c.DesiredSize.Height));
                    at += c.DesiredSize.Height + Gap;
                }
            }
            return final;
        }
    }
}
