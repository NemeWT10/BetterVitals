using BetterVitals.Core.Overlay;

namespace BetterVitals.Overlay;

/// <summary>A rectangle in physical desktop pixels (right / bottom exclusive).</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public override string ToString() => $"[{Left},{Top} {Width}×{Height}]";
}

/// <summary>A connected monitor.</summary>
/// <param name="Key">Stable key for <see cref="OverlayPlacement.MonitorKey"/> (monitor device interface path).</param>
/// <param name="DeviceName">GDI name ("\\.\DISPLAY1"); changes with the port order, not stable.</param>
/// <param name="FriendlyName">EDID name ("DELL U2723QE") or a generic name.</param>
/// <param name="Bounds">Full monitor rectangle, physical pixels.</param>
/// <param name="WorkArea">Without the taskbar.</param>
public sealed record OverlayMonitor(string Key, string DeviceName, string FriendlyName, PixelRect Bounds, PixelRect WorkArea, bool IsPrimary);

/// <summary>Result of snapping a dragged overlay.</summary>
/// <param name="X">Snapped left.</param>
/// <param name="Y">Snapped top.</param>
/// <param name="GuideX">Vertical guide line x (desktop px) or null.</param>
/// <param name="GuideY">Horizontal guide line y or null.</param>
public readonly record struct SnapResult(int X, int Y, int? GuideX, int? GuideY);

/// <summary>
/// Pure placement math (shared with the WPF editor's monitor mock-up): scale, anchor + inward offsets + margin →
/// pixels, the inverse for edit mode, monitor choice with fallback, and snapping.
/// </summary>
public static class OverlayPlacementMath
{
    /// <summary>Reference monitor height: sizes and offsets are "pixels of a 1080 px tall monitor".</summary>
    public const double ReferenceHeight = 1080;

    /// <summary>
    /// Overlay scale: monitor pixel height / 1080 × ScalePercent / 100 with AutoScale (1080p ×1, 1440p ×1.33,
    /// 4K ×2, 5K ×2.67), independent of the Windows display scaling; ScalePercent / 100 alone without it.
    /// </summary>
    public static double Scale(OverlayStyle style, int monitorHeightPx)
    {
        var pct = Math.Clamp(style.ScalePercent, 50, 200) / 100.0;
        var auto = style.AutoScale && monitorHeightPx > 0 ? monitorHeightPx / ReferenceHeight : 1.0;
        return Math.Max(0.25, auto * pct);
    }

    /// <summary>Top-left of a <paramref name="width"/> × <paramref name="height"/> overlay inside <paramref name="area"/>; kept fully inside.</summary>
    public static (int X, int Y) Position(OverlayPlacement p, PixelRect area, int width, int height, double scale)
    {
        var m = p.Margin * scale;
        var ox = p.OffsetX * scale;
        var oy = p.OffsetY * scale;
        double x = Column(p.Anchor) switch
        {
            0 => area.Left + m + ox,
            1 => area.Left + (area.Width - width) / 2.0 + ox,
            _ => area.Right - m - ox - width,
        };
        double y = Row(p.Anchor) switch
        {
            0 => area.Top + m + oy,
            1 => area.Top + (area.Height - height) / 2.0 + oy,
            _ => area.Bottom - m - oy - height,
        };
        return (Clamp((int)Math.Round(x), area.Left, area.Right - width), Clamp((int)Math.Round(y), area.Top, area.Bottom - height));
    }

    /// <summary>
    /// The placement that reproduces the overlay at (<paramref name="x"/>, <paramref name="y"/>): the anchor is the
    /// ninth of the area the overlay's center lies in (so it stays attached to the nearest corner / edge when it grows),
    /// offsets are in reference px relative to that anchor, the margin is kept.
    /// </summary>
    public static OverlayPlacement FromPosition(int x, int y, int width, int height, PixelRect area, double scale, OverlayPlacement current)
    {
        var cx = x + width / 2.0;
        var cy = y + height / 2.0;
        var col = cx < area.Left + area.Width / 3.0 ? 0 : cx > area.Left + area.Width * 2 / 3.0 ? 2 : 1;
        var row = cy < area.Top + area.Height / 3.0 ? 0 : cy > area.Top + area.Height * 2 / 3.0 ? 2 : 1;
        var anchor = (OverlayAnchor)(row * 3 + col);
        var m = current.Margin;
        double ox = col switch
        {
            0 => (x - area.Left) / scale - m,
            1 => (cx - (area.Left + area.Width / 2.0)) / scale,
            _ => (area.Right - (x + width)) / scale - m,
        };
        double oy = row switch
        {
            0 => (y - area.Top) / scale - m,
            1 => (cy - (area.Top + area.Height / 2.0)) / scale,
            _ => (area.Bottom - (y + height)) / scale - m,
        };
        return new OverlayPlacement
        {
            MonitorMode = current.MonitorMode,
            MonitorKey = current.MonitorKey,
            Anchor = anchor,
            Margin = m,
            OffsetX = Math.Round(ox, 1),
            OffsetY = Math.Round(oy, 1),
        };
    }

    /// <summary>
    /// The monitor to show the overlay on: Specific → that key (primary while it is missing, same anchor; it returns
    /// when the monitor comes back); FollowGame → the game's monitor, primary without a game; Primary → primary.
    /// </summary>
    public static OverlayMonitor? Resolve(OverlayPlacement p, IReadOnlyList<OverlayMonitor> monitors, string? gameMonitorKey = null)
    {
        if (monitors.Count == 0) return null;
        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        return p.MonitorMode switch
        {
            OverlayMonitorMode.Specific => Find(monitors, p.MonitorKey) ?? primary,
            OverlayMonitorMode.FollowGame => Find(monitors, gameMonitorKey) ?? primary,
            _ => primary,
        };
    }

    /// <summary>
    /// Snaps a dragged overlay to the area's edges, the default margin from them and the center (both axes)
    /// when within <paramref name="threshold"/> px.
    /// </summary>
    public static SnapResult Snap(int x, int y, int width, int height, PixelRect area, int margin, int threshold)
    {
        int? gx = null, gy = null;
        var bestX = threshold + 1;
        var sx = x;
        void TryX(int candidate, int guide)
        {
            var d = Math.Abs(candidate - x);
            if (d < bestX) { bestX = d; sx = candidate; gx = guide; }
        }
        TryX(area.Left, area.Left);
        TryX(area.Left + margin, area.Left + margin);
        TryX(area.Right - width, area.Right - 1);
        TryX(area.Right - margin - width, area.Right - margin - 1);
        var center = area.Left + (area.Width - width) / 2;
        TryX(center, area.Left + area.Width / 2);
        if (bestX > threshold) { sx = x; gx = null; }

        var bestY = threshold + 1;
        var sy = y;
        void TryY(int candidate, int guide)
        {
            var d = Math.Abs(candidate - y);
            if (d < bestY) { bestY = d; sy = candidate; gy = guide; }
        }
        TryY(area.Top, area.Top);
        TryY(area.Top + margin, area.Top + margin);
        TryY(area.Bottom - height, area.Bottom - 1);
        TryY(area.Bottom - margin - height, area.Bottom - margin - 1);
        var middle = area.Top + (area.Height - height) / 2;
        TryY(middle, area.Top + area.Height / 2);
        if (bestY > threshold) { sy = y; gy = null; }

        return new SnapResult(sx, sy, gx, gy);
    }

    private static OverlayMonitor? Find(IReadOnlyList<OverlayMonitor> monitors, string? key)
        => string.IsNullOrEmpty(key) ? null : monitors.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));

    private static int Column(OverlayAnchor a) => (int)a % 3;

    private static int Row(OverlayAnchor a) => (int)a / 3;

    private static int Clamp(int v, int min, int max) => max < min ? min : Math.Clamp(v, min, max);
}
