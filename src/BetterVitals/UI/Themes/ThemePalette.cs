using System.Windows.Media;
using BetterVitals.Core.Sensors;

namespace BetterVitals.UI.Themes;

/// <summary>
/// One theme = one palette. Base tokens match BetterShot / BetterNotepad 1:1 (same family of apps); chart series and
/// component colors are BetterVitals' own. Every token is `required`, so a theme that forgets one does not compile.
/// </summary>
public sealed record ThemePalette
{
    public required string Id { get; init; }
    public required string NameKey { get; init; }
    public required bool IsDark { get; init; }

    // ── accent ──
    public required Color Accent { get; init; }
    public required Color Accent2 { get; init; }
    public required Color Glow { get; init; }
    public required Color Shadow { get; init; }
    public required Color AccentSoft { get; init; }
    public required Color AccentGlow { get; init; }
    public required Color[] AccentGradient { get; init; }
    public required Color OnAccent { get; init; }

    /// <summary>Accent used as text / thin lines on the background (readable where Accent is too dark or too light).</summary>
    public required Color AccentText { get; init; }

    // ── surfaces & text ──
    public required Color Bg { get; init; }
    public required Color BgAlt { get; init; }
    public required Color Surface { get; init; }
    public required Color SurfaceHover { get; init; }
    public required Color SurfacePressed { get; init; }
    public required Color Border { get; init; }
    public required Color BorderStrong { get; init; }
    public required Color Text { get; init; }
    public required Color TextDim { get; init; }
    public required Color TextFaint { get; init; }

    // ── floating panels (menus, flyout, bubbles, toasts) ──
    public required Color Panel { get; init; }
    public required Color PanelBorder { get; init; }
    public required Color PanelText { get; init; }
    public required Color PanelTextDim { get; init; }
    public required Color PanelHover { get; init; }
    public required Color PanelPressed { get; init; }
    public required Color PanelSeparator { get; init; }
    public required Color Dim { get; init; }

    // ── status (also the heat scale: normal / warm / hot = Success / Warning / Danger) ──
    public required Color Success { get; init; }
    public required Color Danger { get; init; }
    public required Color Warning { get; init; }
    public required Color Info { get; init; }
    public required Color ToggleOff { get; init; }

    // ── window chrome, drawn over Mica when it is on ──
    public required Color ChromeOverMica { get; init; }
    public required Color ChromeSolid { get; init; }

    // ── BetterVitals ──

    /// <summary>Eight distinguishable chart series colors, in the order charts assign them.</summary>
    public required Color[] Series { get; init; }

    /// <summary>Color per component family (labels, icons, overlay labels). Must contain every <see cref="HardwareKind"/>.</summary>
    public required IReadOnlyDictionary<HardwareKind, Color> Category { get; init; }
}
