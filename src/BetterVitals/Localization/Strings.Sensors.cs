using System.Runtime.CompilerServices;

namespace BetterVitals.Localization;

/// <summary>Sensors page and shared sensor-row strings (sensors.*), each key a (Polish, English) pair.</summary>
internal static class SensorsStrings
{
    [ModuleInitializer]
    internal static void Register() => Loc.Register(new Dictionary<string, (string Pl, string En)>
    {
        ["sensors.subtitle"] = ("Co zapisywać do historii i co pokazywać na nakładce. Zmiany działają od razu.",
            "What to record for history and what to show on the overlay. Changes apply right away."),
        ["sensors.count.one"] = ("{0} czujnik", "{0} sensor"),
        ["sensors.count.few"] = ("{0} czujniki", "{0} sensors"),
        ["sensors.count.many"] = ("{0} czujników", "{0} sensors"),

        // ── kind groups (sections of an expanded card, type chips) ──
        ["sensors.group.Temperature"] = ("Temperatury", "Temperatures"),
        ["sensors.group.Load"] = ("Obciążenie", "Load"),
        ["sensors.group.Clock"] = ("Taktowania", "Clocks"),
        ["sensors.group.Voltage"] = ("Napięcia", "Voltages"),
        ["sensors.group.Power"] = ("Moc", "Power"),
        ["sensors.group.Fan"] = ("Wentylatory", "Fans"),
        ["sensors.group.Memory"] = ("Pamięć / dane", "Memory / data"),
        ["sensors.group.Throughput"] = ("Przepustowość", "Throughput"),
        ["sensors.group.Level"] = ("Poziomy", "Levels"),
        ["sensors.group.Fps"] = ("FPS", "FPS"),
        ["sensors.group.Other"] = ("Inne", "Other"),

        // ── row columns ──
        ["sensors.col.sensor"] = ("Czujnik", "Sensor"),
        ["sensors.col.now"] = ("Teraz", "Now"),
        ["sensors.col.min"] = ("Min", "Min"),
        ["sensors.col.max"] = ("Maks.", "Max"),
        ["sensors.col.avg"] = ("Śr.", "Avg"),
        ["sensors.col.minMax"] = ("Min / maks.", "Min / max"),
        ["sensors.col.history"] = ("Historia", "History"),
        ["sensors.col.overlay"] = ("Nakładka", "Overlay"),

        // ── row switches ──
        ["sensors.row.historyAdd"] = ("Nagrywaj do historii", "Record for history"),
        ["sensors.row.historyOn"] = ("Nagrywany do historii — kliknij, aby wyłączyć", "Recorded for history — click to stop"),
        ["sensors.row.historyOffTip"] = ("Zapis historii jest wyłączony (tylko na żywo). Wybór zostanie użyty po włączeniu.",
            "History recording is off (live only). The choice applies once it is on."),
        ["sensors.row.overlayAdd"] = ("Pokaż na nakładce", "Show on the overlay"),
        ["sensors.row.overlayOn"] = ("Na nakładce — kliknij, aby usunąć", "On the overlay — click to remove"),

        // ── row context menu ──
        ["sensors.menu.record"] = ("Nagrywaj do historii", "Record for history"),
        ["sensors.menu.overlay"] = ("Pokaż na nakładce", "Show on overlay"),
        ["sensors.menu.pin"] = ("Przypnij we flyoucie", "Pin in the flyout"),
        ["sensors.menu.history"] = ("Pokaż w historii", "Show in history"),

        // ── section header switches ──
        ["sensors.section.historyAllOn"] = ("Nagrywaj całą grupę", "Record the whole group"),
        ["sensors.section.historyAllOff"] = ("Nie nagrywaj tej grupy", "Stop recording this group"),
        ["sensors.section.overlayAllOn"] = ("Całą grupę na nakładkę", "Whole group on the overlay"),
        ["sensors.section.overlayAllOff"] = ("Usuń grupę z nakładki", "Remove the group from the overlay"),

        // ── toasts ──
        ["sensors.toast.recordOn.single"] = ("Nagrywany do historii: {0}", "Recording for history: {0}"),
        ["sensors.toast.recordOff.single"] = ("Bez nagrywania: {0}", "No longer recorded: {0}"),
        ["sensors.toast.recordOn.one"] = ("Nagrywany do historii: {0} czujnik", "Recording {0} sensor for history"),
        ["sensors.toast.recordOn.few"] = ("Nagrywane do historii: {0} czujniki", "Recording {0} sensors for history"),
        ["sensors.toast.recordOn.many"] = ("Nagrywane do historii: {0} czujników", "Recording {0} sensors for history"),
        ["sensors.toast.recordOff.one"] = ("Bez nagrywania: {0} czujnik", "{0} sensor no longer recorded"),
        ["sensors.toast.recordOff.few"] = ("Bez nagrywania: {0} czujniki", "{0} sensors no longer recorded"),
        ["sensors.toast.recordOff.many"] = ("Bez nagrywania: {0} czujników", "{0} sensors no longer recorded"),
        ["sensors.toast.overlayOn.single"] = ("Na nakładce: {0}", "On the overlay: {0}"),
        ["sensors.toast.overlayOff.single"] = ("Usunięto z nakładki: {0}", "Removed from the overlay: {0}"),
        ["sensors.toast.overlayOn.one"] = ("Na nakładce: {0} czujnik", "{0} sensor on the overlay"),
        ["sensors.toast.overlayOn.few"] = ("Na nakładce: {0} czujniki", "{0} sensors on the overlay"),
        ["sensors.toast.overlayOn.many"] = ("Na nakładce: {0} czujników", "{0} sensors on the overlay"),
        ["sensors.toast.overlayOff.one"] = ("Usunięto z nakładki: {0} czujnik", "{0} sensor removed from the overlay"),
        ["sensors.toast.overlayOff.few"] = ("Usunięto z nakładki: {0} czujniki", "{0} sensors removed from the overlay"),
        ["sensors.toast.overlayOff.many"] = ("Usunięto z nakładki: {0} czujników", "{0} sensors removed from the overlay"),
        ["sensors.toast.pinOn"] = ("Przypięto we flyoucie: {0}", "Pinned in the flyout: {0}"),
        ["sensors.toast.pinOff"] = ("Odpięto z flyoutu: {0}", "Unpinned from the flyout: {0}"),
        ["sensors.toast.pinFull"] = ("Flyout mieści {0} wartości — najpierw odepnij jedną.", "The flyout holds {0} values — unpin one first."),
        ["sensors.toast.preset"] = ("Zestaw „{0}”: {1} do historii", "“{0}” preset: {1} recorded"),
        ["sensors.toast.historyOn"] = ("Zapis historii włączony", "History recording on"),
        ["sensors.toast.historyOff"] = ("Zapis historii wyłączony — tylko na żywo", "History recording off — live only"),

        // ── presets ──
        ["sensors.presets"] = ("Zestawy", "Presets"),
        ["sensors.preset.Key"] = ("Kluczowe", "Key"),
        ["sensors.preset.All"] = ("Wszystkie", "All"),
        ["sensors.preset.None"] = ("Nic", "None"),
        ["sensors.preset.Key.tip"] = ("Nagrywaj najważniejsze parametry: temperatury, obciążenia, taktowania, moc, VRAM, wentylatory, FPS (zalecane).",
            "Record the essentials: temperatures, loads, clocks, power, VRAM, fans, FPS (recommended)."),
        ["sensors.preset.All.tip"] = ("Nagrywaj każdy czujnik tego komputera (więcej miejsca na dysku).", "Record every sensor of this PC (uses more disk space)."),
        ["sensors.preset.None.tip"] = ("Nie nagrywaj żadnego czujnika. Nakładka i wartości na żywo działają dalej.",
            "Record no sensor. The overlay and live values keep working."),

        // ── status card ──
        ["sensors.status.title"] = ("Zapisuj historię", "Record history"),
        ["sensors.status.settings"] = ("Ustawienia zapisu", "Recording settings"),
        ["sensors.status.perDay"] = ("~{0}/dzień", "~{0}/day"),
        ["sensors.status.every"] = ("co {0} s", "every {0} s"),
        ["sensors.status.onDisk"] = ("zajęte {0}", "{0} on disk"),
        ["sensors.status.notRecorded"] = ("nic nie jest zapisywane", "nothing is written"),
        ["sensors.liveOnly.title"] = ("Tylko na żywo", "Live only"),
        ["sensors.liveOnly.body"] = ("Nic nie jest zapisywane na dysk. Strona Historia pokazuje ostatnie 15 minut z pamięci, a wcześniej zapisane dane zostają.",
            "Nothing is written to disk. The History page shows the last 15 minutes from memory, and data recorded earlier stays."),

        // ── toolbar, empty list ──
        ["sensors.search"] = ("Szukaj czujnika lub urządzenia…", "Search a sensor or device…"),
        ["sensors.onlySelected"] = ("Tylko zaznaczone", "Only selected"),
        ["sensors.empty.title"] = ("Żaden czujnik nie pasuje do filtrów", "No sensor matches the filters"),
        ["sensors.empty.clear"] = ("Wyczyść filtry", "Clear filters"),
    });
}
