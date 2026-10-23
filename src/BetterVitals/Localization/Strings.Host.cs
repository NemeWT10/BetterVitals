using System.Runtime.CompilerServices;
using BetterVitals.Core.Catalog;

namespace BetterVitals.Localization;

/// <summary>Texts of the integration layer (tray tips) and the friendly parameter names of the catalog.</summary>
internal static class HostStrings
{
    [ModuleInitializer]
    internal static void Register()
    {
        Loc.Register(new Dictionary<string, (string Pl, string En)>
        {
            ["tip.exclusive.title"] = ("Nakładka nie była widoczna w grze", "The overlay was not visible in the game"),
            ["tip.exclusive.text"] = (
                "Ta gra działała w trybie wyłącznego pełnego ekranu, nad którym Windows nie rysuje innych okien. Ustaw w grze „Okno bez ramki” (borderless), a nakładka będzie widoczna.",
                "This game ran in exclusive full screen, where Windows draws no other windows on top. Switch the game to “Borderless window” and the overlay will show."),
            ["tip.restart.title"] = ("Potrzebny restart", "Restart needed"),
            ["tip.restart.pawnio"] = (
                "Sterownik PawnIO jest zainstalowany. Uruchom ponownie komputer, aby odczytywać temperatury CPU i płyty głównej.",
                "The PawnIO driver is installed. Restart your PC to read CPU and motherboard temperatures."),
            ["tip.update.title"] = ("BetterVitals — aktualizacja", "BetterVitals update"),
            ["tip.update.available"] = ("Dostępna jest wersja {0}. Szczegóły: Ustawienia → Aktualizacje.", "Version {0} is available. Details: Settings → Updates."),
            ["tip.update.ready"] = ("Wersja {0} jest gotowa. Zostanie zainstalowana przy ponownym uruchomieniu.", "Version {0} is ready. It installs when BetterVitals restarts."),
        });
        Loc.Register(FriendlyCatalog.Texts);
    }
}
