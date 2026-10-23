namespace BetterVitals.UI.Explain;

/// <summary>Where the bubble goes (physical pixels): under the anchor, above it when there is no room, always inside the work area.</summary>
internal static class ExplainPlacement
{
    public readonly record struct PxRect(double Left, double Top, double Width, double Height)
    {
        public double Right => Left + Width;
        public double Bottom => Top + Height;
    }

    /// <summary>
    /// Top-left of a <paramref name="width"/> × <paramref name="height"/> bubble for an anchor, with <paramref name="gap"/>
    /// between them: left-aligned with the anchor (shifted to stay on screen), below it, flipped above when it does not
    /// fit below, and when it fits neither way, on the roomier side clamped to the work area.
    /// </summary>
    public static (double X, double Y, bool Above) Compute(PxRect anchor, double width, double height, PxRect work, double gap)
    {
        var x = anchor.Left;
        if (x + width > work.Right) x = work.Right - width;
        if (x < work.Left) x = work.Left;

        var below = anchor.Bottom + gap;
        var above = anchor.Top - gap - height;
        bool flip;
        double y;
        if (below + height <= work.Bottom)
        {
            y = below;
            flip = false;
        }
        else if (above >= work.Top)
        {
            y = above;
            flip = true;
        }
        else
        {
            flip = anchor.Top - work.Top > work.Bottom - anchor.Bottom;
            y = flip ? above : below;
        }
        if (y + height > work.Bottom) y = work.Bottom - height;
        if (y < work.Top) y = work.Top;
        return (Math.Round(x), Math.Round(y), flip);
    }
}
