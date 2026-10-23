using BetterVitals.Core.Catalog;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Settings;

namespace BetterVitals.UI.Pages.OverlayEditor;

/// <summary>
/// The overlay editor's state, without any WPF: a working copy of <c>Settings.Overlay</c> that the preview renders from,
/// and every change also replayed onto the settings through <see cref="SettingsStore.Mutate"/> ("overlay"), right away
/// or — for sliders — when <see cref="Flush"/> runs (the page debounces it). Changes from elsewhere (tray toggle,
/// another window) reload the copy. Changes address items by id, never by object, so the copy and the settings never
/// share mutable objects.
/// </summary>
internal sealed class OverlayEditorModel
{
    /// <summary>Default gap from the screen edge (reference px), as in <see cref="OverlayPlacement.Margin"/>.</summary>
    public const double DefaultMargin = 24;

    private readonly SettingsStore _store;
    private readonly List<Action<OverlayLayout>> _pending = [];
    private bool _self;

    public OverlayEditorModel(SettingsStore store)
    {
        _store = store;
        Working = LayoutJson.Clone(store.Current.Overlay);
        Working.Normalize();
    }

    /// <summary>What the editor shows and the preview renders (never handed out to other threads: clone it).</summary>
    public OverlayLayout Working { get; private set; }

    /// <summary>The working copy changed (an edit or a reload): re-render the preview, sync controls.</summary>
    public event Action? Changed;

    /// <summary>The working copy was replaced from the settings (external change, reset, profile): rebuild views.</summary>
    public event Action? Reloaded;

    public bool HasPending => _pending.Count > 0;

    /// <summary>Applies a change to the working copy and the settings (deferred: settings on <see cref="Flush"/>).</summary>
    public void Edit(Action<OverlayLayout> change, bool deferred = false)
    {
        change(Working);
        _pending.Add(change);
        if (!deferred) Flush();
        Changed?.Invoke();
    }

    /// <summary>
    /// A structural change that may not apply (an id that is gone, a move to where the item already is): applied to the
    /// working copy, and only when it changed something also to the settings. Returns whether it applied.
    /// </summary>
    public bool TryEdit(Func<OverlayLayout, bool> change)
    {
        if (!change(Working)) return false;
        _pending.Add(l => change(l));
        Flush();
        Changed?.Invoke();
        return true;
    }

    /// <summary>Changes one item (by its editor id).</summary>
    public void EditItem(string itemId, Action<OverlayItem> change, bool deferred = false)
        => Edit(l =>
        {
            if (l.FindItem(itemId) is { } item) change(item);
        }, deferred);

    /// <summary>Writes deferred changes to the settings now (debounce elapsed, page left).</summary>
    public void Flush()
    {
        if (_pending.Count == 0) return;
        var batch = _pending.ToArray();
        _pending.Clear();
        _self = true;
        try
        {
            _store.Mutate("overlay", s =>
            {
                foreach (var change in batch) change(s.Overlay);
            });
        }
        finally
        {
            _self = false;
        }
    }

    /// <summary>Replaces the whole layout (reset, profile, undo); the overlay's on / off state and the editor mode stay.</summary>
    public void Replace(OverlayLayout source)
    {
        Flush();
        var fresh = LayoutJson.Clone(source);
        fresh.Normalize();
        fresh.Enabled = Working.Enabled;
        fresh.Advanced = Working.Advanced;
        Working = LayoutJson.Clone(fresh);
        _self = true;
        try
        {
            _store.Mutate("overlay", s =>
            {
                var copy = LayoutJson.Clone(fresh);
                copy.Enabled = s.Overlay.Enabled;
                copy.Advanced = s.Overlay.Advanced;
                s.Overlay = copy;
            });
        }
        finally
        {
            _self = false;
        }
        Reloaded?.Invoke();
        Changed?.Invoke();
    }

    /// <summary>The settings changed (SettingsStore.Changed, UI thread): reload unless it was this editor.</summary>
    public void OnStoreChanged(string section)
    {
        if (_self || section != "overlay") return;
        Reload();
    }

    /// <summary>Takes the settings' layout again; deferred edits not written yet are kept on top.</summary>
    public void Reload()
    {
        var fresh = LayoutJson.Clone(_store.Current.Overlay);
        fresh.Normalize();
        foreach (var change in _pending) change(fresh);
        Working = fresh;
        Reloaded?.Invoke();
        Changed?.Invoke();
    }

    // ───────────── simple mode ─────────────

    /// <summary>An overlay item shows a friendly parameter when it has exactly its sensors.</summary>
    public static bool Matches(OverlayItem item, FriendlyItem friendly)
        => item.SensorIds.Count == friendly.SensorIds.Count && !item.SensorIds.Except(friendly.SensorIds, StringComparer.Ordinal).Any();

    public OverlayItem? Find(FriendlyItem friendly) => Working.AllItems().FirstOrDefault(i => Matches(i, friendly));

    /// <summary>1-based position on the overlay (main box first, row by row), or 0.</summary>
    public int OrderOf(FriendlyItem friendly)
    {
        var i = 0;
        foreach (var item in Working.AllItems())
        {
            i++;
            if (Matches(item, friendly)) return i;
        }
        return 0;
    }

    /// <summary>Click = on (into the main box the way it groups: order = click order) / off.</summary>
    public void Toggle(FriendlyItem friendly)
    {
        if (Find(friendly) is { } existing) Remove(existing.Id);
        else Add([.. friendly.SensorIds]);
    }

    /// <summary>A single sensor from the full list: on / off.</summary>
    public void ToggleSensor(string sensorId)
    {
        var existing = Working.AllItems().FirstOrDefault(i => i.SensorIds.Count == 1 && i.SensorIds[0] == sensorId);
        if (existing is not null) Remove(existing.Id);
        else Add([sensorId]);
    }

    public bool HasSensor(string sensorId) => Working.AllItems().Any(i => i.SensorIds.Count == 1 && i.SensorIds[0] == sensorId);

    /// <summary>Adds an item (one sensor, or several drawn together) to the main box by its grouping; returns its id.</summary>
    public string Add(IReadOnlyList<string> sensorIds, MultiValueDisplay display = MultiValueDisplay.Bars)
    {
        var id = OverlayLayout.NewId();
        var rowId = OverlayLayout.NewId();
        var ids = sensorIds.ToArray();
        Edit(l => l.AddItem(new OverlayItem { Id = id, SensorIds = [.. ids], MultiDisplay = display }, rowId));
        return id;
    }

    public void Remove(string itemId) => TryEdit(l => l.RemoveItem(itemId));

    /// <summary>
    /// Simple mode's list: moves an item to <paramref name="index"/> of its box's reading order (the main box is grouped
    /// again by its grouping).
    /// </summary>
    public void Move(string itemId, int index)
    {
        var prefix = IdPrefix();
        TryEdit(l => l.MoveFlat(itemId, index, prefix));
    }

    // ───────────── rows and boxes (advanced mode; every hand-made change makes the grouping Custom) ─────────────

    private static string IdPrefix() => OverlayLayout.NewId()[..10] + "-";

    private bool Structural(Func<OverlayLayout, bool> change) => TryEdit(l =>
    {
        if (!change(l)) return false;
        l.Grouping = OverlayGrouping.Custom;
        return true;
    });

    /// <summary>Into an existing row, before <paramref name="beforeItemId"/> (null = at the end).</summary>
    public bool MoveItem(string itemId, string rowId, string? beforeItemId) => Structural(l => l.MoveItem(itemId, rowId, beforeItemId));

    /// <summary>Into a new row of a box, before <paramref name="beforeRowId"/> (null = at the end).</summary>
    public bool MoveItemToNewRow(string itemId, string boxId, string? beforeRowId)
    {
        var rowId = OverlayLayout.NewId();
        return Structural(l => l.MoveItemToNewRow(itemId, boxId, beforeRowId, rowId));
    }

    /// <summary>Detaches an item into a box of its own, at the next free corner. Returns the new box id or null.</summary>
    public string? DetachItem(string itemId)
    {
        var boxId = OverlayLayout.NewId();
        var rowId = OverlayLayout.NewId();
        var placement = LayoutJson.Clone(Working.NewBoxPlacement());
        return Structural(l => l.MoveItemToNewBox(itemId, boxId, rowId, LayoutJson.Clone(placement))) ? boxId : null;
    }

    public bool MoveRow(string rowId, string boxId, string? beforeRowId) => Structural(l => l.MoveRow(rowId, boxId, beforeRowId));

    /// <summary>Detaches a row into a box of its own. Returns the new box id or null.</summary>
    public string? DetachRow(string rowId)
    {
        var boxId = OverlayLayout.NewId();
        var placement = LayoutJson.Clone(Working.NewBoxPlacement());
        return Structural(l => l.MoveRowToNewBox(rowId, boxId, LayoutJson.Clone(placement))) ? boxId : null;
    }

    public bool RemoveRow(string rowId) => Structural(l => l.RemoveRow(rowId));

    /// <summary>Removes a detached box; its rows go back to the main box.</summary>
    public bool RemoveBox(string boxId) => Structural(l => l.RemoveBox(boxId));

    public bool MoveBox(string boxId, string? beforeBoxId) => TryEdit(l => l.MoveBox(boxId, beforeBoxId));

    public bool RenameRow(string rowId, string? label) => TryEdit(l => l.FindRow(rowId) is { } r && r.Label != Clean(label) && l.SetRowLabel(rowId, label));

    public bool RenameBox(string boxId, string? name) => TryEdit(l => l.Boxes.Find(b => b.Id == boxId) is { } b && b.Name != Clean(name) && l.SetBoxName(boxId, name));

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim() is { Length: > 64 } t ? t[..64] : s.Trim();

    /// <summary>Changes the placement of a box (main or detached).</summary>
    public void EditPlacement(string boxId, Action<OverlayPlacement> change, bool deferred = false)
        => Edit(l =>
        {
            if (l.PlacementOf(boxId) is { } p) change(p);
        }, deferred);

    /// <summary>Simple mode: groups the main box again and takes the row style that goes with it (one line = compact).</summary>
    public void SetGrouping(OverlayGrouping grouping)
    {
        var prefix = IdPrefix();
        Edit(l =>
        {
            l.Regroup(grouping, prefix);
            if (grouping != OverlayGrouping.Custom) l.Style.Arrangement = OverlayLayoutEdits.ArrangementFor(grouping);
        });
    }

    /// <summary>Advanced mode's quick arrangement: groups the main box again, the row style stays.</summary>
    public void Regroup(OverlayGrouping grouping)
    {
        var prefix = IdPrefix();
        Edit(l => l.Regroup(grouping, prefix));
    }

    /// <summary>The overlay in a screen corner: anchor = corner, no extra offset, default margin.</summary>
    public void SetCorner(OverlayAnchor corner)
    {
        if (!IsCorner(corner)) throw new ArgumentOutOfRangeException(nameof(corner));
        Edit(l =>
        {
            l.Placement.Anchor = corner;
            l.Placement.OffsetX = 0;
            l.Placement.OffsetY = 0;
        });
    }

    public static bool IsCorner(OverlayAnchor a) => a is OverlayAnchor.TopLeft or OverlayAnchor.TopRight or OverlayAnchor.BottomLeft or OverlayAnchor.BottomRight;

    /// <summary>The corner simple mode highlights: the anchor's corner (centers lean top / left).</summary>
    public static OverlayAnchor NearestCorner(OverlayAnchor a)
    {
        var col = (int)a % 3;
        var row = (int)a / 3;
        var right = col == 2;
        var bottom = row == 2;
        return bottom ? right ? OverlayAnchor.BottomRight : OverlayAnchor.BottomLeft : right ? OverlayAnchor.TopRight : OverlayAnchor.TopLeft;
    }

    /// <summary>Adds the starter set (FPS, CPU temp, GPU temp) that this PC has and the overlay does not show yet.</summary>
    public int AddDefaults(IReadOnlyList<FriendlyItem> friendly)
    {
        var added = 0;
        foreach (var key in FriendlyCatalog.DefaultOverlayKeys)
        {
            if (friendly.FirstOrDefault(f => f.Key == key) is not { } f || Find(f) is not null) continue;
            Add([.. f.SensorIds]);
            added++;
        }
        return added;
    }

    /// <summary>The default overlay: starter items (a row per component), top-left, theme look, always visible, hidden from capture.</summary>
    public static OverlayLayout DefaultLayout(IReadOnlyList<FriendlyItem> friendly)
    {
        var layout = new OverlayLayout();
        foreach (var key in FriendlyCatalog.DefaultOverlayKeys)
            if (friendly.FirstOrDefault(f => f.Key == key) is { } f)
                layout.AddItem(new OverlayItem { SensorIds = [.. f.SensorIds] }, OverlayLayout.NewId());
        return layout;
    }

    public void ResetAll(IReadOnlyList<FriendlyItem> friendly) => Replace(DefaultLayout(friendly));

    public void SetAdvanced(bool advanced)
    {
        if (Working.Advanced == advanced) return;
        Edit(l => l.Advanced = advanced);
    }

    // ───────────── "custom look" (advanced customizations simple mode does not show) ─────────────

    private static readonly string DefaultStyleText = LayoutJson.Text(new OverlayStyle());

    /// <summary>A style other than the default one; the row style Simple mode's arrangement brings (one line = compact) is not custom.</summary>
    public static bool HasCustomStyle(OverlayLayout l)
    {
        var implied = l.Grouping == OverlayGrouping.Custom ? OverlayArrangement.Vertical : OverlayLayoutEdits.ArrangementFor(l.Grouping);
        if (l.Style.Arrangement != implied) return true;
        var plain = LayoutJson.Clone(l.Style);
        plain.Arrangement = OverlayArrangement.Vertical;
        return LayoutJson.Text(plain) != DefaultStyleText;
    }

    public static bool HasCustomPlacement(OverlayLayout l)
        => !IsCorner(l.Placement.Anchor) || l.Placement.OffsetX != 0 || l.Placement.OffsetY != 0 || l.Placement.Margin != DefaultMargin;

    public static bool HasCustomLook(OverlayItem item)
    {
        var plain = new OverlayItem { Id = item.Id, SensorIds = item.SensorIds };
        return LayoutJson.Text(plain) != LayoutJson.Text(item);
    }

    /// <summary>Detached boxes or named rows (Advanced mode only).</summary>
    public static bool HasCustomRows(OverlayLayout l) => l.Boxes.Count > 0 || l.AllBoxes().Any(b => b.Rows.Exists(r => r.Label is not null));

    /// <summary>Anything the simple mode does not show: style, per-item look, an exact position, detached boxes, row names.</summary>
    public static bool HasCustomizations(OverlayLayout l)
        => HasCustomStyle(l) || HasCustomPlacement(l) || HasCustomRows(l) || l.AllItems().Any(HasCustomLook);

    /// <summary>
    /// "Restore default look": theme style (with the row style of the arrangement), plain items (same sensors, same
    /// order), detached boxes back in the main box, rows without names (grouped again unless arranged by hand), the
    /// nearest corner.
    /// </summary>
    public void ResetLook()
    {
        var prefix = IdPrefix();
        Edit(l =>
        {
            var grouping = l.Grouping;
            l.Style = new OverlayStyle { Arrangement = grouping == OverlayGrouping.Custom ? OverlayArrangement.Vertical : OverlayLayoutEdits.ArrangementFor(grouping) };
            foreach (var box in l.Boxes.ToList()) l.RemoveBox(box.Id);
            foreach (var row in l.Rows)
            {
                row.Label = null;
                row.Items = row.Items.Select(i => new OverlayItem { Id = i.Id, SensorIds = [.. i.SensorIds] }).ToList();
            }
            l.Regroup(grouping, prefix);
            l.Placement.Anchor = NearestCorner(l.Placement.Anchor);
            l.Placement.OffsetX = 0;
            l.Placement.OffsetY = 0;
            l.Placement.Margin = DefaultMargin;
        });
    }
}
