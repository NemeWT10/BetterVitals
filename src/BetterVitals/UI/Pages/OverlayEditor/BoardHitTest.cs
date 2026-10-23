using System.Windows;
using BetterVitals.Core.Overlay;
using BetterVitals.Overlay;

namespace BetterVitals.UI.Pages.OverlayEditor;

/// <summary>What a drop on the board would do, and where its marker goes (board DIPs, relative to the window's picture).</summary>
/// <param name="Drop">The model edit.</param>
/// <param name="Marker">Swap / move: the target cell's rectangle; insert in a row: a thin vertical bar; new row: a thin horizontal line.</param>
/// <param name="Slot">New row: the column the cell lands in (empty when unknown).</param>
/// <param name="Filled">Cell drops: a cell sits there (a swap); false: a free cell or a new column.</param>
internal readonly record struct BoardTarget(GridDrop Drop, Rect Marker, Rect Slot, bool Filled);

/// <summary>
/// The board's drop rules, pure (unit-tested without WPF windows). For a point over a window's grid:
/// <list type="bullet">
/// <item>near the top / bottom edge of a row, in the gap between two rows, above the first or below the last row → a
/// new row there, the cell in the column under the pointer;</item>
/// <item>near the left / right edge of a cell or in the gap between two cells of a row → inserted into that row there
/// (the row's cells to the right move along up to its first gap);</item>
/// <item>right of the last column → a new column at the right edge of that row;</item>
/// <item>anywhere else on a cell → a swap with that cell (a sensor dragged in from the list makes room instead), on a
/// free cell → moved there.</item>
/// </list>
/// An empty window takes the cell as its first one.
/// </summary>
internal static class BoardHitTest
{
    /// <summary>Distance of insertion markers from the cell they sit next to, DIP.</summary>
    private const double Out = 5;

    /// <param name="g">The window's grid as drawn (pixels).</param>
    /// <param name="px">Pixels per DIP of that picture.</param>
    /// <param name="p">The pointer relative to the picture's top-left, DIP.</param>
    /// <param name="occupied">Cells taken right now (grid row, column) — the dragged cell's own place counts as free.</param>
    /// <param name="external">A new sensor from the list (it never swaps: it makes room).</param>
    public static BoardTarget Find(string windowId, OverlayGridGeometry g, double px, Point p, Func<int, int, bool> occupied, bool external)
    {
        int rows = g.Rows, cols = g.Columns;
        if (g.Cells.Count == 0 || rows == 0 || cols == 0)
        {
            var all = new Rect(0, 0, Math.Max(1, g.Width / px), Math.Max(1, g.Height / px));
            return new BoardTarget(new GridDrop(windowId, GridDropKind.Cell, 0, 0), all, Rect.Empty, false);
        }
        double T(int r) => g.RowTop[r] / px;
        double B(int r) => g.RowBottom[r] / px;
        double L(int c) => g.ColumnLeft[c] / px;
        double R(int c) => g.ColumnRight[c] / px;
        var gap = cols > 1 ? Math.Max(6, L(1) - R(0)) : 12;
        var right = R(cols - 1);
        var gridLeft = L(0);

        // the row band under the pointer: from the middle of the gap above to the middle of the gap below
        var row = rows - 1;
        for (var r = 0; r < rows - 1; r++)
            if (p.Y < (B(r) + T(r + 1)) / 2)
            {
                row = r;
                break;
            }
        var h = B(row) - T(row);
        var edge = Math.Clamp(h * 0.3, 5, 14);
        var column = ColumnAt(p.X);
        if (p.Y < T(row) + edge) return NewRow(row, column);
        if (p.Y > B(row) - edge) return NewRow(row + 1, column);

        // inside the row: right of the last column → a new column there
        if (p.X > right + Math.Min(gap, 24) / 2)
        {
            var at = new Rect(right + gap, T(row), Math.Max(36, gap * 2), h);
            return new BoardTarget(new GridDrop(windowId, GridDropKind.Cell, row, cols), Inflate(at), Rect.Empty, false);
        }
        var c = Math.Min(column, cols - 1);
        var w = R(c) - L(c);
        var band = Math.Clamp(w * 0.22, 6, 16);
        var filled = occupied(row, c);
        if (external && filled) return p.X < (L(c) + R(c)) / 2 ? Insert(row, c) : Insert(row, c + 1); // a new sensor makes room beside the cell under it
        if (p.X < L(c) + band) return Insert(row, c);
        if (p.X > R(c) - band) return Insert(row, c + 1);
        var cell = new Rect(L(c), T(row), Math.Max(1, w), Math.Max(1, h));
        return new BoardTarget(new GridDrop(windowId, GridDropKind.Cell, row, c), Inflate(cell), Rect.Empty, filled);

        int ColumnAt(double x)
        {
            if (x > right + Math.Min(gap, 24) / 2) return cols;
            for (var k = 0; k < cols - 1; k++)
                if (x < (R(k) + L(k + 1)) / 2) return k;
            return cols - 1;
        }

        BoardTarget Insert(int r, int at)
        {
            var x = at <= 0 ? L(0) - Out : at >= cols ? right + Out : (R(at - 1) + L(at)) / 2;
            var bar = new Rect(x - 1.5, T(r) - 3, 3, B(r) - T(r) + 6);
            return new BoardTarget(new GridDrop(windowId, GridDropKind.InsertInRow, r, Math.Clamp(at, 0, cols)), bar, Rect.Empty, false);
        }

        BoardTarget NewRow(int at, int col)
        {
            var y = at <= 0 ? T(0) - Out : at >= rows ? B(rows - 1) + Out : (B(at - 1) + T(at)) / 2;
            var line = new Rect(gridLeft - 6, y - 1.25, right - gridLeft + 12, 2.5);
            var slot = col < cols ? new Rect(L(col), y - 2.5, Math.Max(1, R(col) - L(col)), 5) : new Rect(right + gap, y - 2.5, Math.Max(36, gap * 2), 5);
            return new BoardTarget(new GridDrop(windowId, GridDropKind.NewRow, Math.Clamp(at, 0, rows), Math.Clamp(col, 0, cols)), line, slot, false);
        }
    }

    /// <summary>The cell (grid row, column) whose area holds the point (cells reach to the middle of the gaps around them); null outside.</summary>
    public static OverlayGridCell? CellAt(OverlayGridGeometry g, double px, Point p)
    {
        if (g.Cells.Count == 0) return null;
        var best = (OverlayGridCell?)null;
        foreach (var cell in g.Cells)
        {
            if (CellArea(g, px, cell.Row, cell.Column).Contains(p)) best = cell;
        }
        return best;
    }

    /// <summary>The rectangle of a grid cell, DIP (content extents of its column and row; a cell's sparkline running past its column included).</summary>
    public static Rect CellRect(OverlayGridGeometry g, double px, int row, int column)
    {
        if (row < 0 || row >= g.Rows || column < 0 || column >= g.Columns) return Rect.Empty;
        var l = g.ColumnLeft[column] / px;
        var t = g.RowTop[row] / px;
        var right = g.ColumnRight[column];
        foreach (var c in g.Cells)
            if (c.Row == row && c.Column == column) right = Math.Max(right, c.Right);
        return new Rect(l, t, Math.Max(1, right / px - l), Math.Max(1, g.RowBottom[row] / px - t));
    }

    /// <summary>A cell's rectangle grown to the middle of the gaps around it (and to the picture's edge on the outside).</summary>
    public static Rect CellArea(OverlayGridGeometry g, double px, int row, int column)
    {
        var r = CellRect(g, px, row, column);
        if (r.IsEmpty) return r;
        var left = column == 0 ? 0 : (g.ColumnRight[column - 1] / px + r.Left) / 2;
        var right = column == g.Columns - 1 ? g.Width / px : (r.Right + g.ColumnLeft[column + 1] / px) / 2;
        var top = row == 0 ? 0 : (g.RowBottom[row - 1] / px + r.Top) / 2;
        var bottom = row == g.Rows - 1 ? g.Height / px : (r.Bottom + g.RowTop[row + 1] / px) / 2;
        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static Rect Inflate(Rect r)
    {
        r.Inflate(3, 3);
        return r;
    }
}
