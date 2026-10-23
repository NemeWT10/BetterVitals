namespace BetterVitals.Core.Sensors;

/// <summary>
/// A provider of sensors (LibreHardwareMonitor, native Windows APIs, FPS from ETW, derived values, fakes for tests).
/// All methods except <see cref="InventoryChanged"/> are called on the engine thread only, one at a time.
/// A source must never throw out of <see cref="Update"/> (catch, log once, write NaN) and must never take the app down.
/// </summary>
public interface ISensorSource : IDisposable
{
    /// <summary>"lhm", "native", "fps", "derived", "fake".</summary>
    string Name { get; }

    SourceStatus Status { get; }

    /// <summary>
    /// Finds hardware and sensors. Called at start, after <see cref="InventoryChanged"/> (debounced) and after resume
    /// from sleep. May be slow (hundreds of ms); never called on the UI thread. Return descriptors with stable ids;
    /// returning a sensor with an id seen before keeps its slot.
    /// </summary>
    SourceInventory Discover();

    /// <summary>
    /// Reads the requested sensors and writes each value into <c>values[d.Index]</c> (float.NaN = no value right now,
    /// e.g. a sleeping dGPU). <paramref name="requested"/> holds only this source's sensors that are due this tick; the
    /// source decides which hardware nodes to refresh from it (update only what is asked for).
    /// Hot path: must not allocate, block on I/O for long, or wake sleeping devices (HDD standby, dGPU in D3).
    /// </summary>
    void Update(ReadOnlySpan<SensorDescriptor> requested, Span<float> values);

    /// <summary>Hardware appeared / vanished (USB, hot-plug, driver installed). Any thread. The engine re-discovers.</summary>
    event Action? InventoryChanged;

    /// <summary>Before sleep / hibernate: release what does not survive it.</summary>
    void Suspend() { }

    /// <summary>After resume: the engine calls <see cref="Discover"/> right after.</summary>
    void Resume() { }
}
