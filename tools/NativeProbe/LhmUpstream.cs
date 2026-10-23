using BetterVitals.Core.Sensors;
using LibreHardwareMonitor.Hardware;

namespace BetterVitals.Tools.NativeProbe;

/// <summary>
/// A frozen copy of what LibreHardwareMonitor registers on this PC without administrator rights and without the driver, shaped like
/// an upstream source (ids built with <see cref="SensorIds"/> from the LHM names, as the real LHM source does). Lets the probe show
/// what NativeSource adds on top of a real LHM, not only on top of fakes.
/// </summary>
internal sealed class LhmUpstream : ISensorSource
{
    private readonly List<HardwareDescriptor> _hardware = [];
    private readonly List<SensorDescriptor> _sensors = [];

    public string Name => "lhm-snapshot";

    public SourceStatus Status => new(SourceState.Ok);

    public event Action? InventoryChanged { add { } remove { } }

    public static LhmUpstream? TryCapture(out string error)
    {
        error = "";
        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsStorageEnabled = true,
            IsNetworkEnabled = true,
            IsMotherboardEnabled = false,
            IsControllerEnabled = false,
            IsBatteryEnabled = false,
            IsPsuEnabled = false,
        };
        try
        {
            computer.Open();
            var up = new LhmUpstream();
            var ordinals = new Dictionary<string, int>();
            foreach (var hw in computer.Hardware) up.Add(hw, null, ordinals);
            return up;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
        finally
        {
            try { computer.Close(); } catch { /* ignored */ }
        }
    }

    private void Add(IHardware hw, string? parentId, Dictionary<string, int> ordinals)
    {
        var kind = Map(hw.HardwareType);
        var key = $"{kind}|{hw.Name}";
        ordinals.TryGetValue(key, out var ordinal);
        ordinals[key] = ordinal + 1;
        var id = SensorIds.Hardware(kind, hw.Name, ordinal, parentId);
        _hardware.Add(new HardwareDescriptor { Id = id, Name = hw.Name, Kind = kind, ParentId = parentId, Source = "lhm" });

        var repeats = new Dictionary<string, int>();
        var order = 0;
        foreach (var s in hw.Sensors)
        {
            if (!Enum.TryParse<SensorKind>(s.SensorType.ToString(), out var sk)) continue;
            var rk = $"{sk}|{s.Name}";
            repeats.TryGetValue(rk, out var seen);
            repeats[rk] = seen + 1;
            _sensors.Add(new SensorDescriptor
            {
                Id = SensorIds.Sensor(id, sk, s.Name, seen + 1),
                HardwareId = id,
                Name = s.Name,
                Kind = sk,
                HardwareKind = kind,
                Order = order++,
            });
        }
        foreach (var sub in hw.SubHardware) Add(sub, id, ordinals);
    }

    private static HardwareKind Map(HardwareType type) => type switch
    {
        HardwareType.Cpu => HardwareKind.Cpu,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => HardwareKind.Gpu,
        HardwareType.Memory => HardwareKind.Memory,
        HardwareType.Storage => HardwareKind.Storage,
        HardwareType.Network => HardwareKind.Network,
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => HardwareKind.Motherboard,
        HardwareType.Battery => HardwareKind.Battery,
        HardwareType.Psu => HardwareKind.Psu,
        HardwareType.Cooler => HardwareKind.Cooler,
        _ => HardwareKind.Other,
    };

    public SourceInventory Discover() => new(_hardware, _sensors);

    public void Update(ReadOnlySpan<SensorDescriptor> requested, Span<float> values)
    {
        foreach (var d in requested) values[d.Index] = float.NaN;
    }

    public void Dispose() { }
}
