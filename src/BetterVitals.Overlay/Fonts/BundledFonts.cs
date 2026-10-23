namespace BetterVitals.Overlay.Fonts;

/// <summary>One font file of a bundled family.</summary>
/// <param name="Weight">OpenType weight: 400 (Regular) or 600 (SemiBold).</param>
/// <param name="ResourceName">Manifest resource name inside BetterVitals.Overlay.</param>
public sealed record BundledFontFile(int Weight, string ResourceName)
{
    /// <summary>Opens the TTF data (read-only, memory-backed). Dispose after use.</summary>
    public Stream Open() => BundledFonts.OpenResource(ResourceName);
}

/// <summary>A font family embedded in the app (SIL Open Font License 1.1).</summary>
/// <param name="FamilyName">Family name as stored in <c>OverlayStyle.FontFamily</c> ("Inter", "Space Grotesk"…).</param>
/// <param name="Files">Regular 400 + SemiBold 600, subset to Latin + Latin Extended-A.</param>
/// <param name="LicenseResourceName">Manifest resource name of the family's OFL.txt.</param>
public sealed record BundledFont(string FamilyName, IReadOnlyList<BundledFontFile> Files, string LicenseResourceName)
{
    /// <summary>The OFL license text shipped with the family.</summary>
    public string LicenseText()
    {
        using var s = BundledFonts.OpenResource(LicenseResourceName);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>The file closest to <paramref name="weight"/>.</summary>
    public BundledFontFile File(int weight) => Files.MinBy(f => Math.Abs(f.Weight - weight))!;
}

/// <summary>
/// Overlay fonts: the bundled OFL families (also for the WPF editor, so it can show the same fonts) and the allowed
/// system families. Unknown or failing families fall back to <see cref="Fallback"/>.
/// </summary>
public static class BundledFonts
{
    /// <summary>Default overlay font.</summary>
    public const string Default = "Inter";

    /// <summary>System font used when a family is missing or fails to load.</summary>
    public const string Fallback = "Segoe UI Variable";

    private const string Prefix = "BetterVitals.Overlay.Fonts.";

    /// <summary>Embedded families, in the order the editor lists them.</summary>
    public static IReadOnlyList<BundledFont> All { get; } =
    [
        Make("Inter", "Inter"),
        Make("Geist", "Geist"),
        Make("Space Grotesk", "SpaceGrotesk"),
        Make("JetBrains Mono", "JetBrainsMono"),
        Make("Rajdhani", "Rajdhani"),
        Make("Orbitron", "Orbitron"),
    ];

    /// <summary>System families the overlay may use (shipped with Windows 11).</summary>
    public static IReadOnlyList<string> SystemFamilies { get; } = ["Segoe UI Variable", "Bahnschrift"];

    /// <summary>Every selectable family name: bundled first, then system.</summary>
    public static IReadOnlyList<string> AllFamilies { get; } = [.. All.Select(f => f.FamilyName), .. SystemFamilies];

    /// <summary>The bundled family with this name (case-insensitive), or null.</summary>
    public static BundledFont? Find(string? familyName)
        => familyName is null ? null : All.FirstOrDefault(f => string.Equals(f.FamilyName, familyName, StringComparison.OrdinalIgnoreCase));

    internal static Stream OpenResource(string name)
        => typeof(BundledFonts).Assembly.GetManifestResourceStream(name)
           ?? throw new FileNotFoundException("Missing embedded font resource", name);

    private static BundledFont Make(string family, string stem) => new(
        family,
        [new BundledFontFile(400, $"{Prefix}{stem}-Regular.ttf"), new BundledFontFile(600, $"{Prefix}{stem}-SemiBold.ttf")],
        $"{Prefix}{stem}-OFL.txt");
}
