using BetterVitals.Core.Sensors;

namespace BetterVitals.Core.Overlay;

/// <summary>Where an item sits: box, row and position in the row.</summary>
public readonly record struct OverlayItemSlot(string BoxId, OverlayRow Row, int Index);

/// <summary>
/// Structural edits of a layout (rows, boxes, moving items), addressed by ids and deterministic: every id an edit creates
/// is passed in, so the editor can replay the same edit on its working copy and on the settings and get the same result.
/// No edit leaves an empty row or an empty detached box behind. Pure; no UI.
/// </summary>
public static class OverlayLayoutEdits
{
    /// <summary>Corners and edges tried, in this order, for a new box (the first one no box uses yet).</summary>
    private static readonly OverlayAnchor[] FreeAnchors =
    [
        OverlayAnchor.TopRight, OverlayAnchor.BottomLeft, OverlayAnchor.BottomRight, OverlayAnchor.TopLeft,
        OverlayAnchor.TopCenter, OverlayAnchor.BottomCenter, OverlayAnchor.MiddleLeft, OverlayAnchor.MiddleRight,
    ];

    // ───────────── lookups ─────────────

    /// <summary>The rows of a box (the layout's own for <see cref="OverlayLayout.MainBoxId"/>); null for an unknown id.</summary>
    public static List<OverlayRow>? RowsOf(this OverlayLayout l, string boxId)
        => boxId == OverlayLayout.MainBoxId ? l.Rows : l.Boxes.Find(b => b.Id == boxId)?.Rows;

    public static OverlayPlacement? PlacementOf(this OverlayLayout l, string boxId)
        => boxId == OverlayLayout.MainBoxId ? l.Placement : l.Boxes.Find(b => b.Id == boxId)?.Placement;

    public static OverlayItem? FindItem(this OverlayLayout l, string itemId) => l.AllItems().FirstOrDefault(i => i.Id == itemId);

    public static OverlayItemSlot? Locate(this OverlayLayout l, string itemId)
    {
        foreach (var box in l.AllBoxes())
            foreach (var row in box.Rows)
            {
                var i = row.Items.FindIndex(x => x.Id == itemId);
                if (i >= 0) return new OverlayItemSlot(box.Id, row, i);
            }
        return null;
    }

    /// <summary>Box and index of a row.</summary>
    public static (string BoxId, int Index)? LocateRow(this OverlayLayout l, string rowId)
    {
        foreach (var box in l.AllBoxes())
        {
            var i = box.Rows.FindIndex(r => r.Id == rowId);
            if (i >= 0) return (box.Id, i);
        }
        return null;
    }

    public static OverlayRow? FindRow(this OverlayLayout l, string rowId)
        => l.AllBoxes().SelectMany(b => b.Rows).FirstOrDefault(r => r.Id == rowId);

    /// <summary>The items of a box in reading order (row by row).</summary>
    public static List<OverlayItem> Flatten(this OverlayLayout l, string boxId)
        => l.RowsOf(boxId)?.SelectMany(r => r.Items).ToList() ?? [];

    /// <summary>A detached box can still be added.</summary>
    public static bool CanAddBox(this OverlayLayout l) => l.Boxes.Count < OverlayLayout.MaxBoxes - 1;

    // ───────────── components ─────────────

    /// <summary>
    /// The component an item belongs to, from its first sensor id ("&lt;kind&gt;/&lt;name&gt;/&lt;n&gt;/…"): the hardware kind
    /// ("cpu", "memory", "game"…), for GPUs, drives and network adapters the device itself (two GPUs are two rows).
    /// Empty when the item has no sensor.
    /// </summary>
    public static string ComponentKey(OverlayItem item)
    {
        var id = item.SensorIds.FirstOrDefault(s => !string.IsNullOrEmpty(s));
        if (id is null) return "";
        var parts = id.Split('/');
        var kind = parts[0].ToLowerInvariant();
        return kind is "gpu" or "storage" or "network" && parts.Length >= 3 ? $"{kind}/{parts[1]}/{parts[2]}" : kind;
    }

    /// <summary>The hardware kind of an item (from its first sensor id); <see cref="HardwareKind.Other"/> when unknown.</summary>
    public static HardwareKind ComponentKind(OverlayItem item)
    {
        var id = item.SensorIds.FirstOrDefault(s => !string.IsNullOrEmpty(s));
        if (id is null) return HardwareKind.Other;
        var slash = id.IndexOf('/');
        var head = slash < 0 ? id : id[..slash];
        return Enum.TryParse<HardwareKind>(head, true, out var kind) && Enum.IsDefined(kind) ? kind : HardwareKind.Other;
    }

    // ───────────── simple mode ─────────────

    /// <summary>The row style Simple mode uses with a grouping: one line = the compact cells, otherwise label + values.</summary>
    public static OverlayArrangement ArrangementFor(OverlayGrouping grouping)
        => grouping == OverlayGrouping.OneLine ? OverlayArrangement.Compact : OverlayArrangement.Vertical;

    /// <summary>
    /// Puts the main box's items (kept in reading order) into rows by <paramref name="grouping"/> and remembers it.
    /// A resulting row with exactly the items of an existing row keeps that row's id and label; new rows get
    /// <paramref name="idPrefix"/> + number. Custom only remembers the choice.
    /// </summary>
    public static void Regroup(this OverlayLayout l, OverlayGrouping grouping, string idPrefix)
    {
        l.Grouping = grouping;
        if (grouping == OverlayGrouping.Custom) return;
        l.Rows = Group(l.Flatten(OverlayLayout.MainBoxId), grouping, l.Rows, idPrefix);
    }

    private static List<OverlayRow> Group(List<OverlayItem> items, OverlayGrouping grouping, List<OverlayRow> old, string idPrefix)
    {
        var groups = new List<List<OverlayItem>>();
        switch (grouping)
        {
            case OverlayGrouping.OneLine:
                if (items.Count > 0) groups.Add(items);
                break;
            case OverlayGrouping.ByComponent:
                var byKey = new Dictionary<string, List<OverlayItem>>(StringComparer.Ordinal);
                foreach (var item in items)
                {
                    var key = ComponentKey(item);
                    if (key.Length > 0 && byKey.TryGetValue(key, out var group))
                    {
                        group.Add(item);
                        continue;
                    }
                    group = [item];
                    groups.Add(group);
                    if (key.Length > 0) byKey[key] = group;
                }
                break;
            default:
                groups.AddRange(items.Select(i => new List<OverlayItem> { i }));
                break;
        }
        var used = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<OverlayRow>();
        var n = 0;
        foreach (var group in groups)
        {
            var same = old.FirstOrDefault(r => !used.Contains(r.Id) && r.Items.Count == group.Count && r.Items.Select(i => i.Id).SequenceEqual(group.Select(i => i.Id)));
            if (same is not null)
            {
                used.Add(same.Id);
                rows.Add(same);
            }
            else
            {
                rows.Add(new OverlayRow { Id = $"{idPrefix}{n++}", Items = group });
            }
        }
        return rows;
    }

    /// <summary>
    /// Adds an item to the main box the way its grouping wants: by component → into the row of its component, one line →
    /// into that line, list / custom → a new row at the end (<paramref name="newRowId"/>).
    /// </summary>
    public static void AddItem(this OverlayLayout l, OverlayItem item, string newRowId)
    {
        OverlayRow? row = l.Grouping switch
        {
            OverlayGrouping.OneLine => l.Rows.LastOrDefault(),
            OverlayGrouping.ByComponent when ComponentKey(item) is { Length: > 0 } key
                => l.Rows.FirstOrDefault(r => r.Items.Count > 0 && r.Items.All(i => ComponentKey(i) == key)),
            _ => null,
        };
        if (row is null)
        {
            row = new OverlayRow { Id = newRowId };
            l.Rows.Add(row);
        }
        row.Items.Add(item);
    }

    /// <summary>
    /// Simple mode's "On the overlay" list: moves an item to <paramref name="index"/> of its box's reading order. The main
    /// box is then grouped again by its grouping; in a custom layout the item joins the row of its new neighbour.
    /// </summary>
    public static bool MoveFlat(this OverlayLayout l, string itemId, int index, string idPrefix)
    {
        if (l.Locate(itemId) is not { } slot) return false;
        var flat = l.Flatten(slot.BoxId);
        var from = flat.FindIndex(i => i.Id == itemId);
        index = Math.Clamp(index, 0, flat.Count - 1);
        if (from == index) return false;
        var item = flat[from];
        flat.RemoveAt(from);
        if (slot.BoxId == OverlayLayout.MainBoxId && l.Grouping != OverlayGrouping.Custom)
        {
            // grouped again from the new reading order (rows that keep their items keep their id and label)
            flat.Insert(index, item);
            l.Rows = Group(flat, l.Grouping, l.Rows, idPrefix);
            return true;
        }
        slot.Row.Items.RemoveAt(slot.Index);
        if (index == 0)
        {
            var first = l.Locate(flat[0].Id)!.Value;
            first.Row.Items.Insert(first.Index, item);
        }
        else
        {
            var before = l.Locate(flat[index - 1].Id)!.Value;
            before.Row.Items.Insert(before.Index + 1, item);
        }
        l.Prune();
        return true;
    }

    // ───────────── remove ─────────────

    public static bool RemoveItem(this OverlayLayout l, string itemId)
    {
        if (l.Locate(itemId) is not { } slot) return false;
        slot.Row.Items.RemoveAt(slot.Index);
        l.Prune();
        return true;
    }

    /// <summary>Removes a row and its items.</summary>
    public static bool RemoveRow(this OverlayLayout l, string rowId)
    {
        if (l.LocateRow(rowId) is not { } at) return false;
        l.RowsOf(at.BoxId)!.RemoveAt(at.Index);
        l.Prune();
        return true;
    }

    /// <summary>Removes a detached box; its rows go back to the end of the main box (nothing is lost).</summary>
    public static bool RemoveBox(this OverlayLayout l, string boxId)
    {
        var box = l.Boxes.Find(b => b.Id == boxId);
        if (box is null) return false;
        l.Boxes.Remove(box);
        l.Rows.AddRange(box.Rows);
        return true;
    }

    /// <summary>Drops rows without items and detached boxes without rows.</summary>
    public static void Prune(this OverlayLayout l)
    {
        foreach (var box in l.AllBoxes()) box.Rows.RemoveAll(r => r.Items.Count == 0);
        l.Boxes.RemoveAll(b => b.Rows.Count == 0);
    }

    // ───────────── move ─────────────

    /// <summary>Moves an item into an existing row, before <paramref name="beforeItemId"/> (null = at the end).</summary>
    public static bool MoveItem(this OverlayLayout l, string itemId, string rowId, string? beforeItemId)
    {
        if (beforeItemId == itemId || l.Locate(itemId) is not { } from || l.FindRow(rowId) is not { } target) return false;
        if (ReferenceEquals(from.Row, target))
        {
            var next = from.Index + 1 < target.Items.Count ? target.Items[from.Index + 1].Id : null;
            if (next == beforeItemId) return false; // already there
        }
        if (beforeItemId is not null && !target.Items.Exists(i => i.Id == beforeItemId)) return false;
        var item = from.Row.Items[from.Index];
        from.Row.Items.RemoveAt(from.Index);
        var at = beforeItemId is null ? target.Items.Count : target.Items.FindIndex(i => i.Id == beforeItemId);
        target.Items.Insert(at, item);
        l.Prune();
        return true;
    }

    /// <summary>Moves an item into a new row of a box, before <paramref name="beforeRowId"/> (null = at the end).</summary>
    public static bool MoveItemToNewRow(this OverlayLayout l, string itemId, string boxId, string? beforeRowId, string newRowId)
    {
        if (l.Locate(itemId) is not { } from || l.RowsOf(boxId) is not { } rows) return false;
        if (beforeRowId is not null && !rows.Exists(r => r.Id == beforeRowId)) return false;
        if (from.Row.Items.Count == 1 && from.BoxId == boxId)
        {
            // alone in its row already: only a different position is a change
            var i = rows.IndexOf(from.Row);
            var next = i + 1 < rows.Count ? rows[i + 1].Id : null;
            if (beforeRowId == from.Row.Id || beforeRowId == next) return false;
        }
        var item = from.Row.Items[from.Index];
        from.Row.Items.RemoveAt(from.Index);
        var row = new OverlayRow { Id = newRowId, Items = [item] };
        var at = beforeRowId is null ? rows.Count : rows.FindIndex(r => r.Id == beforeRowId);
        rows.Insert(at, row);
        l.Prune();
        return true;
    }

    /// <summary>Detaches an item into a new box of its own (false when the box limit is reached).</summary>
    public static bool MoveItemToNewBox(this OverlayLayout l, string itemId, string newBoxId, string newRowId, OverlayPlacement placement)
    {
        if (!l.CanAddBox() || l.Locate(itemId) is not { } from) return false;
        if (from.BoxId != OverlayLayout.MainBoxId && l.RowsOf(from.BoxId)!.Sum(r => r.Items.Count) == 1) return false; // it is a box of its own already
        var item = from.Row.Items[from.Index];
        from.Row.Items.RemoveAt(from.Index);
        l.Boxes.Add(new OverlayBox { Id = newBoxId, Placement = placement, Rows = [new OverlayRow { Id = newRowId, Items = [item] }] });
        l.Prune();
        return true;
    }

    /// <summary>Moves a row (with its items) into a box, before <paramref name="beforeRowId"/> (null = at the end).</summary>
    public static bool MoveRow(this OverlayLayout l, string rowId, string boxId, string? beforeRowId)
    {
        if (beforeRowId == rowId || l.LocateRow(rowId) is not { } from || l.RowsOf(boxId) is not { } rows) return false;
        if (beforeRowId is not null && !rows.Exists(r => r.Id == beforeRowId)) return false;
        var source = l.RowsOf(from.BoxId)!;
        if (from.BoxId == boxId)
        {
            var next = from.Index + 1 < source.Count ? source[from.Index + 1].Id : null;
            if (next == beforeRowId) return false;
        }
        var row = source[from.Index];
        source.RemoveAt(from.Index);
        var at = beforeRowId is null ? rows.Count : rows.FindIndex(r => r.Id == beforeRowId);
        rows.Insert(at, row);
        l.Prune();
        return true;
    }

    /// <summary>Detaches a row into a new box (false when the box limit is reached or it is the box's only row already).</summary>
    public static bool MoveRowToNewBox(this OverlayLayout l, string rowId, string newBoxId, OverlayPlacement placement)
    {
        if (!l.CanAddBox() || l.LocateRow(rowId) is not { } from) return false;
        var source = l.RowsOf(from.BoxId)!;
        if (from.BoxId != OverlayLayout.MainBoxId && source.Count == 1) return false;
        var row = source[from.Index];
        source.RemoveAt(from.Index);
        l.Boxes.Add(new OverlayBox { Id = newBoxId, Placement = placement, Rows = [row] });
        l.Prune();
        return true;
    }

    /// <summary>Reorders detached boxes (the main box stays first): before <paramref name="beforeBoxId"/>, null = last.</summary>
    public static bool MoveBox(this OverlayLayout l, string boxId, string? beforeBoxId)
    {
        var from = l.Boxes.FindIndex(b => b.Id == boxId);
        if (from < 0 || beforeBoxId == boxId) return false;
        var next = from + 1 < l.Boxes.Count ? l.Boxes[from + 1].Id : null;
        if (next == beforeBoxId || (beforeBoxId is not null && !l.Boxes.Exists(b => b.Id == beforeBoxId))) return false;
        var box = l.Boxes[from];
        l.Boxes.RemoveAt(from);
        l.Boxes.Insert(beforeBoxId is null ? l.Boxes.Count : l.Boxes.FindIndex(b => b.Id == beforeBoxId), box);
        return true;
    }

    // ───────────── names ─────────────

    public static bool SetRowLabel(this OverlayLayout l, string rowId, string? label)
    {
        if (l.FindRow(rowId) is not { } row) return false;
        row.Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim() is { Length: > 64 } t ? t[..64] : label.Trim();
        return true;
    }

    public static bool SetBoxName(this OverlayLayout l, string boxId, string? name)
    {
        if (l.Boxes.Find(b => b.Id == boxId) is not { } box) return false;
        box.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim() is { Length: > 64 } t ? t[..64] : name.Trim();
        return true;
    }

    // ───────────── placement of a new box ─────────────

    /// <summary>
    /// Where a new box goes: the first corner (then edge middle) no box is anchored to yet, on the main box's monitor,
    /// default margin, no offset — predictable, and never on top of an existing box at its anchor.
    /// </summary>
    public static OverlayPlacement NewBoxPlacement(this OverlayLayout l)
    {
        var used = l.AllBoxes().Select(b => b.Placement.Anchor).ToHashSet();
        var anchor = FreeAnchors.FirstOrDefault(a => !used.Contains(a), OverlayAnchor.TopCenter);
        return new OverlayPlacement
        {
            MonitorMode = l.Placement.MonitorMode,
            MonitorKey = l.Placement.MonitorKey,
            Anchor = anchor,
            Margin = l.Placement.Margin,
        };
    }
}
