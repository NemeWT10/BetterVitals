using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Fake;

namespace ReadmeMedia;

/// <summary>
/// The fake desktop under another name: a popular Ryzen 7 9700X + RTX 5070 Ti build. Only the hardware names change
/// (ids stay, so the history is the same) and the two values that would give the card away: the memory clock (28 Gbps
/// GDDR7) and the board power (300 W).
/// </summary>
internal sealed class ShowcasePc(FakeSensorSource inner) : ISensorSource
{
    public const string Cpu = "AMD Ryzen 7 9700X";
    public const string Gpu = "NVIDIA GeForce RTX 5070 Ti";

    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["AMD Ryzen 7 9800X3D"] = Cpu,
        ["NVIDIA GeForce RTX 5080"] = Gpu,
        ["Gigabyte X870E AORUS ELITE WIFI7"] = "ASUS TUF GAMING B650-PLUS WIFI",
        ["ITE IT8696E"] = "Nuvoton NCT6799D",
        ["KINGSTON SKC3000D2048G"] = "WD_BLACK SN850X 2000GB",
        ["ST4000DM004-2CV104"] = "WDC WD40EZAZ-00SF3B0",
    };

    private SensorDescriptor? _memoryClock;
    private SensorDescriptor? _boardPower;

    public string Name => inner.Name;

    public SourceStatus Status => inner.Status;

    public event Action? InventoryChanged
    {
        add => inner.InventoryChanged += value;
        remove => inner.InventoryChanged -= value;
    }

    public SourceInventory Discover()
    {
        var found = inner.Discover();
        var hardware = found.Hardware.Select(h => Names.TryGetValue(h.Name, out var name) ? h with { Name = name } : h).ToList();
        var gpu = found.Hardware.FirstOrDefault(h => h.Name == "NVIDIA GeForce RTX 5080")?.Id;
        _memoryClock = found.Sensors.FirstOrDefault(s => s.HardwareId == gpu && s.Kind == SensorKind.Clock && s.Name == "GPU Memory");
        _boardPower = found.Sensors.FirstOrDefault(s => s.HardwareId == gpu && s.Kind == SensorKind.Power && s.Name == "GPU Package");
        return new SourceInventory(hardware, found.Sensors);
    }

    public void Update(ReadOnlySpan<SensorDescriptor> requested, Span<float> values)
    {
        inner.Update(requested, values);
        foreach (var d in requested)
        {
            if (ReferenceEquals(d, _memoryClock)) values[d.Index] = 14001;
            else if (ReferenceEquals(d, _boardPower)) values[d.Index] = Math.Min(values[d.Index], 296);
        }
    }

    public void Suspend() => ((ISensorSource)inner).Suspend();

    public void Resume() => ((ISensorSource)inner).Resume();

    public void Dispose() => inner.Dispose();
}
