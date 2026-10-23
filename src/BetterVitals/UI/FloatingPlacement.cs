using System.Windows;
using System.Windows.Interop;

namespace BetterVitals.UI;

/// <summary>
/// Places a borderless, transparent-margin window (flyout, tray menu) next to the taskbar in physical pixels, the way
/// Windows 11 flyouts sit: above a bottom taskbar (below a top one, beside a side one), near the anchor, inside the work
/// area. The window is moved onto the anchor's monitor first, so it takes that monitor's scaling before it is measured.
/// </summary>
internal static class FloatingPlacement
{
    public enum Edge
    {
        Bottom,
        Top,
        Left,
        Right,
    }

    /// <summary>Shows <paramref name="window"/> (not yet activated) and places it; returns the taskbar edge used.</summary>
    /// <param name="shadowMargin">Transparent margin around the card, DIP.</param>
    /// <param name="atCursor">Menu style: the card's corner at the anchor point instead of centered on it.</param>
    public static Edge ShowNear(Window window, Native.RECT anchor, double shadowMargin, double gap = 12, bool atCursor = false)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, anchor.Left, anchor.Top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        if (!window.IsVisible) window.Show();
        window.UpdateLayout();
        Native.GetWindowRect(hwnd, out var r);
        var px = r.Width / Math.Max(1, window.ActualWidth);
        var m = (int)Math.Round(shadowMargin * px);
        var g = (int)Math.Round(gap * px);
        var (bounds, work) = Native.MonitorOf(anchor);
        var edge = work.Bottom < bounds.Bottom ? Edge.Bottom
            : work.Top > bounds.Top ? Edge.Top
            : work.Left > bounds.Left ? Edge.Left
            : work.Right < bounds.Right ? Edge.Right
            : Edge.Bottom;
        var w = r.Width - 2 * m;
        var h = r.Height - 2 * m;
        var cx = (anchor.Left + anchor.Right) / 2;
        var cy = (anchor.Top + anchor.Bottom) / 2;
        int x, y;
        if (atCursor)
        {
            // bottom-right corner at the cursor, kept inside the work area
            x = Math.Clamp(anchor.Left - w, work.Left + g / 2, work.Right - w - g / 2);
            y = Math.Clamp(anchor.Top - h, work.Top + g / 2, work.Bottom - h - g / 2);
        }
        else
        {
            switch (edge)
            {
                case Edge.Top:
                    x = Math.Clamp(cx - w / 2, work.Left + g, work.Right - w - g);
                    y = work.Top + g;
                    break;
                case Edge.Left:
                    x = work.Left + g;
                    y = Math.Clamp(cy - h / 2, work.Top + g, work.Bottom - h - g);
                    break;
                case Edge.Right:
                    x = work.Right - w - g;
                    y = Math.Clamp(cy - h / 2, work.Top + g, work.Bottom - h - g);
                    break;
                default:
                    x = Math.Clamp(cx - w / 2, work.Left + g, work.Right - w - g);
                    y = work.Bottom - h - g;
                    break;
            }
        }
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x - m, y - m, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        return edge;
    }

    /// <summary>The cursor position as a 1 px anchor.</summary>
    public static Native.RECT CursorAnchor()
    {
        Native.GetCursorPos(out var p);
        return new Native.RECT { Left = p.X, Top = p.Y, Right = p.X + 1, Bottom = p.Y + 1 };
    }

    /// <summary>Snapshot / probe mode: shown far off screen, never activated, no taskbar entry.</summary>
    public static void ShowOffscreen(Window window)
    {
        window.ShowActivated = false;
        window.Left = -32000;
        window.Top = -32000;
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        Native.AddExStyle(hwnd, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        window.Show();
        window.UpdateLayout();
    }
}
