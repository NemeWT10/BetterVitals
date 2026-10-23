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

    /// <summary>Changes one item (by its editor id).</summary>
    public void EditItem(string itemId, Action<OverlayItem> change, bool deferred = false)
        => Edit(l =>
        {
            var item = l.Items.Find(i => i.Id == itemId);
            if (item is not null) change(item);
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

    public OverlayItem? Find(FriendlyItem friendly) => Working.Items.Find(i => Matches(i, friendly));

    /// <summary>1-based position on the overlay, or 0.</summary>
    public int OrderOf(FriendlyItem friendly)
    {
        var i = Working.Items.FindIndex(x => Matches(x, friendly));
        return i + 1;
    }

    /// <summary>Click = on (appended: order = click order) / off.</summary>
    public void Toggle(FriendlyItem friendly)
    {
        if (Find(friendly) is { } existing) Remove(existing.Id);
        else Add([.. friendly.SensorIds]);
    }

    /// <summary>A single sensor from the full list: on / off.</summary>
    public void ToggleSensor(string sensorId)
    {
        var existing = Working.Items.Find(i => i.SensorIds.Count == 1 && i.SensorIds[0] == sensorId);
        if (existing is not null) Remove(existing.Id);
        else Add([sensorId]);
    }

    public bool HasSensor(string sensorId) => Working.Items.Exists(i => i.SensorIds.Count == 1 && i.SensorIds[0] == sensorId);

    /// <summary>Adds an item (one sensor, or several drawn together); returns its id.</summary>
    public string Add(IReadOnlyList<string> sensorIds, MultiValueDisplay display = MultiValueDisplay.Bars)
    {
        var id = Guid.NewGuid().ToString("N");
        var ids = sensorIds.ToArray();
        Edit(l => l.Items.Add(new OverlayItem { Id = id, SensorIds = [.. ids], MultiDisplay = display }));
        return id;
    }

    public void Remove(string itemId) => Edit(l => l.Items.RemoveAll(i => i.Id == itemId));

    /// <summary>Moves an item to <paramref name="index"/> (position after removal, clamped).</summary>
    public void Move(string itemId, int index)
    {
        var from = Working.Items.FindIndex(i => i.Id == itemId);
        if (from < 0 || from == Math.Clamp(index, 0, Working.Items.Count - 1)) return;
        Edit(l =>
        {
            var i = l.Items.FindIndex(x => x.Id == itemId);
            if (i < 0) return;
            var item = l.Items[i];
            l.Items.RemoveAt(i);
            l.Items.Insert(Math.Clamp(index, 0, l.Items.Count), item);
        });
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

    /// <summary>The default overlay: starter items, top-left, theme look, always visible, hidden from capture.</summary>
    public static OverlayLayout DefaultLayout(IReadOnlyList<FriendlyItem> friendly)
    {
        var layout = new OverlayLayout();
        foreach (var key in FriendlyCatalog.DefaultOverlayKeys)
            if (friendly.FirstOrDefault(f => f.Key == key) is { } f)
                layout.Items.Add(new OverlayItem { SensorIds = [.. f.SensorIds] });
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

    public static bool HasCustomStyle(OverlayLayout l) => LayoutJson.Text(l.Style) != DefaultStyleText;

    public static bool HasCustomPlacement(OverlayLayout l)
        => !IsCorner(l.Placement.Anchor) || l.Placement.OffsetX != 0 || l.Placement.OffsetY != 0 || l.Placement.Margin != DefaultMargin;

    public static bool HasCustomLook(OverlayItem item)
    {
        var plain = new OverlayItem { Id = item.Id, SensorIds = item.SensorIds };
        return LayoutJson.Text(plain) != LayoutJson.Text(item);
    }

    /// <summary>Anything the simple mode does not show: style, per-item look, an exact position.</summary>
    public static bool HasCustomizations(OverlayLayout l) => HasCustomStyle(l) || HasCustomPlacement(l) || l.Items.Exists(HasCustomLook);

    /// <summary>"Restore default look": theme style, plain items (same sensors, same order), the nearest corner.</summary>
    public void ResetLook()
        => Edit(l =>
        {
            l.Style = new OverlayStyle();
            l.Items = l.Items.Select(i => new OverlayItem { Id = i.Id, SensorIds = [.. i.SensorIds] }).ToList();
            l.Placement.Anchor = NearestCorner(l.Placement.Anchor);
            l.Placement.OffsetX = 0;
            l.Placement.OffsetY = 0;
            l.Placement.Margin = DefaultMargin;
        });
}
