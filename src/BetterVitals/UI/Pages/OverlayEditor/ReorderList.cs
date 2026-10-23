using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BetterVitals.UI.Pages.OverlayEditor;

/// <summary>
/// A vertical list whose rows are reordered by dragging their handle (the other rows make room as the row passes them)
/// or with the keyboard (handle focused: Up / Down). Raises <see cref="Moved"/> once on drop; the owner applies the
/// move to the layout and rebuilds the list. No OLE drag-and-drop, nothing global.
/// </summary>
internal sealed class ReorderList : StackPanel
{
    private readonly List<(string Id, FrameworkElement Row)> _rows = [];
    private int _drag = -1;
    private double _startY;
    private double[] _tops = [];
    private double[] _heights = [];
    private int _target;
    private FrameworkElement? _captured;

    /// <summary>(item id, new index) after a drag or a key move.</summary>
    public event Action<string, int>? Moved;

    public int Count => _rows.Count;

    public void Clear()
    {
        Cancel();
        _rows.Clear();
        Children.Clear();
    }

    /// <summary>Adds a row; <paramref name="handle"/> (inside the row) starts the drag.</summary>
    public void AddRow(string id, FrameworkElement row, FrameworkElement handle)
    {
        _rows.Add((id, row));
        Children.Add(row);
        row.RenderTransform = new TranslateTransform();
        handle.Cursor = Cursors.SizeNS;
        handle.Focusable = true;
        handle.FocusVisualStyle = null;
        handle.MouseLeftButtonDown += (_, e) => Begin(id, handle, e);
        handle.MouseMove += (_, e) => Move(e);
        handle.MouseLeftButtonUp += (_, e) => End(e);
        handle.LostMouseCapture += (_, _) => Cancel();
        handle.KeyDown += (_, e) =>
        {
            var i = _rows.FindIndex(r => r.Id == id);
            if (e.Key == Key.Up && i > 0)
            {
                e.Handled = true;
                Moved?.Invoke(id, i - 1);
            }
            else if (e.Key == Key.Down && i >= 0 && i < _rows.Count - 1)
            {
                e.Handled = true;
                Moved?.Invoke(id, i + 1);
            }
        };
    }

    private void Begin(string id, FrameworkElement handle, MouseButtonEventArgs e)
    {
        var i = _rows.FindIndex(r => r.Id == id);
        if (i < 0 || _rows.Count < 2) return;
        _drag = i;
        _target = i;
        _startY = e.GetPosition(this).Y;
        _tops = _rows.Select(r => r.Row.TranslatePoint(new Point(0, 0), this).Y).ToArray();
        _heights = _rows.Select(r => r.Row.ActualHeight + r.Row.Margin.Top + r.Row.Margin.Bottom).ToArray();
        _captured = handle;
        handle.CaptureMouse();
        System.Windows.Controls.Panel.SetZIndex(_rows[i].Row, 10);
        _rows[i].Row.Opacity = 0.92;
        e.Handled = true;
    }

    private void Move(MouseEventArgs e)
    {
        if (_drag < 0) return;
        var dy = e.GetPosition(this).Y - _startY;
        // keep the row inside the list
        var minDy = _tops[0] - _tops[_drag];
        var maxDy = _tops[^1] + _heights[^1] - (_tops[_drag] + _heights[_drag]);
        dy = Math.Clamp(dy, minDy, maxDy);
        var center = _tops[_drag] + _heights[_drag] / 2 + dy;
        var target = 0;
        for (var j = 0; j < _rows.Count; j++)
        {
            if (j == _drag) continue;
            if (_tops[j] + _heights[j] / 2 < center) target++;
        }
        _target = target;
        for (var j = 0; j < _rows.Count; j++)
        {
            var t = (TranslateTransform)_rows[j].Row.RenderTransform;
            if (j == _drag) t.Y = dy;
            else if (_drag < _target && j > _drag && j <= _target) t.Y = -_heights[_drag];
            else if (_target < _drag && j >= _target && j < _drag) t.Y = _heights[_drag];
            else t.Y = 0;
        }
    }

    private void End(MouseButtonEventArgs e)
    {
        if (_drag < 0) return;
        e.Handled = true;
        var id = _rows[_drag].Id;
        var from = _drag;
        var to = _target;
        Reset();
        if (to != from) Moved?.Invoke(id, to);
    }

    private void Cancel()
    {
        if (_drag >= 0) Reset();
    }

    private void Reset()
    {
        _drag = -1;
        foreach (var (_, row) in _rows)
        {
            if (row.RenderTransform is TranslateTransform t) t.Y = 0;
            System.Windows.Controls.Panel.SetZIndex(row, 0);
            row.Opacity = 1;
        }
        if (_captured is { IsMouseCaptured: true } c) c.ReleaseMouseCapture();
        _captured = null;
    }
}
