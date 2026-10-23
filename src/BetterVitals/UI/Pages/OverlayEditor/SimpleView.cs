using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BetterVitals.Core.Catalog;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Localization;
using BetterVitals.Overlay;
using BetterVitals.Services;
using BetterVitals.UI.Toasts;

namespace BetterVitals.UI.Pages.OverlayEditor;

/// <summary>
/// Simple mode (PROJEKT §8, user requirement: a normal user changes no sizes or colors): (1) what to show — friendly
/// parameter tiles per component with live values, click = on / off, order = click order; (2) where — a screen corner
/// (+ monitor); (3) when — always / only in games. Everything else stays at the defaults with automatic scaling.
/// </summary>
internal sealed class SimpleView : StackPanel
{
    /// <summary>The category the user looked at last (kept while the app runs).</summary>
    private static HardwareKind _lastCategory = HardwareKind.Cpu;

    private readonly EditorContext _ctx;
    private readonly Border _customBanner;
    private readonly Border _emptyBanner;
    private readonly Button _startButton;
    private readonly WrapPanel _chips;
    private readonly TileGrid _tiles;
    private readonly Dictionary<HardwareKind, (RadioButton Chip, TextBlock Count)> _chipMap = [];
    private readonly List<Tile> _tileList = [];
    private readonly Dictionary<OverlayAnchor, RadioButton> _corners = [];
    private readonly StackPanel _monitorHost;
    private readonly ComboBox _monitor;
    private readonly SegmentBar<OverlayVisibility> _when;
    private readonly TextBlock _whenHint;
    private HardwareKind _category;
    private bool _syncing;

    private sealed record Tile(FriendlyItem Item, ToggleButton Button, TextBlock Value, TextBlock Unit, MiniBars? Bars);

    public SimpleView(EditorContext ctx)
    {
        _ctx = ctx;

        // ── "you use your own look" (advanced customizations are kept, simple mode just says so) ──
        _customBanner = Banner("Icon.Palette", "ovl.simple.custom", "ovl.simple.custom.reset", (_, _) =>
        {
            var before = LayoutJson.Clone(_ctx.Layout);
            _ctx.Model.ResetLook();
            _ctx.Toast(Loc.T("ovl.toast.lookReset"), ToastKind.Success, Loc.T("common.undo"), () => _ctx.Model.Replace(before));
        }, "Brush.InfoSoft", "Brush.Info");
        _customBanner.Margin = new Thickness(0, 0, 0, 14);
        Children.Add(_customBanner);

        // ── 1. what ──
        var what = new StackPanel();
        _startButton = Ui.Button("PrimaryButton", "ovl.simple.empty.start", "Icon.Plus", (_, _) =>
        {
            var n = _ctx.Model.AddDefaults(_ctx.Friendly);
            if (n > 0) _ctx.Toast(Loc.T("ovl.toast.started"), ToastKind.Success, null, null);
        });
        _emptyBanner = Banner("Icon.Sparkle", "ovl.simple.empty", null, null, "Brush.AccentSoft", "Brush.AccentText", _startButton);
        _emptyBanner.Margin = new Thickness(0, 0, 0, 14);
        what.Children.Add(_emptyBanner);

        _chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        var group = Ui.NewGroup();
        foreach (var (kind, key) in EditorText.Categories)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = Ui.InheritIcon(SensorPresentation.IconKey(kind), 15);
            icon.Margin = new Thickness(0, 0, 6, 0);
            row.Children.Add(icon);
            var name = Ui.Text(key);
            name.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(name);
            var count = new TextBlock { Margin = new Thickness(6, 0, 0, 0), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            count.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentText");
            row.Children.Add(count);
            var chip = new RadioButton { GroupName = group, Content = row, Margin = new Thickness(0, 0, 8, 8), FontSize = 12.5 };
            chip.SetResourceReference(StyleProperty, "ChipToggle");
            var k = kind;
            chip.Checked += (_, _) =>
            {
                if (_syncing) return;
                _category = k;
                _lastCategory = k;
                BuildTiles();
                CategoryChanged?.Invoke();
            };
            _chipMap[kind] = (chip, count);
            _chips.Children.Add(chip);
        }
        what.Children.Add(_chips);
        _tiles = new TileGrid();
        what.Children.Add(_tiles);
        Children.Add(Ui.Section("ovl.simple.what", "ovl.simple.what.hint", what, step: "1"));

        // ── 2. where ──
        var where = new StackPanel();
        // four corners as a 2 × 2 grid, laid out like the screen
        var cornerGrid = new UniformGrid { Columns = 2, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 300 };
        var cornerGroup = Ui.NewGroup();
        foreach (var a in new[] { OverlayAnchor.TopLeft, OverlayAnchor.TopRight, OverlayAnchor.BottomLeft, OverlayAnchor.BottomRight })
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = Ui.InheritIcon("Icon.Anchor." + a, 17);
            icon.Margin = new Thickness(0, 0, 8, 0);
            content.Children.Add(icon);
            var t = Ui.Text("ovl.corner.short." + a, size: 12.5);
            t.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(t);
            var rb = new RadioButton { GroupName = cornerGroup, Content = content };
            rb.SetResourceReference(StyleProperty, "OvEd.Option");
            rb.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, Ui.Tr("ovl.corner." + a));
            var corner = a;
            rb.Checked += (_, _) =>
            {
                if (!_syncing) _ctx.Model.SetCorner(corner);
            };
            _corners[a] = rb;
            cornerGrid.Children.Add(rb);
        }
        where.Children.Add(cornerGrid);
        where.Children.Add(Ui.Hint("ovl.simple.where.mock", new Thickness(0, 8, 0, 0)));
        _monitor = new ComboBox { MinWidth = 240 };
        _monitor.SetResourceReference(StyleProperty, "Combo");
        _monitor.SelectionChanged += (_, _) => OnMonitorPicked();
        _monitorHost = Ui.Stacked("ovl.monitor", _monitor, 14);
        where.Children.Add(_monitorHost);
        Children.Add(Ui.Section("ovl.simple.where", "ovl.simple.where.hint", where, step: "2"));

        // ── 3. when ──
        var when = new StackPanel();
        _when = new SegmentBar<OverlayVisibility>(
            [(OverlayVisibility.Always, "ovl.when.always"), (OverlayVisibility.GamesOnly, "ovl.when.games")],
            ctx.Layout.Visibility, v => _ctx.Model.Edit(l => l.Visibility = v));
        when.Children.Add(_when);
        _whenHint = new TextBlock { Margin = new Thickness(0, 8, 0, 0) };
        _whenHint.SetResourceReference(StyleProperty, "Hint");
        when.Children.Add(_whenHint);
        Children.Add(Ui.Section("ovl.simple.when", null, when, step: "3"));

        // the last viewed category, or the first one this PC has
        _category = _lastCategory;
        Rebuild();
    }

    /// <summary>The tiles now show another category (the page updates its sensor demand).</summary>
    public event Action? CategoryChanged;

    /// <summary>Sensors whose live values the visible tiles show.</summary>
    public IEnumerable<string> VisibleSensorIds => _tileList.SelectMany(t => t.Item.SensorIds);

    /// <summary>Friendly items or monitors changed: rebuild chips and tiles.</summary>
    public void Rebuild()
    {
        if (!_ctx.Friendly.Any(f => f.Category == _category))
            _category = EditorText.Categories.Select(c => c.Kind).FirstOrDefault(k => _ctx.Friendly.Any(f => f.Category == k), _category);
        BuildTiles();
        BuildMonitors();
        Sync();
    }

    /// <summary>Re-reads the working layout (after any change).</summary>
    public void Sync()
    {
        _syncing = true;
        try
        {
            var layout = _ctx.Layout;
            foreach (var (kind, (chip, count)) in _chipMap)
            {
                var available = _ctx.Friendly.Any(f => f.Category == kind);
                chip.IsEnabled = available;
                chip.IsChecked = kind == _category;
                var on = _ctx.Friendly.Count(f => f.Category == kind && _ctx.Model.Find(f) is not null);
                count.Text = on > 0 ? on.ToString(Loc.I.Culture) : "";
                count.Visibility = on > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            foreach (var tile in _tileList)
            {
                var order = _ctx.Model.OrderOf(tile.Item);
                tile.Button.IsChecked = order > 0;
                tile.Button.Tag = order > 0 ? order.ToString(Loc.I.Culture) : "";
            }
            _emptyBanner.Visibility = layout.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _startButton.Visibility = FriendlyCatalog.DefaultOverlayKeys.Any(k => _ctx.Friendly.Any(f => f.Key == k)) ? Visibility.Visible : Visibility.Collapsed;
            _customBanner.Visibility = OverlayEditorModel.HasCustomizations(layout) ? Visibility.Visible : Visibility.Collapsed;
            var corner = OverlayEditorModel.NearestCorner(layout.Placement.Anchor);
            foreach (var (a, rb) in _corners) rb.IsChecked = a == corner;
            _when.Set(layout.Visibility);
            _whenHint.Text = Loc.T(layout.Visibility == OverlayVisibility.Always ? "ovl.when.always.hint" : "ovl.when.games.hint");
            SelectMonitor();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Live values on the tiles (editor tick, 1 Hz).</summary>
    public void UpdateValues()
    {
        foreach (var tile in _tileList)
        {
            var sensors = tile.Item.SensorIds.Select(_ctx.Hub.Find).Where(s => s is not null).Select(s => s!).ToList();
            if (sensors.Count == 0) continue;
            var values = sensors.Select(s => _ctx.Feed.Value(s.Id)).ToArray();
            if (tile.Bars is not null)
            {
                tile.Bars.SetValues(sensors[0], values);
                var max = values.Where(v => !float.IsNaN(v)).DefaultIfEmpty(float.NaN).Max();
                var (v, u) = _ctx.FormatParts(sensors[0], max);
                tile.Value.Text = float.IsNaN(max) ? "—" : Loc.T("ovl.tile.max", v);
                tile.Unit.Text = u;
            }
            else
            {
                var (v, u) = _ctx.FormatParts(sensors[0], values[0]);
                tile.Value.Text = v;
                tile.Unit.Text = u;
                tile.Value.SetResourceReference(TextBlock.ForegroundProperty, SensorPresentation.HeatBrushKey(SensorPresentation.Heat(sensors[0], values[0])));
            }
        }
    }

    private void BuildTiles()
    {
        _tiles.Children.Clear();
        _tileList.Clear();
        foreach (var f in _ctx.Friendly.Where(f => f.Category == _category))
        {
            var tile = MakeTile(f);
            _tileList.Add(tile);
            _tiles.Children.Add(tile.Button);
        }
        var all = new Button();
        all.SetResourceReference(StyleProperty, "OvEd.AddTile");
        var allContent = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var allIcon = Ui.InheritIcon("Icon.Search", 18);
        allContent.Children.Add(allIcon);
        var allText = Ui.Text("ovl.simple.allSensors", size: 12.5);
        allText.Margin = new Thickness(0, 6, 0, 0);
        allText.HorizontalAlignment = HorizontalAlignment.Center;
        allContent.Children.Add(allText);
        all.Content = allContent;
        all.Click += (_, _) => _ctx.OpenPicker(false);
        _tiles.Children.Add(all);
        UpdateValues();
        Sync();
    }

    private Tile MakeTile(FriendlyItem f)
    {
        var first = _ctx.Hub.Find(f.SensorIds[0]);
        // the title takes the slack of a taller neighbour (two-line title, core bars): values and captions line up
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new DockPanel();
        var icon = Ui.Icon(first is null ? "Icon.Pulse" : SensorPresentation.IconKey(first.Kind), 15, "Brush.AccentText");
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 1, 7, 0);
        head.Children.Add(icon);
        var name = new TextBlock { Text = EditorText.Friendly(f.Key), FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxHeight = 36, TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        head.Children.Add(name);
        grid.Children.Add(head);

        var valueRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        MiniBars? bars = null;
        var value = new TextBlock { FontSize = 19, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Bottom, Text = "—" };
        value.SetResourceReference(StyleProperty, "Text.Value");
        value.FontSize = f.IsMulti ? 13 : 19;
        var unit = new TextBlock { FontSize = 11.5, Margin = new Thickness(3, 0, 0, 2), VerticalAlignment = VerticalAlignment.Bottom };
        unit.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        if (f.IsMulti)
        {
            var multi = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            bars = new MiniBars { Height = 22, HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Min(146, f.SensorIds.Count * 9) };
            multi.Children.Add(bars);
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            line.Children.Add(value);
            line.Children.Add(unit);
            multi.Children.Add(line);
            Grid.SetRow(multi, 1);
            grid.Children.Add(multi);
        }
        else
        {
            valueRow.Children.Add(value);
            valueRow.Children.Add(unit);
            Grid.SetRow(valueRow, 1);
            grid.Children.Add(valueRow);
        }

        // verbatim sensor name(s) with the explanation bubble
        var sensorName = new TextBlock { FontSize = 11, Margin = new Thickness(0, 5, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        sensorName.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
        sensorName.Text = first is null ? "" : f.IsMulti ? $"{first.Name} … ({f.SensorIds.Count})" : first.Name;
        if (first is not null) Explain.Explain.SetSensor(sensorName, first);
        Grid.SetRow(sensorName, 2);
        grid.Children.Add(sensorName);

        var button = new ToggleButton { Content = grid };
        button.SetResourceReference(StyleProperty, "OvEd.Tile");
        System.Windows.Automation.AutomationProperties.SetName(button, EditorText.Friendly(f.Key));
        button.Click += (_, _) =>
        {
            _ctx.Model.Toggle(f);
            Sync();
        };
        return new Tile(f, button, value, unit, bars);
    }

    // ───────────── monitors ─────────────

    private sealed record MonitorChoice(OverlayMonitorMode Mode, string? Key, string Text)
    {
        public override string ToString() => Text;
    }

    private void BuildMonitors()
    {
        var monitors = AppServices.Overlay.Monitors;
        _monitorHost.Visibility = monitors.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _syncing = true;
        try
        {
            _monitor.Items.Clear();
            if (monitors.Count <= 1) return;
            _monitor.Items.Add(new MonitorChoice(OverlayMonitorMode.Primary, null, Loc.T("ovl.monitor.primary")));
            _monitor.Items.Add(new MonitorChoice(OverlayMonitorMode.FollowGame, null, Loc.T("ovl.monitor.game")));
            foreach (var m in monitors)
                _monitor.Items.Add(new MonitorChoice(OverlayMonitorMode.Specific, m.Key, $"{m.FriendlyName} · {m.Bounds.Width} × {m.Bounds.Height}"));
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SelectMonitor()
    {
        if (_monitor.Items.Count == 0) return;
        var p = _ctx.Layout.Placement;
        var pick = _monitor.Items.OfType<MonitorChoice>().FirstOrDefault(c => c.Mode == p.MonitorMode && (c.Mode != OverlayMonitorMode.Specific || string.Equals(c.Key, p.MonitorKey, StringComparison.OrdinalIgnoreCase)))
                   ?? _monitor.Items.OfType<MonitorChoice>().First();
        _monitor.SelectedItem = pick;
    }

    private void OnMonitorPicked()
    {
        if (_syncing || _monitor.SelectedItem is not MonitorChoice c) return;
        _ctx.Model.Edit(l =>
        {
            l.Placement.MonitorMode = c.Mode;
            if (c.Mode == OverlayMonitorMode.Specific) l.Placement.MonitorKey = c.Key;
        });
    }

    // ───────────── banners ─────────────

    internal static Border Banner(string iconKey, string textKey, string? linkKey, RoutedEventHandler? link, string background, string accent, UIElement? action = null)
    {
        var dock = new DockPanel();
        var icon = Ui.Icon(iconKey, 18, accent);
        icon.Margin = new Thickness(0, 0, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        dock.Children.Add(icon);
        if (linkKey is not null)
        {
            var b = Ui.Button("GhostButton", linkKey, "Icon.Refresh", link);
            b.Margin = new Thickness(12, 0, 0, 0);
            b.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(b, Dock.Right);
            dock.Children.Add(b);
        }
        var text = Ui.Text(textKey, size: 13, wrap: true);
        text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        text.VerticalAlignment = VerticalAlignment.Center;
        if (action is null)
        {
            dock.Children.Add(text);
        }
        else
        {
            // the action under the sentence: long texts (PL) never squeeze into a narrow column
            var stack = new StackPanel();
            stack.Children.Add(text);
            if (action is FrameworkElement fa)
            {
                fa.Margin = new Thickness(0, 10, 0, 2);
                fa.HorizontalAlignment = HorizontalAlignment.Left;
            }
            stack.Children.Add(action);
            dock.Children.Add(stack);
        }
        var border = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10, 10, 10), Child = dock };
        border.SetResourceReference(Border.BackgroundProperty, background);
        return border;
    }
}

/// <summary>Simple mode, next to the preview: the overlay's items in order, reordered by dragging, removable.</summary>
internal sealed class OrderPanel : StackPanel
{
    private readonly EditorContext _ctx;
    private readonly ReorderList _list = new();
    private readonly TextBlock _empty;
    private readonly List<(OverlayItem Item, TextBlock Value)> _values = [];

    public OrderPanel(EditorContext ctx)
    {
        _ctx = ctx;
        var title = Ui.Text("ovl.order.title", size: 14.5);
        title.FontWeight = FontWeights.SemiBold;
        title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        Children.Add(title);
        Children.Add(Ui.Hint("ovl.order.hint", new Thickness(0, 2, 0, 12)));
        _list.Moved += (id, index) =>
        {
            _ctx.Model.Move(id, index);
            Rebuild();
        };
        Children.Add(_list);
        _empty = Ui.Hint("ovl.order.empty", new Thickness(0, 0, 0, 0));
        Children.Add(_empty);
        Rebuild();
    }

    public int ListCount => _list.Count;

    public void Rebuild()
    {
        _list.Clear();
        _values.Clear();
        var items = _ctx.Layout.Items;
        _empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var row = new Border { Padding = new Thickness(4, 5, 4, 5) };
            row.SetResourceReference(StyleProperty, "OvEd.Row");
            var dock = new DockPanel();

            var handle = new Border { Width = 26, Height = 28, Background = Brushes.Transparent, CornerRadius = new CornerRadius(6) };
            handle.Child = Ui.Icon("OvEd.Icon.Grip", 16, "Brush.TextFaint", 2.6);
            handle.SetBinding(ToolTipProperty, Ui.Tr("ovl.order.drag"));
            dock.Children.Add(handle);

            var remove = Ui.IconButton("Icon.Close", "ovl.order.remove", (_, _) =>
            {
                _ctx.Model.Remove(item.Id);
                Rebuild();
            }, 14);
            remove.Width = 28;
            remove.Height = 28;
            DockPanel.SetDock(remove, Dock.Right);
            dock.Children.Add(remove);

            var value = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0), FontSize = 12.5 };
            value.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
            System.Windows.Documents.Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
            DockPanel.SetDock(value, Dock.Right);
            dock.Children.Add(value);
            _values.Add((item, value));

            var number = new TextBlock { Text = (i + 1).ToString(Loc.I.Culture), Width = 18, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            number.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentText");
            dock.Children.Add(number);
            var name = new TextBlock { Text = _ctx.Title(item), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
            if (_ctx.Sensors(item) is { Count: > 0 } sensors) Explain.Explain.SetSensor(name, sensors[0]);
            dock.Children.Add(name);
            row.Child = dock;
            _list.AddRow(item.Id, row, handle);
        }
        UpdateValues();
    }

    public void UpdateValues()
    {
        foreach (var (item, text) in _values)
        {
            var sensors = _ctx.Sensors(item);
            if (sensors.Count == 0)
            {
                text.Text = "—";
                continue;
            }
            if (sensors.Count == 1)
            {
                text.Text = _ctx.Format(sensors[0], _ctx.Feed.Value(sensors[0].Id));
                continue;
            }
            var max = sensors.Select(s => _ctx.Feed.Value(s.Id)).Where(v => !float.IsNaN(v)).DefaultIfEmpty(float.NaN).Max();
            text.Text = float.IsNaN(max) ? "—" : Loc.T("ovl.tile.max", _ctx.Format(sensors[0], max));
        }
    }
}

/// <summary>
/// Tiles in equal columns: as many ≥ <see cref="MinTileWidth"/> columns as fit (2 next to the preview, 3–4 in a wide
/// window), rows as tall as their tallest tile. Children's own margins are ignored in favor of <see cref="Gap"/>.
/// </summary>
internal sealed class TileGrid : System.Windows.Controls.Panel
{
    public double MinTileWidth { get; set; } = 150;

    public double Gap { get; set; } = 10;

    private int Columns(double width) => double.IsInfinity(width) ? 3 : Math.Max(1, (int)((width + Gap) / (MinTileWidth + Gap)));

    protected override Size MeasureOverride(Size available)
    {
        var cols = Columns(available.Width);
        var w = double.IsInfinity(available.Width) ? MinTileWidth + 20 : (available.Width - Gap * (cols - 1)) / cols;
        double height = 0;
        var rows = 0;
        for (var start = 0; start < InternalChildren.Count; start += cols)
        {
            double row = 0;
            for (var i = start; i < Math.Min(start + cols, InternalChildren.Count); i++)
            {
                var c = InternalChildren[i];
                if (c is FrameworkElement fe) fe.Margin = new Thickness(0);
                c.Measure(new Size(w, double.PositiveInfinity));
                row = Math.Max(row, c.DesiredSize.Height);
            }
            height += row;
            rows++;
        }
        if (rows > 1) height += Gap * (rows - 1);
        return new Size(double.IsInfinity(available.Width) ? cols * w + Gap * (cols - 1) : available.Width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var cols = Columns(final.Width);
        var w = (final.Width - Gap * (cols - 1)) / cols;
        double y = 0;
        for (var start = 0; start < InternalChildren.Count; start += cols)
        {
            double row = 0;
            for (var i = start; i < Math.Min(start + cols, InternalChildren.Count); i++) row = Math.Max(row, InternalChildren[i].DesiredSize.Height);
            for (var i = start; i < Math.Min(start + cols, InternalChildren.Count); i++)
                InternalChildren[i].Arrange(new Rect((i - start) * (w + Gap), y, w, row));
            y += row + Gap;
        }
        return final;
    }
}
