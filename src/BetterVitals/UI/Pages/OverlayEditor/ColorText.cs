using System.Globalization;
using System.Windows.Media;
using BetterVitals.Core.Overlay;

namespace BetterVitals.UI.Pages.OverlayEditor;

/// <summary>
/// Color text as the user types it in the color popover: "#RGB", "#ARGB", "#RRGGBB", "#AARRGGBB" (WPF order), with or
/// without '#', any case, surrounding spaces ignored. Stored normalized: "#RRGGBB" (opaque) or "#AARRGGBB".
/// </summary>
internal static class ColorText
{
    public static bool TryParse(string? text, out Rgba color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length is not (3 or 4 or 6 or 8)) return false;
        foreach (var ch in s)
            if (!Uri.IsHexDigit(ch)) return false;
        if (s.Length is 3 or 4)
        {
            // short form: every digit doubled
            s = string.Concat(s.Select(ch => new string(ch, 2)));
        }
        var v = uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        color = s.Length == 6
            ? new Rgba((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : new Rgba((byte)(v >> 16), (byte)(v >> 8), (byte)v, (byte)(v >> 24));
        return true;
    }

    /// <summary>The stored form of user input, or null when it is not a color.</summary>
    public static string? Normalize(string? text) => TryParse(text, out var c) ? Format(c) : null;

    public static string Format(Rgba c) => c.ToString();

    public static Color ToColor(Rgba c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    public static Rgba FromColor(Color c) => new(c.R, c.G, c.B, c.A);

    /// <summary>A stored color as a WPF color, or <paramref name="fallback"/>.</summary>
    public static Color ToColor(string? stored, Color fallback) => TryParse(stored, out var c) ? ToColor(c) : fallback;
}
