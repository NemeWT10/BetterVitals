using System.IO;
using BetterVitals.Core;
using BetterVitals.Overlay.Fonts;

namespace BetterVitals.UI.About;

/// <summary>A component and its license; the text is read only when the user expands it.</summary>
internal sealed record LicenseEntry(string Name, string License, string? Url, Func<string> Text);

/// <summary>
/// Licenses shown in About: BetterVitals (MIT, the repository's LICENSE), LibreHardwareMonitor (MPL-2.0), PawnIO, the full
/// THIRD-PARTY-NOTICES.md and the SIL Open Font License of every bundled overlay font. LICENSE and the notices are
/// embedded from the repository root by BetterVitals.csproj, so they never drift from the repo.
/// </summary>
internal static class Licenses
{
    public const string Repository = "https://github.com/NemeWT10/BetterVitals";
    public const string LicenseResource = "BetterVitals.LICENSE.txt";
    public const string NoticesResource = "BetterVitals.THIRD-PARTY-NOTICES.md";

    private const string LhmNotice =
        "LibreHardwareMonitorLib\n" +
        "Copyright (c) LibreHardwareMonitor contributors, based on Open Hardware Monitor by Michael Möller.\n\n" +
        "BetterVitals uses the library unmodified, as a NuGet package. It is licensed under the Mozilla Public License 2.0 (MPL-2.0).\n" +
        "Source code of the library: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor\n" +
        "Full text of the license: https://mozilla.org/MPL/2.0/";

    private const string PawnIoNotice =
        "PawnIO by namazso — https://pawnio.eu/\n\n" +
        "BetterVitals carries the official, signed and unmodified PawnIO_setup.exe and runs it only when you choose to install " +
        "the driver. The official build is proprietary freeware, redistributed unmodified with the author's permission. " +
        "The PawnIO source code is available under GPL-2.0-or-later with an exception: https://github.com/namazso/PawnIO";

    public static IReadOnlyList<LicenseEntry> All { get; } = Build();

    private static List<LicenseEntry> Build()
    {
        var list = new List<LicenseEntry>
        {
            new(AppEnv.ProductName, "MIT", Repository, () => Embedded(LicenseResource)),
            new("LibreHardwareMonitor", "MPL-2.0", "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor", () => LhmNotice),
            new("PawnIO", "Freeware", "https://pawnio.eu/", () => PawnIoNotice),
        };
        foreach (var font in BundledFonts.All)
            list.Add(new LicenseEntry(font.FamilyName, "SIL OFL 1.1", null, () => SafeFont(font)));
        list.Add(new("THIRD-PARTY-NOTICES", "", Repository + "/blob/main/THIRD-PARTY-NOTICES.md", () => Embedded(NoticesResource)));
        return list;
    }

    internal static string Embedded(string name)
    {
        try
        {
            using var s = typeof(Licenses).Assembly.GetManifestResourceStream(name);
            if (s is null) return "";
            using var r = new StreamReader(s);
            return r.ReadToEnd().Trim();
        }
        catch (Exception ex)
        {
            Log.Error($"about: cannot read {name}", ex);
            return "";
        }
    }

    private static string SafeFont(BundledFont font)
    {
        try
        {
            return font.LicenseText().Trim();
        }
        catch (Exception ex)
        {
            Log.Error($"about: cannot read the license of {font.FamilyName}", ex);
            return "";
        }
    }
}
