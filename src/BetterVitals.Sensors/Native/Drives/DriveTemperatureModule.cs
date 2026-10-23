using BetterVitals.Core;
using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Native.Interop;

namespace BetterVitals.Sensors.Native.Drives;

/// <summary>A physical drive found by the probe (the handle stays open for the node that will read it).</summary>
internal sealed class ProbedDrive
{
    public int PhysicalIndex { get; init; }

    public string Model { get; init; } = "";

    public string Serial { get; init; } = "";

    /// <summary>Rotating (heads move) or unknown — such a drive is never read while it sleeps.</summary>
    public bool Rotating { get; init; }

    /// <summary>A rotating drive in standby at probe time: not queried (it would spin up); it gets a sensor after a later discovery.</summary>
    public bool Asleep { get; init; }

    /// <summary>The drive answered the temperature query with a plausible value.</summary>
    public bool TemperatureSupported => Method != TemperatureMethod.None;

    /// <summary>The query that works for this drive.</summary>
    public TemperatureMethod Method { get; init; }

    public float Temperature { get; init; } = float.NaN;

    public nint Handle { get; set; }
}

/// <summary>
/// Drive temperatures without administrator rights: <c>\\.\PhysicalDriveN</c> opened with desiredAccess 0 and
/// <c>StorageDeviceTemperatureProperty</c> (SATA, some NVMe) or, for NVMe drives that refuse that, the SMART / health log page.
/// Adds a "Temperature" sensor only for drives whose firmware answers and that upstream has no temperature for.
/// A rotating drive in standby is never read (GetDevicePowerState says it is not in D0) and is read every 30 s instead of 10 s.
/// </summary>
internal sealed class DriveTemperatureModule(Func<List<ProbedDrive>>? probe = null, IStorageIo? io = null) : INativeModule
{
    internal const string SensorName = "Temperature";
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    /// <summary>A rotating drive is read less often: the command reaches the disk (~15 ms) and the value moves slowly.</summary>
    internal static readonly TimeSpan RotatingInterval = TimeSpan.FromSeconds(30);
    private const int MaxDrives = 32;

    private readonly IStorageIo _io = io ?? SystemStorageIo.Instance;
    private readonly Func<List<ProbedDrive>> _probe = probe ?? (() => ProbeDrives(io ?? SystemStorageIo.Instance));
    private readonly List<DriveNode> _nodes = [];

    public string Name => "drives";

    /// <summary>A planned sensor: which drive, on which hardware node.</summary>
    internal sealed record Plan(ProbedDrive Drive, string HardwareId, string? NewHardwareName, int Ordinal, bool ReusedHardware);

    public void Discover(InventoryBuilder b)
    {
        Release();
        var drives = _probe();
        var plans = PlanSensors(drives, b.Upstream);
        var used = new HashSet<ProbedDrive>();
        foreach (var plan in plans)
        {
            var d = plan.Drive;
            var node = new DriveNode(_io, d.PhysicalIndex, d.Model, d.Handle, d.Rotating, d.Method, d.Temperature);
            var hwId = plan.NewHardwareName is null ? plan.HardwareId : b.AddHardware(HardwareKind.Storage, plan.NewHardwareName, plan.Ordinal);
            if (b.AddSensor(hwId, HardwareKind.Storage, SensorKind.Temperature, SensorName, node, 0, d.Rotating ? RotatingInterval : Interval, reusedHardware: plan.ReusedHardware) is null)
                continue; // already present upstream
            _nodes.Add(node);
            used.Add(d);
        }
        // handles of drives that got no sensor are not kept open
        foreach (var d in drives)
        {
            if (used.Contains(d) || d.Handle == 0) continue;
            _io.Close(d.Handle);
            d.Handle = 0;
        }
    }

    /// <summary>
    /// Gap detection: a drive needs a sensor when it answered the temperature query and upstream has fewer temperature
    /// sensors for its model than there are drives of that model. Upstream hardware of the same model without a temperature
    /// is reused (its id), otherwise an own Storage hardware named after the model is added.
    /// </summary>
    internal static List<Plan> PlanSensors(IReadOnlyList<ProbedDrive> drives, UpstreamIndex upstream)
    {
        var plans = new List<Plan>();
        var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in drives)
        {
            ordinals.TryGetValue(d.Model, out var ordinal);
            ordinals[d.Model] = ordinal + 1;
            if (!d.TemperatureSupported || d.Model.Length == 0) continue;

            var matches = upstream.MatchAll(HardwareKind.Storage, d.Model);
            var withTemp = matches.Count(h => h.HasMeasuredTemperature());
            if (ordinal < withTemp) continue; // upstream covers this one

            var reuse = matches.FirstOrDefault(h => h.Ordinal == ordinal && !h.HasMeasuredTemperature())
                        ?? matches.FirstOrDefault(h => !h.HasMeasuredTemperature());
            plans.Add(reuse is null
                ? new Plan(d, SensorIds.Hardware(HardwareKind.Storage, d.Model, ordinal), d.Model, ordinal, false)
                : new Plan(d, reuse.Id, null, ordinal, true));
        }
        return plans;
    }

    /// <summary>Opens every \\.\PhysicalDriveN and asks it who it is and whether it knows its temperature.</summary>
    internal static List<ProbedDrive> ProbeDrives(IStorageIo io)
    {
        var result = new List<ProbedDrive>();
        for (var i = 0; i < MaxDrives; i++)
        {
            var h = io.Open(i);
            if (h == 0) continue;
            var keep = false;
            try
            {
                var desc = StorageQuery.ReadDescriptor(h);
                if (desc is null || !StorageQuery.IsFixedBus(desc.BusType, desc.Removable)) continue;
                var rotating = StorageQuery.ReadIncursSeekPenalty(h) ?? true; // unknown = treat as rotating (more careful)
                var asleep = rotating && !io.IsInD0(h);
                var t = float.NaN;
                var method = asleep ? TemperatureMethod.None : StorageQuery.ProbeTemperature(h, desc.BusType, out t);
                result.Add(new ProbedDrive
                {
                    PhysicalIndex = i,
                    Model = desc.Name,
                    Serial = desc.Serial,
                    Rotating = rotating,
                    Asleep = asleep,
                    Method = method,
                    Temperature = t,
                    Handle = h,
                });
                keep = true;
            }
            finally
            {
                if (!keep) io.Close(h);
            }
        }
        return result;
    }

    public void Release()
    {
        foreach (var n in _nodes) n.Release();
        _nodes.Clear();
    }

    public void Dispose() => Release();

    /// <summary>
    /// Reads one drive. Keeps its handle open; after three failures in a row closes it and tries to reopen 30 s later.
    /// A rotating drive that is not in D0 (or whose state Windows does not report) is not touched at all.
    /// </summary>
    internal sealed class DriveNode(IStorageIo io, int physicalIndex, string model, nint handle, bool rotating, TemperatureMethod method, float initial) : NativeNode
    {
        private static readonly long RetryTicks = Ticks(TimeSpan.FromSeconds(30));
        private nint _handle = handle;
        private float _value = initial;
        private int _failures;
        private long _retryAt;

        internal override void Refresh(long timestamp)
        {
            if (_handle == 0)
            {
                _value = float.NaN;
                if (timestamp < _retryAt) return;
                _retryAt = timestamp + RetryTicks;
                _handle = io.Open(physicalIndex);
                if (_handle == 0) return;
            }
            if (rotating && !io.IsInD0(_handle))
            {
                _value = float.NaN; // standby (or unknown): leave the platters alone
                return;
            }
            if (io.TryReadTemperature(_handle, method, out var t))
            {
                _value = t;
                _failures = 0;
                return;
            }
            _value = float.NaN;
            if (++_failures < 3) return;
            _failures = 0;
            CloseHandle(); // reopened by a refresh 30 s from now
            _retryAt = timestamp + RetryTicks;
        }

        internal override float Read(int slot) => _value;

        internal override void Fault() => _value = float.NaN;

        internal override void Release() => CloseHandle();

        private void CloseHandle()
        {
            if (_handle != 0) io.Close(_handle);
            _handle = 0;
        }

        public override string ToString() => $"PhysicalDrive{physicalIndex} {model}";
    }
}
