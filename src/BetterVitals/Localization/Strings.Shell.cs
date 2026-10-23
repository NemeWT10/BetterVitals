using System.Runtime.CompilerServices;

namespace BetterVitals.Localization;

/// <summary>Shell strings (tray, flyout, panel, settings, common names), each key a (Polish, English) pair.</summary>
internal static class ShellStrings
{
    [ModuleInitializer]
    internal static void Register() => Loc.Register(new Dictionary<string, (string Pl, string En)>
    {
        // ── common ──
        ["common.undo"] = ("Cofnij", "Undo"),
        ["common.on"] = ("Wł.", "On"),
        ["common.off"] = ("Wył.", "Off"),
        ["common.live"] = ("Na żywo", "Live"),

        // ── window chrome ──
        ["window.minimize"] = ("Minimalizuj", "Minimize"),
        ["window.maximize"] = ("Maksymalizuj", "Maximize"),
        ["window.restore"] = ("Przywróć", "Restore"),
        ["window.close"] = ("Zamknij", "Close"),

        // ── tray menu ──
        ["tray.overlay"] = ("Nakładka", "Overlay"),
        ["tray.editOverlay"] = ("Edytuj nakładkę…", "Edit overlay…"),
        ["tray.panel"] = ("Panel", "Panel"),
        ["tray.settings"] = ("Ustawienia", "Settings"),
        ["tray.about"] = ("O programie", "About"),
        ["tray.exit"] = ("Zakończ", "Exit"),

        // ── flyout ──
        ["flyout.overlay"] = ("Nakładka na ekranie", "On-screen overlay"),
        ["flyout.overlay.on"] = ("Widoczna na ekranie i w grach", "Visible on screen and in games"),
        ["flyout.overlay.off"] = ("Ukryta", "Hidden"),
        ["flyout.panel"] = ("Panel", "Panel"),
        ["flyout.editOverlay"] = ("Nakładka", "Overlay"),
        ["flyout.settings"] = ("Ustawienia", "Settings"),
        ["flyout.recording.one"] = ("Nagrywanie: {0} czujnik", "Recording: {0} sensor"),
        ["flyout.recording.few"] = ("Nagrywanie: {0} czujniki", "Recording: {0} sensors"),
        ["flyout.recording.many"] = ("Nagrywanie: {0} czujników", "Recording: {0} sensors"),
        ["flyout.liveOnly"] = ("Tylko na żywo — bez zapisu historii", "Live only — no history recorded"),
        ["flyout.noSensors"] = ("Czekam na czujniki…", "Waiting for sensors…"),

        // ── component families (HardwareKind) ──
        ["hw.Cpu"] = ("CPU", "CPU"),
        ["hw.Gpu"] = ("GPU", "GPU"),
        ["hw.Motherboard"] = ("Płyta główna", "Motherboard"),
        ["hw.Memory"] = ("RAM", "RAM"),
        ["hw.Storage"] = ("Dysk", "Drive"),
        ["hw.Network"] = ("Sieć", "Network"),
        ["hw.Battery"] = ("Bateria", "Battery"),
        ["hw.Psu"] = ("Zasilacz", "Power supply"),
        ["hw.Cooler"] = ("Chłodzenie", "Cooling"),
        ["hw.Controller"] = ("Kontroler", "Controller"),
        ["hw.Game"] = ("Gra", "Game"),
        ["hw.Other"] = ("Inne", "Other"),

        // ── what a sensor measures (SensorKind) ──
        ["kind.Voltage"] = ("Napięcie", "Voltage"),
        ["kind.Current"] = ("Natężenie", "Current"),
        ["kind.Power"] = ("Moc", "Power"),
        ["kind.Clock"] = ("Taktowanie", "Clock"),
        ["kind.Temperature"] = ("Temperatura", "Temperature"),
        ["kind.Load"] = ("Obciążenie", "Load"),
        ["kind.Frequency"] = ("Częstotliwość", "Frequency"),
        ["kind.Fan"] = ("Wentylator", "Fan"),
        ["kind.Flow"] = ("Przepływ", "Flow"),
        ["kind.Control"] = ("Sterowanie", "Control"),
        ["kind.Level"] = ("Poziom", "Level"),
        ["kind.Factor"] = ("Współczynnik", "Factor"),
        ["kind.Data"] = ("Pamięć", "Memory"),
        ["kind.SmallData"] = ("Pamięć", "Memory"),
        ["kind.Throughput"] = ("Transfer", "Throughput"),
        ["kind.TimeSpan"] = ("Czas", "Time"),
        ["kind.Timing"] = ("Opóźnienie", "Timing"),
        ["kind.Energy"] = ("Energia", "Energy"),
        ["kind.Noise"] = ("Hałas", "Noise"),
        ["kind.Conductivity"] = ("Przewodność", "Conductivity"),
        ["kind.Humidity"] = ("Wilgotność", "Humidity"),
        ["kind.Fps"] = ("FPS", "FPS"),
        ["kind.FrameTime"] = ("Czas klatki", "Frame time"),

        // ── sensor notes (SensorDescriptor.NoteKey) ──

        // ── panel: navigation and page headers ──
        ["nav.overview"] = ("Przegląd", "Overview"),
        ["nav.history"] = ("Historia", "History"),
        ["nav.sensors"] = ("Czujniki", "Sensors"),
        ["nav.overlay"] = ("Nakładka", "Overlay"),
        ["nav.settings"] = ("Ustawienia", "Settings"),
        ["page.overview.subtitle"] = ("Karty podzespołów z kluczowymi wartościami na żywo.", "Component cards with the key values, live."),
        ["page.history.subtitle"] = ("Wykresy w czasie i min / śr. / max dla dowolnego zakresu.", "Charts over time with min / avg / max for any range."),
        ["page.sensors.subtitle"] = ("Co zapisywać do historii i co pokazywać na nakładce.", "What to record for history and what to show on the overlay."),
        ["page.overlay.subtitle"] = ("Co i gdzie pokazywać na ekranie — także w grach.", "What to show on screen and where — in games too."),
        ["page.placeholder.title"] = ("Ta strona jest w budowie", "This page is on its way"),
        ["page.placeholder.body"] = ("Pojawi się w jednej z najbliższych wersji. Czujniki działają już w tle.", "It arrives in an upcoming version. Sensors already run in the background."),

        // ── settings: general ──
        ["settings.title"] = ("Ustawienia", "Settings"),
        ["settings.subtitle"] = ("Wygląd i zachowanie programu. Zmiany działają od razu.", "How the app looks and behaves. Changes apply right away."),
        ["settings.general"] = ("Ogólne", "General"),
        ["settings.language"] = ("Język", "Language"),
        ["settings.language.desc"] = ("Język interfejsu. Nazwy czujników zostają takie, jak podaje sprzęt.", "Interface language. Sensor names stay as the hardware reports them."),
        ["settings.language.system"] = ("Jak system", "System"),
        ["settings.language.pl"] = ("Polski", "Polski"),
        ["settings.language.en"] = ("English", "English"),
        ["settings.theme"] = ("Motyw", "Theme"),
        ["settings.theme.desc"] = ("Zmienia tylko kolory — od razu, bez ponownego uruchamiania.", "Changes the colors only — instantly, no restart."),
        ["settings.animations"] = ("Animacje", "Animations"),
        ["settings.animations.desc"] = ("Płynne przejścia i mikroanimacje. Wyłączone także wtedy, gdy Windows ma wyłączone efekty animacji.", "Smooth transitions and micro-animations. Also off while Windows animation effects are off."),
        ["settings.explanations"] = ("Objaśnienia parametrów", "Parameter explanations"),
        ["settings.explanations.desc"] = ("Po najechaniu na nazwę czujnika krótko wyjaśnia, co oznacza.", "Hover over a sensor name for a short explanation of what it means."),
        ["settings.explanations.info"] = ("Na przykład „Tctl/Tdie” to temperatura sterująca procesorów AMD — według niej pracują wentylatory.", "For example “Tctl/Tdie” is the control temperature of AMD processors — the one the fans follow."),

        // ── themes ──
        ["theme.system"] = ("Jak system", "System"),
        ["theme.aurora"] = ("Aurora", "Aurora"),
        ["theme.graphite"] = ("Graphite", "Graphite"),
        ["theme.daylight"] = ("Daylight", "Daylight"),
        ["theme.neon"] = ("Neon", "Neon"),
        ["theme.sunset"] = ("Sunset", "Sunset"),

        // ── about ──
        ["settings.about"] = ("O programie", "About"),
        ["about.tagline"] = ("Lekki monitor sprzętu w zasobniku: temperatury, obciążenia, taktowania, historia i nakładka w grach.", "A light hardware monitor in the tray: temperatures, loads, clocks, history and an in-game overlay."),
        ["about.version"] = ("Wersja {0}", "Version {0}"),
        ["about.license"] = ("Licencja MIT · LibreHardwareMonitor (MPL-2.0)", "MIT license · LibreHardwareMonitor (MPL-2.0)"),

        // ── toasts ──
        ["toast.overlayOn"] = ("Nakładka włączona", "Overlay on"),
        ["toast.overlayOff"] = ("Nakładka wyłączona", "Overlay off"),

        // ── developer style gallery (snapshots only) ──
        ["dev.gallery"] = ("Galeria stylów", "Style gallery"),
        ["dev.gallery.subtitle"] = ("Podstawowe kontrolki dla stron — przyciski, przełączniki, pola, listy, chipy.", "Base controls for pages — buttons, toggles, fields, lists, chips."),
        ["dev.primary"] = ("Zapisz", "Save"),
        ["dev.secondary"] = ("Eksportuj", "Export"),
        ["dev.ghost"] = ("Anuluj", "Cancel"),
        ["dev.search"] = ("Szukaj czujników…", "Search sensors…"),
        ["dev.field"] = ("Moja etykieta", "My label"),
        ["dev.simple"] = ("Prosty", "Simple"),
        ["dev.advanced"] = ("Zaawansowany", "Advanced"),
        ["dev.range"] = ("Zakres", "Range"),
    });
}
