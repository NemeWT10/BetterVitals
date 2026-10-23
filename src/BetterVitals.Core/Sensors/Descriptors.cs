namespace BetterVitals.Core.Sensors;

/// <summary>A hardware node (CPU, GPU, a drive, the SuperIO chip…). Immutable after discovery.</summary>
public sealed record HardwareDescriptor
{
    /// <summary>
    /// Stable across restarts and device re-ordering (history and settings key on it), e.g. "cpu/amd-ryzen-7-9800x3d/0".
    /// Built by the source from kind + name (+ serial / PCI location when available) + ordinal among equal names.
    /// Lowercase, '/'-separated, no spaces.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>Verbatim name reported by the hardware / driver ("NVIDIA GeForce RTX 5080"). Never translated.</summary>
    public required string Name { get; init; }

    public required HardwareKind Kind { get; init; }

    /// <summary>Parent node for sub-hardware (e.g. the SuperIO chip under the motherboard); null for top level.</summary>
    public string? ParentId { get; init; }

    /// <summary>Source that owns it: "lhm", "native", "fps", "derived", "fake".</summary>
    public required string Source { get; init; }
}

/// <summary>
/// A single sensor. Created by a source in <see cref="ISensorSource.Discover"/>; the hub assigns <see cref="Index"/>
/// (its slot in <see cref="SensorFrame.Values"/>), which stays the same for the whole process lifetime — also when the
/// sensor disappears (its value is then NaN) and comes back.
/// </summary>
public sealed class SensorDescriptor
{
    /// <summary>
    /// Stable across restarts: "&lt;HardwareId&gt;/&lt;kind&gt;/&lt;name-slug&gt;" (+ "#k" when a name repeats), e.g.
    /// "gpu/nvidia-geforce-rtx-5080/0/temperature/gpu-hot-spot". Settings (recorded / overlay) and history key on it.
    /// </summary>
    public required string Id { get; init; }

    public required string HardwareId { get; init; }

    /// <summary>Verbatim sensor name from the hardware / LHM ("Core #1 VID", "GPU Hot Spot"). Never translated.</summary>
    public required string Name { get; init; }

    public required SensorKind Kind { get; init; }

    /// <summary>Component family (copied from the hardware for quick filtering).</summary>
    public required HardwareKind HardwareKind { get; init; }

    /// <summary>Unit text; defaults to <see cref="SensorUnits.Unit"/>.</summary>
    public string Unit { get; init; } = "";

    /// <summary>
    /// The fastest this sensor may be polled (expensive reads: SMART / NVMe health 10 s+, slow counters 60 s+).
    /// Zero = as fast as any consumer asks.
    /// </summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.Zero;

    /// <summary>Optional ordering inside its hardware (sources keep the hardware's natural order).</summary>
    public int Order { get; init; }

    /// <summary>Optional note shown next to the value (e.g. "unofficial, ±1 °C" for the RTX 50 hot spot) — a Loc key.</summary>
    public string? NoteKey { get; init; }

    /// <summary>Source-private handle (e.g. the LHM <c>ISensor</c>); only the owning source reads it.</summary>
    public object? SourceTag { get; init; }

    /// <summary>Slot in <see cref="SensorFrame.Values"/>; -1 until the hub registers the sensor.</summary>
    public int Index { get; internal set; } = -1;

    public string DisplayUnit => Unit.Length > 0 ? Unit : SensorUnits.Unit(Kind);

    public override string ToString() => $"{Id} [{Kind}] #{Index}";
}

/// <summary>What a source found.</summary>
public sealed record SourceInventory(IReadOnlyList<HardwareDescriptor> Hardware, IReadOnlyList<SensorDescriptor> Sensors)
{
    public static SourceInventory Empty { get; } = new([], []);
}

/// <summary>Health of a source as shown on the "Permissions and driver" page.</summary>
public enum SourceState : byte
{
    /// <summary>Not started yet.</summary>
    Idle,
    Ok,
    /// <summary>Works, but some groups need administrator rights or the PawnIO driver.</summary>
    Limited,
    /// <summary>Paused on purpose (e.g. "pause during anti-cheat games").</summary>
    Paused,
    /// <summary>Not available on this PC / blocked / failed; the app keeps running without it.</summary>
    Unavailable,
}

/// <summary>State + a Loc key explaining it ("source.lhm.needsDriver"…).</summary>
public readonly record struct SourceStatus(SourceState State, string? MessageKey = null);
