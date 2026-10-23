using BetterVitals.Core.Sensors;

namespace BetterVitals.Core.Overlay;

/// <summary>What happens to a cell dropped on a grid.</summary>
public enum GridDropKind : byte
{
    /// <summary>Onto the cell at (Row, Column): a filled cell swaps places with the dropped one, an empty one takes it.</summary>
    Cell,

    /// <summary>Into row Row before column Column: the row's cells from there up to its first gap move one column right.</summary>
    InsertInRow,

    /// <summary>A new row before row Row (Row = the row count: at the bottom), the cell at column Column.</summary>
    NewRow,
}

/// <summary>Where a dragged cell lands: a window and a position in its grid as it is before the move.</summary>
public readonly record struct GridDrop(string WindowId, GridDropKind Kind, int Row, int Column);

/// <summary>A cell and the window it is in.</summary>
public readonly record struct OverlayCellSlot(string WindowId, OverlayItem Item);

/// <summary>
/// Grid helpers on a window's cell list: size, lookup, the tidy form (no two cells on one position, no empty rows or
/// columns, reading order) and the placements Simple mode's presets generate. Pure.
/// </summary>
public static class OverlayGrid
{
    public static int RowCount(IReadOnlyList<OverlayItem> cells)
    {
        var n = 0;
        foreach (var c in cells) n = Math.Max(n, c.Row + 1);
        return n;
    }

    public static int ColumnCount(IReadOnlyList<OverlayItem> cells)
    {
        var n = 0;
        foreach (var c in cells) n = Math.Max(n, c.Column + 1);
        return n;
    }

    public static OverlayItem? At(IReadOnlyList<OverlayItem> cells, int row, int column)
    {
        foreach (var c in cells)
            if (c.Row == row && c.Column == column) return c;
        return null;
    }

    /// <summary>The cells in reading order (row by row, left to right; list order breaks ties).</summary>
    public static List<OverlayItem> Ordered(IEnumerable<OverlayItem> cells)
        => cells.Select((c, i) => (c, i)).OrderBy(x => x.c.Row).ThenBy(x => x.c.Column).ThenBy(x => x.i).Select(x => x.c).ToList();

    /// <summary>
    /// The tidy form of a grid: indices clamped to 0…<see cref="OverlayLayout.MaxIndex"/>, a cell on a taken position
    /// moved to the next free column of its row, empty rows and columns collapsed, the list in reading order.
    /// </summary>
    public static void Tidy(List<OverlayItem> cells)
    {
        foreach (var c in cells)
        {
            c.Row = Math.Clamp(c.Row, 0, OverlayLayout.MaxIndex);
            c.Column = Math.Clamp(c.Column, 0, OverlayLayout.MaxIndex);
        }
        var ordered = Ordered(cells);
        var taken = new HashSet<(int, int)>();
        foreach (var c in ordered)
            while (!taken.Add((c.Row, c.Column))) c.Column++;
        var rows = Dense(ordered.Select(c => c.Row));
        var columns = Dense(ordered.Select(c => c.Column));
        foreach (var c in ordered)
        {
            c.Row = rows[c.Row];
            c.Column = columns[c.Column];
        }
        cells.Clear();
        cells.AddRange(Ordered(ordered));
    }

    private static Dictionary<int, int> Dense(IEnumerable<int> used)
    {
        var map = new Dictionary<int, int>();
        foreach (var v in used.Distinct().Order()) map[v] = map.Count;
        return map;
    }

    /// <summary>Moves <paramref name="moved"/> below the rows of <paramref name="target"/>, keeping their own grid; tidies the result.</summary>
    public static void Append(List<OverlayItem> target, List<OverlayItem> moved)
    {
        var copy = Ordered(moved);
        var top = copy.Count == 0 ? 0 : copy.Min(c => c.Row);
        var below = RowCount(target);
        foreach (var c in copy)
        {
            c.Row = c.Row - top + below;
            target.Add(c);
        }
        Tidy(target);
    }

    /// <summary>Places the cells (in this order) the way a Simple-mode preset does: a list, a row per component, one line.</summary>
    public static void Place(IReadOnlyList<OverlayItem> order, OverlayGrouping grouping)
    {
        switch (grouping)
        {
            case OverlayGrouping.OneLine:
                for (var i = 0; i < order.Count; i++) (order[i].Row, order[i].Column) = (0, i);
                break;
            case OverlayGrouping.ByComponent:
                var rows = new Dictionary<string, (int Row, int Next)>(StringComparer.Ordinal);
                var count = 0;
                foreach (var item in order)
                {
                    var key = OverlayGridEdits.ComponentKey(item);
                    if (key.Length > 0 && rows.TryGetValue(key, out var at))
                    {
                        (item.Row, item.Column) = (at.Row, at.Next);
                        rows[key] = (at.Row, at.Next + 1);
                        continue;
                    }
                    (item.Row, item.Column) = (count, 0);
                    if (key.Length > 0) rows[key] = (count, 1);
                    count++;
                }
                break;
            default:
                for (var i = 0; i < order.Count; i++) (order[i].Row, order[i].Column) = (i, 0);
                break;
        }
    }
}

/// <summary>
/// Edits of a layout's grids (cells, windows), addressed by ids and deterministic: every new item comes in with its id,
/// so the editor can replay the same edit on its working copy and on the settings and get the same result. Every edit
/// leaves its grids tidy (<see cref="OverlayGrid.Tidy"/>). Pure; no UI.
/// </summary>
public static class OverlayGridEdits
{
    /// <summary>Corners tried, in this order, for the second window (the first one the main window does not use).</summary>
    private static readonly OverlayAnchor[] FreeAnchors = [OverlayAnchor.TopRight, OverlayAnchor.TopLeft, OverlayAnchor.BottomRight, OverlayAnchor.BottomLeft];

    // ───────────── lookups ─────────────

    /// <summary>The cells of a window (the layout's own for <see cref="OverlayLayout.MainWindowId"/>); null for an unknown id.</summary>
    public static List<OverlayItem>? CellsOf(this OverlayLayout l, string windowId) => windowId switch
    {
        OverlayLayout.MainWindowId => l.Cells,
        OverlayLayout.SecondWindowId => l.Second.Cells,
        _ => null,
    };

    public static OverlayPlacement? PlacementOf(this OverlayLayout l, string windowId) => windowId switch
    {
        OverlayLayout.MainWindowId => l.Placement,
        OverlayLayout.SecondWindowId => l.Second.Placement,
        _ => null,
    };

    public static OverlayItem? FindItem(this OverlayLayout l, string itemId) => l.AllItems().FirstOrDefault(i => i.Id == itemId);

    public static OverlayCellSlot? Locate(this OverlayLayout l, string itemId)
    {
        foreach (var c in l.Cells)
            if (c.Id == itemId) return new OverlayCellSlot(OverlayLayout.MainWindowId, c);
        foreach (var c in l.Second.Cells)
            if (c.Id == itemId) return new OverlayCellSlot(OverlayLayout.SecondWindowId, c);
        return null;
    }

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

    /// <summary>The row style Simple mode uses with a preset: one line = the compact cells, otherwise label + values.</summary>
    public static OverlayArrangement ArrangementFor(OverlayGrouping grouping)
        => grouping == OverlayGrouping.OneLine ? OverlayArrangement.Compact : OverlayArrangement.Vertical;

    /// <summary>Places the main window's cells (kept in reading order) by a preset and remembers it; Custom only remembers the choice.</summary>
    public static void Regroup(this OverlayLayout l, OverlayGrouping grouping)
    {
        l.Grouping = grouping;
        if (grouping == OverlayGrouping.Custom) return;
        var order = OverlayGrid.Ordered(l.Cells);
        OverlayGrid.Place(order, grouping);
        l.Cells = OverlayGrid.Ordered(order);
    }

    /// <summary>
    /// Adds an item to the main window the way its preset wants (at the end of the reading order: by component → the
    /// end of its component's row, one line → the end of the line, list → a new last row); a grid arranged by hand gets a
    /// new last row with the item in its first column.
    /// </summary>
    public static void AddItem(this OverlayLayout l, OverlayItem item)
    {
        if (l.Grouping == OverlayGrouping.Custom)
        {
            item.Row = OverlayGrid.RowCount(l.Cells);
            item.Column = 0;
            l.Cells.Add(item);
            OverlayGrid.Tidy(l.Cells);
            return;
        }
        var order = OverlayGrid.Ordered(l.Cells);
        order.Add(item);
        OverlayGrid.Place(order, l.Grouping);
        l.Cells = OverlayGrid.Ordered(order);
    }

    /// <summary>Adds an item where it was dropped (a filled cell under it makes room: the row moves right from there).</summary>
    public static bool AddCell(this OverlayLayout l, OverlayItem item, GridDrop drop)
    {
        if (l.CellsOf(drop.WindowId) is not { } target) return false;
        if (drop.Kind == GridDropKind.Cell && OverlayGrid.At(target, drop.Row, drop.Column) is not null)
            drop = drop with { Kind = GridDropKind.InsertInRow };
        Place(target, item, drop, target, 0, 0);
        OverlayGrid.Tidy(target);
        return true;
    }

    /// <summary>
    /// Simple mode's "On the overlay" list: moves an item to <paramref name="index"/> of its window's reading order. With a
    /// preset the main window is placed again; in a grid arranged by hand the cell goes right after its new neighbour,
    /// in that neighbour's row.
    /// </summary>
    public static bool MoveFlat(this OverlayLayout l, string itemId, int index)
    {
        if (l.Locate(itemId) is not { } slot) return false;
        var flat = OverlayGrid.Ordered(l.CellsOf(slot.WindowId)!);
        var from = flat.IndexOf(slot.Item);
        index = Math.Clamp(index, 0, flat.Count - 1);
        if (from == index) return false;
        flat.RemoveAt(from);
        if (slot.WindowId == OverlayLayout.MainWindowId && l.Grouping != OverlayGrouping.Custom)
        {
            flat.Insert(index, slot.Item);
            OverlayGrid.Place(flat, l.Grouping);
            l.Cells = OverlayGrid.Ordered(flat);
            return true;
        }
        var drop = index == 0
            ? new GridDrop(slot.WindowId, GridDropKind.InsertInRow, flat[0].Row, flat[0].Column)
            : new GridDrop(slot.WindowId, GridDropKind.InsertInRow, flat[index - 1].Row, flat[index - 1].Column + 1);
        return l.MoveCell(itemId, drop);
    }

    // ───────────── remove ─────────────

    /// <summary>Removes a cell; its place stays empty unless its row or column is left empty (then that collapses).</summary>
    public static bool RemoveCell(this OverlayLayout l, string itemId)
    {
        if (l.Locate(itemId) is not { } slot) return false;
        var cells = l.CellsOf(slot.WindowId)!;
        cells.Remove(slot.Item);
        OverlayGrid.Tidy(cells);
        return true;
    }

    // ───────────── move ─────────────

    /// <summary>
    /// Moves a cell to <paramref name="drop"/> (positions as the grids are before the move; a swap partner takes the
    /// moved cell's old place, also across windows). False when nothing changes.
    /// </summary>
    public static bool MoveCell(this OverlayLayout l, string itemId, GridDrop drop)
    {
        if (l.Locate(itemId) is not { } from || l.CellsOf(drop.WindowId) is not { } target) return false;
        if (drop.WindowId == OverlayLayout.SecondWindowId && !l.Second.Enabled) return false;
        var before = Signature(l);
        var source = l.CellsOf(from.WindowId)!;
        var item = from.Item;
        int row = item.Row, column = item.Column;
        source.Remove(item);
        Place(target, item, drop, source, row, column);
        OverlayGrid.Tidy(source);
        if (!ReferenceEquals(source, target)) OverlayGrid.Tidy(target);
        return Signature(l) != before;
    }

    /// <summary>
    /// Keyboard move of a cell by one step: left / right swap with the neighbour or move into a free cell (past the
    /// last column: a new column), up / down the same with the cell above / below (past the edge: a new row).
    /// </summary>
    public static bool MoveBy(this OverlayLayout l, string itemId, int rows, int columns)
    {
        if (l.Locate(itemId) is not { } slot || (rows == 0 && columns == 0)) return false;
        var cells = l.CellsOf(slot.WindowId)!;
        var item = slot.Item;
        if (columns != 0)
        {
            var c = item.Column + Math.Sign(columns);
            return c >= 0 && l.MoveCell(itemId, new GridDrop(slot.WindowId, GridDropKind.Cell, item.Row, c));
        }
        var r = item.Row + Math.Sign(rows);
        var count = OverlayGrid.RowCount(cells);
        if (r < 0) return l.MoveCell(itemId, new GridDrop(slot.WindowId, GridDropKind.NewRow, 0, item.Column));
        if (r >= count) return l.MoveCell(itemId, new GridDrop(slot.WindowId, GridDropKind.NewRow, count, item.Column));
        return l.MoveCell(itemId, new GridDrop(slot.WindowId, GridDropKind.Cell, r, item.Column));
    }

    private static void Place(List<OverlayItem> target, OverlayItem item, GridDrop drop, List<OverlayItem> source, int fromRow, int fromColumn)
    {
        var r = Math.Clamp(drop.Row, 0, OverlayLayout.MaxIndex);
        var c = Math.Clamp(drop.Column, 0, OverlayLayout.MaxIndex);
        switch (drop.Kind)
        {
            case GridDropKind.InsertInRow:
            {
                var gap = c;
                while (OverlayGrid.At(target, r, gap) is not null) gap++;
                foreach (var x in target)
                    if (x.Row == r && x.Column >= c && x.Column < gap) x.Column++;
                break;
            }
            case GridDropKind.NewRow:
                foreach (var x in target)
                    if (x.Row >= r) x.Row++;
                break;
            default:
                if (OverlayGrid.At(target, r, c) is { } occupant)
                {
                    // the cell under it takes the moved cell's old place
                    target.Remove(occupant);
                    occupant.Row = fromRow;
                    occupant.Column = fromColumn;
                    source.Add(occupant);
                }
                break;
        }
        item.Row = r;
        item.Column = c;
        target.Add(item);
    }

    private static string Signature(OverlayLayout l)
        => string.Join(";", l.Cells.Select(c => $"{c.Id}:{c.Row},{c.Column}")) + "|" + l.Second.Enabled + "|"
           + string.Join(";", l.Second.Cells.Select(c => $"{c.Id}:{c.Row},{c.Column}"));

    // ───────────── second window ─────────────

    /// <summary>
    /// Turns the second window on (an empty grid; at a free corner when it would sit on the main window's) or off (its
    /// cells go back to the main window, below its rows; nothing is lost). False when it already is so.
    /// </summary>
    public static bool SetSecondWindow(this OverlayLayout l, bool on)
    {
        if (l.Second.Enabled == on) return false;
        if (on)
        {
            l.Second.Enabled = true;
            if (l.Second.Placement.Anchor == l.Placement.Anchor && l.Second.Placement.MonitorMode == l.Placement.MonitorMode)
                l.Second.Placement = l.NewWindowPlacement();
            return true;
        }
        l.Second.Enabled = false;
        if (l.Second.Cells.Count > 0)
        {
            OverlayGrid.Append(l.Cells, l.Second.Cells);
            l.Second.Cells.Clear();
            l.Grouping = OverlayGrouping.Custom;
        }
        return true;
    }

    /// <summary>Where the second window goes: the first corner the main window is not anchored to, on its monitor, default margin.</summary>
    public static OverlayPlacement NewWindowPlacement(this OverlayLayout l) => new()
    {
        MonitorMode = l.Placement.MonitorMode,
        MonitorKey = l.Placement.MonitorKey,
        Anchor = FreeAnchors.FirstOrDefault(a => a != l.Placement.Anchor, OverlayAnchor.TopRight),
        Margin = l.Placement.Margin,
    };
}
