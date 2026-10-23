using BetterVitals.Core;
using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Native.Interop;

namespace BetterVitals.Sensors.Native.Gpu;

/// <summary>
/// GPU basics for adapters upstream has no data for. Driver values come from D3DKMT (ADAPTERPERFDATA: temperature, fan,
/// memory clock; NODEPERFDATA of the 3D node: core clock and voltage — no administrator rights needed); load and memory use
/// come from the PDH "GPU Engine" / "GPU Adapter Memory" counters. An adapter that can power down (hybrid laptop dGPU) is
/// checked with the <see cref="IDevicePowerGate"/> before every driver call: asleep = NaN, looked at again after
/// <see cref="AsleepRecheck"/>. PDH reads are kernel accounting and never reach the card.
/// </summary>
internal sealed class GpuModule(IDevicePowerGate gate, Action? requestRediscover = null,
    Func<List<GpuAdapterInfo>>? enumerate = null, Func<uint, GpuPerfProbe>? probe = null,
    Func<AdapterAddress, string?>? findInstanceId = null, bool usePdh = true) : INativeModule
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan AsleepRecheck = TimeSpan.FromSeconds(10);

    // sensor slots
    internal const int SlotTemperature = 0, SlotFan = 1, SlotMemoryClock = 2, SlotPower = 3, SlotCoreClock = 4, SlotCoreVoltage = 5,
        SlotLoad3D = 6, SlotDedicated = 7, SlotShared = 8, SlotCount = 9;

    private readonly Func<List<GpuAdapterInfo>> _enumerate = enumerate ?? GpuAdapters.Enumerate;
    private readonly Func<uint, GpuPerfProbe> _probe = probe ?? GpuAdapters.Probe;
    private readonly Func<AdapterAddress, string?> _findInstanceId = findInstanceId ?? GpuAdapters.FindInstanceId;
    private readonly List<GpuAdapterInfo> _adapters = [];
    private readonly List<GpuNode> _nodes = [];
    private GpuCounters? _counters;

    public string Name => "gpu";

    /// <summary>A sensor the module will register.</summary>
    internal readonly record struct Planned(int Slot, SensorKind Kind, string Name);

    public void Discover(InventoryBuilder b)
    {
        Release();
        var adapters = _enumerate();
        _adapters.AddRange(adapters);
        if (adapters.Count == 0) return;

        // PDH is opened lazily (first collection), not here: it costs ~450 ms and ~7 MB. Every hardware adapter has counter
        // instances on Windows 10 1709+ (WDDM 2.x); if not, the counters fail once and the PDH sensors stay NaN.
        _counters = new GpuCounters(adapters.Select(a => a.Luid.Value).ToList());

        var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<GpuAdapterInfo>();
        var needsCounters = false;
        for (var i = 0; i < adapters.Count; i++)
        {
            var a = adapters[i];
            ordinals.TryGetValue(a.Name, out var ordinal);
            ordinals[a.Name] = ordinal + 1;

            var gated = a.NeedsPowerGate && a.HasAddress;
            if (gated) a.InstanceId = _findInstanceId(a.Address);
            var asleep = gated && a.InstanceId is not null && !gate.IsAwake(a.InstanceId);
            var perf = asleep || a.Handle == 0 ? GpuPerfProbe.None : _probe(a.Handle);
            var planned = PlanSensors(a.Name, ordinal, perf, pdh: usePdh && i < 8, engineCounter: true, b.Upstream, out var upstreamHw);
            if (planned.Count == 0) continue;

            var hwId = upstreamHw?.Id ?? b.AddHardware(HardwareKind.Gpu, a.Name, ordinal);
            var usesCounters = planned.Exists(p => p.Slot >= SlotLoad3D);
            var node = new GpuNode(a, perf, asleep, gated && a.InstanceId is not null ? gate : null, usesCounters ? _counters : null, i, _probe, requestRediscover);
            var added = false;
            foreach (var p in planned)
                added |= b.AddSensor(hwId, HardwareKind.Gpu, p.Kind, p.Name, node, p.Slot, Interval, "", upstreamHw is not null) is not null;
            if (!added) continue;
            needsCounters |= usesCounters;
            _nodes.Add(node);
            used.Add(a);
        }

        // adapters that got no sensor do not keep their handle
        foreach (var a in adapters)
        {
            if (a.Handle == 0 || used.Contains(a)) continue;
            D3dKmt.Close(a.Handle);
            a.Handle = 0;
        }
        if (!needsCounters)
        {
            _counters.Dispose();
            _counters = null;
        }
    }

    /// <summary>
    /// Gap detection for one adapter. <paramref name="perf"/> says what the driver answered, <paramref name="pdh"/> whether load and
    /// memory sensors from the performance counters are on offer (<paramref name="engineCounter"/>: the 3D engine counter too).
    /// Upstream hardware of the same GPU is reused; a sensor is planned only when upstream has no equivalent (any temperature,
    /// any fan, the same clock / load / memory name).
    /// </summary>
    internal static List<Planned> PlanSensors(string name, int ordinal, GpuPerfProbe perf, bool pdh, bool engineCounter, UpstreamIndex up, out UpstreamIndex.Hw? upstreamHw)
    {
        var hw = up.Match(HardwareKind.Gpu, name, ordinal);
        upstreamHw = hw;
        bool Has(SensorKind k, string n) => hw is not null && hw.Has(k, n);
        bool HasKind(SensorKind k) => hw is not null && hw.HasKind(k);
        // memory sizes are SmallData (MB) in our own convention but LibreHardwareMonitor registers some of them as Data
        bool HasMemory(string n) => Has(SensorKind.SmallData, n) || Has(SensorKind.Data, n);
        var list = new List<Planned>();

        if (perf.Temperature && !(hw?.HasMeasuredTemperature() ?? false)) list.Add(new(SlotTemperature, SensorKind.Temperature, "GPU Core"));
        if (perf.Fan && !HasKind(SensorKind.Fan)) list.Add(new(SlotFan, SensorKind.Fan, "GPU Fan"));
        if (perf.MemoryClock && !Has(SensorKind.Clock, "GPU Memory")) list.Add(new(SlotMemoryClock, SensorKind.Clock, "GPU Memory"));
        if (perf.CoreClock && !Has(SensorKind.Clock, "GPU Core")) list.Add(new(SlotCoreClock, SensorKind.Clock, "GPU Core"));
        if (perf.CoreVoltage && !HasKind(SensorKind.Voltage)) list.Add(new(SlotCoreVoltage, SensorKind.Voltage, "GPU Core"));
        // "Power" of ADAPTERPERFDATA is a percentage, not watts: offered as a load, and only where upstream has no power reading at all
        if (perf.Power && !HasKind(SensorKind.Power) && !Has(SensorKind.Load, "GPU Power")) list.Add(new(SlotPower, SensorKind.Load, "GPU Power"));

        if (pdh)
        {
            if (engineCounter && !Has(SensorKind.Load, "D3D 3D") && !Has(SensorKind.Load, "GPU Core")) list.Add(new(SlotLoad3D, SensorKind.Load, "D3D 3D"));
            var hasVram = HasMemory("D3D Dedicated Memory Used")
                          || (hw is not null && (hw.HasNameContaining(SensorKind.SmallData, "Memory Used") || hw.HasNameContaining(SensorKind.Data, "Memory Used")));
            if (!hasVram) list.Add(new(SlotDedicated, SensorKind.SmallData, "D3D Dedicated Memory Used"));
            if (!HasMemory("D3D Shared Memory Used")) list.Add(new(SlotShared, SensorKind.SmallData, "D3D Shared Memory Used"));
        }
        return list;
    }

    public void Release()
    {
        foreach (var n in _nodes) n.Release();
        _nodes.Clear();
        foreach (var a in _adapters)
        {
            if (a.Handle != 0) D3dKmt.Close(a.Handle);
            a.Handle = 0;
        }
        _adapters.Clear();
        _counters?.Dispose();
        _counters = null;
    }

    public void Dispose() => Release();

    /// <summary>One adapter: D3DKMT driver values (gated) and its slice of the shared PDH counters.</summary>
    private sealed class GpuNode : NativeNode
    {
        private static readonly long AsleepTicks = Ticks(AsleepRecheck);
        private static readonly long ReopenTicks = Ticks(TimeSpan.FromSeconds(30));
        private readonly GpuAdapterInfo _adapter;
        private GpuPerfProbe _perf;
        private bool _deferred;
        private readonly IDevicePowerGate? _gate;
        private readonly GpuCounters? _counters;
        private readonly int _counterSlot;
        private readonly Func<uint, GpuPerfProbe> _probe;
        private readonly Action? _requestRediscover;
        private readonly float[] _values = new float[SlotCount];
        private long _asleepUntil;
        private long _reopenAfter;
        private int _failures;

        public GpuNode(GpuAdapterInfo adapter, GpuPerfProbe perf, bool deferred, IDevicePowerGate? gate, GpuCounters? counters, int counterSlot,
            Func<uint, GpuPerfProbe> probe, Action? requestRediscover)
        {
            _adapter = adapter;
            _perf = perf;
            _deferred = deferred;
            _gate = gate;
            _counters = counters;
            _counterSlot = counterSlot;
            _probe = probe;
            _requestRediscover = requestRediscover;
            Array.Fill(_values, float.NaN);
        }

        internal override void Refresh(long timestamp)
        {
            ReadDriver(timestamp);
            if (_counters is not null && _counterSlot >= 0 && _counters.Collect(timestamp))
            {
                _values[GpuModule.SlotLoad3D] = _counters.Load3D(_counterSlot);
                _values[GpuModule.SlotDedicated] = _counters.DedicatedMb(_counterSlot);
                _values[GpuModule.SlotShared] = _counters.SharedMb(_counterSlot);
            }
        }

        private unsafe void ReadDriver(long timestamp)
        {
            var anyDriver = _perf.Temperature || _perf.Fan || _perf.MemoryClock || _perf.Power || _perf.CoreClock || _perf.CoreVoltage || _deferred;
            if (!anyDriver) return;

            if (_gate is not null && _adapter.InstanceId is { } id)
            {
                if (timestamp < _asleepUntil || !_gate.IsAwake(id))
                {
                    if (timestamp >= _asleepUntil) _asleepUntil = timestamp + AsleepTicks;
                    ClearDriverValues();
                    return;
                }
                _asleepUntil = 0;
            }
            if (_adapter.Handle == 0) { ClearDriverValues(); return; }

            if (_deferred)
            {
                // the card was asleep at discovery: now it is awake, so learn what it reports and ask for a re-discovery
                _perf = _probe(_adapter.Handle);
                _deferred = false;
                _requestRediscover?.Invoke();
            }

            var perf = new AdapterPerfData();
            if (D3dKmt.Ok(D3dKmt.Query(_adapter.Handle, D3dKmt.QaiAdapterPerfData, &perf, (uint)sizeof(AdapterPerfData))))
            {
                _values[SlotTemperature] = Plausible.GpuTemperatureDeci(perf.Temperature) ? perf.Temperature / 10f : float.NaN;
                _values[SlotFan] = Plausible.FanRpm(perf.FanRpm) ? perf.FanRpm : float.NaN;
                _values[SlotMemoryClock] = Plausible.ClockHz(perf.MemoryFrequency) ? perf.MemoryFrequency / 1e6f : float.NaN;
                _values[SlotPower] = Plausible.PowerTenthsPercent(perf.Power) ? perf.Power / 10f : float.NaN;
            }
            else
            {
                ClearDriverValues();
                if (++_failures >= 3) TryReopen(timestamp);
                return;
            }
            _failures = 0;

            if (_perf.CoreClock || _perf.CoreVoltage)
            {
                var node = new NodePerfData { NodeOrdinal = _perf.NodeOrdinal };
                if (D3dKmt.Ok(D3dKmt.Query(_adapter.Handle, D3dKmt.QaiNodePerfData, &node, (uint)sizeof(NodePerfData))))
                {
                    _values[SlotCoreClock] = Plausible.ClockHz(node.Frequency) ? node.Frequency / 1e6f : float.NaN;
                    _values[SlotCoreVoltage] = Plausible.VoltageMilli(node.Voltage) ? node.Voltage / 1000f : float.NaN;
                }
                else
                {
                    _values[SlotCoreClock] = _values[SlotCoreVoltage] = float.NaN;
                }
            }
        }

        private void ClearDriverValues()
        {
            for (var i = 0; i <= SlotCoreVoltage; i++) _values[i] = float.NaN;
        }

        /// <summary>Repeated failures can mean a stale handle (driver reset): reopen by LUID; if that fails, ask for a re-discovery.</summary>
        private void TryReopen(long timestamp)
        {
            _failures = 0;
            if (timestamp < _reopenAfter) return;
            _reopenAfter = timestamp + ReopenTicks;
            D3dKmt.Close(_adapter.Handle);
            _adapter.Handle = D3dKmt.OpenLuid(_adapter.Luid);
            if (_adapter.Handle == 0) _requestRediscover?.Invoke();
        }

        internal override float Read(int slot) => (uint)slot < (uint)_values.Length ? _values[slot] : float.NaN;

        internal override void Fault() => Array.Fill(_values, float.NaN);
    }
}
