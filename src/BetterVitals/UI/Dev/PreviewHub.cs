using BetterVitals.Core.Sensors;

namespace BetterVitals.UI.Dev;

/// <summary>
/// A fake <see cref="ISensorHub"/> for UI development until the engine is integrated: ~20 sensors of a typical gaming PC
/// (CPU / GPU / RAM / drive / network / FPS) following smooth, deterministic random walks, one tick per second on a pool
/// timer — the way the engine raises <see cref="FrameReady"/> off the UI thread. Test and snapshot mode only.
/// </summary>
internal sealed class PreviewHub : ISensorHub, IDisposable
{
    private sealed class Walk(float value, float min, float max, float speed, float noise)
    {
        public float Value = value;
        public float Target = value;
        public readonly float Min = min, Max = max, Speed = speed, Noise = noise;
    }

    private const int Keep = 120;

    private readonly Lock _gate = new();
    private readonly Random _rng;
    private readonly List<HardwareDescriptor> _hardware = [];
    private readonly List<SensorDescriptor> _sensors = [];
    private readonly List<Walk> _walks = [];
    private readonly List<float[]> _recent = [];
    private readonly SensorFrame[] _ring = new SensorFrame[3];
    private readonly Timer _timer;
    private int _sequence;
    private int _recentCount;
    private SensorFrame _latest;
    private bool _disposed;

    public PreviewHub(int seed = 42, bool start = true)
    {
        _rng = new Random(seed);
        var cpu = Hw(HardwareKind.Cpu, "AMD Ryzen 7 9800X3D");
        var gpu = Hw(HardwareKind.Gpu, "NVIDIA GeForce RTX 5080");
        var ram = Hw(HardwareKind.Memory, "Generic Memory");
        var board = Hw(HardwareKind.Motherboard, "Gigabyte X870E AORUS ELITE WIFI7");
        var disk = Hw(HardwareKind.Storage, "KINGSTON SKC3000D2048G");
        var net = Hw(HardwareKind.Network, "Realtek Gaming 2.5GbE Family Controller");
        var game = Hw(HardwareKind.Game, "Foreground game");

        S(cpu, SensorKind.Temperature, "Core (Tctl/Tdie)", 54, 38, 88, 0.10f, 0.6f);
        S(cpu, SensorKind.Load, "CPU Total", 23, 2, 96, 0.25f, 3f);
        S(cpu, SensorKind.Clock, "Core #1", 5150, 3600, 5250, 0.30f, 40f);
        S(cpu, SensorKind.Clock, "Core #2", 5080, 3600, 5250, 0.30f, 40f);
        S(cpu, SensorKind.Power, "Package", 68, 25, 140, 0.20f, 2.5f);
        S(cpu, SensorKind.Voltage, "Core #1 VID", 1.21f, 0.95f, 1.35f, 0.20f, 0.01f);
        S(gpu, SensorKind.Temperature, "GPU Core", 61, 34, 84, 0.08f, 0.5f);
        S(gpu, SensorKind.Temperature, "GPU Hot Spot", 72, 40, 98, 0.08f, 0.6f, "sensor.note.unofficialHotSpot");
        S(gpu, SensorKind.Temperature, "GPU Memory Junction", 66, 40, 96, 0.06f, 0.4f);
        S(gpu, SensorKind.Load, "GPU Core", 78, 0, 100, 0.25f, 4f);
        S(gpu, SensorKind.Clock, "GPU Core", 2640, 210, 2860, 0.25f, 25f);
        S(gpu, SensorKind.Clock, "GPU Memory", 15000, 405, 15000, 0.05f, 0);
        S(gpu, SensorKind.Power, "GPU Package", 245, 18, 360, 0.20f, 8f);
        S(gpu, SensorKind.SmallData, "GPU Memory Used", 9420, 900, 16300, 0.10f, 60f);
        S(gpu, SensorKind.Fan, "GPU Fan 1", 1480, 0, 3000, 0.10f, 12f);
        S(ram, SensorKind.Load, "Memory", 47, 20, 92, 0.05f, 0.4f);
        S(ram, SensorKind.Data, "Memory Used", 15.1f, 6, 29, 0.05f, 0.08f);
        S(board, SensorKind.Temperature, "System", 36, 28, 50, 0.04f, 0.2f);
        S(disk, SensorKind.Temperature, "Temperature", 44, 30, 70, 0.03f, 0.2f);
        S(net, SensorKind.Throughput, "Download Speed", 2.4e6f, 0, 2.8e8f, 0.35f, 4e5f);
        S(game, SensorKind.Fps, "FPS", 141, 40, 240, 0.30f, 4f);
        S(game, SensorKind.FrameTime, "Frame Time", 7.1f, 4, 25, 0.30f, 0.3f);

        for (var i = 0; i < _ring.Length; i++) _ring[i] = new SensorFrame(_sensors.Count) { Count = _sensors.Count };
        _latest = _ring[0];
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        if (start) _timer.Change(1000, 1000);
    }

    private HardwareDescriptor Hw(HardwareKind kind, string name)
    {
        var d = new HardwareDescriptor { Id = SensorIds.Hardware(kind, name, 0), Name = name, Kind = kind, Source = "fake" };
        _hardware.Add(d);
        return d;
    }

    private void S(HardwareDescriptor hw, SensorKind kind, string name, float value, float min, float max, float speed, float noise, string? note = null)
    {
        var d = new SensorDescriptor
        {
            Id = SensorIds.Sensor(hw.Id, kind, name),
            HardwareId = hw.Id,
            Name = name,
            Kind = kind,
            HardwareKind = hw.Kind,
            NoteKey = note,
            Order = _sensors.Count,
            Index = _sensors.Count,
        };
        _sensors.Add(d);
        _walks.Add(new Walk(value, min, max, speed, noise));
        _recent.Add(new float[Keep]);
    }

    public IReadOnlyList<HardwareDescriptor> Hardware => _hardware;

    public IReadOnlyList<SensorDescriptor> Sensors => _sensors;

    public bool IsPresent(int index) => (uint)index < (uint)_sensors.Count;

    public SensorDescriptor? Find(string sensorId) => _sensors.FirstOrDefault(s => s.Id == sensorId);

    public HardwareDescriptor? FindHardware(string hardwareId) => _hardware.FirstOrDefault(h => h.Id == hardwareId);

    public SensorFrame Latest => _latest;

    public SessionStats Stats { get; } = new();

    public IDemand Demand(string consumer, IEnumerable<string> sensorIds, TimeSpan interval) => new NoDemand();

    public event Action<SensorFrame>? FrameReady;

    public event Action? InventoryChanged
    {
        add { }
        remove { }
    }

    public IReadOnlyList<(string Source, SourceStatus Status)> SourceStatuses { get; } = [("fake", new SourceStatus(SourceState.Ok))];

    /// <summary>Runs <paramref name="ticks"/> ticks at once, without events (snapshots start with filled sparklines).</summary>
    public void Warm(int ticks)
    {
        for (var i = 0; i < ticks; i++) Step(raise: false);
    }

    /// <summary>The last values of a sensor, oldest first (up to 120).</summary>
    public float[] Recent(int index, int count)
    {
        lock (_gate)
        {
            var n = Math.Min(count, _recentCount);
            var result = new float[n];
            var ring = _recent[index];
            for (var i = 0; i < n; i++) result[i] = ring[(_recentCount - n + i) % Keep];
            return result;
        }
    }

    private void Tick() => Step(raise: true);

    private void Step(bool raise)
    {
        SensorFrame frame;
        lock (_gate)
        {
            if (_disposed) return;
            _sequence++;
            frame = _ring[_sequence % _ring.Length];
            for (var i = 0; i < _walks.Count; i++)
            {
                var w = _walks[i];
                if (_rng.NextDouble() < 0.08) w.Target = (float)(w.Min + _rng.NextDouble() * (w.Max - w.Min) * 0.8 + (w.Max - w.Min) * 0.1);
                w.Value += (w.Target - w.Value) * w.Speed * 0.35f + (float)((_rng.NextDouble() * 2 - 1) * w.Noise);
                w.Value = Math.Clamp(w.Value, w.Min, w.Max);
                frame.Values[i] = w.Value;
                frame.UpdatedAt[i] = _sequence;
                _recent[i][_recentCount % Keep] = w.Value;
            }
            _recentCount++;
            frame.Count = _sensors.Count;
            frame.Sequence = _sequence;
            frame.TimestampUtcTicks = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond;
            Stats.Accumulate(frame);
            _latest = frame;
        }
        if (raise) FrameReady?.Invoke(frame);
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _timer.Dispose();
    }

    private sealed class NoDemand : IDemand
    {
        public void Update(IEnumerable<string> sensorIds, TimeSpan interval)
        {
        }

        public void Dispose()
        {
        }
    }
}
