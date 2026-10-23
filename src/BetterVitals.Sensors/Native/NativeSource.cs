using System.Diagnostics;
using BetterVitals.Core;
using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Engine;
using BetterVitals.Sensors.Native.Cpu;
using BetterVitals.Sensors.Native.Drives;
using BetterVitals.Sensors.Native.Gpu;
using BetterVitals.Sensors.Native.Memory;
using BetterVitals.Sensors.Native.Network;

namespace BetterVitals.Sensors.Native;

/// <summary>
/// The gap filler ("basic mode" source): native Windows APIs that need no administrator rights and no driver. It adds only
/// what the other sources do not already have on this PC right now — the engine hands it everything upstream registered
/// (<see cref="SetUpstream"/>) before each <see cref="Discover"/>, and the gaps are re-checked every time:
/// drive temperatures (StorageDeviceTemperatureProperty), RAM speed and modules (SMBIOS), GPU basics (D3DKMT perf data +
/// PDH counters), and — only if upstream has no CPU / network at all — CPU load and network throughput.
/// Hardware ids are built like LibreHardwareMonitor's (same names), so they line up between basic and full mode.
/// <see cref="Update"/> reads only the hardware that one of the requested sensors belongs to, allocates nothing and never
/// throws; sleeping rotating drives and sleeping dGPUs are not touched (NaN).
/// </summary>
public sealed class NativeSource : IDependentSource
{
    private static readonly long RediscoverSpacing = Stopwatch.Frequency * 60;

    private readonly List<INativeModule> _modules;
    private readonly MemoryModule? _memory;
    private UpstreamIndex _upstream = UpstreamIndex.Empty;
    private SourceStatus _status = new(SourceState.Idle);
    private int _stamp;
    private long _lastRediscoverRequest;
    private long _lastErrorLog;

    /// <summary>Creates the source with every area enabled.</summary>
    public NativeSource() : this(new NativeSourceOptions())
    {
    }

    /// <summary>Creates the source with the areas chosen in <paramref name="options"/>.</summary>
    public NativeSource(NativeSourceOptions options)
    {
        _modules = [];
        if (options.Drives) _modules.Add(new DriveTemperatureModule());
        if (options.Memory)
        {
            _memory = new MemoryModule(usageFallback: options.MemoryUsageFallback);
            _modules.Add(_memory);
        }
        if (options.Gpu) _modules.Add(new GpuModule(options.PowerGate ?? new CmDevicePowerGate(), RequestRediscover));
        if (options.Cpu) _modules.Add(new CpuLoadModule());
        if (options.Network) _modules.Add(new NetworkModule());
    }

    /// <summary>Tests: build the source from prepared modules (fake probes instead of the OS).</summary>
    internal NativeSource(IEnumerable<INativeModule> modules)
    {
        _modules = [.. modules];
        _memory = _modules.OfType<MemoryModule>().FirstOrDefault();
    }

    /// <inheritdoc />
    public string Name => "native";

    /// <inheritdoc />
    public SourceStatus Status => _status;

    /// <summary>
    /// Raised (at most once a minute) when the source learns something that needs a new <see cref="Discover"/>: a GPU that was
    /// asleep at discovery woke up and reported its sensors, or an adapter handle could not be reopened.
    /// </summary>
    public event Action? InventoryChanged;

    /// <summary>
    /// Populated memory slots from SMBIOS (count, type, manufacturer, part number, capacity, speeds) as of the last
    /// <see cref="Discover"/>; empty before it or when the firmware gives nothing. Safe to read from any thread.
    /// </summary>
    public IReadOnlyList<MemoryModuleInfo> MemoryModules => _memory?.Modules ?? [];

    /// <inheritdoc />
    public void SetUpstream(IReadOnlyList<SensorDescriptor> upstream) => _upstream = new UpstreamIndex(upstream);

    /// <inheritdoc />
    public ReadOnlySpan<int> InputsOf(SensorDescriptor output) => [];

    /// <inheritdoc />
    public SourceInventory Discover()
    {
        var builder = new InventoryBuilder(_upstream);
        var failed = 0;
        foreach (var module in _modules)
        {
            var mark = builder.Mark();
            try
            {
                module.Discover(builder);
            }
            catch (Exception ex)
            {
                failed++;
                builder.Rollback(mark);
                try { module.Release(); } catch { /* nothing more to do */ }
                Log.Warn($"native: {module.Name} discovery failed: {ex.Message}");
            }
        }
        Log.Info($"native: {builder.Sensors.Count} sensors on {builder.Hardware.Count} own hardware ({string.Join(", ", builder.Sensors.GroupBy(x => x.HardwareKind).Select(g => $"{g.Key} {g.Count()}"))}); {MemoryModules.Count} memory modules");
        _status = failed == 0 ? new SourceStatus(SourceState.Ok)
            : failed == _modules.Count ? new SourceStatus(SourceState.Unavailable, "source.native.failed")
            : new SourceStatus(SourceState.Limited, "source.native.partial");
        return new SourceInventory(builder.Hardware, builder.Sensors);
    }

    /// <inheritdoc />
    public void Update(ReadOnlySpan<SensorDescriptor> requested, Span<float> values)
    {
        var stamp = ++_stamp;
        var now = Stopwatch.GetTimestamp();
        foreach (var d in requested)
        {
            if (d.SourceTag is not NativeTag tag)
            {
                values[d.Index] = float.NaN;
                continue;
            }
            var node = tag.Node;
            if (node.Stamp != stamp)
            {
                node.Stamp = stamp; // one refresh per call, however many of its sensors are due
                try
                {
                    node.Refresh(now);
                }
                catch (Exception ex)
                {
                    node.Fault();
                    LogOnce(ex);
                }
            }
            values[d.Index] = node.Read(tag.Slot);
        }
    }

    /// <inheritdoc />
    public void Suspend()
    {
        foreach (var m in _modules)
        {
            try { m.Release(); } catch { /* ignored */ }
        }
    }

    /// <inheritdoc />
    public void Resume()
    {
        // the engine calls Discover right after, which re-opens everything
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var m in _modules)
        {
            try { m.Dispose(); } catch { /* ignored */ }
        }
    }

    private void RequestRediscover()
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastRediscoverRequest != 0 && now - _lastRediscoverRequest < RediscoverSpacing) return;
        _lastRediscoverRequest = now;
        InventoryChanged?.Invoke();
    }

    private void LogOnce(Exception ex)
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastErrorLog != 0 && now - _lastErrorLog < Stopwatch.Frequency * 60) return;
        _lastErrorLog = now;
        Log.Error("native: update failed", ex);
    }
}
