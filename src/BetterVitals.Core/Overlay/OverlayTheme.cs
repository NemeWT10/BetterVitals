using System.Globalization;
using BetterVitals.Core.Sensors;

namespace BetterVitals.Core.Overlay;

/// <summary>A straight (non-premultiplied) sRGB color.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    /// <summary>"#RRGGBB" or "#AARRGGBB" (WPF order); null on anything else.</summary>
    public static Rgba? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().TrimStart('#');
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return null;
        return s.Length switch
        {
            6 => new Rgba((byte)(v >> 16), (byte)(v >> 8), (byte)v),
            8 => new Rgba((byte)(v >> 16), (byte)(v >> 8), (byte)v, (byte)(v >> 24)),
            _ => null,
        };
    }

    public Rgba WithAlpha(double opacity) => this with { A = (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255) };

    public override string ToString() => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// Colors the overlay takes from the active app theme (the app resolves them and hands them to the renderer, which
/// knows nothing about WPF). Explicit colors in <see cref="OverlayStyle"/> / <see cref="OverlayItem"/> win over these.
/// </summary>
public sealed record OverlayTheme
{
    public required Rgba Background { get; init; }

    public required Rgba Border { get; init; }

    /// <summary>Values.</summary>
    public required Rgba Text { get; init; }

    /// <summary>Units.</summary>
    public required Rgba TextDim { get; init; }

    public required Rgba Warning { get; init; }

    public required Rgba Danger { get; init; }

    /// <summary>Label color per component family (CPU = accent, GPU = accent 2, …).</summary>
    public required IReadOnlyDictionary<HardwareKind, Rgba> Category { get; init; }

    public Rgba CategoryColor(HardwareKind kind) => Category.TryGetValue(kind, out var c) ? c : TextDim;

    /// <summary>Aurora — the default dark theme (also the fallback before the app theme is known).</summary>
    public static OverlayTheme Default { get; } = new()
    {
        Background = new Rgba(0x0F, 0x11, 0x17),
        Border = new Rgba(0xFF, 0xFF, 0xFF, 0x2E),
        Text = new Rgba(0xEE, 0xF0, 0xF7),
        TextDim = new Rgba(0xA2, 0xA8, 0xB8),
        Warning = new Rgba(0xFB, 0xBF, 0x24),
        Danger = new Rgba(0xF4, 0x3F, 0x5E),
        Category = new Dictionary<HardwareKind, Rgba>
        {
            [HardwareKind.Cpu] = new(0x8B, 0x6C, 0xFF),
            [HardwareKind.Gpu] = new(0x22, 0xD3, 0xEE),
            [HardwareKind.Memory] = new(0xF4, 0x72, 0xB6),
            [HardwareKind.Motherboard] = new(0xFB, 0xBF, 0x24),
            [HardwareKind.Storage] = new(0xA3, 0xE6, 0x35),
            [HardwareKind.Network] = new(0x60, 0xA5, 0xFA),
            [HardwareKind.Game] = new(0x34, 0xD3, 0x99),
            [HardwareKind.Cooler] = new(0x38, 0xBD, 0xF8),
            [HardwareKind.Battery] = new(0x34, 0xD3, 0x99),
        },
    };
}
