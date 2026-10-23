using System.Diagnostics;
using System.Reflection;
using BetterVitals.Core;
using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Power;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Gpu;
using LhmStorageDevice = LibreHardwareMonitor.Hardware.Storage.StorageDevice;

namespace BetterVitals.Sensors.Lhm;

/// <summary>
/// Sensor source over LibreHardwareMonitorLib (CPU, GPU incl. the RTX 50 "GPU Hot Spot", mainboard SuperIO / EC, DIMMs,
/// drives, network, battery, PSU, coolers, controllers).
/// <para>
/// Threading: <see cref="Discover"/>, <see cref="Update"/>, <see cref="Suspend"/>, <see cref="Resume"/>,
/// <see cref="Dispose"/> and the diagnostics run on the engine thread only (LHM objects never leave it).
/// <see cref="SetDriverPaused"/> and <see cref="NotifyDeviceChange"/> may be called from any thread.
/// </para>
/// <para>
/// Hot path: <see cref="Update"/> refreshes only the hardware nodes owning requested sensors, once each, through a plan
/// cached per distinct request; sleeping laptop dGPUs and drives are never touched (see <c>Power/</c>).
/// </para>
/// </summary>
public sealed class LhmSource : ISensorSource
{
    /// <summary>Source name used in descriptors and statuses.</summary>
    public const string SourceName = "lhm";

    private static readonly TimeSpan ChangeDebounce = TimeSpan.FromSeconds(2);

    /// <summary>DiskInfoToolkit's removable-media poll (its default: 1 s).</summary>
    internal static readonly TimeSpan MediaWatchDelay = TimeSpan.FromSeconds(30);

    private readonly Debouncer _debouncer;
    private readonly Dictionary<IHardware, LhmNode> _nodesByHardware = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ISensor> _knownSensors = new(ReferenceEqualityComparer.Instance);
    private readonly LhmPlanCache<LhmUpdatePlan> _plans = new();
    private readonly SensorEventHandler _onSensorAdded;
    private readonly HardwareEventHandler _onHardwareChanged;

    private Computer? _computer;
    private LhmNode[] _nodes = [];
    private Dictionary<string, LhmSensorEntry> _entries = new(StringComparer.Ordinal);
    private SourceInventory _inventory = SourceInventory.Empty;

    // Driver-backed hardware / sensors from the last discovery with the driver active (copies without LHM references):
    // while paused they stay in the inventory as ghosts (same ids, NaN values).
    private List<HardwareDescriptor> _ghostHardware = [];
    private List<SensorDescriptor> _ghostSensors = [];

    private int _generation;
    private bool _openFailed;
    private bool _pausedApplied;
    private bool _unpauseSignaled;
    private bool _suspended;
    private bool _updateFailureLogged;
    private bool? _deviceOpens;
    private bool _driverWorks;
    private long _lhmAllocThisCall;
    private volatile bool _pauseWanted;
    private volatile bool _deviceChangePending;
    private volatile bool _reopenPending;
    private volatile bool _suppressEvents;
    private volatile bool _disposed;
    private volatile StatusBox _status = new(new SourceStatus(SourceState.Idle));
    private volatile Version? _pawnIoVersion;

    public LhmSource()
    {
        _onSensorAdded = OnSensorAdded;
        _onHardwareChanged = OnHardwareChanged;
        _debouncer = new Debouncer(ChangeDebounce, RaiseInventoryChanged);
        IsElevated = LhmDriverStatus.IsElevated();
        _pawnIoVersion = LhmDriverStatus.DetectPawnIo();
    }

    /// <inheritdoc />
    public string Name => SourceName;

    /// <inheritdoc />
    public SourceStatus Status => _status.Value;

    /// <summary>Installed PawnIO version ("2.2.0"), null when PawnIO is not installed. Re-read at every (re)open.</summary>
    public Version? PawnIoVersion => _pawnIoVersion;

    /// <summary>The process runs elevated (PawnIO opens only for administrators).</summary>
    public bool IsElevated { get; }

    /// <summary>The driver pause was requested (anti-cheat game running).</summary>
    public bool IsDriverPaused => _pauseWanted;

    /// <inheritdoc />
    public event Action? InventoryChanged;

    // ── diagnostics (engine thread) ──

    /// <summary>Duration of the last <see cref="Discover"/> (includes opening LHM the first time).</summary>
    public TimeSpan LastDiscoverDuration { get; private set; }

    /// <summary>Managed bytes allocated on the engine thread by the last <see cref="Update"/>, LHM included.</summary>
    public long LastUpdateAllocatedBytes { get; private set; }

    /// <summary>Part of <see cref="LastUpdateAllocatedBytes"/> allocated inside LHM's <c>IHardware.Update</c>.</summary>
    public long LastUpdateLhmAllocatedBytes { get; private set; }

    /// <summary>Update plans built so far (one per distinct request content and inventory generation).</summary>
    public int PlanBuilds => _plans.Builds;

    /// <summary>PawnIO module handles closed by the last driver pause (reflection on GPU nodes).</summary>
    public int LastPauseClosedModules { get; private set; }

    /// <summary>
    /// Releases (true) or re-acquires (false) everything that holds PawnIO. Paused: CPU / mainboard / DIMM / Intel iGPU
    /// hardware and the NVIDIA PawnIO sensors keep their ids but read NaN, <see cref="Status"/> = Paused; GPUs (NVAPI /
    /// ADL / IGCL), drives, network, battery, memory usage keep working. Any thread: raises <see cref="InventoryChanged"/>;
    /// the release happens on the engine thread at the next <see cref="Update"/> or <see cref="Discover"/>, whichever comes
    /// first; resuming re-opens LHM in the next <see cref="Discover"/>.
    /// </summary>
    public void SetDriverPaused(bool paused)
    {
        if (_pauseWanted == paused) return;
        _pauseWanted = paused;
        RaiseInventoryChanged();
    }

    /// <summary>
    /// Call on WM_DEVICECHANGE. Debounced (2 s); then <see cref="InventoryChanged"/> fires and the next
    /// <see cref="Discover"/> re-enumerates hot-pluggable groups (USB coolers / controllers / PSUs / power monitors).
    /// Drives are tracked by LHM itself.
    /// </summary>
    public void NotifyDeviceChange()
    {
        _deviceChangePending = true;
        _debouncer.Signal();
    }

    /// <inheritdoc />
    public SourceInventory Discover()
    {
        if (_disposed) return SourceInventory.Empty;
        var sw = Stopwatch.StartNew();
        try
        {
            ApplyPendingChanges();
            if (_computer is null)
            {
                _inventory = SourceInventory.Empty;
                ResetTables();
            }
            else
            {
                RebuildWithRetry(warmUp: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error("lhm: discovery failed", ex);
        }
        finally
        {
            LastDiscoverDuration = sw.Elapsed;
            UpdateStatus();
        }
        return _inventory;
    }

    /// <inheritdoc />
    public void Update(ReadOnlySpan<SensorDescriptor> requested, Span<float> values)
    {
        var alloc0 = GC.GetAllocatedBytesForCurrentThread();
        _lhmAllocThisCall = 0;
        try
        {
            if (!_disposed && _pauseWanted != _pausedApplied) OnPauseChangeInUpdate();
            if (requested.IsEmpty) return;
            if (_disposed || _suspended || _computer is null)
            {
                FillNaN(requested, values);
                return;
            }

            var plan = _plans.Find(requested, _generation);
            if (plan is null)
            {
                plan = BuildPlan(requested);
                _plans.Add(requested, _generation, plan);
            }

            var now = Stopwatch.GetTimestamp();
            var nodes = plan.Nodes;
            var needs = plan.Needs;
            for (var i = 0; i < nodes.Length; i++) nodes[i].Readable = RefreshNode(nodes[i], needs[i], now);

            // paused: PawnIO sensors are NaN; driver not working: also LHM's fake zeros of driver-only sensors
            var blocked = _pausedApplied
                ? LhmSensorFlags.DriverBacked | LhmSensorFlags.NeedsDriver
                : _driverWorks ? LhmSensorFlags.None : LhmSensorFlags.NeedsDriver;
            var entries = plan.Entries;
            for (var i = 0; i < requested.Length; i++)
            {
                var slot = requested[i].Index;
                if ((uint)slot < (uint)values.Length) values[slot] = Read(entries[i], blocked);
            }
        }
        catch (Exception ex)
        {
            if (!_updateFailureLogged)
            {
                _updateFailureLogged = true;
                Log.Error("lhm: update failed", ex);
            }
            FillNaN(requested, values);
        }
        finally
        {
            LastUpdateAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - alloc0;
            LastUpdateLhmAllocatedBytes = _lhmAllocThisCall;
        }
    }

    /// <inheritdoc />
    public void Suspend() => _suspended = true;

    /// <inheritdoc />
    public void Resume()
    {
        _suspended = false;
        _reopenPending = true;
    }

    /// <summary>Per-node diagnostics of the current inventory (engine thread; allocates).</summary>
    public IReadOnlyList<LhmNodeStats> GetNodeStats()
    {
        var freq = (double)Stopwatch.Frequency;
        var list = new List<LhmNodeStats>(_nodes.Length);
        foreach (var n in _nodes)
        {
            list.Add(new LhmNodeStats(
                n.Descriptor.Id, n.Descriptor.Name, n.Kind, n.Updates, n.TotalTicks * 1000 / freq, n.MaxTicks * 1000 / freq,
                n.AllocatedBytes, n.SkippedAsleep, n.Failures, n.Gate?.Target, n.Gate?.Last,
                (n.Gate as DiskPowerGate)?.IncursSeekPenalty, n.DriverBacked, n.Hardware is null));
        }
        return list;
    }

    /// <summary>Closes LHM (releases PawnIO, NVAPI, HID…). Engine thread.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debouncer.Dispose();
        CloseComputer();
    }

    // ───────────────────────────── open / close / pause ─────────────────────────────

    private void ApplyPendingChanges()
    {
        var wantPaused = _pauseWanted;
        if (_computer is null || _reopenPending || (_pausedApplied && !wantPaused))
        {
            Reopen(wantPaused);
            return;
        }
        if (wantPaused && !_pausedApplied) ApplyPause();
        if (_deviceChangePending)
        {
            _deviceChangePending = false;
            ResetHotPlugGroups();
        }
    }

    private void Reopen(bool paused)
    {
        _reopenPending = false;
        _deviceChangePending = false;
        CloseComputer();
        _pawnIoVersion = LhmDriverStatus.DetectPawnIo();
        if (paused) LhmDriverRelease.BlockSpdDriver();

        var computer = new Computer
        {
            IsCpuEnabled = !paused,
            IsMotherboardEnabled = !paused,
            IsMemoryEnabled = true,
            IsGpuEnabled = true,
            IsStorageEnabled = true,
            IsNetworkEnabled = true,
            IsBatteryEnabled = true,
            IsControllerEnabled = true,
            IsPsuEnabled = true,
            IsPowerMonitorEnabled = true,
        };
        // DiskInfoToolkit (LHM's storage backend) polls removable media every second on its own thread; card-reader media
        // also raises WM_DEVICECHANGE (→ NotifyDeviceChange), so a slow poll only stays as a safety net
        DiskInfoToolkit.Storage.MediaWatchLoopDelay = MediaWatchDelay;
        _suppressEvents = true;
        try
        {
            computer.Open();
            if (paused) LastPauseClosedModules = LhmDriverRelease.ClosePawnIoModules(computer.Hardware);
            LhmGpuTweaks.StopNvidiaPolling(computer);
        }
        catch (Exception ex)
        {
            Log.Error("lhm: opening LibreHardwareMonitor failed", ex);
            _openFailed = true;
            try
            {
                computer.Close();
            }
            catch
            {
                // best effort
            }
            return;
        }
        finally
        {
            _suppressEvents = false;
        }

        LhmGpuTweaks.InstallPcieGate();
        LhmGpuTweaks.InstallPowerTopologyGate();
        computer.HardwareAdded += _onHardwareChanged;
        computer.HardwareRemoved += _onHardwareChanged;
        _computer = computer;
        _openFailed = false;
        _pausedApplied = paused;
        _unpauseSignaled = false;
        _deviceOpens = !paused && IsElevated && _pawnIoVersion is not null ? LhmDriverStatus.CanOpenDevice() : null;
        UpdateStatus(); // _driverWorks decides how the discovery warm-up updates CPU nodes
    }

    /// <summary>Fast part of the pause (safe inside Update): drop the driver groups and close every remaining PawnIO handle.</summary>
    private void ApplyPause()
    {
        var c = _computer;
        if (c is null) return;
        _suppressEvents = true;
        try
        {
            c.IsCpuEnabled = false;
            c.IsMotherboardEnabled = false;
            c.IsMemoryEnabled = false;
            LhmDriverRelease.BlockSpdDriver();
            c.IsMemoryEnabled = true; // Total / Virtual Memory without DIMMs
            LastPauseClosedModules = LhmDriverRelease.ClosePawnIoModules(c.Hardware);
        }
        catch (Exception ex)
        {
            Log.Error("lhm: driver pause failed", ex);
        }
        finally
        {
            _suppressEvents = false;
        }
        _pausedApplied = true;
        _unpauseSignaled = false;
        _deviceOpens = null;
    }

    private void OnPauseChangeInUpdate()
    {
        if (_pauseWanted && !_pausedApplied)
        {
            if (_computer is null) return;
            ApplyPause();
            RebuildWithRetry(warmUp: false);
            UpdateStatus();
            RaiseInventoryChanged();
        }
        else if (!_pauseWanted && _pausedApplied && !_unpauseSignaled)
        {
            // re-opening is slow: leave it to Discover
            _unpauseSignaled = true;
            RaiseInventoryChanged();
        }
    }

    private void ResetHotPlugGroups()
    {
        var c = _computer;
        if (c is null) return;
        _suppressEvents = true;
        try
        {
            c.IsControllerEnabled = false;
            c.IsControllerEnabled = true;
            c.IsPsuEnabled = false;
            c.IsPsuEnabled = true;
            c.IsPowerMonitorEnabled = false;
            c.IsPowerMonitorEnabled = true;
            LhmGpuTweaks.RefreshNvidiaGroups(c); // GPU added / removed, driver restarted (instead of LHM's 1 s poll)
        }
        catch (Exception ex)
        {
            Log.Warn($"lhm: re-enumerating hot-plug groups failed: {ex.Message}");
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void CloseComputer()
    {
        var c = _computer;
        _computer = null;
        foreach (var hw in _nodesByHardware.Keys)
        {
            try
            {
                hw.SensorAdded -= _onSensorAdded;
            }
            catch
            {
                // closed hardware
            }
        }
        _nodesByHardware.Clear();
        ResetTables();
        if (c is null) return;
        _suppressEvents = true;
        try
        {
            c.HardwareAdded -= _onHardwareChanged;
            c.HardwareRemoved -= _onHardwareChanged;
            c.Close();
        }
        catch (Exception ex)
        {
            Log.Warn($"lhm: closing LibreHardwareMonitor failed: {ex.Message}");
        }
        finally
        {
            _suppressEvents = false;
        }
        LhmDriverRelease.UnloadSpdDriver();
    }

    private void ResetTables()
    {
        _knownSensors.Clear();
        _nodes = [];
        _entries = new Dictionary<string, LhmSensorEntry>(StringComparer.Ordinal);
        _generation++;
        _plans.Clear();
    }

    // ───────────────────────────── discovery ─────────────────────────────

    private void RebuildWithRetry(bool warmUp)
    {
        // LHM groups can change their lists from background threads (drive hot-plug, DIMM retry): retry once.
        try
        {
            Rebuild(warmUp);
        }
        catch (InvalidOperationException)
        {
            Rebuild(warmUp);
        }
    }

    private void Rebuild(bool warmUp)
    {
        var c = _computer!;
        var walk = new List<(IHardware Hw, int Parent)>();
        foreach (var hw in c.Hardware) Walk(hw, -1, walk);

        var alive = new HashSet<IHardware>(ReferenceEqualityComparer.Instance);
        var nodes = new List<LhmNode>(walk.Count);
        List<string>? displayIds = null;
        foreach (var (hw, _) in walk)
        {
            if (!_nodesByHardware.TryGetValue(hw, out var node))
            {
                node = CreateNode(hw, ref displayIds);
                _nodesByHardware[hw] = node;
                hw.SensorAdded += _onSensorAdded;
            }
            nodes.Add(node);
            alive.Add(hw);
        }
        foreach (var stale in _nodesByHardware.Keys.Where(h => !alive.Contains(h)).ToList())
        {
            stale.SensorAdded -= _onSensorAdded;
            _nodesByHardware.Remove(stale);
        }

        if (warmUp) WarmUp(nodes);

        // hardware ids
        var keys = new List<HardwareKey>(nodes.Count);
        for (var i = 0; i < nodes.Count; i++)
            keys.Add(new HardwareKey(walk[i].Parent, nodes[i].Kind, walk[i].Hw.Name, nodes[i].StableKey, i));
        var hwIds = LhmIds.HardwareIds(keys);

        var hardware = new List<HardwareDescriptor>(nodes.Count);
        var sensors = new List<SensorDescriptor>(256);
        var entries = new Dictionary<string, LhmSensorEntry>(StringComparer.Ordinal);
        var ghostHardware = new List<HardwareDescriptor>();
        var ghostSensors = new List<SensorDescriptor>();
        _knownSensors.Clear();

        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var hw = walk[i].Hw;
            node.Descriptor = new HardwareDescriptor
            {
                Id = hwIds[i],
                Name = hw.Name,
                Kind = node.Kind,
                ParentId = walk[i].Parent < 0 ? null : hwIds[walk[i].Parent],
                Source = SourceName,
            };
            hardware.Add(node.Descriptor);
            if (node.DriverBacked) ghostHardware.Add(node.Descriptor);
            AddSensors(node, hw, sensors, entries, ghostSensors);
        }

        if (_pausedApplied)
        {
            AddGhosts(hardware, sensors, entries, nodes);
        }
        else
        {
            _ghostHardware = ghostHardware;
            _ghostSensors = ghostSensors;
        }

        _nodes = nodes.ToArray();
        _entries = entries;
        _inventory = new SourceInventory(hardware, sensors);
        _generation++;
        _plans.Clear();
    }

    private void AddSensors(LhmNode node, IHardware hw, List<SensorDescriptor> sensors, Dictionary<string, LhmSensorEntry> entries,
        List<SensorDescriptor> ghostSensors)
    {
        var raw = hw.Sensors;
        var mapped = new List<(ISensor Sensor, SensorKind Kind)>(raw.Length);
        foreach (var s in raw)
        {
            _knownSensors.Add(s);
            try
            {
                s.ValuesTimeWindow = TimeSpan.Zero; // LHM keeps a 24 h value history per sensor otherwise
            }
            catch
            {
                // not supported by this sensor implementation
            }
            if (LhmClassifier.MapSensor(s.SensorType) is { } kind) mapped.Add((s, kind));
        }
        mapped.Sort((a, b) =>
        {
            var c = a.Kind.CompareTo(b.Kind);
            return c != 0 ? c : a.Sensor.Index.CompareTo(b.Sensor.Index);
        });

        var keys = new List<SensorKey>(mapped.Count);
        foreach (var (s, kind) in mapped) keys.Add(new SensorKey(kind, s.Name, s.Index));
        var ids = LhmIds.SensorIdsFor(node.Descriptor.Id, keys);

        for (var j = 0; j < mapped.Count; j++)
        {
            var (s, kind) = mapped[j];
            var flags = LhmClassifier.Classify(node.Type, hw.Name, s.SensorType, s.Name);
            if (LhmClassifier.NeedsDriver(node.Type, node.Identifier, s.SensorType)) flags |= LhmSensorFlags.NeedsDriver;
            var d = new SensorDescriptor
            {
                Id = ids[j],
                HardwareId = node.Descriptor.Id,
                Name = s.Name,
                Kind = kind,
                HardwareKind = node.Kind,
                MinInterval = LhmClassifier.MinInterval(node.Type, s.SensorType, flags),
                Order = s.Index,
                NoteKey = LhmClassifier.NoteKey(node.Type, hw.Name, s.SensorType, s.Name),
                SourceTag = s,
            };
            if (!entries.TryAdd(d.Id, new LhmSensorEntry(s, node, LhmClassifier.Scale(s.SensorType), flags, d))) continue;
            sensors.Add(d);
            if (node.DriverBacked || (flags & LhmSensorFlags.DriverBacked) != 0) ghostSensors.Add(CopyWithoutTag(d));
        }
    }

    /// <summary>While paused: keep driver-backed hardware and sensors from the last full inventory (NaN, same ids).</summary>
    private void AddGhosts(List<HardwareDescriptor> hardware, List<SensorDescriptor> sensors, Dictionary<string, LhmSensorEntry> entries,
        List<LhmNode> nodes)
    {
        var nodeById = new Dictionary<string, LhmNode>(StringComparer.Ordinal);
        foreach (var n in nodes) nodeById[n.Descriptor.Id] = n;

        foreach (var h in _ghostHardware)
        {
            if (nodeById.ContainsKey(h.Id)) continue;
            var ghost = new LhmNode(null, HardwareType.Cpu, h.Kind, h.Id, driverBacked: true, gate: null) { Descriptor = h };
            hardware.Add(h);
            nodes.Add(ghost);
            nodeById[h.Id] = ghost;
        }
        foreach (var s in _ghostSensors)
        {
            if (entries.ContainsKey(s.Id) || !nodeById.TryGetValue(s.HardwareId, out var node)) continue;
            var d = CopyWithoutTag(s);
            entries[d.Id] = new LhmSensorEntry(null, node, 1f, LhmSensorFlags.DriverBacked, d);
            sensors.Add(d);
        }
    }

    private static SensorDescriptor CopyWithoutTag(SensorDescriptor s) => new()
    {
        Id = s.Id,
        HardwareId = s.HardwareId,
        Name = s.Name,
        Kind = s.Kind,
        HardwareKind = s.HardwareKind,
        Unit = s.Unit,
        MinInterval = s.MinInterval,
        Order = s.Order,
        NoteKey = s.NoteKey,
    };

    private static void Walk(IHardware hw, int parent, List<(IHardware, int)> list)
    {
        if (hw.HardwareType == HardwareType.Network && LhmClassifier.IsNetworkFilterPseudoAdapter(hw.Name)) return;
        list.Add((hw, parent));
        var self = list.Count - 1;
        foreach (var sub in hw.SubHardware) Walk(sub, self, list);
    }

    private void WarmUp(List<LhmNode> nodes)
    {
        var now = Stopwatch.GetTimestamp();
        _suppressEvents = true;
        try
        {
            foreach (var node in nodes)
            {
                if (node.WarmedUp) continue;
                if (RefreshNode(node, LhmSensorFlags.AllNeeds, now)) node.WarmedUp = true;
            }
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private LhmNode CreateNode(IHardware hw, ref List<string>? displayIds)
    {
        var type = hw.HardwareType;
        var identifier = hw.Identifier.ToString();
        PowerGate? gate = null;
        var stableKey = identifier;
        try
        {
            switch (hw)
            {
                case GenericGpu gpu:
                {
                    var instance = ResolveGpuInstanceId(gpu, type, ref displayIds);
                    var pnp = PnpPowerGate.TryCreate(instance);
                    gate = pnp;
                    stableKey = pnp?.InstanceId.ToUpperInvariant() ?? NonEmpty(gpu.DeviceId) ?? identifier;
                    break;
                }
                case LhmStorageDevice storage:
                {
                    var dit = storage.Storage;
                    gate = DiskPowerGate.TryCreate(dit?.StorageDeviceNumber, dit?.DevicePath);
                    stableKey = NonEmpty(dit?.SerialNumber?.Trim()) ?? NonEmpty(dit?.DeviceInstanceID) ?? identifier;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"lhm: identifying '{hw.Name}' failed: {ex.Message}");
        }
        var node = new LhmNode(hw, type, LhmClassifier.MapHardware(type), stableKey, LhmClassifier.IsDriverBackedHardware(type, identifier), gate)
        {
            Identifier = identifier,
            Dit = (hw as LhmStorageDevice)?.Storage,
        };
        try
        {
            node.D3dField = LhmGpuTweaks.D3dField(hw, out var d3dId);
            node.D3dId = d3dId;
        }
        catch (Exception ex)
        {
            Log.Warn($"lhm: D3D gate unavailable for '{hw.Name}': {ex.Message}");
        }
        return node;
    }

    /// <summary>LHM GPU → PnP device instance id: DeviceId, the private D3D id, or a unique VEN/DEV match among display devnodes.</summary>
    private static string? ResolveGpuInstanceId(GenericGpu gpu, HardwareType type, ref List<string>? displayIds)
    {
        var candidates = new List<string>(2);
        if (NonEmpty(gpu.DeviceId) is { } deviceId) candidates.Add(deviceId);
        if (gpu.GetType().GetField("_d3dDeviceId", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(gpu) is string d3d && d3d.Length > 0)
            candidates.Add(d3d);

        foreach (var c in candidates)
        {
            if (PnpIds.NormalizeInstanceId(c) is { } id && PnpPowerGate.TryCreate(id) is not null) return id;
        }

        foreach (var c in candidates)
        {
            if (!PnpIds.TryParseVenDev(c, out var vendor, out var device)) continue;
            vendor ??= type switch
            {
                HardwareType.GpuNvidia => "10DE",
                HardwareType.GpuAmd => "1002",
                HardwareType.GpuIntel => "8086",
                _ => null,
            };
            displayIds ??= PowerNative.GetPresentDeviceIds(PowerNative.DisplayClassGuid);
            if (PnpIds.FindUnique(displayIds, vendor, device!) is { } match) return match;
        }
        return null;
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // ───────────────────────────── hot path ─────────────────────────────

    private LhmUpdatePlan BuildPlan(ReadOnlySpan<SensorDescriptor> requested)
    {
        var entries = new LhmSensorEntry?[requested.Length];
        var nodes = new List<LhmNode>();
        var needs = new List<LhmSensorFlags>();
        for (var i = 0; i < requested.Length; i++)
        {
            if (!_entries.TryGetValue(requested[i].Id, out var e)) continue;
            entries[i] = e;
            var at = nodes.IndexOf(e.Node);
            if (at < 0)
            {
                at = nodes.Count;
                nodes.Add(e.Node);
                needs.Add(LhmSensorFlags.None);
            }
            needs[at] |= e.Flags & LhmSensorFlags.AllNeeds;
        }
        return new LhmUpdatePlan(nodes.ToArray(), needs.ToArray(), entries);
    }

    /// <summary>Refreshes one node unless it is a ghost, paused, or asleep. Returns whether its sensors are readable.</summary>
    private bool RefreshNode(LhmNode node, LhmSensorFlags needs, long now)
    {
        var hw = node.Hardware;
        if (hw is null) return false;
        if (_pausedApplied && node.DriverBacked) return false;
        if (node.Gate is { } gate && !gate.IsAwake(now))
        {
            node.SkippedAsleep++;
            return false;
        }

        // SMART only when a SMART-backed sensor of this drive is requested (LHM otherwise reads it on every update).
        if (node.IsStorage) LhmStorageDevice.SmartUpdateCycleCount = (needs & LhmSensorFlags.NeedsSmart) != 0 ? 1u : uint.MaxValue;
        if (node.Type == HardwareType.GpuNvidia)
        {
            LhmGpuTweaks.PcieWanted = (needs & LhmSensorFlags.NeedsPcie) != 0;
            LhmGpuTweaks.PowerTopologyWanted = (needs & LhmSensorFlags.NeedsPowerTopology) != 0;
        }
        var skipD3d = node.D3dField is not null && (needs & LhmSensorFlags.NeedsD3d) == 0;

        var a0 = GC.GetAllocatedBytesForCurrentThread();
        var t0 = Stopwatch.GetTimestamp();
        try
        {
            if (skipD3d) node.D3dField!.SetValue(hw, null);
            // without a working driver only the CPU loads are real (the rest reads NaN): skip LHM's per-core MSR walk
            if (!(node.Type == HardwareType.Cpu && !_driverWorks && LhmCpuTweaks.UpdateLoadsOnly(hw))) hw.Update();
            if (node.Dit is { } dit) node.SmartMissing = dit.SmartAttributes is not { Count: > 0 };
            return true;
        }
        catch (Exception ex)
        {
            node.Failures++;
            if (!node.FailureLogged)
            {
                node.FailureLogged = true;
                Log.Warn($"lhm: updating '{hw.Name}' failed: {ex.GetType().Name}: {ex.Message}");
            }
            return false;
        }
        finally
        {
            if (skipD3d) node.D3dField!.SetValue(hw, node.D3dId);
            var dt = Stopwatch.GetTimestamp() - t0;
            var da = GC.GetAllocatedBytesForCurrentThread() - a0;
            node.Updates++;
            node.TotalTicks += dt;
            if (dt > node.MaxTicks) node.MaxTicks = dt;
            node.AllocatedBytes += da;
            _lhmAllocThisCall += da;
        }
    }

    private static float Read(LhmSensorEntry? e, LhmSensorFlags blocked)
    {
        if (e?.Sensor is not { } s || !e.Node.Readable) return float.NaN;
        if ((e.Flags & blocked) != 0) return float.NaN;
        if ((e.Flags & LhmSensorFlags.SmartCounter) != 0 && e.Node.SmartMissing) return float.NaN;
        return LhmClassifier.Sanitize(s.Value, e.Scale, (e.Flags & LhmSensorFlags.HotSpotLike) != 0);
    }

    private static void FillNaN(ReadOnlySpan<SensorDescriptor> requested, Span<float> values)
    {
        foreach (var d in requested)
            if ((uint)d.Index < (uint)values.Length) values[d.Index] = float.NaN;
    }

    // ───────────────────────────── events / status ─────────────────────────────

    private void OnSensorAdded(ISensor sensor)
    {
        // LHM activates sensors lazily (hot spot after the first valid read, D3D loads…) and re-activates flapping ones.
        if (_suppressEvents || _knownSensors.Contains(sensor)) return;
        _debouncer.Signal();
    }

    private void OnHardwareChanged(IHardware hardware)
    {
        if (_suppressEvents || _disposed) return;
        _debouncer.Signal();
    }

    private void RaiseInventoryChanged()
    {
        if (_disposed) return;
        try
        {
            InventoryChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("lhm: InventoryChanged handler failed", ex);
        }
    }

    private void UpdateStatus()
    {
        _driverWorks = IsElevated && _pawnIoVersion is not null && _deviceOpens != false;
        var s = LhmDriverStatus.Compute(_openFailed, _pausedApplied, _pawnIoVersion, IsElevated, _deviceOpens);
        if (s != _status.Value) _status = new StatusBox(s);
    }

    /// <summary>Immutable holder so <see cref="Status"/> is read atomically from any thread.</summary>
    private sealed record StatusBox(SourceStatus Value);
}
