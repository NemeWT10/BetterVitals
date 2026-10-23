using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BetterVitals.Core;
using BetterVitals.Core.Overlay;
using BetterVitals.Localization;
using BetterVitals.Overlay;
using BetterVitals.Services;
using BetterVitals.UI.Toasts;
using Microsoft.Win32;

namespace BetterVitals.UI.Pages.OverlayEditor;

/// <summary>
/// Advanced mode (user requirement: customize everything "once and for all"): what is on the overlay as boxes, rows and
/// items (drag items between rows, into new rows and into boxes of their own; every per-item option), the global style,
/// the exact position of each box (drag in the preview, 9 anchors + X / Y, monitor, "Set on screen"), behavior and
/// profiles.
/// </summary>
internal sealed class AdvancedView : StackPanel
{
    private readonly EditorContext _ctx;
    private readonly LayoutTree _tree;
    private readonly SegmentBar<OverlayGrouping> _arrange;
    private readonly Border _placeCard;
    private readonly FrameworkElement _boxLine;
    private readonly ComboBox _box;
    private readonly Dictionary<OverlayAnchor, RadioButton> _anchors = [];
    private readonly TextBox _x;
    private readonly TextBox _y;
    private readonly SliderLine _margin;
    private readonly SegmentBar<OverlayMonitorMode> _monitorMode;
    private readonly ComboBox _monitor;
    private readonly FrameworkElement _monitorLine;
    private readonly TextBlock _monitorNote;
    private readonly Button _onScreen;
    private readonly StackPanel _onScreenActive;
    private readonly TextBlock _onScreenHint;
    private readonly FrameworkElement _tabKey;
    private readonly ComboBox _profiles;
    private readonly Button _load;
    private readonly Button _delete;
    private readonly TextBox _profileName;
    private readonly OverlayProfileStore _store;
    private bool _syncing;

    /// <summary>The box whose placement the Position section edits (kept across rebuilds while the app runs).</summary>
    private static string _boxId = OverlayLayout.MainBoxId;

    public AdvancedView(EditorContext ctx, OverlayProfileStore profiles)
    {
        _ctx = ctx;
        _store = profiles;
        var layout = ctx.Layout;

        // ── on the overlay: boxes, rows, items ──
        var items = new StackPanel();
        _arrange = new SegmentBar<OverlayGrouping>(
            [(OverlayGrouping.List, "ovl.group.list"), (OverlayGrouping.ByComponent, "ovl.group.byComponent"), (OverlayGrouping.OneLine, "ovl.group.oneLine")],
            layout.Grouping, OnArrange, fontSize: 12);
        var arrangeLine = Ui.Above("ovl.tree.arrange", _arrange, "ovl.tree.arrange.hint", top: 0);
        arrangeLine.Margin = new Thickness(0, 0, 0, 16);
        items.Children.Add(arrangeLine);
        _tree = new LayoutTree(ctx);
        _tree.PositionRequested += ShowPosition;
        _tree.BoxCreated += id =>
        {
            _boxId = id;
            BuildBoxes();
            Sync();
        };
        items.Children.Add(_tree);
        var addRow = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var add = Ui.Button("SecondaryButton", "ovl.items.add", "Icon.Plus", (_, _) => _ctx.OpenPicker(true));
        add.Margin = new Thickness(0, 0, 8, 0);
        addRow.Children.Add(add);
        var start = Ui.Button("GhostButton", "ovl.items.defaults", "Icon.Sparkle", (_, _) =>
        {
            if (_ctx.Model.AddDefaults(_ctx.Friendly) > 0) BuildItems();
        });
        addRow.Children.Add(start);
        items.Children.Add(addRow);
        Children.Add(Ui.Section("ovl.tree.title", "ovl.tree.hint", items, iconKey: "Icon.List"));

        // ── style ──
        Children.Add(Ui.Section("ovl.style.title", "ovl.style.hint", new StyleSection(ctx), iconKey: "Icon.Palette"));

        // ── placement (of the box chosen at the top) ──
        var place = new StackPanel();
        _box = new ComboBox { MinWidth = 240 };
        _box.SetResourceReference(StyleProperty, "Combo");
        _box.SelectionChanged += (_, _) =>
        {
            if (_syncing || _box.SelectedItem is not Choice<string> c) return;
            _boxId = c.Value;
            Sync();
        };
        _boxLine = Ui.Stacked("ovl.place.box", _box, top: 0);
        _boxLine.Margin = new Thickness(0, 0, 0, 14);
        place.Children.Add(_boxLine);
        place.Children.Add(Ui.Hint("ovl.place.dragHint", new Thickness(0, 0, 0, 12)));
        var grid = new UniformGrid { Rows = 3, Columns = 3, Width = 96, Height = 72 };
        var group = Ui.NewGroup();
        foreach (var a in Enum.GetValues<OverlayAnchor>())
        {
            var rb = new RadioButton { GroupName = group };
            rb.SetResourceReference(StyleProperty, "OvEd.Anchor");
            rb.SetBinding(ToolTipProperty, Ui.Tr("ovl.anchor." + a));
            System.Windows.Automation.AutomationProperties.SetName(rb, Loc.T("ovl.anchor." + a));
            var anchor = a;
            rb.Checked += (_, _) =>
            {
                if (_syncing) return;
                _ctx.Model.EditPlacement(_boxId, p =>
                {
                    p.Anchor = anchor;
                    p.OffsetX = 0;
                    p.OffsetY = 0;
                });
                Sync();
            };
            _anchors[a] = rb;
            grid.Children.Add(rb);
        }
        var anchorBox = new Border { Child = grid, Padding = new Thickness(4), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
        anchorBox.SetResourceReference(Border.BackgroundProperty, "Brush.BgAlt");
        anchorBox.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");

        _x = Number(v => _ctx.Model.EditPlacement(_boxId, p => p.OffsetX = v));
        _y = Number(v => _ctx.Model.EditPlacement(_boxId, p => p.OffsetY = v));
        var xy = new StackPanel { Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        xy.Children.Add(Field("X", _x));
        xy.Children.Add(Field("Y", _y, top: 8));
        var xyHint = Ui.Hint("ovl.place.xyHint", new Thickness(0, 8, 0, 0));
        xyHint.MaxWidth = 220;
        xy.Children.Add(xyHint);
        var anchorRow = new StackPanel { Orientation = Orientation.Horizontal };
        anchorRow.Children.Add(anchorBox);
        anchorRow.Children.Add(xy);
        place.Children.Add(Ui.Stacked("ovl.place.anchor", anchorRow, top: 0));
        _margin = new SliderLine(0, 80, layout.Placement.Margin, 1, v => v.ToString("0", Loc.I.Culture) + " px", v =>
        {
            var id = _boxId;
            _ctx.Defer(l =>
            {
                if (l.PlacementOf(id) is { } p) p.Margin = v;
            });
        });
        place.Children.Add(Ui.Line("ovl.place.margin", _margin, "ovl.place.margin.hint", top: 14));

        _monitorMode = new SegmentBar<OverlayMonitorMode>(
            [(OverlayMonitorMode.Primary, "ovl.monitor.mode.primary"), (OverlayMonitorMode.Specific, "ovl.monitor.mode.specific"), (OverlayMonitorMode.FollowGame, "ovl.monitor.mode.game")],
            layout.Placement.MonitorMode, OnMonitorMode, fontSize: 12);
        place.Children.Add(Ui.Stacked("ovl.monitor", _monitorMode, top: 14));
        _monitor = new ComboBox { MinWidth = 260 };
        _monitor.SetResourceReference(StyleProperty, "Combo");
        _monitor.SelectionChanged += (_, _) =>
        {
            if (_syncing || _monitor.SelectedItem is not Choice<string> c) return;
            _ctx.Model.EditPlacement(_boxId, p => p.MonitorKey = c.Value);
        };
        _monitorLine = Ui.Stacked("ovl.monitor.which", _monitor, top: 10);
        place.Children.Add(_monitorLine);
        _monitorNote = new TextBlock { Margin = new Thickness(0, 6, 0, 0) };
        _monitorNote.SetResourceReference(StyleProperty, "Hint");
        place.Children.Add(_monitorNote);

        // "Set on screen": every box of the real overlay becomes draggable; Done / Cancel here while it runs
        place.Children.Add(Ui.Divider());
        _onScreen = Ui.Button("PrimaryButton", "ovl.onScreen", "Icon.Monitor", (_, _) => BeginOnScreen());
        _onScreen.HorizontalAlignment = HorizontalAlignment.Left;
        _onScreen.Margin = new Thickness(0, 6, 0, 0);
        place.Children.Add(_onScreen);
        _onScreenHint = Ui.Hint("ovl.onScreen.hint", new Thickness(0, 6, 0, 0));
        place.Children.Add(_onScreenHint);
        _onScreenActive = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        var activeBanner = SimpleView.Banner("Icon.Monitor", "ovl.onScreen.active", null, null, "Brush.AccentSoft", "Brush.AccentText");
        _onScreenActive.Children.Add(activeBanner);
        var keys = new StackPanel { Margin = new Thickness(4, 10, 0, 0) };
        foreach (var (key, textKey) in new[] { ("← ↑ → ↓", "ovl.onScreen.keys.arrows"), ("Shift + ← ↑ → ↓", "ovl.onScreen.keys.shift"), ("Shift + " + Loc.T("ovl.onScreen.mouse"), "ovl.onScreen.keys.free"), ("Tab", "ovl.onScreen.keys.tab"), ("Enter", "ovl.onScreen.keys.enter"), ("Esc", "ovl.onScreen.keys.esc") })
            keys.Children.Add(KeyLine(key, textKey));
        _tabKey = keys.Children[3] as FrameworkElement ?? new Border();
        _onScreenActive.Children.Add(keys);
        var doneRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var done = Ui.Button("PrimaryButton", "ovl.onScreen.done", "Icon.Check", (_, _) => AppServices.Overlay.EndOnScreenEdit(true));
        done.Margin = new Thickness(0, 0, 8, 0);
        doneRow.Children.Add(done);
        doneRow.Children.Add(Ui.Button("SecondaryButton", "ovl.onScreen.cancel", null, (_, _) => AppServices.Overlay.EndOnScreenEdit(false)));
        _onScreenActive.Children.Add(doneRow);
        place.Children.Add(_onScreenActive);
        _placeCard = Ui.Section("ovl.place.title", "ovl.place.hint", place, iconKey: "Icon.Monitor");
        Children.Add(_placeCard);

        // ── behavior ──
        var behavior = new StackPanel();
        var refresh = new SliderLine(250, 2000, layout.RefreshMs, 250, Ms, v => _ctx.Defer(l => l.RefreshMs = (int)v), 240) { HorizontalAlignment = HorizontalAlignment.Left };
        behavior.Children.Add(Ui.Line("ovl.behavior.refresh", new Border(), "ovl.behavior.refresh.hint", top: 0));
        refresh.Margin = new Thickness(0, 8, 0, 4);
        behavior.Children.Add(refresh);
        var when = new SegmentBar<OverlayVisibility>(
            [(OverlayVisibility.Always, "ovl.when.always"), (OverlayVisibility.GamesOnly, "ovl.when.games")],
            layout.Visibility, v => _ctx.Model.Edit(l => l.Visibility = v), fontSize: 12);
        behavior.Children.Add(Ui.Line("ovl.behavior.when", when, "ovl.behavior.when.hint"));
        behavior.Children.Add(Ui.Line("ovl.behavior.capture", Ui.Switch(layout.HideFromCapture, v => _ctx.Model.Edit(l => l.HideFromCapture = v)), "ovl.behavior.capture.hint"));
        Children.Add(Ui.Section("ovl.behavior.title", null, behavior, iconKey: "Icon.Settings"));

        // ── profiles ──
        var prof = new StackPanel();
        _profiles = new ComboBox { MinWidth = 200 };
        _profiles.SetResourceReference(StyleProperty, "Combo");
        var saved = new WrapPanel();
        saved.Children.Add(_profiles);
        var load = _load = Ui.Button("SecondaryButton", "ovl.profiles.load", null, (_, _) => LoadProfile());
        load.Margin = new Thickness(8, 0, 0, 0);
        saved.Children.Add(load);
        var delete = _delete = Ui.IconButton("Icon.Trash", "ovl.profiles.delete", (_, _) => DeleteProfile());
        delete.Margin = new Thickness(4, 0, 0, 0);
        delete.Height = 34;
        saved.Children.Add(delete);
        prof.Children.Add(Ui.Stacked("ovl.profiles.saved", saved, top: 0));
        _profileName = new TextBox { Width = 200, MaxLength = 60 };
        _profileName.SetResourceReference(StyleProperty, "OvEd.SmallField");
        _profileName.SetBinding(TagProperty, Ui.Tr("ovl.profiles.name"));
        _profileName.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                SaveProfile();
            }
        };
        var saveRow = new WrapPanel();
        saveRow.Children.Add(_profileName);
        var save = Ui.Button("SecondaryButton", "ovl.profiles.save", "Icon.Check", (_, _) => SaveProfile());
        save.Margin = new Thickness(8, 0, 0, 0);
        saveRow.Children.Add(save);
        prof.Children.Add(Ui.Stacked("ovl.profiles.saveAs", saveRow));
        var files = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var export = Ui.Button("GhostButton", "ovl.profiles.export", "Icon.Export", (_, _) => Export());
        export.Margin = new Thickness(0, 0, 6, 0);
        files.Children.Add(export);
        var import = Ui.Button("GhostButton", "ovl.profiles.import", "Icon.Download", (_, _) => Import());
        import.Margin = new Thickness(0, 0, 6, 0);
        files.Children.Add(import);
        files.Children.Add(Ui.Button("GhostButton", "ovl.reset", "Icon.Refresh", (_, _) => ResetRequested?.Invoke()));
        prof.Children.Add(files);
        Children.Add(Ui.Section("ovl.profiles.title", "ovl.profiles.hint", prof, iconKey: "Icon.Layers"));

        BuildBoxes();
        BuildMonitors();
        BuildProfiles();
        Sync();
    }

    /// <summary>"Restore defaults" (the page asks and offers Undo).</summary>
    public event Action? ResetRequested;

    /// <summary>The "On the overlay" tree (tests).</summary>
    internal LayoutTree Tree => _tree;

    /// <summary>The box whose placement the Position section edits.</summary>
    internal string SelectedBox => _boxId;

    /// <summary>Re-reads placement after a drag / external change, the boxes and the item titles.</summary>
    public void Sync()
    {
        _syncing = true;
        try
        {
            if (_ctx.Layout.PlacementOf(_boxId) is null) _boxId = OverlayLayout.MainBoxId;
            if (_box.Items.Count != _ctx.Layout.Boxes.Count + 1) BuildBoxes();
            _boxLine.Visibility = _ctx.Layout.Boxes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _box.SelectedItem = _box.Items.OfType<Choice<string>>().FirstOrDefault(c => c.Value == _boxId);
            _arrange.Set(_ctx.Layout.Grouping);
            var p = _ctx.Layout.PlacementOf(_boxId)!;
            foreach (var (a, rb) in _anchors) rb.IsChecked = a == p.Anchor;
            if (!_x.IsKeyboardFocused) _x.Text = Num(p.OffsetX);
            if (!_y.IsKeyboardFocused) _y.Text = Num(p.OffsetY);
            _margin.Set(p.Margin);
            _monitorMode.Set(p.MonitorMode);
            var monitors = AppServices.Overlay.Monitors;
            _monitorLine.Visibility = p.MonitorMode == OverlayMonitorMode.Specific && monitors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _monitor.SelectedItem = _monitor.Items.OfType<Choice<string>>().FirstOrDefault(c => string.Equals(c.Value, p.MonitorKey, StringComparison.OrdinalIgnoreCase));
            _monitorNote.Text = Loc.T(p.MonitorMode switch
            {
                OverlayMonitorMode.Specific when monitors.Count == 0 => "ovl.monitor.none",
                OverlayMonitorMode.Specific => "ovl.monitor.specific.hint",
                OverlayMonitorMode.FollowGame => "ovl.monitor.game.hint",
                _ => "ovl.monitor.primary.hint",
            });
            var editing = AppServices.Overlay.IsEditingOnScreen;
            _onScreen.Visibility = _onScreenHint.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
            _onScreenActive.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
            _tabKey.Visibility = _ctx.Layout.Boxes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _tree.RefreshTitles();
        }
        finally
        {
            _syncing = false;
        }
    }

    public void UpdateValues() => _tree.UpdateValues();

    /// <summary>Rebuilds the boxes / rows / items tree (after items were added elsewhere, or a reload).</summary>
    public void BuildItems()
    {
        _tree.Build();
        BuildBoxes();
        Sync();
    }

    /// <summary>A box was dragged on the preview: its placement shows below.</summary>
    public void OnBoxDragged(string boxId)
    {
        _boxId = boxId;
        Sync();
        _tree.Build();
    }

    // ───────────── boxes ─────────────

    private void OnArrange(OverlayGrouping grouping)
    {
        if (_syncing) return;
        var before = LayoutJson.Clone(_ctx.Layout);
        var custom = _ctx.Layout.Grouping == OverlayGrouping.Custom;
        _ctx.Model.Regroup(grouping);
        _tree.Build();
        if (custom) _ctx.Toast(Loc.T("ovl.toast.regrouped"), ToastKind.Success, Loc.T("common.undo"), () => _ctx.Model.Replace(before));
    }

    private void BuildBoxes()
    {
        var was = _syncing;
        _syncing = true;
        try
        {
            _box.Items.Clear();
            foreach (var box in _ctx.Layout.AllBoxes()) _box.Items.Add(new Choice<string>(box.Id, _ctx.BoxTitle(box.Id)));
            _box.SelectedItem = _box.Items.OfType<Choice<string>>().FirstOrDefault(c => c.Value == _boxId);
        }
        finally
        {
            _syncing = was;
        }
    }

    /// <summary>The tree's "Position" of a box: that box in the Position section, scrolled into view.</summary>
    private void ShowPosition(string boxId)
    {
        _boxId = boxId;
        BuildBoxes();
        Sync();
        _placeCard.BringIntoView();
    }

    // ───────────── placement ─────────────

    private void OnMonitorMode(OverlayMonitorMode mode)
    {
        if (_syncing) return;
        var current = _ctx.Layout.PlacementOf(_boxId)!;
        var key = mode == OverlayMonitorMode.Specific
            ? current.MonitorKey ?? AppServices.Overlay.Monitors.FirstOrDefault(m => m.IsPrimary)?.Key ?? AppServices.Overlay.Monitors.FirstOrDefault()?.Key
            : current.MonitorKey;
        _ctx.Model.EditPlacement(_boxId, p =>
        {
            p.MonitorMode = mode;
            p.MonitorKey = key;
        });
        Sync();
    }

    public void BuildMonitors()
    {
        _syncing = true;
        try
        {
            _monitor.Items.Clear();
            foreach (var m in AppServices.Overlay.Monitors)
                _monitor.Items.Add(new Choice<string>(m.Key, $"{m.FriendlyName} · {m.Bounds.Width} × {m.Bounds.Height}{(m.IsPrimary ? " · " + Loc.T("ovl.monitor.isPrimary") : "")}"));
        }
        finally
        {
            _syncing = false;
        }
    }

    private void BeginOnScreen()
    {
        AppServices.Overlay.BeginOnScreenEdit(
            commit: placements =>
            {
                var placed = placements.ToDictionary(kv => kv.Key, kv => LayoutJson.Clone(kv.Value), StringComparer.Ordinal);
                _ctx.Model.Edit(l =>
                {
                    foreach (var (id, p) in placed)
                    {
                        if (l.PlacementOf(id) is not { } target) continue;
                        target.Anchor = p.Anchor;
                        target.OffsetX = p.OffsetX;
                        target.OffsetY = p.OffsetY;
                        target.Margin = p.Margin;
                        target.MonitorMode = p.MonitorMode;
                        target.MonitorKey = p.MonitorKey;
                    }
                });
                Sync();
                _tree.Build();
                _ctx.Toast(Loc.T("ovl.toast.placed"), ToastKind.Success, null, null);
            },
            cancel: Sync);
        Sync();
    }

    private static string Num(double v) => v.ToString(Math.Abs(v % 1) < 1e-6 ? "0" : "0.#", Loc.I.Culture);

    private static TextBox Number(Action<double> commit)
    {
        var box = new TextBox { Width = 76 };
        box.SetResourceReference(StyleProperty, "OvEd.SmallField");
        void Commit()
        {
            if (ThresholdsEditor.TryNumber(box.Text, out var v))
            {
                box.SetResourceReference(Control.ForegroundProperty, "Brush.Text");
                commit(Math.Round(Math.Clamp(v, -5000, 5000), 1));
            }
            else
            {
                box.SetResourceReference(Control.ForegroundProperty, "Brush.Danger");
            }
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Commit();
            }
        };
        return box;
    }

    private static StackPanel Field(string name, TextBox box, double top = 0)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, top, 0, 0) };
        var label = new TextBlock { Text = name, Width = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        row.Children.Add(label);
        row.Children.Add(box);
        var unit = new TextBlock { Text = "px", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        unit.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
        row.Children.Add(unit);
        return row;
    }

    private static DockPanel KeyLine(string keys, string textKey)
    {
        var d = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        var cap = new Border { Padding = new Thickness(7, 2, 7, 3), MinWidth = 120, Margin = new Thickness(0, 0, 12, 0) };
        cap.SetResourceReference(StyleProperty, "Tag");
        cap.Child = new TextBlock { Text = keys, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI") };
        d.Children.Add(cap);
        var t = Ui.Text(textKey, size: 12.5);
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        t.VerticalAlignment = VerticalAlignment.Center;
        d.Children.Add(t);
        return d;
    }

    /// <summary>Refresh in seconds ("0,25 s", "1 s"), as in Settings ▸ Overlay.</summary>
    private static string Ms(double v) => Loc.T("settings.common.seconds", (v / 1000).ToString("0.##", Loc.I.Culture));

    // ───────────── profiles ─────────────

    private void BuildProfiles()
    {
        _profiles.Items.Clear();
        foreach (var name in _store.List()) _profiles.Items.Add(name);
        if (_profiles.Items.Count == 0)
        {
            _profiles.Items.Add(new Choice<string?>(null, Loc.T("ovl.profiles.none")));
            _profiles.IsEnabled = false;
        }
        else
        {
            _profiles.IsEnabled = true;
        }
        _profiles.SelectedIndex = 0;
        _load.IsEnabled = _delete.IsEnabled = _profiles.IsEnabled;
    }

    private void SaveProfile()
    {
        var name = _profileName.Text.Trim();
        if (name.Length == 0)
        {
            _profileName.Focus();
            return;
        }
        try
        {
            _ctx.Model.Flush();
            _store.Save(name, _ctx.Layout);
            _profileName.Text = "";
            BuildProfiles();
            _profiles.SelectedItem = _profiles.Items.OfType<string>().FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            _ctx.Toast(Loc.T("ovl.toast.saved", name), ToastKind.Success, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("overlay profiles: save failed: " + ex.Message);
            _ctx.Toast(Loc.T("ovl.toast.saveFailed"), ToastKind.Error, null, null);
        }
    }

    private void LoadProfile()
    {
        if (_profiles.SelectedItem is not string name) return;
        try
        {
            var layout = _store.Load(name);
            if (layout is null) return;
            Apply(layout, name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            Log.Warn("overlay profiles: load failed: " + ex.Message);
            _ctx.Toast(Loc.T("ovl.toast.loadFailed"), ToastKind.Error, null, null);
        }
    }

    private void DeleteProfile()
    {
        if (_profiles.SelectedItem is not string name) return;
        var path = _store.PathOf(name);
        string? backup = null;
        try
        {
            backup = File.Exists(path) ? File.ReadAllText(path) : null;
            _store.Delete(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("overlay profiles: delete failed: " + ex.Message);
            return;
        }
        BuildProfiles();
        _ctx.Toast(Loc.T("ovl.toast.deleted", name), ToastKind.Info, backup is null ? null : Loc.T("common.undo"), backup is null ? null : () =>
        {
            try
            {
                Directory.CreateDirectory(_store.Directory);
                File.WriteAllText(path, backup);
                BuildProfiles();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("overlay profiles: undo delete failed: " + ex.Message);
            }
        });
    }

    private void Export()
    {
        var dialog = new SaveFileDialog
        {
            Title = Loc.T("ovl.profiles.export"),
            Filter = Loc.T("ovl.profiles.filter") + " (*.json)|*.json",
            FileName = "BetterVitals-overlay.json",
            AddExtension = true,
            DefaultExt = ".json",
        };
        if (dialog.ShowDialog(_ctx.Owner()) != true) return;
        try
        {
            _ctx.Model.Flush();
            File.WriteAllText(dialog.FileName, LayoutJson.WriteProfile(Path.GetFileNameWithoutExtension(dialog.FileName), _ctx.Layout));
            _ctx.Toast(Loc.T("ovl.toast.exported"), ToastKind.Success, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("overlay profiles: export failed: " + ex.Message);
            _ctx.Toast(Loc.T("ovl.toast.saveFailed"), ToastKind.Error, null, null);
        }
    }

    private void Import()
    {
        var dialog = new OpenFileDialog { Title = Loc.T("ovl.profiles.import"), Filter = Loc.T("ovl.profiles.filter") + " (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(_ctx.Owner()) != true) return;
        try
        {
            var info = new FileInfo(dialog.FileName);
            if (info.Length > 2 * 1024 * 1024) throw new FormatException("File too large.");
            var (name, layout) = LayoutJson.ReadProfile(File.ReadAllText(dialog.FileName));
            Apply(layout, name ?? Path.GetFileNameWithoutExtension(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            Log.Warn("overlay profiles: import failed: " + ex.Message);
            _ctx.Toast(Loc.T("ovl.toast.importFailed"), ToastKind.Error, null, null);
        }
    }

    private void Apply(OverlayLayout layout, string name)
    {
        var before = LayoutJson.Clone(_ctx.Layout);
        _ctx.Model.Replace(layout);
        _ctx.Toast(Loc.T("ovl.toast.loaded", name), ToastKind.Success, Loc.T("common.undo"), () => _ctx.Model.Replace(before));
    }
}
