using System.Runtime.CompilerServices;

namespace BetterVitals.Localization;

/// <summary>Settings ▸ About (the shell owns settings.about, about.version, about.tagline, about.license).</summary>
internal static class SettingsAboutStrings
{
    [ModuleInitializer]
    internal static void Register() => Loc.Register(new Dictionary<string, (string Pl, string En)>
    {
        ["about.build"] = ("Kompilacja {0} · {1}", "Build {0} · {1}"),
        ["about.github"] = ("Kod na GitHubie", "Code on GitHub"),
        ["about.releases"] = ("Wszystkie wersje", "All releases"),
        ["about.issues"] = ("Zgłoś problem", "Report a problem"),
        ["about.free"] = ("Darmowy i otwarty (licencja MIT). Bez konta, bez reklam, bez telemetrii.", "Free and open source (MIT license). No account, no ads, no telemetry."),
        ["about.whatsNew"] = ("Co nowego", "What's new"),
        ["about.release"] = ("Wersja {0}", "Version {0}"),
        ["about.release.dated"] = ("Wersja {0} · {1}", "Version {0} · {1}"),
        ["about.current"] = ("Ta wersja", "This version"),
        ["about.older"] = ("Starsze wersje", "Older versions"),
        ["about.kind.New"] = ("Nowe", "New"),
        ["about.kind.Improved"] = ("Ulepszone", "Improved"),
        ["about.kind.Fixed"] = ("Poprawione", "Fixed"),
        ["about.licenses"] = ("Licencje", "Licenses"),
        ["about.licenses.lead"] = ("BetterVitals korzysta z tych składników — dziękujemy ich autorom. Kliknij, żeby zobaczyć pełny tekst.", "BetterVitals uses these components — thanks to their authors. Click one to read the full text."),
        ["about.licenses.font"] = ("Czcionka nakładki", "Overlay font"),
        ["about.licenses.notices"] = ("Pełna lista składników", "All third-party notices"),
        ["about.licenses.open"] = ("Otwórz stronę", "Open website"),
        ["about.licenses.missing"] = ("Tekst niedostępny w tej kompilacji.", "Text not available in this build."),
        ["about.folders"] = ("Gdzie są dane", "Where the data is"),
        ["about.folder.settings"] = ("Ustawienia", "Settings"),
        ["about.folder.data"] = ("Historia i logi", "History and logs"),
        ["about.folder.open"] = ("Otwórz", "Open"),
    });
}
