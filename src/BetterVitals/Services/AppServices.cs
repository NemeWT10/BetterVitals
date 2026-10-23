using BetterVitals.Core.History;
using BetterVitals.Core.Sensors;

namespace BetterVitals.Services;

/// <summary>
/// The app's long-lived services, set once at start-up (before any window opens) and read by view models and pages.
/// Integration point for the engine: assign <see cref="Hub"/> (and <see cref="History"/>) before <c>App.Run</c>.
/// </summary>
public static class AppServices
{
    /// <summary>Live sensors. Never null: <see cref="NullSensorHub"/> until the engine is attached.</summary>
    public static ISensorHub Hub { get; set; } = NullSensorHub.Instance;

    /// <summary>History queries (charts, stats, export); null until the history store is attached.</summary>
    public static IHistoryQuery? History { get; set; }

    /// <summary>Keeps sensors in the 15-minute live buffer while a page shows them; null until history is attached.</summary>
    public static WatchSensors? WatchHistory { get; set; }

    /// <summary>The live overlay (editor: monitors, on-screen positioning). Never null.</summary>
    public static IOverlayService Overlay { get; set; } = NullOverlayService.Instance;

    /// <summary>Install / driver / autostart / updates. Never null.</summary>
    public static IPlatformService Platform { get; set; } = NullPlatformService.Instance;
}

/// <summary>A hub with no sensors (the shell runs without the engine; every value shows "—").</summary>
public sealed class NullSensorHub : ISensorHub
{
    public static NullSensorHub Instance { get; } = new();

    private readonly SensorFrame _frame = new(0);

    public IReadOnlyList<HardwareDescriptor> Hardware => [];

    public IReadOnlyList<SensorDescriptor> Sensors => [];

    public bool IsPresent(int index) => false;

    public SensorDescriptor? Find(string sensorId) => null;

    public HardwareDescriptor? FindHardware(string hardwareId) => null;

    public SensorFrame Latest => _frame;

    public SessionStats Stats { get; } = new();

    public IDemand Demand(string consumer, IEnumerable<string> sensorIds, TimeSpan interval) => NoDemand.Instance;

    public event Action<SensorFrame>? FrameReady
    {
        add { }
        remove { }
    }

    public event Action? InventoryChanged
    {
        add { }
        remove { }
    }

    public IReadOnlyList<(string Source, SourceStatus Status)> SourceStatuses => [];

    private sealed class NoDemand : IDemand
    {
        public static NoDemand Instance { get; } = new();

        public void Update(IEnumerable<string> sensorIds, TimeSpan interval)
        {
        }

        public void Dispose()
        {
        }
    }
}
