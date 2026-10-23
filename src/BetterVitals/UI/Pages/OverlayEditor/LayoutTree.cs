using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using BetterVitals.Core.Overlay;
using BetterVitals.Localization;
using BetterVitals.Overlay;
using BetterVitals.UI.Toasts;

namespace BetterVitals.UI.Pages.OverlayEditor;

/// <summary>
/// Advanced mode's "On the overlay": the boxes (main first), their rows and items as nested cards. An item or a row is
/// dragged by its handle — between items, into another row, into the gap between two rows or onto a box's "New row"
/// zone, or onto "New box" to detach it — and an insertion line shows where it lands. Mouse capture only: no OLE
/// drag-and-drop, nothing global. A focused handle also takes Up / Down. Rows and boxes can be renamed, reordered and
/// removed. Every change goes to the model by id; the tree is rebuilt after it.
/// </summary>
internal sealed class LayoutTree : Grid
{
    private const double DragThreshold = 4;
    private const double ScrollEdge = 40;

    /// <summary>The item whose editor is open (kept across rebuilds while the app runs).</summary>
    internal static string? Expanded;

    private readonly EditorContext _ctx;
    private readonly StackPanel _stack = new();
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Rectangle _line;
    private readonly Rectangle _ghost;
    private readonly DispatcherTimer _scroll;
    private readonly List<BoxView> _boxes = [];
    private readonly Dictionary<string, FrameworkElement> _handles = new(StringComparer.Ordinal);
    private readonly List<(OverlayItem Item, TextBlock Title, TextBlock Value)> _values = [];
    private DropZone? _newBox;

    // ── drag state ──
    private DragKind _kind;
    private string? _dragId;
    private FrameworkElement? _source;
    private FrameworkElement? _handle;
    private ScrollViewer? _scroller;
    private Point _start;
    private Point _grab;
    private bool _started;
    private Target? _target;
    private double _scrollStep;

    private enum DragKind
    {
        None,
        Item,
        Row,
    }

    /// <summary>Where a dragged item or row lands.</summary>
    internal abstract record Target;

    /// <summary>Into an existing row, before an item (null = at the end).</summary>
    internal sealed record IntoRow(string RowId, string? BeforeItemId) : Target;

    /// <summary>A new row of a box (an item) or a row's new place in a box (a row), before a row (null = at the end).</summary>
    internal sealed record RowSlot(string BoxId, string? BeforeRowId) : Target;

    /// <summary>A new box of its own.</summary>
    internal sealed record NewBox : Target;

    private sealed class BoxView(string id, Border card, DropZone newRow)
    {
        public string Id { get; } = id;

        public Border Card { get; } = card;

        public DropZone NewRow { get; } = newRow;

        public List<RowView> Rows { get; } = [];
    }

    private sealed class RowView(string id, Border container)
    {
        public string Id { get; } = id;

        public Border Container { get; } = container;

        public List<(string Id, FrameworkElement Element)> Items { get; } = [];
    }

    public LayoutTree(EditorContext ctx)
    {
        _ctx = ctx;
        _line = new Rectangle { Height = 2, RadiusX = 1, RadiusY = 1, Visibility = Visibility.Collapsed };
        _line.SetResourceReference(Shape.FillProperty, "Brush.Accent");
        _ghost = new Rectangle { Visibility = Visibility.Collapsed, Opacity = 0.92, RadiusX = 10, RadiusY = 10 };
        _ghost.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.28, Direction = 270 };
        _overlay.Children.Add(_line);
        _overlay.Children.Add(_ghost);
        Children.Add(_stack);
        Children.Add(_overlay);
        _scroll = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
        _scroll.Tick += (_, _) => ScrollTick();
        Unloaded += (_, _) => CancelDrag();
        Build();
    }

    /// <summary>The "Position" of a box was asked for (its placement controls).</summary>
    public event Action<string>? PositionRequested;

    /// <summary>A new box was made from an item or a row (its id).</summary>
    public event Action<string>? BoxCreated;

    /// <summary>Items listed (tests).</summary>
    public int ItemCount => _values.Count;

    // ───────────── build ─────────────

    public void Build()
    {
        CancelDrag();
        _stack.Children.Clear();
        _boxes.Clear();
        _handles.Clear();
        _values.Clear();
        var layout = _ctx.Layout;
        foreach (var box in layout.AllBoxes()) _stack.Children.Add(BuildBox(box));
        var full = !layout.CanAddBox();
        _newBox = new DropZone("OvEd.Icon.Detach", Loc.T("ovl.tree.newBox"),
            full ? Loc.T("ovl.tree.newBox.full", (OverlayLayout.MaxBoxes - 1).ToString(Loc.I.Culture)) : Loc.T("ovl.tree.newBox.hint"), 54);
        _newBox.Margin = new Thickness(0, 2, 0, 0);
        if (full) _newBox.Opacity = 0.55;
        _stack.Children.Add(_newBox);
        UpdateValues();
    }

    private Border BuildBox(OverlayBoxRef box)
    {
        var card = new Border();
        card.SetResourceReference(StyleProperty, "OvEd.Box");
        var stack = new StackPanel();

        // header: name + where it sits | position, rename, up / down, remove
        var head = new DockPanel { LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var position = Ui.Button("GhostButton", "ovl.box.position", "Icon.Monitor", (_, _) => PositionRequested?.Invoke(box.Id));
        position.Margin = new Thickness(0, 0, 2, 0);
        buttons.Children.Add(position);
        var detachedIndex = _ctx.Layout.Boxes.FindIndex(b => b.Id == box.Id);
        if (!box.IsMain)
        {
            buttons.Children.Add(Small("OvEd.Icon.Edit", "ovl.box.rename", (_, _) => Rename(card, box.Id, isRow: false)));
            if (_ctx.Layout.Boxes.Count > 1)
            {
                var up = Small("Icon.ChevronUp", "ovl.box.up", (_, _) => MoveBox(box.Id, -1));
                up.IsEnabled = detachedIndex > 0;
                buttons.Children.Add(up);
                var down = Small("Icon.ChevronDown", "ovl.box.down", (_, _) => MoveBox(box.Id, 1));
                down.IsEnabled = detachedIndex < _ctx.Layout.Boxes.Count - 1;
                buttons.Children.Add(down);
            }
            buttons.Children.Add(Small("Icon.Trash", "ovl.box.remove", (_, _) => RemoveBox(box.Id)));
        }
        DockPanel.SetDock(buttons, Dock.Right);
        head.Children.Add(buttons);
        var badge = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        badge.SetResourceReference(Border.BackgroundProperty, box.IsMain ? "Brush.AccentSoft" : "Brush.SurfaceHover");
        badge.Child = Ui.Icon(box.IsMain ? "Icon.Overlay" : "OvEd.Icon.Detach", 15, "Brush.AccentText");
        head.Children.Add(badge);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = _ctx.BoxTitle(box.Id), FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Tag = "title" };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        titles.Children.Add(title);
        var where = new TextBlock { Text = _ctx.PlacementSummary(box.Placement), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
        where.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
        titles.Children.Add(where);
        head.Children.Add(titles);
        stack.Children.Add(head);

        var rows = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        stack.Children.Add(rows);
        var newRow = new DropZone("OvEd.Icon.NewRow", Loc.T("ovl.tree.newRow"), Loc.T("ovl.tree.newRow.hint"), 34);
        var view = new BoxView(box.Id, card, newRow);
        if (box.Rows.Count == 0)
            rows.Children.Add(Ui.Hint(box.IsMain ? "ovl.tree.mainEmpty" : "ovl.items.empty", new Thickness(0, 0, 0, 10)));
        foreach (var row in box.Rows)
        {
            var rv = BuildRow(row, box.Id);
            view.Rows.Add(rv);
            rows.Children.Add(rv.Container);
        }
        stack.Children.Add(newRow);
        card.Child = stack;
        _boxes.Add(view);
        return card;
    }

    private RowView BuildRow(OverlayRow row, string boxId)
    {
        var container = new Border();
        container.SetResourceReference(StyleProperty, "OvEd.RowGroup");
        var view = new RowView(row.Id, container);
        var stack = new StackPanel();

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
        var handle = Handle("ovl.row.drag", 24);
        head.Children.Add(handle);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Small("OvEd.Icon.Edit", "ovl.row.rename", (_, _) => Rename(container, row.Id, isRow: true)));
        var detach = Small("OvEd.Icon.Detach", "ovl.row.detach", (_, _) => DetachRow(row.Id));
        detach.IsEnabled = _ctx.Layout.CanAddBox() && (boxId == OverlayLayout.MainBoxId || _ctx.Layout.RowsOf(boxId)!.Count > 1);
        buttons.Children.Add(detach);
        buttons.Children.Add(Small("Icon.Trash", "ovl.row.remove", (_, _) => RemoveRow(row.Id)));
        DockPanel.SetDock(buttons, Dock.Right);
        head.Children.Add(buttons);
        var label = new TextBlock { Text = _ctx.RowLabel(row), FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Tag = "title" };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        var labelLine = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) };
        labelLine.Children.Add(label);
        if (row.Label is null)
        {
            var auto = new TextBlock { Text = Loc.T("ovl.row.auto"), FontSize = 11, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            auto.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
            labelLine.Children.Add(auto);
        }
        head.Children.Add(labelLine);
        stack.Children.Add(head);
        Arm(DragKind.Row, row.Id, container, handle);

        foreach (var item in row.Items)
        {
            var card = ItemCard(item, boxId);
            view.Items.Add((item.Id, card));
            stack.Children.Add(card);
        }
        container.Child = stack;
        return view;
    }

    private Border ItemCard(OverlayItem item, string boxId)
    {
        var card = new Border();
        card.SetResourceReference(StyleProperty, "OvEd.Row");
        var stack = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(4, 6, 6, 6) };

        var handle = Handle("ovl.item.drag", 30);
        head.Children.Add(handle);

        var remove = Small("Icon.Trash", "ovl.items.remove", (_, _) => RemoveItem(item));
        DockPanel.SetDock(remove, Dock.Right);
        head.Children.Add(remove);
        var detach = Small("OvEd.Icon.Detach", "ovl.item.detach", (_, _) => DetachItem(item.Id));
        detach.IsEnabled = _ctx.Layout.CanAddBox() && (boxId == OverlayLayout.MainBoxId || _ctx.Layout.Flatten(boxId).Count > 1);
        DockPanel.SetDock(detach, Dock.Right);
        head.Children.Add(detach);
        var open = Expanded == item.Id;
        var expand = new ToggleButton { IsChecked = open, Content = Ui.InheritIcon(open ? "Icon.ChevronUp" : "Icon.ChevronDown", 14), Width = 30, Height = 30 };
        expand.SetResourceReference(StyleProperty, "IconButton");
        expand.SetBinding(ToolTipProperty, Ui.Tr("ovl.items.edit"));
        System.Windows.Automation.AutomationProperties.SetName(expand, Loc.T("ovl.items.edit"));
        DockPanel.SetDock(expand, Dock.Right);
        head.Children.Add(expand);
        var value = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0), FontSize = 12.5 };
        value.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        System.Windows.Documents.Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
        DockPanel.SetDock(value, Dock.Right);
        head.Children.Add(value);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) };
        var title = new TextBlock { Text = _ctx.Title(item), FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        texts.Children.Add(title);
        var sensors = _ctx.Sensors(item);
        var names = new TextBlock { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
        names.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
        names.Text = sensors.Count switch
        {
            0 => item.SensorIds.FirstOrDefault() ?? "",
            1 => $"{sensors[0].Name} · {_ctx.Hub.FindHardware(sensors[0].HardwareId)?.Name ?? DefaultLabels.Hardware(sensors[0].HardwareKind)}",
            // the count first, so trimming a long list never eats it (nor stacks "…" on the trim's own "…")
            _ => $"{Loc.Plural("sensors.count", sensors.Count)} · {string.Join(", ", sensors.Take(3).Select(s => s.Name))}{(sensors.Count > 3 ? ", …" : "")}",
        };
        if (sensors.Count > 0) Explain.Explain.SetSensor(names, sensors[0]);
        texts.Children.Add(names);
        head.Children.Add(texts);
        stack.Children.Add(head);

        if (open)
        {
            stack.Children.Add(new ItemEditor(_ctx, item, () =>
            {
                title.Text = _ctx.Title(_ctx.Layout.FindItem(item.Id) ?? item);
                RefreshTitles();
            }));
            card.SetResourceReference(Border.BorderBrushProperty, "Brush.AccentGlow");
        }
        expand.Click += (_, _) =>
        {
            Expanded = Expanded == item.Id ? null : item.Id;
            Build();
        };
        card.Child = stack;
        Arm(DragKind.Item, item.Id, card, handle);
        _values.Add((item, title, value));
        return card;
    }

    private Border Handle(string tipKey, double height)
    {
        var handle = new Border { Width = 26, Height = height, Background = Brushes.Transparent, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center };
        handle.Child = Ui.Icon("OvEd.Icon.Grip", 16, "Brush.TextFaint", 2.6);
        handle.SetBinding(ToolTipProperty, Ui.Tr(tipKey));
        System.Windows.Automation.AutomationProperties.SetName(handle, Loc.T(tipKey));
        return handle;
    }

    private static Button Small(string icon, string tipKey, RoutedEventHandler click)
    {
        var b = Ui.IconButton(icon, tipKey, click, 14);
        b.Width = 30;
        b.Height = 30;
        System.Windows.Automation.AutomationProperties.SetName(b, Loc.T(tipKey));
        return b;
    }

    /// <summary>Live values next to the items (editor tick).</summary>
    public void UpdateValues()
    {
        foreach (var (item, _, value) in _values)
        {
            var sensors = _ctx.Sensors(item);
            if (sensors.Count == 0)
            {
                value.Text = "—";
            }
            else if (sensors.Count == 1)
            {
                value.Text = _ctx.Format(sensors[0], _ctx.Feed.Value(sensors[0].Id));
            }
            else
            {
                var max = sensors.Select(s => _ctx.Feed.Value(s.Id)).Where(v => !float.IsNaN(v)).DefaultIfEmpty(float.NaN).Max();
                value.Text = float.IsNaN(max) ? "—" : Loc.T("ovl.tile.max", _ctx.Format(sensors[0], max));
            }
        }
    }

    /// <summary>Item titles after a label edit (row labels follow them).</summary>
    public void RefreshTitles()
    {
        foreach (var (item, title, _) in _values)
            if (_ctx.Layout.FindItem(item.Id) is { } now) title.Text = _ctx.Title(now);
    }

    // ───────────── actions ─────────────

    private void RemoveItem(OverlayItem item)
    {
        var before = LayoutJson.Clone(_ctx.Layout);
        var title = _ctx.Title(item);
        if (!_ctx.Model.TryEdit(l => l.RemoveItem(item.Id))) return;
        Build();
        _ctx.Toast(Loc.T("ovl.toast.removed", title), ToastKind.Info, Loc.T("common.undo"), () => _ctx.Model.Replace(before));
    }

    private void RemoveRow(string rowId)
    {
        var before = LayoutJson.Clone(_ctx.Layout);
        if (!_ctx.Model.RemoveRow(rowId)) return;
        Build();
        _ctx.Toast(Loc.T("ovl.toast.rowRemoved"), ToastKind.Info, Loc.T("common.undo"), () => _ctx.Model.Replace(before));
    }

    private void RemoveBox(string boxId)
    {
        var before = LayoutJson.Clone(_ctx.Layout);
        if (!_ctx.Model.RemoveBox(boxId)) return;
        Build();
        _ctx.Toast(Loc.T("ovl.toast.boxRemoved"), ToastKind.Info, Loc.T("common.undo"), () => _ctx.Model.Replace(before));
    }

    private void MoveBox(string boxId, int dir)
    {
        var boxes = _ctx.Layout.Boxes;
        var i = boxes.FindIndex(b => b.Id == boxId);
        var to = i + dir;
        if (i < 0 || to < 0 || to >= boxes.Count) return;
        var before = dir < 0 ? boxes[to].Id : to + 1 < boxes.Count ? boxes[to + 1].Id : null;
        if (_ctx.Model.MoveBox(boxId, before)) Build();
    }

    private void DetachItem(string itemId)
    {
        if (_ctx.Model.DetachItem(itemId) is { } box) Detached(box);
    }

    private void DetachRow(string rowId)
    {
        if (_ctx.Model.DetachRow(rowId) is { } box) Detached(box);
    }

    private void Detached(string boxId)
    {
        Build();
        BoxCreated?.Invoke(boxId);
        _ctx.Toast(Loc.T("ovl.toast.detached"), ToastKind.Success, null, null);
    }

    /// <summary>Swaps a row's label / a box's name for a text box: Enter or leaving it saves, Esc keeps the old one.</summary>
    private void Rename(Border container, string id, bool isRow)
    {
        if (FindTitle(container) is not { Parent: System.Windows.Controls.Panel host } title) return;
        var current = isRow ? _ctx.Layout.FindRow(id)?.Label : _ctx.Layout.Boxes.Find(b => b.Id == id)?.Name;
        var auto = isRow && _ctx.Layout.FindRow(id) is { } r ? _ctx.RowLabel(new OverlayRow { Items = r.Items }) : Loc.T("ovl.box.n", (_ctx.Layout.Boxes.FindIndex(b => b.Id == id) + 2).ToString(Loc.I.Culture));
        var box = new TextBox { Text = current ?? "", Tag = auto, MinWidth = 160, MaxLength = 40 };
        box.SetResourceReference(StyleProperty, "OvEd.SmallField");
        System.Windows.Automation.AutomationProperties.SetName(box, Loc.T(isRow ? "ovl.row.rename" : "ovl.box.rename"));
        var index = host.Children.IndexOf(title);
        host.Children.RemoveAt(index);
        host.Children.Insert(index, box);
        var done = false;
        void Finish(bool save)
        {
            if (done) return;
            done = true;
            var text = box.Text.Trim();
            if (save) _ = isRow ? _ctx.Model.RenameRow(id, text) : _ctx.Model.RenameBox(id, text);
            Build();
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Finish(true); }
            else if (e.Key == Key.Escape) { e.Handled = true; Finish(false); }
        };
        box.LostKeyboardFocus += (_, _) => Finish(true);
        box.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
    }

    private static TextBlock? FindTitle(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock { Tag: "title" } t) return t;
            if (FindTitle(child) is { } found) return found;
        }
        return null;
    }

    // ───────────── keyboard: a focused handle moves with Up / Down ─────────────

    private void OnKey(DragKind kind, string id, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _kind != DragKind.None)
        {
            e.Handled = true;
            CancelDrag();
            return;
        }
        if (e.Key is not (Key.Up or Key.Down)) return;
        var dir = e.Key == Key.Up ? -1 : 1;
        var layout = _ctx.Layout;
        var moved = false;
        if (kind == DragKind.Item && layout.Locate(id) is { } slot)
        {
            var items = slot.Row.Items;
            if (items.Count == 1)
            {
                // alone in its row: the row moves, like a line of a list
                moved = MoveRowBy(slot.Row.Id, dir);
            }
            else
            {
                var to = slot.Index + dir;
                if (to >= 0 && to < items.Count)
                {
                    var before = dir < 0 ? items[to].Id : to + 1 < items.Count ? items[to + 1].Id : null;
                    moved = _ctx.Model.MoveItem(id, slot.Row.Id, before);
                }
            }
        }
        else if (kind == DragKind.Row)
        {
            moved = MoveRowBy(id, dir);
        }
        e.Handled = true;
        if (!moved) return;
        Build();
        if (_handles.TryGetValue(id, out var handle)) handle.Focus();
    }

    private bool MoveRowBy(string rowId, int dir)
    {
        if (_ctx.Layout.LocateRow(rowId) is not { } at) return false;
        var rows = _ctx.Layout.RowsOf(at.BoxId)!;
        var to = at.Index + dir;
        if (to < 0 || to >= rows.Count) return false;
        var before = dir < 0 ? rows[to].Id : to + 1 < rows.Count ? rows[to + 1].Id : null;
        return _ctx.Model.MoveRow(rowId, at.BoxId, before);
    }

    // ───────────── drag ─────────────

    private void Arm(DragKind kind, string id, FrameworkElement source, FrameworkElement handle)
    {
        _handles[id] = handle;
        handle.Cursor = Cursors.SizeAll;
        handle.Focusable = true;
        handle.FocusVisualStyle = null;
        handle.MouseLeftButtonDown += (_, e) =>
        {
            if (_kind != DragKind.None) return;
            _kind = kind;
            _dragId = id;
            _source = source;
            _handle = handle;
            _start = e.GetPosition(this);
            _grab = e.GetPosition(source);
            _started = false;
            _target = null;
            handle.Focus();
            handle.CaptureMouse();
            e.Handled = true;
        };
        handle.MouseMove += (_, e) =>
        {
            if (_kind != DragKind.None && _handle == handle) OnMove(e.GetPosition(this));
        };
        handle.MouseLeftButtonUp += (_, e) =>
        {
            if (_kind == DragKind.None || _handle != handle) return;
            e.Handled = true;
            DropNow();
        };
        handle.LostMouseCapture += (_, _) =>
        {
            if (_handle == handle) CancelDrag();
        };
        handle.KeyDown += (_, e) => OnKey(kind, id, e);
    }

    private void OnMove(Point p)
    {
        if (!_started)
        {
            if ((p - _start).Length < DragThreshold) return;
            BeginVisuals();
            _started = true;
        }
        Canvas.SetLeft(_ghost, p.X - _grab.X);
        Canvas.SetTop(_ghost, p.Y - _grab.Y);
        _target = HitTest(p);
        ShowTarget(_target);
        AutoScroll(p);
    }

    private void BeginVisuals()
    {
        var src = _source!;
        var dpi = VisualTreeHelper.GetDpi(src);
        var w = Math.Max(1, src.ActualWidth);
        var h = Math.Max(1, src.ActualHeight);
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(w * dpi.DpiScaleX), (int)Math.Ceiling(h * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(src);
        bmp.Freeze();
        _ghost.Fill = new ImageBrush(bmp) { Stretch = Stretch.Fill };
        _ghost.Width = w;
        _ghost.Height = h;
        _ghost.Visibility = Visibility.Visible;
        src.Opacity = 0.35;
        _scroller = FindScroller(this);
        foreach (var b in _boxes) b.NewRow.SetState(_kind == DragKind.Item ? DropState.Armed : DropState.Idle);
        _newBox?.SetState(_ctx.Layout.CanAddBox() ? DropState.Armed : DropState.Idle);
    }

    private static ScrollViewer? FindScroller(DependencyObject from)
    {
        for (var d = VisualTreeHelper.GetParent(from); d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is ScrollViewer sv) return sv;
        return null;
    }

    /// <summary>Where the dragged item / row would land with the mouse at <paramref name="p"/> (tree coordinates).</summary>
    private Target? HitTest(Point p)
    {
        if (_newBox is not null && Bounds(_newBox).Contains(p)) return _ctx.Layout.CanAddBox() ? new NewBox() : null;
        foreach (var box in _boxes)
        {
            var card = Bounds(box.Card);
            if (p.Y < card.Top || p.Y > card.Bottom + 6) continue;
            if (Bounds(box.NewRow).Contains(p) || p.Y > Bounds(box.NewRow).Top) return new RowSlot(box.Id, null);
            if (_kind == DragKind.Row)
            {
                foreach (var row in box.Rows)
                {
                    var r = Bounds(row.Container);
                    if (p.Y < r.Top + r.Height / 2) return new RowSlot(box.Id, row.Id);
                }
                return new RowSlot(box.Id, null);
            }
            foreach (var row in box.Rows)
            {
                var r = Bounds(row.Container);
                // the strip just above a row (the gap between two rows): a new row there
                if (p.Y < r.Top + 3) return new RowSlot(box.Id, row.Id);
                if (p.Y > r.Bottom) continue;
                foreach (var (id, element) in row.Items)
                {
                    var e = Bounds(element);
                    if (p.Y < e.Top + e.Height / 2) return new IntoRow(row.Id, id);
                }
                return new IntoRow(row.Id, null);
            }
            return new RowSlot(box.Id, null);
        }
        return null;
    }

    private Rect Bounds(FrameworkElement e)
    {
        if (!e.IsVisible && e.ActualWidth == 0) return Rect.Empty;
        var at = e.TranslatePoint(new Point(0, 0), this);
        return new Rect(at, new Size(e.ActualWidth, e.ActualHeight));
    }

    private void ShowTarget(Target? t)
    {
        foreach (var b in _boxes) b.NewRow.SetState(_kind == DragKind.Item ? DropState.Armed : DropState.Idle);
        _newBox?.SetState(_ctx.Layout.CanAddBox() ? DropState.Armed : DropState.Idle);
        _line.Visibility = Visibility.Collapsed;
        switch (t)
        {
            case NewBox:
                _newBox?.SetState(DropState.Hot);
                break;
            case RowSlot { BeforeRowId: null } s when _kind == DragKind.Item:
                _boxes.Find(b => b.Id == s.BoxId)?.NewRow.SetState(DropState.Hot);
                break;
            case RowSlot s when _boxes.Find(b => b.Id == s.BoxId) is { } box:
            {
                var rows = Bounds(box.Card);
                double y;
                if (s.BeforeRowId is { } before && box.Rows.Find(r => r.Id == before) is { } row) y = Bounds(row.Container).Top - 5;
                else if (box.Rows.Count > 0) y = Bounds(box.Rows[^1].Container).Bottom - 4;
                else y = Bounds(box.NewRow).Top - 6;
                Line(rows.Left + 10, rows.Right - 10, y);
                break;
            }
            case IntoRow r when FindRow(r.RowId) is { } row:
            {
                var area = Bounds(row.Container);
                double y;
                if (r.BeforeItemId is { } before && row.Items.Find(i => i.Id == before) is { Element: { } el }) y = Bounds(el).Top - 4;
                else if (row.Items.Count > 0) y = Bounds(row.Items[^1].Element).Bottom - 2;
                else y = area.Bottom;
                Line(area.Left + 10, area.Right, y);
                break;
            }
        }
    }

    private RowView? FindRow(string rowId) => _boxes.SelectMany(b => b.Rows).FirstOrDefault(r => r.Id == rowId);

    private void Line(double left, double right, double y)
    {
        _line.Width = Math.Max(10, right - left);
        Canvas.SetLeft(_line, left);
        Canvas.SetTop(_line, Math.Round(y) - 1);
        _line.Visibility = Visibility.Visible;
    }

    private void AutoScroll(Point p)
    {
        if (_scroller is null)
        {
            _scrollStep = 0;
            return;
        }
        var y = TranslatePoint(p, _scroller).Y;
        _scrollStep = y < ScrollEdge ? -(ScrollEdge - y) / 2.5 : y > _scroller.ActualHeight - ScrollEdge ? (y - (_scroller.ActualHeight - ScrollEdge)) / 2.5 : 0;
        if (_scrollStep != 0 && !_scroll.IsEnabled) _scroll.Start();
        else if (_scrollStep == 0) _scroll.Stop();
    }

    private void ScrollTick()
    {
        if (_kind == DragKind.None || _scroller is null || _scrollStep == 0)
        {
            _scroll.Stop();
            return;
        }
        _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset + _scrollStep);
        _scroller.UpdateLayout();
        OnMove(Mouse.GetPosition(this));
    }

    private void DropNow()
    {
        var kind = _kind;
        var id = _dragId!;
        var target = _target;
        var started = _started;
        CancelDrag();
        if (!started || target is null) return;
        string? box = null;
        var changed = (kind, target) switch
        {
            (DragKind.Item, IntoRow r) => _ctx.Model.MoveItem(id, r.RowId, r.BeforeItemId),
            (DragKind.Item, RowSlot s) => _ctx.Model.MoveItemToNewRow(id, s.BoxId, s.BeforeRowId),
            (DragKind.Item, NewBox) => (box = _ctx.Model.DetachItem(id)) is not null,
            (DragKind.Row, RowSlot s) => _ctx.Model.MoveRow(id, s.BoxId, s.BeforeRowId),
            (DragKind.Row, NewBox) => (box = _ctx.Model.DetachRow(id)) is not null,
            _ => false,
        };
        if (box is not null) Detached(box);
        else if (changed) Build();
    }

    /// <summary>Applies a drop as if dragged there (tests: the same path as the mouse, without the mouse).</summary>
    internal bool DropForTest(bool row, string id, Target target)
    {
        _kind = row ? DragKind.Row : DragKind.Item;
        _dragId = id;
        _target = target;
        _started = true;
        var before = LayoutJson.Text(_ctx.Layout);
        DropNow();
        return LayoutJson.Text(_ctx.Layout) != before;
    }

    private void CancelDrag()
    {
        _scroll.Stop();
        _scrollStep = 0;
        if (_kind == DragKind.None) return;
        _kind = DragKind.None;
        var handle = _handle;
        _handle = null;
        if (_source is not null) _source.Opacity = 1;
        _source = null;
        _target = null;
        _ghost.Visibility = Visibility.Collapsed;
        _ghost.Fill = null;
        _line.Visibility = Visibility.Collapsed;
        foreach (var b in _boxes) b.NewRow.SetState(DropState.Idle);
        _newBox?.SetState(DropState.Idle);
        if (handle is { IsMouseCaptured: true }) handle.ReleaseMouseCapture();
    }

    internal enum DropState
    {
        Idle,
        Armed,
        Hot,
    }

    /// <summary>A dashed drop area ("New row", "New box"): quiet at rest, accent while dragging, filled under the mouse.</summary>
    internal sealed class DropZone : Grid
    {
        private readonly Rectangle _dash;
        private readonly Border _fill;

        public DropZone(string iconKey, string title, string hint, double minHeight)
        {
            MinHeight = minHeight;
            _fill = new Border { CornerRadius = new CornerRadius(10), Opacity = 0 };
            _fill.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSoft");
            _dash = new Rectangle { RadiusX = 10, RadiusY = 10, StrokeThickness = 1.2, StrokeDashArray = [4, 3] };
            _dash.SetResourceReference(Shape.StrokeProperty, "Brush.Border");
            // icon, then "Title — what it does" wrapping inside the zone, centered
            var content = new DockPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 6, 12, 6) };
            var icon = Ui.Icon(iconKey, 15, "Brush.TextDim");
            icon.Margin = new Thickness(0, 0, 8, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(icon);
            var text = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            var head = new System.Windows.Documents.Run(title) { FontWeight = FontWeights.SemiBold, FontSize = 12.5 };
            head.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Brush.TextDim");
            var tail = new System.Windows.Documents.Run(" — " + hint);
            tail.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Brush.TextFaint");
            text.Inlines.Add(head);
            text.Inlines.Add(tail);
            content.Children.Add(text);
            Children.Add(_fill);
            Children.Add(_dash);
            Children.Add(content);
        }

        public DropState State { get; private set; }

        public void SetState(DropState state)
        {
            State = state;
            _dash.SetResourceReference(Shape.StrokeProperty, state == DropState.Idle ? "Brush.Border" : "Brush.Accent");
            _fill.Opacity = state == DropState.Hot ? 1 : 0;
        }
    }
}
