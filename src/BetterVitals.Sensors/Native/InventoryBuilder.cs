using BetterVitals.Core.Sensors;

namespace BetterVitals.Sensors.Native;

/// <summary>
/// Collects what the native source adds in one discovery: its own hardware nodes and its sensors. Guarantees no sensor
/// id repeats (inside the source, or against an id an upstream source already registered).
/// </summary>
internal sealed class InventoryBuilder(UpstreamIndex upstream)
{
    private readonly HashSet<string> _hardwareIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sensorIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _order = new(StringComparer.Ordinal);

    public UpstreamIndex Upstream { get; } = upstream;

    public List<HardwareDescriptor> Hardware { get; } = [];

    public List<SensorDescriptor> Sensors { get; } = [];

    /// <summary>State to return to with <see cref="Rollback"/> when a module fails half way.</summary>
    public (int Hardware, int Sensors) Mark() => (Hardware.Count, Sensors.Count);

    /// <summary>Drops everything added after <paramref name="mark"/>.</summary>
    public void Rollback((int Hardware, int Sensors) mark)
    {
        for (var i = Hardware.Count - 1; i >= mark.Hardware; i--)
        {
            _hardwareIds.Remove(Hardware[i].Id);
            Hardware.RemoveAt(i);
        }
        for (var i = Sensors.Count - 1; i >= mark.Sensors; i--)
        {
            _sensorIds.Remove(Sensors[i].Id);
            Sensors.RemoveAt(i);
        }
    }

    /// <summary>Registers an own hardware node (once per id) and returns its id.</summary>
    public string AddHardware(HardwareKind kind, string name, int ordinal)
    {
        var id = SensorIds.Hardware(kind, name, ordinal);
        if (_hardwareIds.Add(id))
            Hardware.Add(new HardwareDescriptor { Id = id, Name = name, Kind = kind, Source = "native" });
        return id;
    }

    /// <summary>
    /// Adds a sensor to <paramref name="hardwareId"/> (an own node or an upstream one). Returns null when a sensor with the
    /// same id already exists, so a gap that turned out to be filled never produces a duplicate.
    /// </summary>
    public SensorDescriptor? AddSensor(string hardwareId, HardwareKind hardwareKind, SensorKind kind, string name,
        NativeNode node, int slot, TimeSpan minInterval = default, string unit = "", bool reusedHardware = false)
    {
        var id = SensorIds.Sensor(hardwareId, kind, name);
        if (Upstream.ContainsSensorId(id) || !_sensorIds.Add(id)) return null;
        _order.TryGetValue(hardwareId, out var order);
        _order[hardwareId] = order + 1;
        var d = new SensorDescriptor
        {
            Id = id,
            HardwareId = hardwareId,
            Name = name,
            Kind = kind,
            HardwareKind = hardwareKind,
            Unit = unit,
            MinInterval = minInterval,
            Order = (reusedHardware ? 1000 : 0) + order,
            SourceTag = new NativeTag(node, slot),
        };
        Sensors.Add(d);
        return d;
    }
}
