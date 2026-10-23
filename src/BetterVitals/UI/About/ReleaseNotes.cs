namespace BetterVitals.UI.About;

public enum ChangeKind : byte
{
    New,
    Improved,
    Fixed,
}

/// <summary>One line of "What's new", in both languages.</summary>
public sealed record ReleaseChange(ChangeKind Kind, string Pl, string En);

/// <summary>A version's notes. <paramref name="Date"/> null = not released yet (shown without a date).</summary>
public sealed record ReleaseNote(string Version, DateOnly? Date, string SummaryPl, string SummaryEn, IReadOnlyList<ReleaseChange> Changes);

/// <summary>
/// "What's new" per version (PL + EN), newest first. Add an entry for every version that changes something a user can
/// notice (project rule: the version is bumped by the first change after a release, then stays until the next one).
/// </summary>
public static class ReleaseNotes
{
    public static IReadOnlyList<ReleaseNote> All { get; } =
    [
        new("0.1.0", null,
            "Pierwsza wersja BetterVitals — lekki monitor sprzętu w zasobniku.",
            "The first version of BetterVitals — a light hardware monitor in the tray.",
            [
                new(ChangeKind.New, "Ikona w zasobniku z okienkiem kluczowych wartości i mini-wykresami.", "A tray icon with a flyout of key values and mini charts."),
                new(ChangeKind.New, "Panel: przegląd podzespołów, historia, czujniki, edytor nakładki i ustawienia.", "The panel: component overview, history, sensors, overlay editor and settings."),
                new(ChangeKind.New, "Czujniki CPU, płyty głównej, RAM i GPU (także GPU Hot Spot w RTX 50) przez LibreHardwareMonitor i PawnIO oraz czujniki Windows bez uprawnień administratora.", "CPU, motherboard, RAM and GPU sensors (including the RTX 50 GPU Hot Spot) through LibreHardwareMonitor and PawnIO, plus Windows sensors without administrator rights."),
                new(ChangeKind.New, "FPS, czas klatki oraz 1% / 0.1% low — mierzone przez Windows, bez dotykania gier.", "FPS, frame time and 1% / 0.1% low — measured through Windows, without touching games."),
                new(ChangeKind.New, "Nakładka na ekranie i w grach, niewidoczna w streamach i nagraniach.", "An on-screen and in-game overlay, hidden from streams and recordings."),
                new(ChangeKind.New, "Historia zapisywana oszczędnie (ok. 1–2 MB na dzień) z min / śr. / max dla dowolnego zakresu i eksportem do CSV.", "History saved sparingly (about 1–2 MB per day) with min / avg / max for any range and CSV export."),
                new(ChangeKind.New, "Pięć motywów, polski i angielski, objaśnienia nazw czujników.", "Five themes, Polish and English, explanations of sensor names."),
                new(ChangeKind.New, "Tryb pełny (jedno okno UAC) albo podstawowy oraz aktualizacje z GitHuba ze sprawdzaniem podpisu.", "Full mode (one UAC prompt) or basic mode, and updates from GitHub with a signature check."),
            ]),
    ];
}
