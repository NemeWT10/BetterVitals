using System.Runtime.CompilerServices;

namespace BetterVitals.Localization;

/// <summary>The first-run guide: welcome, how to read sensors, history and autostart, where the tray icon is.</summary>
internal static class FirstRunStrings
{
    [ModuleInitializer]
    internal static void Register() => Loc.Register(new Dictionary<string, (string Pl, string En)>
    {
        ["firstrun.title"] = ("Witaj w BetterVitals", "Welcome to BetterVitals"),
        ["firstrun.stepOf"] = ("Krok {0} z {1}", "Step {0} of {1}"),
        ["firstrun.back"] = ("Wstecz", "Back"),
        ["firstrun.next"] = ("Dalej", "Next"),
        ["firstrun.finish"] = ("Zakończ", "Finish"),
        ["firstrun.finishFull"] = ("Zakończ i daj zgodę", "Finish and allow"),
        ["firstrun.close"] = ("Gotowe", "Done"),
        ["firstrun.recommended"] = ("Zalecane", "Recommended"),

        // 1 · welcome
        ["firstrun.welcome.title"] = ("Witaj w BetterVitals", "Welcome to BetterVitals"),
        ["firstrun.welcome.body"] = ("Lekki monitor sprzętu w zasobniku: temperatury, obciążenia, taktowania, wykresy z historią i nakładka w grach. Trzy krótkie pytania i gotowe.", "A light hardware monitor in the tray: temperatures, loads, clocks, history charts and an in-game overlay. Three short questions and you're set."),
        ["firstrun.welcome.language"] = ("Język", "Language"),
        ["firstrun.welcome.languageHint"] = ("Wybrany automatycznie według Windows. Zmienisz go też później w Ustawieniach.", "Picked automatically from Windows. You can change it later in Settings too."),
        ["firstrun.welcome.point1"] = ("Działa offline — bez konta, bez telemetrii", "Works offline — no account, no telemetry"),
        ["firstrun.welcome.point2"] = ("Prawie nie obciąża komputera", "Barely uses your PC"),
        ["firstrun.welcome.point3"] = ("Bezpieczny dla gier z anticheatem — niczego w nich nie uruchamia", "Safe for anti-cheat games — never runs anything inside them"),

        // 2 · how to read sensors
        ["firstrun.mode.title"] = ("Jak czytać czujniki?", "How should sensors be read?"),
        ["firstrun.mode.body"] = ("Część czujników (np. temperatura CPU) Windows pokazuje tylko programom z uprawnieniami administratora.", "Windows only shows some sensors (e.g. the CPU temperature) to apps with administrator rights."),
        ["firstrun.mode.full"] = ("Tryb pełny", "Full mode"),
        ["firstrun.mode.full.desc"] = ("Jedno okno zgody Windows (UAC) teraz — potem BetterVitals startuje z potrzebnymi uprawnieniami bez pytania. Instaluje się w Program Files i może zainstalować PawnIO — podpisany sterownik, dzięki któremu czyta temperatury CPU, płyty głównej i RAM oraz GPU Hot Spot w kartach RTX 50. Nigdy niczego nie uruchamia w Twoich grach.", "One Windows permission prompt (UAC) now; after that BetterVitals starts with the needed rights without asking. It installs to Program Files and can install PawnIO — a signed driver that lets it read CPU, motherboard and RAM temperatures and the RTX 50 GPU Hot Spot. It never runs anything in your games."),
        ["firstrun.mode.pawnio"] = ("Zainstaluj PawnIO", "Install PawnIO"),
        ["firstrun.mode.pawnio.installed"] = ("PawnIO {0} jest już zainstalowany", "PawnIO {0} is already installed"),
        ["firstrun.mode.basic"] = ("Tryb podstawowy", "Basic mode"),
        ["firstrun.mode.basic.desc"] = ("Bez uprawnień administratora: GPU, obciążenia, pamięć, dyski i sieć. Bez temperatury CPU. Tryb pełny włączysz później w Ustawieniach.", "No administrator rights: GPU, loads, memory, drives and network; no CPU temperature. You can turn on full mode later in Settings."),

        // 3 · history
        ["firstrun.history.title"] = ("Zapisywać historię do wykresów?", "Save history for charts?"),
        ["firstrun.history.body"] = ("Historia pozwala zobaczyć, co działo się z komputerem godzinę, dzień czy tydzień temu.", "History lets you see what your PC was doing an hour, a day or a week ago."),
        ["firstrun.history.yes"] = ("Tak, kluczowe parametry", "Yes, the key values"),
        ["firstrun.history.yes.desc"] = ("Temperatury, obciążenia, taktowania, moc, pamięć, dyski, sieć i FPS — ok. 1–2 MB na dzień. Co zapisywać, zmienisz w każdej chwili.", "Temperatures, loads, clocks, power, memory, drives, network and FPS — about 1–2 MB per day. Change what is saved any time."),
        ["firstrun.history.no"] = ("Nie, tylko na żywo", "No, live only"),
        ["firstrun.history.no.desc"] = ("Nic nie trafia na dysk. Wykresy pokazują tylko ostatnie 15 minut.", "Nothing goes to disk. Charts show only the last 15 minutes."),
        ["firstrun.autostart"] = ("Uruchamiaj z Windows", "Start with Windows"),
        ["firstrun.autostart.desc"] = ("Startuje w zasobniku chwilę po zalogowaniu i od razu zbiera dane.", "Starts in the tray shortly after you sign in and collects data right away."),

        // 4 · tray icon
        ["firstrun.tray.title"] = ("Gdzie mnie znajdziesz", "Where to find me"),
        ["firstrun.tray.body"] = ("BetterVitals mieszka w zasobniku obok zegara. Windows 11 chowa nowe ikony pod strzałką „^” — przypnij ją, żeby była zawsze widoczna.", "BetterVitals lives in the tray next to the clock. Windows 11 hides new icons under the “^” arrow — pin it to keep it visible."),
        ["firstrun.tray.step1"] = ("Kliknij strzałkę „^” obok zegara na pasku zadań.", "Click the “^” arrow next to the clock on the taskbar."),
        ["firstrun.tray.step2"] = ("Przeciągnij ikonę BetterVitals na pasek zadań — zostanie tam na stałe.", "Drag the BetterVitals icon onto the taskbar — it stays there."),
        ["firstrun.tray.step3"] = ("Albo: Ustawienia Windows → Personalizacja → Pasek zadań → Inne ikony zasobnika systemowego → BetterVitals.", "Or: Windows Settings → Personalization → Taskbar → Other system tray icons → BetterVitals."),
        ["firstrun.tray.fullNote"] = ("Po kliknięciu „Zakończ” Windows raz poprosi o zgodę. Potem BetterVitals uruchomi się ponownie z pełnym odczytem.", "After “Finish” Windows asks for permission once. Then BetterVitals restarts with full reading."),

        // working / result
        ["firstrun.working.title"] = ("Przygotowuję tryb pełny…", "Setting up full mode…"),
        ["firstrun.working.body"] = ("Potwierdź okno Windows (UAC). Za chwilę BetterVitals uruchomi się ponownie.", "Confirm the Windows prompt (UAC). BetterVitals restarts in a moment."),
        ["firstrun.failed.title"] = ("Tryb pełny nie został włączony", "Full mode was not turned on"),
        ["firstrun.failed.body"] = ("Windows nie dał zgody albo instalacja się nie udała. Nic się nie zepsuło — możesz spróbować jeszcze raz albo zacząć w trybie podstawowym i włączyć pełny później w Ustawieniach.", "Windows did not allow it or the install failed. Nothing broke — try again, or start in basic mode and turn on full mode later in Settings."),
        ["firstrun.failed.retry"] = ("Spróbuj ponownie", "Try again"),
        ["firstrun.failed.basic"] = ("Kontynuuj w trybie podstawowym", "Continue in basic mode"),
    });
}
