using System.Collections.Frozen;
using BetterVitals.Core;
using BetterVitals.Core.Sensors;

namespace BetterVitals.Sensors.Engine;

/// <summary>
/// The sampling engine — the only place that reads hardware. One background thread; everything about sources runs
/// on it. Demand-driven: a sensor is read only while some consumer (recorder, overlay, an open page) asks for it, at the
/// shortest interval asked for (never faster than its <see cref="SensorDescriptor.MinInterval"/>), on a wall-clock
/// aligned grid of 250 ms steps so different consumers share ticks. Between ticks the thread sleeps; with no demand it
/// does not wake at all. The tick path does not allocate.
/// </summary>
public sealed class SensorEngine : ISensorHub, IDisposable
{
    public static readonly TimeSpan BaseTick = TimeSpan.FromMilliseconds(250);
    private static readonly long BaseTickTicks = BaseTick.Ticks;
    private static readonly long RediscoverDebounceTicks = TimeSpan.FromSeconds(2).Ticks;
    private const int MaxWaitMs = 60_000;

    private readonly ISensorSource[] _sources;
    private readonly Func<long> _utcNow;
    private readonly Lock _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile bool _stop;
    private int _disposed;

    // ── registry: written on the engine thread, published as immutable snapshots ──
    private readonly Dictionary<string, int> _indexById = new(StringComparer.Ordinal);
    private readonly List<SensorDescriptor> _registered = [];
    private readonly List<int> _sourceOfList = [];
    private SensorDescriptor[] _sensors = [];
    private bool[] _present = [];
    private int[] _sourceOf = [];
    private HardwareDescriptor[] _hardware = [];
    private FrozenDictionary<string, int> _lookup = FrozenDictionary<string, int>.Empty;
    private FrozenDictionary<string, HardwareDescriptor> _hardwareLookup = FrozenDictionary<string, HardwareDescriptor>.Empty;
    private readonly Dictionary<string, HardwareDescriptor[]> _hardwareBySource = new(StringComparer.Ordinal);
    private readonly List<int> _clearPending = [];

    // ── frames ──
    private readonly SensorFrame[] _frames = [new(64), new(64), new(64)];
    private int _cursor;
    private SensorFrame _latest;
    private int _sequence;

    // ── demand ──
    private readonly List<DemandEntry> _demands = [];
    private volatile bool _demandDirty = true;

    // ── schedule (engine thread only) ──
    private Group[] _groups = [];
    private SensorDescriptor[][] _due = [];
    private int[] _dueCount = [];
    private bool[] _dueMark = [];

    // ── requests from other threads ──
    private volatile bool _inventoryDirty = true;
    private long _inventoryDirtySince;
    private volatile bool _suspendRequested;
    private volatile bool _resumeRequested;
    private volatile bool _resetStats;
    private bool _suspended;
    private readonly long[] _lastErrorLog;
    private long _lastHandlerErrorLog;
    private int _discoveryFailures;

    private Action<SensorFrame>[] _frameHandlers = [];
    private Action[] _inventoryHandlers = [];

    private sealed class Group
    {
        public required long IntervalTicks { get; init; }
        public long NextDue { get; set; }
        public required int[] Indices { get; init; }
    }

    private sealed class DemandEntry(SensorEngine owner, string consumer, string[] ids, long intervalTicks) : IDemand
    {
        public string Consumer { get; } = consumer;
        public string[] Ids { get; set; } = ids;
        public long IntervalTicks { get; set; } = intervalTicks;

        public void Update(IEnumerable<string> sensorIds, TimeSpan interval) => owner.UpdateDemand(this, sensorIds, interval);

        public void Dispose() => owner.RemoveDemand(this);
    }

    /// <param name="sources">Dependent sources (<see cref="IDependentSource"/>) are moved after the others.</param>
    /// <param name="utcNow">Clock (DateTime UTC ticks); tests pass a fake one.</param>
    public SensorEngine(IEnumerable<ISensorSource> sources, Func<long>? utcNow = null)
    {
        _sources = sources.OrderBy(s => s is IDependentSource ? 1 : 0).ToArray();
        _utcNow = utcNow ?? (() => DateTime.UtcNow.Ticks);
        _latest = _frames[0];
        _lastErrorLog = new long[_sources.Length];
        _due = new SensorDescriptor[_sources.Length][];
        for (var i = 0; i < _due.Length; i++) _due[i] = [];
        _dueCount = new int[_sources.Length];
        foreach (var s in _sources) s.InventoryChanged += OnSourceInventoryChanged;
    }

    // ───────────────────────── lifecycle ─────────────────────────

    /// <summary>Starts the engine thread (discovery happens on it, not on the caller's thread).</summary>
    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "BetterVitals sampling" };
        _thread.Start();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop = true;
        Wake();
        if (_thread is { } t && t != Thread.CurrentThread) t.Join(TimeSpan.FromSeconds(5));
        foreach (var s in _sources)
        {
            s.InventoryChanged -= OnSourceInventoryChanged;
            try { s.Dispose(); } catch (Exception ex) { Log.Warn($"engine: dispose {s.Name}: {ex.Message}"); }
        }
        _wake.Dispose();
    }

    /// <summary>Wakes the engine thread; a late caller (a window closing during shutdown) may find the engine already gone.</summary>
    private void Wake()
    {
        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
            // nothing left to wake
        }
    }

    /// <summary>Before sleep / hibernate (from the app's power notification).</summary>
    public void NotifySuspend()
    {
        _suspendRequested = true;
        Wake();
    }

    /// <summary>After resume: sources re-open and re-discover; the schedule realigns.</summary>
    public void NotifyResume()
    {
        _resumeRequested = true;
        Wake();
    }

    /// <summary>Forget the session min / max / average ("reset" in the UI).</summary>
    public void ResetStats()
    {
        _resetStats = true;
        Wake();
    }

    /// <summary>A source reported new / vanished hardware; re-discover after a short quiet period.</summary>
    public void RequestRediscover() => OnSourceInventoryChanged();

    private void OnSourceInventoryChanged()
    {
        Interlocked.Exchange(ref _inventoryDirtySince, _utcNow());
        _inventoryDirty = true;
        Wake();
    }

    private void Run()
    {
        // first discovery right away, no debounce
        SafeRediscover();
        while (!_stop)
        {
            try
            {
                var waitMs = Step();
                if (waitMs != 0) _wake.WaitOne(waitMs);
            }
            catch (Exception ex)
            {
                // the engine must survive anything a source or a consumer does
                Log.Error("engine: step failed", ex);
                _wake.WaitOne(1000);
            }
        }
    }

    /// <summary>One pass of the loop; returns how long to sleep (ms, -1 = until woken).</summary>
    internal int Step()
    {
        if (_suspendRequested)
        {
            _suspendRequested = false;
            if (!_suspended)
            {
                _suspended = true;
                foreach (var s in _sources) Safe(s, static x => x.Suspend());
            }
        }
        if (_resumeRequested)
        {
            _resumeRequested = false;
            if (_suspended)
            {
                _suspended = false;
                foreach (var s in _sources) Safe(s, static x => x.Resume());
                SafeRediscover();
                foreach (var g in _groups) g.NextDue = AlignNext(_utcNow(), g.IntervalTicks);
            }
        }
        if (_suspended) return -1;

        if (_resetStats)
        {
            _resetStats = false;
            Stats.Reset();
        }

        var now = _utcNow();
        if (_inventoryDirty && now - Interlocked.Read(ref _inventoryDirtySince) >= RediscoverDebounceTicks) SafeRediscover();
        if (_demandDirty) RecomputeSchedule(now);

        now = _utcNow();
        Tick(now);

        // sleep until the next due group (or the rediscovery deadline)
        var next = long.MaxValue;
        foreach (var g in _groups)
        {
            // the wall clock jumped back (time sync, user change): realign instead of sleeping for hours
            if (g.NextDue - now > 2 * g.IntervalTicks + BaseTickTicks) g.NextDue = AlignNext(now, g.IntervalTicks);
            if (g.NextDue < next) next = g.NextDue;
        }
        if (_inventoryDirty) next = Math.Min(next, Interlocked.Read(ref _inventoryDirtySince) + RediscoverDebounceTicks);
        if (next == long.MaxValue) return -1;
        var ms = (next - _utcNow()) / TimeSpan.TicksPerMillisecond;
        return (int)Math.Clamp(ms + 1, 0, MaxWaitMs);
    }

    // ───────────────────────── tick ─────────────────────────

    /// <summary>Reads every group due at <paramref name="now"/> into a new frame and publishes it. Returns false when nothing was due.</summary>
    internal bool Tick(long now)
    {
        var anyDue = false;
        foreach (var g in _groups)
        {
            if (g.NextDue > now) continue;
            anyDue = true;
            foreach (var idx in g.Indices)
            {
                if (_dueMark[idx] || !_present[idx]) continue;
                _dueMark[idx] = true;
                var s = _sourceOf[idx];
                _due[s][_dueCount[s]++] = _sensors[idx];
            }
            // next slot on the grid; after a stall skip missed slots instead of bursting
            g.NextDue = AlignNext(now, g.IntervalTicks);
        }
        if (!anyDue && _clearPending.Count == 0) return false;

        var prev = _latest;
        _cursor = (_cursor + 1) % _frames.Length;
        var frame = _frames[_cursor];
        frame.EnsureCapacity(_sensors.Length);
        frame.CopyFrom(prev);
        frame.Count = _sensors.Length;
        var seq = ++_sequence;
        var values = frame.Values;
        foreach (var idx in _clearPending) values[idx] = float.NaN;
        _clearPending.Clear();

        for (var s = 0; s < _sources.Length; s++)
        {
            var count = _dueCount[s];
            if (count == 0) continue;
            var due = _due[s].AsSpan(0, count);
            try
            {
                _sources[s].Update(due, values);
            }
            catch (Exception ex)
            {
                foreach (var d in due) values[d.Index] = float.NaN;
                if (now - _lastErrorLog[s] > TimeSpan.TicksPerMinute)
                {
                    _lastErrorLog[s] = now;
                    Log.Error($"engine: {_sources[s].Name} update failed", ex);
                }
            }
            var stamps = frame.UpdatedAt;
            foreach (var d in due)
            {
                stamps[d.Index] = seq;
                _dueMark[d.Index] = false;
            }
            _dueCount[s] = 0;
        }

        frame.Sequence = seq;
        frame.TimestampUtcTicks = now - now % BaseTickTicks;
        Volatile.Write(ref _latest, frame);
        Stats.Accumulate(frame);

        foreach (var h in Volatile.Read(ref _frameHandlers))
        {
            try { h(frame); }
            catch (Exception ex)
            {
                // a consumer with a bug fails on every tick: say so once a minute, like for the sources
                if (now - _lastHandlerErrorLog > TimeSpan.TicksPerMinute)
                {
                    _lastHandlerErrorLog = now;
                    Log.Error("engine: FrameReady handler failed", ex);
                }
            }
        }
        return true;
    }

    private static long AlignNext(long now, long interval) => (now / interval + 1) * interval;

    // ───────────────────────── discovery ─────────────────────────

    private void SafeRediscover()
    {
        _inventoryDirty = false;
        try
        {
            Rediscover();
        }
        catch (Exception ex)
        {
            Log.Error("engine: discovery failed", ex);
        }
    }

    private void Rediscover()
    {
        var seen = new bool[_registered.Count + 512];
        var hardware = new List<HardwareDescriptor>();
        var anyFailed = false;
        for (var s = 0; s < _sources.Length; s++)
        {
            var source = _sources[s];
            if (source is IDependentSource dependent) dependent.SetUpstream(_registered.ToArray());
            SourceInventory inv;
            try
            {
                inv = source.Discover();
            }
            catch (Exception ex)
            {
                // a hiccup (driver not ready, a transient error) must not wipe what the source had: keep it, try again soon
                Log.Error($"engine: {source.Name} discovery failed", ex);
                inv = KeepKnown(s);
                anyFailed = true;
            }
            _hardwareBySource[source.Name] = inv.Hardware.ToArray();
            foreach (var d in inv.Sensors)
            {
                if (!_indexById.TryGetValue(d.Id, out var idx))
                {
                    idx = _registered.Count;
                    _indexById[d.Id] = idx;
                    _registered.Add(d);
                    _sourceOfList.Add(s);
                }
                else
                {
                    _registered[idx] = d; // fresh descriptor (new SourceTag after re-open), same slot
                    _sourceOfList[idx] = s;
                }
                d.Index = idx;
                if (idx >= seen.Length) Array.Resize(ref seen, Math.Max(idx + 1, seen.Length * 2));
                seen[idx] = true;
            }
        }
        foreach (var list in _hardwareBySource.Values) hardware.AddRange(list);

        var n = _registered.Count;
        var present = new bool[n];
        for (var i = 0; i < n; i++)
        {
            present[i] = i < seen.Length && seen[i];
            if (!present[i] && (i >= _present.Length || _present[i])) _clearPending.Add(i);
        }

        // per-source scratch buffers for the tick (sized to what each source owns)
        var owned = new int[_sources.Length];
        for (var i = 0; i < n; i++) owned[_sourceOfList[i]]++;
        for (var s = 0; s < _sources.Length; s++)
            if (_due[s].Length < owned[s]) _due[s] = new SensorDescriptor[owned[s]];
        if (_dueMark.Length < n) Array.Resize(ref _dueMark, Math.Max(n, _dueMark.Length * 2));

        _sourceOf = _sourceOfList.ToArray();
        _present = present;
        Volatile.Write(ref _sensors, _registered.ToArray());
        Volatile.Write(ref _hardware, hardware.ToArray());
        Volatile.Write(ref _lookup, _indexById.ToFrozenDictionary(StringComparer.Ordinal));
        Volatile.Write(ref _hardwareLookup, hardware.GroupBy(h => h.Id).ToFrozenDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal));
        foreach (var f in _frames) f.EnsureCapacity(n);
        _demandDirty = true;

        if (anyFailed)
        {
            // 5 s, 10 s, 20 s … at most every 5 minutes; the regular debounce makes it due after exactly that delay
            var delay = TimeSpan.FromSeconds(Math.Min(300, 5 << Math.Min(_discoveryFailures++, 6)));
            _inventoryDirty = true;
            Interlocked.Exchange(ref _inventoryDirtySince, _utcNow() + delay.Ticks - RediscoverDebounceTicks);
        }
        else
        {
            _discoveryFailures = 0;
        }

        Log.Info($"engine: {hardware.Count} hardware, {present.Count(p => p)} of {n} sensors present");
        foreach (var h in Volatile.Read(ref _inventoryHandlers))
        {
            try { h(); }
            catch (Exception ex) { Log.Error("engine: InventoryChanged handler failed", ex); }
        }
    }

    /// <summary>What source <paramref name="s"/> had after the last successful discovery (its sensors that were present).</summary>
    private SourceInventory KeepKnown(int s)
    {
        var sensors = new List<SensorDescriptor>();
        for (var i = 0; i < _registered.Count; i++)
            if (_sourceOfList[i] == s && i < _present.Length && _present[i]) sensors.Add(_registered[i]);
        return new SourceInventory(_hardwareBySource.GetValueOrDefault(_sources[s].Name) ?? [], sensors);
    }

    // ───────────────────────── demand → schedule ─────────────────────────

    private void RecomputeSchedule(long now)
    {
        _demandDirty = false;
        var n = _sensors.Length;
        var best = new long[n];
        Array.Fill(best, long.MaxValue);
        lock (_gate)
        {
            foreach (var d in _demands)
            {
                foreach (var id in d.Ids)
                {
                    if (_indexById.TryGetValue(id, out var idx) && d.IntervalTicks < best[idx]) best[idx] = d.IntervalTicks;
                }
            }
        }

        var byInterval = new SortedDictionary<long, HashSet<int>>();
        for (var i = 0; i < n; i++)
        {
            if (best[i] == long.MaxValue) continue;
            var interval = Math.Max(best[i], _sensors[i].MinInterval.Ticks);
            interval = Math.Max(BaseTickTicks, (interval + BaseTickTicks - 1) / BaseTickTicks * BaseTickTicks);
            if (!byInterval.TryGetValue(interval, out var set)) byInterval[interval] = set = [];
            set.Add(i);
            // a computed sensor needs its inputs read in the same tick
            if (_sources[_sourceOf[i]] is IDependentSource dep)
                foreach (var input in dep.InputsOf(_sensors[i]))
                    if ((uint)input < (uint)n) set.Add(input);
        }

        var old = _groups.ToDictionary(g => g.IntervalTicks, g => g.NextDue);
        _groups = byInterval.Select(kv => new Group
        {
            IntervalTicks = kv.Key,
            // keep the phase of an existing group; a new one starts at the next grid slot (first read soon)
            NextDue = old.TryGetValue(kv.Key, out var due) ? due : Math.Min(AlignNext(now, kv.Key), AlignNext(now, BaseTickTicks)),
            // dependents last inside a group is not needed: sources are updated in order, dependents after the others
            Indices = kv.Value.OrderBy(x => x).ToArray(),
        }).ToArray();
    }

    private void UpdateDemand(DemandEntry demand, IEnumerable<string> ids, TimeSpan interval)
    {
        var arr = ids.Distinct(StringComparer.Ordinal).ToArray();
        lock (_gate)
        {
            demand.Ids = arr;
            demand.IntervalTicks = Math.Max(BaseTickTicks, interval.Ticks);
        }
        _demandDirty = true;
        Wake();
    }

    private void RemoveDemand(DemandEntry demand)
    {
        lock (_gate)
        {
            if (!_demands.Remove(demand)) return;
        }
        _demandDirty = true;
        Wake();
    }

    // ───────────────────────── ISensorHub ─────────────────────────

    public IReadOnlyList<HardwareDescriptor> Hardware => Volatile.Read(ref _hardware);

    public IReadOnlyList<SensorDescriptor> Sensors => Volatile.Read(ref _sensors);

    public bool IsPresent(int index)
    {
        var p = Volatile.Read(ref _present);
        return (uint)index < (uint)p.Length && p[index];
    }

    public SensorDescriptor? Find(string sensorId)
    {
        var sensors = Volatile.Read(ref _sensors);
        return Volatile.Read(ref _lookup).TryGetValue(sensorId, out var i) && i < sensors.Length ? sensors[i] : null;
    }

    public HardwareDescriptor? FindHardware(string hardwareId)
        => Volatile.Read(ref _hardwareLookup).TryGetValue(hardwareId, out var h) ? h : null;

    public SensorFrame Latest => Volatile.Read(ref _latest);

    public SessionStats Stats { get; } = new();

    public IDemand Demand(string consumer, IEnumerable<string> sensorIds, TimeSpan interval)
    {
        var d = new DemandEntry(this, consumer, sensorIds.Distinct(StringComparer.Ordinal).ToArray(), Math.Max(BaseTickTicks, interval.Ticks));
        lock (_gate) _demands.Add(d);
        _demandDirty = true;
        Wake();
        return d;
    }

    public event Action<SensorFrame>? FrameReady
    {
        add { if (value is not null) lock (_gate) _frameHandlers = [.. _frameHandlers, value]; }
        remove { if (value is not null) lock (_gate) _frameHandlers = _frameHandlers.Where(h => h != value).ToArray(); }
    }

    public event Action? InventoryChanged
    {
        add { if (value is not null) lock (_gate) _inventoryHandlers = [.. _inventoryHandlers, value]; }
        remove { if (value is not null) lock (_gate) _inventoryHandlers = _inventoryHandlers.Where(h => h != value).ToArray(); }
    }

    public IReadOnlyList<(string Source, SourceStatus Status)> SourceStatuses
        => _sources.Select(s => (s.Name, SafeStatus(s))).ToArray();

    /// <summary>Who asks for what (diagnostics / perf reports).</summary>
    public IReadOnlyList<(string Consumer, int Sensors, TimeSpan Interval)> Demands
    {
        get
        {
            lock (_gate) return _demands.Select(d => (d.Consumer, d.Ids.Length, TimeSpan.FromTicks(d.IntervalTicks))).ToArray();
        }
    }

    /// <summary>Active schedule (interval → sensors read), for diagnostics and tests.</summary>
    internal IReadOnlyList<(TimeSpan Interval, int Count)> Schedule => _groups.Select(g => (TimeSpan.FromTicks(g.IntervalTicks), g.Indices.Length)).ToArray();

    // ───────────────────────── test hooks ─────────────────────────

    /// <summary>Tests drive the engine synchronously instead of <see cref="Start"/>: discover now.</summary>
    internal void DiscoverNow() => SafeRediscover();

    /// <summary>Tests: apply pending demand changes now.</summary>
    internal void ApplyDemandNow() => RecomputeSchedule(_utcNow());

    private static SourceStatus SafeStatus(ISensorSource s)
    {
        try { return s.Status; }
        catch { return new SourceStatus(SourceState.Unavailable); }
    }

    private static void Safe(ISensorSource s, Action<ISensorSource> action)
    {
        try { action(s); }
        catch (Exception ex) { Log.Warn($"engine: {s.Name}: {ex.Message}"); }
    }
}
