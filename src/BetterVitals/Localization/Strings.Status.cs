using System.Runtime.CompilerServices;

namespace BetterVitals.Localization;

/// <summary>
/// Sensor-source status texts (SourceStatus.MessageKey from LhmStatusKeys / FpsSource), source names and states, sensor
/// notes. Shown on Settings ▸ Permissions and driver and wherever a source explains itself. Plain words; technical names
/// kept, explained in a sentence.
/// </summary>
internal static class StatusStrings
{
    /// <summary>Every status key a sensor source can report (tests check each has a text).</summary>
    internal static readonly string[] SourceKeys =
    [
        "source.lhm.notElevated", "source.lhm.noPawnIo", "source.lhm.oldPawnIo", "source.lhm.driverNotRunning", "source.lhm.paused",
        "source.lhm.failed", "source.fps.noAccess", "source.fps.blocked",
    ];

    [ModuleInitializer]
    internal static void Register() => Loc.Register(new Dictionary<string, (string Pl, string En)>
    {
        // ── LibreHardwareMonitor + PawnIO (LhmStatusKeys) ──
        ["source.lhm.notElevated"] = (
            "Program działa bez uprawnień administratora, więc temperatur CPU, płyty głównej i RAM nie da się odczytać. Włącz tryb pełny, aby je zobaczyć.",
            "BetterVitals runs without administrator rights, so CPU, motherboard and RAM temperatures can't be read. Turn on full mode to see them."),
        ["source.lhm.noPawnIo"] = (
            "Brak sterownika PawnIO. Bez niego nie ma temperatur CPU, płyty głównej i RAM ani GPU Hot Spot w kartach RTX 50. Zainstalujesz go w Ustawieniach → Uprawnienia i sterownik.",
            "The PawnIO driver is not installed. Without it there are no CPU, motherboard or RAM temperatures and no GPU Hot Spot on RTX 50 cards. You can install it in Settings → Permissions and driver."),
        ["source.lhm.oldPawnIo"] = (
            "Zainstalowany PawnIO jest starszy niż 2.1. Niektóre anticheaty (np. FACEIT) blokują tę wersję — zaktualizuj go w Ustawieniach → Uprawnienia i sterownik.",
            "The installed PawnIO is older than 2.1. Some anti-cheats (e.g. FACEIT) block that version — update it in Settings → Permissions and driver."),
        ["source.lhm.driverNotRunning"] = (
            "PawnIO jest zainstalowany, ale sterownik jeszcze nie działa. Zwykle wystarczy ponownie uruchomić komputer.",
            "PawnIO is installed, but the driver is not running yet. Restarting the PC usually fixes this."),
        ["source.lhm.paused"] = (
            "Wstrzymane na czas gry z anticheatem, tak jak ustawiono w Uprawnieniach i sterowniku. Pozostałe czujniki działają dalej.",
            "Paused while a game with anti-cheat runs, as chosen in Permissions and driver. All other sensors keep working."),
        ["source.lhm.failed"] = (
            "Nie udało się uruchomić odczytu sprzętu. Program działa dalej z czujnikami Windows (jak w trybie podstawowym).",
            "Hardware reading could not start. BetterVitals keeps running with the Windows sensors (as in basic mode)."),

        // ── FPS (FpsSource) ──
        ["source.fps.noAccess"] = (
            "Pomiar FPS wymaga uprawnień administratora albo członkostwa w grupie „Użytkownicy dzienników wydajności”. Włącz tryb pełny.",
            "Measuring FPS needs administrator rights or membership in the “Performance Log Users” group. Turn on full mode."),
        ["source.fps.blocked"] = (
            "Windows albo anticheat gry zablokował pomiar FPS. Nic się nie psuje — FPS pokazuje wtedy „—”. Zwykle pomaga uruchomienie BetterVitals przed grą.",
            "Windows or the game's anti-cheat blocked FPS measuring. Nothing breaks — FPS then shows “—”. Starting BetterVitals before the game usually helps."),

        // ── source names (ISensorSource.Name) ──
        ["source.name.lhm"] = ("Czujniki sprzętowe (LibreHardwareMonitor)", "Hardware sensors (LibreHardwareMonitor)"),
        ["source.name.native"] = ("Czujniki Windows", "Windows sensors"),
        ["source.name.fps"] = ("FPS w grach", "FPS in games"),
        ["source.name.derived"] = ("Wartości wyliczane (maks., średnia)", "Calculated values (max, average)"),
        ["source.name.fake"] = ("Czujniki pokazowe", "Demo sensors"),
        ["source.name.other"] = ("Źródło „{0}”", "Source “{0}”"),

        // ── states (SourceState) ──
        ["source.state.Idle"] = ("Uruchamia się", "Starting"),
        ["source.state.Ok"] = ("Działa", "Working"),
        ["source.state.Limited"] = ("Częściowo", "Partly"),
        ["source.state.Paused"] = ("Wstrzymane", "Paused"),
        ["source.state.Unavailable"] = ("Niedostępne", "Unavailable"),
        ["source.ok"] = ("Wszystko w porządku.", "All good."),
        ["source.idle"] = ("Czeka na pierwszy odczyt.", "Waiting for the first reading."),

        // ── sensor notes (SensorDescriptor.NoteKey) ──
        ["sensor.note.unofficialHotspot"] = (
            "Odczyt nieoficjalny (nieudokumentowany czujnik), ok. ±1 °C",
            "Unofficial reading (undocumented sensor), about ±1 °C"),
    });
}
