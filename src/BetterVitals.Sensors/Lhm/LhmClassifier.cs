using BetterVitals.Core.Sensors;
using LibreHardwareMonitor.Hardware;

namespace BetterVitals.Sensors.Lhm;

/// <summary>Per-sensor traits decided at discovery (drive the hot path without string work).</summary>
[Flags]
internal enum LhmSensorFlags : byte
{
    None = 0,
    /// <summary>"GPU Hot Spot*", "GPU Memory #k", "GPU Memory Junction": 0 / 255 / NaN mean "no value".</summary>
    HotSpotLike = 1,
    /// <summary>Read through PawnIO (RTX 50 hot spot channels, GDDR7 chips): NaN while the driver is paused.</summary>
    DriverBacked = 2,
    /// <summary>Storage sensor whose value comes from SMART / NVMe health (needs a SMART refresh).</summary>
    NeedsSmart = 4,
    /// <summary>NVIDIA "GPU PCIe Rx / Tx" (NVML throughput: ~32 ms blocking per counter).</summary>
    NeedsPcie = 8,
    /// <summary>GPU "D3D …" sensors (D3DKMT statistics query).</summary>
    NeedsD3d = 16,

    /// <summary>Everything a node update can be asked for (discovery warm-up).</summary>
    AllNeeds = NeedsSmart | NeedsPcie | NeedsD3d,
}

/// <summary>Pure mapping and classification rules for LibreHardwareMonitor objects (no hardware access).</summary>
internal static class LhmClassifier
{
    /// <summary>LHM prefix of the RTX 50 series (RTX PRO Blackwell cards deliberately do not match).</summary>
    public const string Rtx50Prefix = "NVIDIA GeForce RTX 50";

    public const string HotSpotNoteKey = "sensor.note.unofficialHotspot";

    /// <summary>Bytes → GB (GiB) — LHM 0.9.7 reports <see cref="SensorType.Data"/> in bytes, BetterVitals in GB.</summary>
    public const float BytesToGb = 1f / (1024f * 1024f * 1024f);

    public static readonly TimeSpan SlowStorageInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan StorageInterval = TimeSpan.FromSeconds(10);
    /// <summary>PawnIO MMIO hot spot / memory chip reads: not more often than once a second (PROJEKT §5).</summary>
    public static readonly TimeSpan HotSpotInterval = TimeSpan.FromSeconds(1);

    private static readonly SensorKind?[] KindBySensorType = BuildKindMap();

    /// <summary>Storage sensors LHM computes from Windows counters / partition data — no SMART read needed.</summary>
    private static readonly HashSet<string> NonSmartStorageSensors = new(StringComparer.Ordinal)
    {
        "Used Space", "Free Space", "Total Space",
        "Read Activity", "Write Activity", "Total Activity",
        "Read Rate", "Write Rate",
    };

    public static HardwareKind MapHardware(HardwareType type) => type switch
    {
        HardwareType.Cpu => HardwareKind.Cpu,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => HardwareKind.Gpu,
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => HardwareKind.Motherboard,
        HardwareType.Memory => HardwareKind.Memory,
        HardwareType.Storage => HardwareKind.Storage,
        HardwareType.Network => HardwareKind.Network,
        HardwareType.Battery => HardwareKind.Battery,
        HardwareType.Psu => HardwareKind.Psu,
        HardwareType.Cooler => HardwareKind.Cooler,
        HardwareType.PowerMonitor => HardwareKind.Controller,
        _ => HardwareKind.Other,
    };

    /// <summary>LHM <see cref="SensorType"/> → <see cref="SensorKind"/> by name; null for a type BetterVitals does not know.</summary>
    public static SensorKind? MapSensor(SensorType type)
        => (uint)type < (uint)KindBySensorType.Length ? KindBySensorType[(int)type] : null;

    /// <summary>Unit conversion from LHM to BetterVitals base units.</summary>
    public static float Scale(SensorType type) => type == SensorType.Data ? BytesToGb : 1f;

    public static bool IsRtx50(string hardwareName) => hardwareName.StartsWith(Rtx50Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>"GPU Hot Spot" (the maximum).</summary>
    public static bool IsHotSpotMax(string name) => name == "GPU Hot Spot";

    /// <summary>"GPU Hot Spot #1…#6" (PawnIO MMIO channels).</summary>
    public static bool IsHotSpotChannel(string name) => IsNumbered(name, "GPU Hot Spot #");

    /// <summary>"GPU Memory #1…#48" (one per GDDR7 chip, PawnIO).</summary>
    public static bool IsMemoryChip(string name) => IsNumbered(name, "GPU Memory #");

    public static LhmSensorFlags Classify(HardwareType hardwareType, string hardwareName, SensorType sensorType, string sensorName)
    {
        var flags = LhmSensorFlags.None;
        var isGpu = hardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;
        if (isGpu && sensorType == SensorType.Temperature)
        {
            var channel = IsHotSpotChannel(sensorName);
            var chip = IsMemoryChip(sensorName);
            var max = IsHotSpotMax(sensorName);
            if (channel || chip || max || sensorName == "GPU Memory Junction") flags |= LhmSensorFlags.HotSpotLike;
            if (hardwareType == HardwareType.GpuNvidia && (channel || chip || (max && IsRtx50(hardwareName))))
                flags |= LhmSensorFlags.DriverBacked;
        }
        if (hardwareType == HardwareType.Storage && !NonSmartStorageSensors.Contains(sensorName))
            flags |= LhmSensorFlags.NeedsSmart;
        if (hardwareType == HardwareType.GpuNvidia && sensorType == SensorType.Throughput && sensorName.StartsWith("GPU PCIe ", StringComparison.Ordinal))
            flags |= LhmSensorFlags.NeedsPcie;
        if (isGpu && sensorName.StartsWith("D3D ", StringComparison.Ordinal))
            flags |= LhmSensorFlags.NeedsD3d;
        return flags;
    }

    /// <summary>
    /// SMART / NVMe health and "Data Read / Written"-style counters: 60 s; other storage sensors (temperatures, activity,
    /// space): 10 s; PawnIO hot spot / memory chips: 1 s; everything else: no limit.
    /// </summary>
    public static TimeSpan MinInterval(HardwareType hardwareType, SensorType sensorType, LhmSensorFlags flags)
    {
        if (hardwareType == HardwareType.Storage)
            return (flags & LhmSensorFlags.NeedsSmart) != 0 && sensorType != SensorType.Temperature ? SlowStorageInterval : StorageInterval;
        if ((flags & LhmSensorFlags.DriverBacked) != 0) return HotSpotInterval;
        return TimeSpan.Zero;
    }

    /// <summary>"sensor.note.unofficialHotspot" on the RTX 50 hot spot (maximum and channels).</summary>
    public static string? NoteKey(HardwareType hardwareType, string hardwareName, SensorType sensorType, string sensorName)
        => hardwareType == HardwareType.GpuNvidia && sensorType == SensorType.Temperature && IsRtx50(hardwareName)
           && (IsHotSpotMax(sensorName) || IsHotSpotChannel(sensorName))
            ? HotSpotNoteKey
            : null;

    /// <summary>
    /// Hardware whose every sensor needs PawnIO and that is released (paused) for anti-cheat games: CPU, mainboard
    /// (SuperIO, EC), DIMMs (SPD over SMBus) and Intel integrated GPUs (power via MSR).
    /// </summary>
    public static bool IsDriverBackedHardware(HardwareType type, string identifier) => type switch
    {
        HardwareType.Cpu or HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => true,
        HardwareType.Memory => identifier.StartsWith("/memory/dimm", StringComparison.OrdinalIgnoreCase),
        HardwareType.GpuIntel => identifier.StartsWith("/gpu-intel-integrated", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>
    /// NDIS filter module instances Windows lists as interfaces ("Ethernet 2-QoS Packet Scheduler-0000",
    /// "…-WFP Native MAC Layer LightWeight Filter-0000"): duplicates of the real adapter, not hardware.
    /// </summary>
    public static bool IsNetworkFilterPseudoAdapter(string name)
    {
        if (name.Length < 6 || name[^5] != '-') return false;
        for (var i = name.Length - 4; i < name.Length; i++)
            if (!char.IsAsciiDigit(name[i])) return false;
        return name.Contains("Filter", StringComparison.OrdinalIgnoreCase) || name.Contains("Scheduler", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>LHM value → BetterVitals value (NaN = none). Hot-spot-like sensors treat 0 and 255 as "no reading".</summary>
    public static float Sanitize(float? raw, float scale, bool hotSpotLike)
    {
        if (!raw.HasValue) return float.NaN;
        var v = raw.Value;
        if (!float.IsFinite(v)) return float.NaN;
        if (hotSpotLike && (v == 0f || v == 255f)) return float.NaN;
        return v * scale;
    }

    private static bool IsNumbered(string name, string prefix)
    {
        if (name.Length <= prefix.Length || !name.StartsWith(prefix, StringComparison.Ordinal)) return false;
        for (var i = prefix.Length; i < name.Length; i++)
            if (!char.IsAsciiDigit(name[i])) return false;
        return true;
    }

    private static SensorKind?[] BuildKindMap()
    {
        var types = Enum.GetValues<SensorType>();
        var max = 0;
        foreach (var t in types) max = Math.Max(max, (int)t);
        var map = new SensorKind?[max + 1];
        foreach (var t in types)
            map[(int)t] = Enum.TryParse<SensorKind>(t.ToString(), ignoreCase: false, out var k) ? k : null;
        return map;
    }
}
