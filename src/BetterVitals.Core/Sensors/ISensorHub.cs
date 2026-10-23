namespace BetterVitals.Core.Sensors;

/// <summary>
/// The app's view of all sensors: inventory, latest values, session statistics and the demand registry that decides
/// what gets read and how often. Implemented by the sampling engine (BetterVitals.Sensors); fakes exist for tests.
/// Thread-safe unless noted.
/// </summary>
public interface ISensorHub
{
    /// <summary>All hardware nodes known so far (snapshot list, replaced on re-discovery).</summary>
    IReadOnlyList<HardwareDescriptor> Hardware { get; }

    /// <summary>All sensors by <see cref="SensorDescriptor.Index"/> (snapshot list, only grows).</summary>
    IReadOnlyList<SensorDescriptor> Sensors { get; }

    /// <summary>Is the sensor present right now (false after its device vanished)?</summary>
    bool IsPresent(int index);

    SensorDescriptor? Find(string sensorId);

    HardwareDescriptor? FindHardware(string hardwareId);

    /// <summary>The most recent frame (see <see cref="SensorFrame"/> for how long it stays valid).</summary>
    SensorFrame Latest { get; }

    /// <summary>Min / max / average of every sensor since the app started (only ticks where it was read).</summary>
    SessionStats Stats { get; }

    /// <summary>
    /// Declares that <paramref name="consumer"/> needs these sensors at least every <paramref name="interval"/>.
    /// The engine reads each sensor at the shortest interval any live demand asks for (but never faster than its
    /// <see cref="SensorDescriptor.MinInterval"/>) and does not read sensors nobody needs. Dispose to release.
    /// Unknown ids are kept and start working when the sensor appears (e.g. FPS when a game starts).
    /// </summary>
    IDemand Demand(string consumer, IEnumerable<string> sensorIds, TimeSpan interval);

    /// <summary>After each tick, on the engine thread. Handlers must be quick (copy, signal, return) and never block.</summary>
    event Action<SensorFrame>? FrameReady;

    /// <summary>After a re-discovery changed hardware / sensors, on the engine thread (UI marshals to its own thread).</summary>
    event Action? InventoryChanged;

    /// <summary>Status of every source (name + state) for the "Permissions and driver" page.</summary>
    IReadOnlyList<(string Source, SourceStatus Status)> SourceStatuses { get; }
}

/// <summary>A live demand; change it in place (no churn) or dispose it.</summary>
public interface IDemand : IDisposable
{
    void Update(IEnumerable<string> sensorIds, TimeSpan interval);
}

/// <summary>
/// Running statistics since start, indexed like <see cref="SensorFrame.Values"/>. Written by the engine thread,
/// read anywhere (single floats / doubles are read atomically on x64; a reader may see a value one tick old).
/// </summary>
public sealed class SessionStats
{
    private float[] _min = [];
    private float[] _max = [];
    private double[] _sum = [];
    private int[] _count = [];

    public float Min(int i) => (uint)i < (uint)_count.Length && _count[i] > 0 ? _min[i] : float.NaN;

    public float Max(int i) => (uint)i < (uint)_count.Length && _count[i] > 0 ? _max[i] : float.NaN;

    public float Average(int i) => (uint)i < (uint)_count.Length && _count[i] > 0 ? (float)(_sum[i] / _count[i]) : float.NaN;

    public int Count(int i) => (uint)i < (uint)_count.Length ? _count[i] : 0;

    /// <summary>Engine only: account the fresh values of a frame.</summary>
    internal void Accumulate(SensorFrame frame)
    {
        if (_count.Length < frame.Count) Grow(frame.Values.Length);
        var values = frame.Values;
        var stamps = frame.UpdatedAt;
        var seq = frame.Sequence;
        for (var i = 0; i < frame.Count; i++)
        {
            if (stamps[i] != seq) continue;
            var v = values[i];
            if (!float.IsFinite(v)) continue; // NaN and infinity would poison the sum for the rest of the session
            if (_count[i] == 0)
            {
                _min[i] = v;
                _max[i] = v;
            }
            else
            {
                if (v < _min[i]) _min[i] = v;
                if (v > _max[i]) _max[i] = v;
            }
            _sum[i] += v;
            _count[i]++;
        }
    }

    /// <summary>Engine only: forget everything (e.g. "reset min / max" in the UI).</summary>
    internal void Reset()
    {
        Array.Clear(_count);
        Array.Clear(_sum);
    }

    private void Grow(int size)
    {
        Array.Resize(ref _min, size);
        Array.Resize(ref _max, size);
        Array.Resize(ref _sum, size);
        Array.Resize(ref _count, size);
    }
}
