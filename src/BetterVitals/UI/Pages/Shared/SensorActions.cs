using BetterVitals.Core.Catalog;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.UI.Panel;
using BetterVitals.UI.Toasts;

namespace BetterVitals.UI.Pages.Shared;

/// <summary>The two per-sensor switches and the flyout pin, read from / written to settings (pure, testable).</summary>
public static class SensorToggles
{
    /// <summary>The flyout shows this many values; more pins would never appear.</summary>
    public const int MaxPinned = 4;

    public static bool IsRecorded(AppSettings s, string id) => s.Recording.SensorIds.Contains(id);

    /// <summary>On the overlay = in any overlay item (a single one or a multi-value one) of any box.</summary>
    public static bool IsOnOverlay(AppSettings s, string id)
    {
        foreach (var item in s.Overlay.AllItems())
            if (item.SensorIds.Contains(id)) return true;
        return false;
    }

    public static bool IsPinned(AppSettings s, string id) => s.Flyout.PinnedSensorIds.Contains(id);

    /// <summary>
    /// Adds a single-sensor item to the main box the way it groups its rows (no-op when the sensor is already shown).
    /// <paramref name="newRowId"/> names the row it may need.
    /// </summary>
    public static bool AddToOverlay(OverlayLayout o, string id, string? newRowId = null)
    {
        foreach (var item in o.AllItems())
            if (item.SensorIds.Contains(id)) return false;
        o.AddItem(new OverlayItem { SensorIds = [id] }, newRowId ?? OverlayLayout.NewId());
        return true;
    }

    /// <summary>Takes the sensor out of every item; items left without sensors go, and so do rows and boxes left empty.</summary>
    public static bool RemoveFromOverlay(OverlayLayout o, string id)
    {
        var changed = false;
        foreach (var box in o.AllBoxes())
            foreach (var row in box.Rows)
            {
                foreach (var item in row.Items) changed |= item.SensorIds.Remove(id);
                row.Items.RemoveAll(i => i.SensorIds.Count == 0);
            }
        o.Prune();
        return changed;
    }

    /// <summary>All on = true, none = false, some = null (the section header's partial state).</summary>
    public static bool? Combine(int on, int total) => total == 0 || on == 0 ? false : on == total ? true : null;

    /// <summary>What clicking a section header toggle does: anything off → all on; all on → all off.</summary>
    public static bool NextGroupValue(bool? state) => state != true;

    /// <summary>The overlay's rows and boxes as they are now (the same item objects), restorable after edits that mutate items in place.</summary>
    public static Action<OverlayLayout> CaptureOverlay(OverlayLayout o)
    {
        static List<(OverlayRow Row, List<(OverlayItem Item, List<string> Ids)> Items)> Rows(List<OverlayRow> rows)
            => rows.Select(r => (r, r.Items.Select(i => (i, i.SensorIds.ToList())).ToList())).ToList();
        static List<OverlayRow> Restore(List<(OverlayRow Row, List<(OverlayItem Item, List<string> Ids)> Items)> rows)
            => rows.Select(r =>
            {
                r.Row.Items = r.Items.Select(x =>
                {
                    x.Item.SensorIds = x.Ids.ToList();
                    return x.Item;
                }).ToList();
                return r.Row;
            }).ToList();
        var main = Rows(o.Rows);
        var boxes = o.Boxes.Select(b => (Box: b, Rows: Rows(b.Rows))).ToList();
        var grouping = o.Grouping;
        return target =>
        {
            target.Rows = Restore(main);
            target.Boxes = boxes.Select(b =>
            {
                b.Box.Rows = Restore(b.Rows);
                return b.Box;
            }).ToList();
            target.Grouping = grouping;
        };
    }
}

/// <summary>The one-click recording presets of the Sensors page.</summary>
public enum RecordingPreset
{
    Key,
    All,
    None,
}

/// <summary>
/// Everything a sensor row can change — Historia / Nakładka switches, flyout pin, recording presets, the master switch —
/// applied immediately through <see cref="SettingsStore.Mutate"/> with a toast that offers Undo, plus "Show in history".
/// One instance per page (it carries the page's toast and navigation).
/// </summary>
public sealed class SensorActions
{
    private readonly Func<SettingsStore?> _store;
    private readonly Action<string, ToastKind, string?, Action?> _toast;
    private readonly Action<string, object?> _navigate;
    private readonly RecordingExclusions _exclusions;

    public SensorActions(Func<SettingsStore?> store, Action<string, ToastKind, string?, Action?> toast, Action<string, object?> navigate, RecordingExclusions? exclusions = null)
    {
        _store = store;
        _toast = toast;
        _navigate = navigate;
        _exclusions = exclusions ?? RecordingExclusions.I;
    }

    public static SensorActions For(PageContext ctx)
        => new(() => SettingsStore.I, ctx.Toast, ctx.Navigate);

    public RecordingExclusions Exclusions => _exclusions;

    public AppSettings? Settings => _store()?.Current;

    // ───────────── recording ─────────────

    /// <summary>Records (or stops recording) the given sensors. Switching off marks them as explicitly removed.</summary>
    public void SetRecorded(IReadOnlyList<SensorDescriptor> sensors, bool on)
    {
        if (_store() is not { } store || sensors.Count == 0) return;
        var ids = sensors.Select(s => s.Id).ToList();
        var current = store.Current.Recording.SensorIds;
        if (ids.All(id => current.Contains(id) == on)) return;
        var undo = CaptureRecording(store);
        store.Mutate("recording", s =>
        {
            if (on) s.Recording.SensorIds.UnionWith(ids);
            else s.Recording.SensorIds.ExceptWith(ids);
        });
        if (on) _exclusions.Include(ids);
        else _exclusions.Exclude(ids);
        var text = sensors.Count == 1
            ? Loc.T(on ? "sensors.toast.recordOn.single" : "sensors.toast.recordOff.single", sensors[0].Name)
            : Loc.Plural(on ? "sensors.toast.recordOn" : "sensors.toast.recordOff", sensors.Count);
        Toast(text, undo);
    }

    /// <summary>Kluczowe / Wszystkie / Nic: replaces the recorded set (sensors of hardware not present now stay as they are).</summary>
    public void ApplyPreset(RecordingPreset preset, ISensorHub hub)
    {
        if (_store() is not { } store) return;
        var present = hub.Sensors.Where(s => hub.IsPresent(s.Index)).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var key = FriendlyCatalog.KeyPresetSensorIds(FriendlyCatalog.Resolve(hub.Hardware, hub.Sensors));
        var target = PresetIds(preset, present, key);
        var undo = CaptureRecording(store);
        store.Mutate("recording", s =>
        {
            s.Recording.SensorIds.ExceptWith(present);
            s.Recording.SensorIds.UnionWith(target);
            // any preset is an explicit choice: hardware that shows up later gets its key sensors unless excluded
            s.Recording.KeyPresetApplied = true;
        });
        switch (preset)
        {
            case RecordingPreset.Key: _exclusions.Include(key); break;
            case RecordingPreset.All: _exclusions.Include(present); break;
            default: _exclusions.Exclude(key); break;
        }
        var name = Loc.T("sensors.preset." + preset);
        Toast(Loc.T("sensors.toast.preset", name, Loc.Plural("sensors.count", target.Count)), undo);
    }

    /// <summary>The ids a preset records among the sensors present now.</summary>
    public static HashSet<string> PresetIds(RecordingPreset preset, IReadOnlySet<string> present, IReadOnlySet<string> key) => preset switch
    {
        RecordingPreset.Key => key.Where(present.Contains).ToHashSet(StringComparer.Ordinal),
        RecordingPreset.All => new HashSet<string>(present, StringComparer.Ordinal),
        _ => new HashSet<string>(StringComparer.Ordinal),
    };

    /// <summary>The master switch "Zapisuj historię".</summary>
    public void SetRecordingEnabled(bool on)
    {
        if (_store() is not { } store || store.Current.Recording.Enabled == on) return;
        store.Mutate("recording", s => s.Recording.Enabled = on);
        Toast(Loc.T(on ? "sensors.toast.historyOn" : "sensors.toast.historyOff"), () => store.Mutate("recording", s => s.Recording.Enabled = !on));
    }

    private Action CaptureRecording(SettingsStore store)
    {
        var ids = new HashSet<string>(store.Current.Recording.SensorIds, StringComparer.Ordinal);
        var applied = store.Current.Recording.KeyPresetApplied;
        var excluded = _exclusions.Snapshot();
        return () =>
        {
            store.Mutate("recording", s =>
            {
                s.Recording.SensorIds = new HashSet<string>(ids, StringComparer.Ordinal);
                s.Recording.KeyPresetApplied = applied;
            });
            _exclusions.Restore(excluded);
        };
    }

    // ───────────── overlay ─────────────

    public void SetOnOverlay(IReadOnlyList<SensorDescriptor> sensors, bool on)
    {
        if (_store() is not { } store || sensors.Count == 0) return;
        var o = store.Current.Overlay;
        if (sensors.All(s => SensorToggles.IsOnOverlay(store.Current, s.Id) == on)) return;
        var restore = SensorToggles.CaptureOverlay(o);
        store.Mutate("overlay", s =>
        {
            foreach (var d in sensors)
            {
                if (on) SensorToggles.AddToOverlay(s.Overlay, d.Id);
                else SensorToggles.RemoveFromOverlay(s.Overlay, d.Id);
            }
        });
        var text = sensors.Count == 1
            ? Loc.T(on ? "sensors.toast.overlayOn.single" : "sensors.toast.overlayOff.single", sensors[0].Name)
            : Loc.Plural(on ? "sensors.toast.overlayOn" : "sensors.toast.overlayOff", sensors.Count);
        Toast(text, () => store.Mutate("overlay", s => restore(s.Overlay)));
    }

    // ───────────── flyout pin ─────────────

    public void SetPinned(SensorDescriptor sensor, bool on)
    {
        if (_store() is not { } store) return;
        var pinned = store.Current.Flyout.PinnedSensorIds;
        if (pinned.Contains(sensor.Id) == on) return;
        if (on && pinned.Count >= SensorToggles.MaxPinned)
        {
            _toast(Loc.T("sensors.toast.pinFull", SensorToggles.MaxPinned), ToastKind.Warning, null, null);
            return;
        }
        var before = pinned.ToList();
        store.Mutate("flyout", s =>
        {
            if (on) s.Flyout.PinnedSensorIds.Add(sensor.Id);
            else s.Flyout.PinnedSensorIds.Remove(sensor.Id);
        });
        Toast(Loc.T(on ? "sensors.toast.pinOn" : "sensors.toast.pinOff", sensor.Name), () => store.Mutate("flyout", s => s.Flyout.PinnedSensorIds = before.ToList()));
    }

    // ───────────── navigation ─────────────

    /// <summary>The History page with this sensor selected (parameter = the sensor id).</summary>
    public void ShowInHistory(SensorDescriptor sensor) => _navigate(PageIds.History, sensor.Id);

    public void Navigate(string pageId, object? parameter = null) => _navigate(pageId, parameter);

    private void Toast(string text, Action undo) => _toast(text, ToastKind.Success, Loc.T("common.undo"), undo);
}
