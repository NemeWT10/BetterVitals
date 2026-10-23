using BetterVitals.Core.Sensors;

namespace BetterVitals.Tests.Overlay;

/// <summary>Minimal in-memory <see cref="ISensorHub"/> for overlay tests (values set directly).</summary>
internal sealed class FakeHub : ISensorHub
{
    private readonly List<SensorDescriptor> _sensors = [];
    private readonly SensorFrame _frame = new(64);

    public FakeHub(params SensorDescriptor[] sensors)
    {
        foreach (var s in sensors) Add(s);
    }

    public int DemandCalls { get; private set; }

    public int ActiveDemands { get; private set; }

    public IReadOnlyList<string> LastDemandIds { get; private set; } = [];

    public void Add(SensorDescriptor s)
    {
        s.Index = _sensors.Count;
        _sensors.Add(s);
        _frame.EnsureCapacity(_sensors.Count);
        _frame.Count = _sensors.Count;
    }

    public void Set(SensorDescriptor s, float value)
    {
        _frame.Values[s.Index] = value;
        _frame.Sequence++;
        _frame.UpdatedAt[s.Index] = _frame.Sequence;
    }

    public void RaiseInventoryChanged() => InventoryChanged?.Invoke();

    public IReadOnlyList<HardwareDescriptor> Hardware => [];

    public IReadOnlyList<SensorDescriptor> Sensors => _sensors;

    public bool IsPresent(int index) => index >= 0 && index < _sensors.Count;

    public SensorDescriptor? Find(string sensorId) => _sensors.FirstOrDefault(s => s.Id == sensorId);

    public HardwareDescriptor? FindHardware(string hardwareId) => null;

    public SensorFrame Latest => _frame;

    public SessionStats Stats { get; } = new();

    public IDemand Demand(string consumer, IEnumerable<string> sensorIds, TimeSpan interval)
    {
        DemandCalls++;
        ActiveDemands++;
        LastDemandIds = sensorIds.ToArray();
        return new FakeDemand(this);
    }

    public event Action<SensorFrame>? FrameReady
    {
        add { }
        remove { }
    }

    public event Action? InventoryChanged;

    public IReadOnlyList<(string Source, SourceStatus Status)> SourceStatuses => [];

    private sealed class FakeDemand(FakeHub hub) : IDemand
    {
        private bool _disposed;

        public void Update(IEnumerable<string> sensorIds, TimeSpan interval) => hub.LastDemandIds = sensorIds.ToArray();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            hub.ActiveDemands--;
        }
    }
}
