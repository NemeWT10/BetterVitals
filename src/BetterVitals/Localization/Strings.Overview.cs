using System.Runtime.CompilerServices;

namespace BetterVitals.Localization;

/// <summary>Overview page strings (overview.*), each key a (Polish, English) pair.</summary>
internal static class OverviewStrings
{
    [ModuleInitializer]
    internal static void Register() => Loc.Register(new Dictionary<string, (string Pl, string En)>
    {
        ["overview.subtitle"] = ("Wszystkie podzespoły na żywo. Kliknij kartę, aby zobaczyć każdy czujnik z min / maks. / śr. od uruchomienia.",
            "Every component, live. Click a card to see each sensor with min / max / avg since start."),
        ["overview.expand"] = ("Pokaż wszystkie czujniki", "Show all sensors"),
        ["overview.collapse"] = ("Zwiń", "Collapse"),

        // ── short titles of the big values (the card header names the component) ──
        ["overview.key.cpu.temp"] = ("Temperatura", "Temperature"),
        ["overview.key.cpu.load"] = ("Obciążenie", "Load"),
        ["overview.key.cpu.clockMax"] = ("Taktowanie", "Clock"),
        ["overview.key.cpu.power"] = ("Moc", "Power"),
        ["overview.key.gpu.temp"] = ("Temperatura", "Temperature"),
        ["overview.key.gpu.hotspot"] = ("Hot Spot", "Hot Spot"),
        ["overview.key.gpu.memJunction"] = ("Pamięć (temp.)", "Memory temp."),
        ["overview.key.gpu.load"] = ("Obciążenie", "Load"),
        ["overview.key.gpu.vram"] = ("VRAM", "VRAM"),
        ["overview.key.gpu.power"] = ("Moc", "Power"),
        ["overview.key.gpu.clock"] = ("Taktowanie", "Clock"),
        ["overview.key.ram.used"] = ("Zajęta", "Used"),
        ["overview.key.ram.load"] = ("Obciążenie", "Load"),
        ["overview.key.ram.dimm"] = ("Temperatura", "Temperature"),
        ["overview.key.mb.vrm"] = ("VRM", "VRM"),
        ["overview.key.mb.chipset"] = ("Chipset", "Chipset"),
        ["overview.key.mb.cpuFan"] = ("Wentylator CPU", "CPU fan"),
        ["overview.key.mb.pump"] = ("Pompa", "Pump"),
        ["overview.key.disk.read"] = ("Odczyt", "Read"),
        ["overview.key.disk.write"] = ("Zapis", "Write"),
        ["overview.key.net.down"] = ("Pobieranie", "Download"),
        ["overview.key.net.up"] = ("Wysyłanie", "Upload"),
        ["overview.key.fps.fps"] = ("FPS", "FPS"),
        ["overview.key.fps.low1"] = ("1% low", "1% low"),
        ["overview.key.fps.frametime"] = ("Frametime", "Frametime"),

        // ── card badges ──
        ["overview.badge.asleep"] = ("w uśpieniu", "asleep"),
        ["overview.badge.asleep.gpu"] = ("uśpiona", "asleep"),
        ["overview.badge.asleep.drive"] = ("uśpiony", "asleep"),
        ["overview.badge.asleep.info"] = ("Urządzenie śpi, żeby oszczędzać energię. BetterVitals go nie budzi — wartości wrócą, gdy samo się obudzi.",
            "The device sleeps to save power. BetterVitals never wakes it — values come back when it wakes up by itself."),
        ["overview.badge.noGame"] = ("brak gry", "no game"),
        ["overview.badge.noGame.info"] = ("FPS i czasy klatek pojawią się, gdy gra lub inna aplikacja 3D będzie na pierwszym planie.",
            "FPS and frame times appear while a game or another 3D app is in the foreground."),
        ["overview.badge.limited"] = ("ograniczony odczyt", "limited"),
        ["overview.badge.limited.info"] = ("Część czujników tego urządzenia wymaga trybu pełnego (uprawnienia administratora i sterownik PawnIO). Ustawienia → Uprawnienia.",
            "Some sensors of this device need full mode (administrator rights and the PawnIO driver). Settings → Permissions."),

        // ── banner ──
        ["overview.banner.limited"] = ("Część czujników wymaga trybu pełnego — Ustawienia → Uprawnienia.",
            "Some sensors need full mode — Settings → Permissions."),
        ["overview.banner.limitedWith"] = ("Część czujników wymaga trybu pełnego: {0}", "Some sensors need full mode: {0}"),
        ["overview.banner.action"] = ("Uprawnienia", "Permissions"),

        // ── empty ──
        ["overview.empty.title"] = ("Czekam na czujniki…", "Waiting for sensors…"),
        ["overview.empty.body"] = ("Pierwszy odczyt sprzętu trwa zwykle sekundę. Jeśli nic się nie pojawia, sprawdź Ustawienia → Uprawnienia.",
            "The first hardware scan usually takes a second. If nothing shows up, check Settings → Permissions."),
    });
}
