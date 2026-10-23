using BetterVitals.Core.Sensors;

namespace BetterVitals.Sensors.Fake;

/// <summary>Built-in fake PCs for tests, snapshots, perf probes and UI previews.</summary>
public enum FakeProfile
{
    /// <summary>A current high-end desktop: Ryzen 7 9800X3D + Radeon iGPU, RTX 5080, X870E board, 2× DDR5, NVMe + HDD, 2.5GbE, FPS.</summary>
    DesktopAmdNvidia,

    /// <summary>Intel laptop: per-core temps, battery, a hybrid dGPU that sleeps.</summary>
    LaptopIntel,

    /// <summary>"Basic mode" (no admin, no driver): GPU basics, memory, network, one drive.</summary>
    Minimal,
}

/// <summary>
/// Deterministic fake sensors with realistic LibreHardwareMonitor-style names and values (smooth waves + seeded
/// jitter, a function of time only, so tests are reproducible). Allocation-free updates. Test controls: remove /
/// restore hardware, make a device "sleep" (NaN), throw from Update, count reads per sensor.
/// </summary>
public sealed class FakeSensorSource : ISensorSource
{
    private sealed record Wave(float Base, float Amp, float PeriodSec, float Jitter, float Min, float Max);

    private readonly FakeProfile _profile;
    private readonly int _seed;
    private readonly Func<long> _utcNow;
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _asleep = new(StringComparer.Ordinal);
    private int[] _reads = [];

    public FakeSensorSource(FakeProfile profile = FakeProfile.DesktopAmdNvidia, int seed = 1, Func<long>? utcNow = null)
    {
        _profile = profile;
        _seed = seed;
        _utcNow = utcNow ?? (() => DateTime.UtcNow.Ticks);
    }

    public string Name => "fake";

    public SourceStatus Status => new(SourceState.Ok);

    public event Action? InventoryChanged;

    /// <summary>Update throws (to test the engine's resilience).</summary>
    public bool ThrowOnUpdate { get; set; }

    /// <summary>How many times each slot was read (index = SensorDescriptor.Index).</summary>
    public int Reads(int index) => (uint)index < (uint)_reads.Length ? _reads[index] : 0;

    public int TotalReads => _reads.Sum();

    /// <summary>Unplug / re-plug a hardware node (raises InventoryChanged).</summary>
    public void SetPresent(string hardwareId, bool present)
    {
        if (present ? _removed.Remove(hardwareId) : _removed.Add(hardwareId)) InventoryChanged?.Invoke();
    }

    /// <summary>A sleeping device reports NaN (dGPU in D3, HDD in standby).</summary>
    public void SetAsleep(string hardwareId, bool asleep)
    {
        if (asleep) _asleep.Add(hardwareId);
        else _asleep.Remove(hardwareId);
    }

    public SourceInventory Discover()
    {
        var b = new Builder();
        switch (_profile)
        {
            case FakeProfile.DesktopAmdNvidia: BuildDesktop(b); break;
            case FakeProfile.LaptopIntel: BuildLaptop(b); break;
            default: BuildMinimal(b); break;
        }
        var hw = b.Hardware.Where(h => !_removed.Contains(h.Id) && (h.ParentId is null || !_removed.Contains(h.ParentId))).ToList();
        var ids = hw.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
        return new SourceInventory(hw, b.Sensors.Where(s => ids.Contains(s.HardwareId)).ToList());
    }

    public void Update(ReadOnlySpan<SensorDescriptor> requested, Span<float> values)
    {
        if (ThrowOnUpdate) throw new InvalidOperationException("fake failure");
        var t = _utcNow() / (double)TimeSpan.TicksPerSecond;
        var second = (long)t;
        foreach (var d in requested)
        {
            if ((uint)d.Index >= (uint)_reads.Length) Array.Resize(ref _reads, Math.Max(d.Index + 1, _reads.Length * 2 + 16));
            _reads[d.Index]++;
            if (_asleep.Count > 0 && _asleep.Contains(d.HardwareId))
            {
                values[d.Index] = float.NaN;
                continue;
            }
            if (d.SourceTag is not Wave w)
            {
                values[d.Index] = float.NaN;
                continue;
            }
            var phase = (Hash(d.Order, _seed) & 1023) / 1023.0 * Math.PI * 2;
            var v = w.Base + w.Amp * Math.Sin(t * 2 * Math.PI / w.PeriodSec + phase);
            v += w.Jitter * ((Hash(d.Order * 7919 + (int)second, _seed) & 2047) / 1023.5 - 1);
            values[d.Index] = (float)Math.Clamp(v, w.Min, w.Max);
        }
    }

    public void Dispose() { }

    private static int Hash(int x, int seed)
    {
        unchecked
        {
            var h = (uint)(x * 374761393 + seed * 668265263);
            h = (h ^ (h >> 13)) * 1274126177;
            return (int)(h ^ (h >> 16)) & int.MaxValue;
        }
    }

    // ───────────────────────── profiles ─────────────────────────

    private sealed class Builder
    {
        public List<HardwareDescriptor> Hardware { get; } = [];
        public List<SensorDescriptor> Sensors { get; } = [];
        private int _order;
        private string _hw = "";
        private HardwareKind _kind;
        private readonly Dictionary<string, int> _ordinals = new(StringComparer.Ordinal);

        public Builder Hw(HardwareKind kind, string name, string? parentId = null)
        {
            var key = $"{kind}|{name}";
            _ordinals.TryGetValue(key, out var ordinal);
            _ordinals[key] = ordinal + 1;
            _hw = SensorIds.Hardware(kind, name, ordinal, parentId);
            _kind = kind;
            Hardware.Add(new HardwareDescriptor { Id = _hw, Name = name, Kind = kind, ParentId = parentId, Source = "fake" });
            return this;
        }

        public string Current => _hw;

        public Builder S(SensorKind kind, string name, float baseValue, float amp, float periodSec = 30, float jitter = 0,
            float min = float.MinValue, float max = float.MaxValue, TimeSpan minInterval = default, string? noteKey = null)
        {
            Sensors.Add(new SensorDescriptor
            {
                Id = SensorIds.Sensor(_hw, kind, name),
                HardwareId = _hw,
                HardwareKind = _kind,
                Name = name,
                Kind = kind,
                MinInterval = minInterval,
                Order = _order++,
                NoteKey = noteKey,
                SourceTag = new Wave(baseValue, amp, periodSec, jitter, min, max),
            });
            return this;
        }
    }

    private static readonly TimeSpan Slow = TimeSpan.FromSeconds(10);

    private static void BuildDesktop(Builder b)
    {
        b.Hw(HardwareKind.Cpu, "AMD Ryzen 7 9800X3D")
            .S(SensorKind.Temperature, "Core (Tctl/Tdie)", 58, 10, 40, 1.5f, 30, 95)
            .S(SensorKind.Temperature, "CCD1 (Tdie)", 55, 9, 40, 1.5f, 30, 95);
        for (var i = 1; i <= 8; i++) b.S(SensorKind.Clock, $"Core #{i}", 5000, 220, 9 + i, 60, 600, 5250);
        b.S(SensorKind.Clock, "Bus Speed", 100, 0, 60, 0.05f);
        b.S(SensorKind.Load, "CPU Total", 22, 15, 20, 4, 0, 100);
        for (var i = 1; i <= 8; i++) b.S(SensorKind.Load, $"CPU Core #{i}", 25, 20, 7 + i, 6, 0, 100);
        b.S(SensorKind.Power, "Package", 62, 25, 30, 4, 18, 160)
            .S(SensorKind.Power, "Cores", 40, 20, 30, 3, 5, 140)
            .S(SensorKind.Power, "SoC", 18, 1, 60, 0.3f, 10, 30)
            .S(SensorKind.Voltage, "Core (SVI3 TFN)", 1.21f, 0.06f, 15, 0.01f, 0.6f, 1.45f)
            .S(SensorKind.Voltage, "SoC (SVI3 TFN)", 1.05f, 0.002f, 60, 0.001f, 0.9f, 1.3f);
        for (var i = 1; i <= 8; i++) b.S(SensorKind.Voltage, $"Core #{i} VID", 1.22f, 0.07f, 11 + i, 0.01f, 0.6f, 1.45f);

        b.Hw(HardwareKind.Gpu, "NVIDIA GeForce RTX 5080")
            .S(SensorKind.Temperature, "GPU Core", 62, 8, 50, 0.6f, 25, 95)
            .S(SensorKind.Temperature, "GPU Hot Spot", 78, 10, 50, 1, 25, 110, noteKey: "sensor.note.unofficialHotspot")
            .S(SensorKind.Temperature, "GPU Memory Junction", 72, 6, 60, 0.8f, 25, 110)
            .S(SensorKind.Clock, "GPU Core", 2700, 150, 20, 15, 210, 3100)
            .S(SensorKind.Clock, "GPU Memory", 15001, 0, 60)
            .S(SensorKind.Clock, "GPU Video", 2400, 30, 40, 5, 300, 2600)
            .S(SensorKind.Voltage, "GPU Core", 0.97f, 0.05f, 20, 0.005f, 0.7f, 1.1f)
            .S(SensorKind.Load, "GPU Core", 85, 15, 25, 4, 0, 100)
            .S(SensorKind.Load, "GPU Memory Controller", 40, 15, 25, 3, 0, 100)
            .S(SensorKind.Load, "GPU Video Engine", 0, 0, 60, 0, 0, 100)
            .S(SensorKind.Load, "GPU Bus", 6, 3, 20, 1, 0, 100)
            .S(SensorKind.SmallData, "GPU Memory Used", 9400, 600, 90, 20, 300, 16303)
            .S(SensorKind.SmallData, "GPU Memory Free", 6900, 600, 90, 20, 0, 16303)
            .S(SensorKind.SmallData, "GPU Memory Total", 16303, 0, 60)
            .S(SensorKind.Power, "GPU Package", 290, 60, 25, 8, 20, 360)
            .S(SensorKind.Fan, "GPU Fan 1", 1450, 150, 60, 10, 0, 3200)
            .S(SensorKind.Fan, "GPU Fan 2", 1440, 150, 60, 10, 0, 3200)
            .S(SensorKind.Control, "GPU Fan 1", 45, 5, 60, 0.5f, 0, 100)
            .S(SensorKind.Control, "GPU Fan 2", 45, 5, 60, 0.5f, 0, 100);

        b.Hw(HardwareKind.Gpu, "AMD Radeon(TM) Graphics")
            .S(SensorKind.Temperature, "GPU Core", 44, 3, 60, 0.3f, 25, 95)
            .S(SensorKind.Load, "D3D 3D", 2, 2, 20, 1, 0, 100)
            .S(SensorKind.SmallData, "D3D Shared Memory Used", 420, 40, 90, 4, 0, 8192);

        b.Hw(HardwareKind.Motherboard, "Gigabyte X870E AORUS ELITE WIFI7");
        var board = b.Current;
        b.Hw(HardwareKind.Motherboard, "ITE IT8696E", board)
            .S(SensorKind.Temperature, "System #1", 34, 1.5f, 120, 0.2f, 15, 80)
            .S(SensorKind.Temperature, "Chipset", 52, 2, 120, 0.3f, 15, 95)
            .S(SensorKind.Temperature, "CPU", 55, 8, 40, 0.5f, 15, 100)
            .S(SensorKind.Temperature, "PCIe x16", 41, 2, 120, 0.3f, 15, 90)
            .S(SensorKind.Temperature, "VRM MOS", 48, 5, 60, 0.4f, 15, 110)
            .S(SensorKind.Fan, "CPU Fan", 1050, 180, 45, 12, 0, 2400)
            .S(SensorKind.Fan, "System Fan #1", 820, 40, 90, 8, 0, 2000)
            .S(SensorKind.Fan, "System Fan #2", 815, 40, 90, 8, 0, 2000)
            .S(SensorKind.Voltage, "Vcore", 1.22f, 0.06f, 15, 0.01f, 0.6f, 1.45f)
            .S(SensorKind.Voltage, "+3.3V", 3.31f, 0.005f, 120, 0.002f, 3.1f, 3.5f)
            .S(SensorKind.Voltage, "+5V", 5.02f, 0.01f, 120, 0.003f, 4.7f, 5.3f)
            .S(SensorKind.Voltage, "+12V", 12.1f, 0.03f, 120, 0.01f, 11.4f, 12.6f)
            .S(SensorKind.Voltage, "DRAM", 1.35f, 0.002f, 120, 0.001f, 1.1f, 1.5f)
            .S(SensorKind.Voltage, "VBAT", 3.18f, 0.001f, 600, 0, 2.8f, 3.4f);

        b.Hw(HardwareKind.Memory, "Generic Memory")
            .S(SensorKind.Data, "Memory Used", 14.2f, 0.8f, 120, 0.05f, 2, 31.1f)
            .S(SensorKind.Data, "Memory Available", 16.9f, 0.8f, 120, 0.05f, 0, 31.1f)
            .S(SensorKind.Load, "Memory", 46, 3, 120, 0.2f, 0, 100)
            .S(SensorKind.Temperature, "DIMM #1", 41, 2, 90, 0.2f, 20, 85)
            .S(SensorKind.Temperature, "DIMM #2", 42, 2, 90, 0.2f, 20, 85);

        b.Hw(HardwareKind.Storage, "KINGSTON SKC3000D2048G")
            .S(SensorKind.Temperature, "Composite Temperature", 47, 4, 120, 0.3f, 15, 90, Slow)
            .S(SensorKind.Load, "Total Activity", 4, 4, 15, 2, 0, 100)
            .S(SensorKind.Throughput, "Read Rate", 4e7f, 4e7f, 15, 5e6f, 0, 7e9f)
            .S(SensorKind.Throughput, "Write Rate", 6e6f, 6e6f, 15, 1e6f, 0, 7e9f)
            .S(SensorKind.Level, "Available Spare", 100, 0, 600, 0, 0, 100, TimeSpan.FromSeconds(60))
            .S(SensorKind.Level, "Percentage Used", 3, 0, 600, 0, 0, 100, TimeSpan.FromSeconds(60));

        b.Hw(HardwareKind.Storage, "ST4000DM004-2CV104")
            .S(SensorKind.Temperature, "Temperature", 36, 1, 300, 0.2f, 15, 70, Slow)
            .S(SensorKind.Load, "Total Activity", 1, 1, 20, 0.5f, 0, 100);

        b.Hw(HardwareKind.Network, "Ethernet")
            .S(SensorKind.Throughput, "Download Speed", 3e6f, 3e6f, 20, 4e5f, 0, 3e8f)
            .S(SensorKind.Throughput, "Upload Speed", 2e5f, 2e5f, 20, 3e4f, 0, 3e8f)
            .S(SensorKind.Load, "Network Utilization", 2, 2, 20, 0.5f, 0, 100);

        BuildGame(b);
    }

    private static void BuildLaptop(Builder b)
    {
        b.Hw(HardwareKind.Cpu, "Intel Core Ultra 7 155H");
        for (var i = 1; i <= 6; i++) b.S(SensorKind.Temperature, $"Core #{i}", 62, 12, 25 + i, 2, 30, 105);
        b.S(SensorKind.Temperature, "Core Max", 70, 12, 25, 2, 30, 105)
            .S(SensorKind.Temperature, "CPU Package", 71, 12, 25, 2, 30, 105);
        for (var i = 1; i <= 6; i++) b.S(SensorKind.Clock, $"Core #{i}", 3800, 700, 8 + i, 80, 400, 4800);
        b.S(SensorKind.Load, "CPU Total", 18, 12, 20, 4, 0, 100)
            .S(SensorKind.Power, "CPU Package", 22, 10, 30, 2, 3, 65);

        b.Hw(HardwareKind.Gpu, "NVIDIA GeForce RTX 4060 Laptop GPU")
            .S(SensorKind.Temperature, "GPU Core", 55, 6, 50, 0.5f, 25, 95)
            .S(SensorKind.Load, "GPU Core", 30, 25, 25, 4, 0, 100);

        b.Hw(HardwareKind.Battery, "Primary Battery")
            .S(SensorKind.Level, "Charge Level", 72, 2, 600, 0, 0, 100)
            .S(SensorKind.Power, "Discharge Rate", 14, 4, 60, 0.5f, 0, 90)
            .S(SensorKind.Voltage, "Voltage", 16.2f, 0.2f, 600, 0.01f, 12, 17.6f);

        b.Hw(HardwareKind.Memory, "Generic Memory")
            .S(SensorKind.Data, "Memory Used", 9.8f, 0.6f, 120, 0.05f, 2, 15.6f)
            .S(SensorKind.Load, "Memory", 63, 3, 120, 0.2f, 0, 100);

        b.Hw(HardwareKind.Storage, "Samsung SSD 990 PRO 1TB")
            .S(SensorKind.Temperature, "Composite Temperature", 44, 3, 120, 0.3f, 15, 90, Slow);

        b.Hw(HardwareKind.Network, "Wi-Fi")
            .S(SensorKind.Throughput, "Download Speed", 1e6f, 1e6f, 20, 1e5f, 0, 2e8f)
            .S(SensorKind.Throughput, "Upload Speed", 1e5f, 1e5f, 20, 1e4f, 0, 2e8f);

        BuildGame(b);
    }

    private static void BuildMinimal(Builder b)
    {
        b.Hw(HardwareKind.Gpu, "NVIDIA GeForce RTX 5080")
            .S(SensorKind.Temperature, "GPU Core", 60, 6, 50, 0.5f, 25, 95)
            .S(SensorKind.Temperature, "GPU Memory Junction", 70, 5, 60, 0.5f, 25, 110)
            .S(SensorKind.Load, "GPU Core", 50, 30, 25, 4, 0, 100);
        b.Hw(HardwareKind.Memory, "Generic Memory")
            .S(SensorKind.Data, "Memory Used", 12.0f, 0.6f, 120, 0.05f, 2, 31.1f)
            .S(SensorKind.Load, "Memory", 40, 3, 120, 0.2f, 0, 100);
        b.Hw(HardwareKind.Network, "Ethernet")
            .S(SensorKind.Throughput, "Download Speed", 2e6f, 2e6f, 20, 3e5f, 0, 3e8f)
            .S(SensorKind.Throughput, "Upload Speed", 1e5f, 1e5f, 20, 2e4f, 0, 3e8f);
        b.Hw(HardwareKind.Storage, "KINGSTON SKC3000D2048G")
            .S(SensorKind.Temperature, "Composite Temperature", 46, 3, 120, 0.3f, 15, 90, Slow);
    }

    private static void BuildGame(Builder b)
    {
        b.Hw(HardwareKind.Game, "Foreground app")
            .S(SensorKind.Fps, "Framerate", 142, 18, 12, 4, 0, 1000)
            .S(SensorKind.FrameTime, "Frametime", 7.0f, 0.9f, 12, 0.2f, 0.5f, 200)
            .S(SensorKind.Fps, "1% Low", 112, 10, 30, 2, 0, 1000)
            .S(SensorKind.Fps, "0.1% Low", 94, 10, 30, 2, 0, 1000)
            .S(SensorKind.FrameTime, "Frametime Max", 11.5f, 2, 20, 0.5f, 0.5f, 500);
    }
}
