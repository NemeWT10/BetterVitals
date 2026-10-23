using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Power;
using LibreHardwareMonitor.Hardware;

namespace BetterVitals.Sensors.Lhm;

/// <summary>One LHM hardware node plus everything the hot path needs about it. Engine thread only.</summary>
internal sealed class LhmNode
{
    public LhmNode(IHardware? hardware, HardwareType type, HardwareKind kind, string stableKey, bool driverBacked, PowerGate? gate)
    {
        Hardware = hardware;
        Type = type;
        Kind = kind;
        StableKey = stableKey;
        DriverBacked = driverBacked;
        Gate = gate;
        IsStorage = type == HardwareType.Storage;
    }

    /// <summary>Null for a "ghost": hardware released while the driver is paused (descriptor kept, values NaN).</summary>
    public IHardware? Hardware { get; }

    public HardwareType Type { get; }

    public HardwareKind Kind { get; }

    public string StableKey { get; }

    /// <summary>Every sensor needs PawnIO (CPU, mainboard, DIMM, Intel iGPU).</summary>
    public bool DriverBacked { get; }

    public PowerGate? Gate { get; }

    public bool IsStorage { get; }

    public HardwareDescriptor Descriptor { get; set; } = null!;

    /// <summary>NVIDIA / AMD: LHM's private D3D adapter id field, nulled during updates that need no "D3D …" sensor.</summary>
    public System.Reflection.FieldInfo? D3dField { get; set; }

    public string? D3dId { get; set; }

    /// <summary>Updated once at discovery so LHM activates its lazily created sensors.</summary>
    public bool WarmedUp { get; set; }

    /// <summary>Result of the refresh in the current Update call.</summary>
    public bool Readable { get; set; }

    public bool FailureLogged { get; set; }

    // ── statistics ──
    public long Updates { get; set; }
    public long TotalTicks { get; set; }
    public long MaxTicks { get; set; }
    public long AllocatedBytes { get; set; }
    public long SkippedAsleep { get; set; }
    public long Failures { get; set; }
}

/// <summary>A discovered sensor: the LHM object (null for a ghost), its node and precomputed traits.</summary>
internal sealed class LhmSensorEntry(ISensor? sensor, LhmNode node, float scale, LhmSensorFlags flags, SensorDescriptor descriptor)
{
    public ISensor? Sensor { get; } = sensor;

    public LhmNode Node { get; } = node;

    public float Scale { get; } = scale;

    public LhmSensorFlags Flags { get; } = flags;

    public SensorDescriptor Descriptor { get; } = descriptor;
}

/// <summary>What one distinct request needs: the nodes to refresh (each once) and the entry per requested slot.</summary>
internal sealed class LhmUpdatePlan(LhmNode[] nodes, LhmSensorFlags[] needs, LhmSensorEntry?[] entries)
{
    public LhmNode[] Nodes { get; } = nodes;

    /// <summary>Per node: the <c>Needs*</c> flags of its requested sensors (SMART refresh, NVML PCIe, D3D query).</summary>
    public LhmSensorFlags[] Needs { get; } = needs;

    /// <summary>Per requested slot, in request order; null = unknown id (NaN).</summary>
    public LhmSensorEntry?[] Entries { get; } = entries;
}

/// <summary>Per-node diagnostics (SensorProbe, perf-guard).</summary>
/// <param name="HardwareId">Stable hardware id.</param>
/// <param name="Name">LHM name.</param>
/// <param name="Kind">Component family.</param>
/// <param name="Updates">LHM <c>IHardware.Update</c> calls.</param>
/// <param name="TotalMilliseconds">Time spent in them.</param>
/// <param name="MaxMilliseconds">Slowest call.</param>
/// <param name="AllocatedBytes">Managed bytes LHM allocated in them (engine thread).</param>
/// <param name="SkippedAsleep">Updates skipped because the device slept.</param>
/// <param name="Failures">Updates that threw.</param>
/// <param name="PowerGate">Power gate target, null when the node has none.</param>
/// <param name="PowerState">Last power query result.</param>
/// <param name="Rotating">Storage: rotating medium (seek penalty), null when unknown / not storage.</param>
/// <param name="DriverBacked">Needs PawnIO (paused for anti-cheat games).</param>
/// <param name="Ghost">Kept from before the pause, not present right now.</param>
public readonly record struct LhmNodeStats(
    string HardwareId,
    string Name,
    HardwareKind Kind,
    long Updates,
    double TotalMilliseconds,
    double MaxMilliseconds,
    long AllocatedBytes,
    long SkippedAsleep,
    long Failures,
    string? PowerGate,
    PowerQuery? PowerState,
    bool? Rotating,
    bool DriverBacked,
    bool Ghost);
