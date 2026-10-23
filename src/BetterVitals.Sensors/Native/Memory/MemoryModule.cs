using BetterVitals.Core;
using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Native.Interop;

namespace BetterVitals.Sensors.Native.Memory;

/// <summary>
/// RAM: "Memory Speed" (MT/s) and the module list from SMBIOS (read once per discovery, the values never change), plus a
/// small usage fallback ("Memory Used", "Memory Available", "Memory" load) for the case that upstream has no memory sensors
/// at all. Attaches to the upstream memory hardware when there is one; otherwise adds its own "Total Memory" node (the name
/// LibreHardwareMonitor uses, so ids line up in full and basic mode).
/// </summary>
internal sealed unsafe class MemoryModule(Func<byte[]?>? readSmbios = null, bool usageFallback = true) : INativeModule
{
    internal const string HardwareName = "Total Memory";
    internal const string SpeedName = "Memory Speed";
    internal const string SpeedUnit = "MT/s";
    internal static readonly TimeSpan SpeedInterval = TimeSpan.FromSeconds(60);

    private readonly Func<byte[]?> _readSmbios = readSmbios ?? Smbios.ReadRaw;
    private IReadOnlyList<MemoryModuleInfo> _modules = [];
    private UsageNode? _usage;

    public string Name => "memory";

    /// <summary>Populated memory slots (SMBIOS type 17), as of the last discovery.</summary>
    public IReadOnlyList<MemoryModuleInfo> Modules => Volatile.Read(ref _modules);

    public void Discover(InventoryBuilder b)
    {
        Release();
        var raw = _readSmbios();
        var modules = raw is null ? [] : Smbios.ParseMemoryModules(raw);
        Volatile.Write(ref _modules, modules);

        var up = b.Upstream;
        var main = FindMainMemory(up);
        string? hardwareId = main?.Id;
        var reused = main is not null;

        string Hardware()
        {
            if (hardwareId is not null) return hardwareId;
            return hardwareId = b.AddHardware(HardwareKind.Memory, HardwareName, 0);
        }

        var speed = SpeedOf(modules);
        if (speed > 0 && !AnyUpstream(up, SensorKind.Clock, SpeedName, "Speed"))
            b.AddSensor(Hardware(), HardwareKind.Memory, SensorKind.Clock, SpeedName, new ConstantNode(speed), 0, SpeedInterval, SpeedUnit, reused);

        // the usage fallback is for the case that upstream reports no memory use at all (LibreHardwareMonitor failed)
        if (!usageFallback || AnyUpstream(up, SensorKind.Data, "Memory Used", "Memory Available") || AnyUpstream(up, SensorKind.Load, "Memory")) return;
        var usage = new UsageNode();
        var added = b.AddSensor(Hardware(), HardwareKind.Memory, SensorKind.Data, "Memory Used", usage, UsageNode.Used, default, "", reused) is not null;
        added |= b.AddSensor(Hardware(), HardwareKind.Memory, SensorKind.Data, "Memory Available", usage, UsageNode.Available, default, "", reused) is not null;
        added |= b.AddSensor(Hardware(), HardwareKind.Memory, SensorKind.Load, "Memory", usage, UsageNode.Load, default, "", reused) is not null;
        if (added) _usage = usage;
    }

    /// <summary>
    /// The upstream memory hardware that carries the totals (the card "Memory Speed" belongs to); null if none. LibreHardwareMonitor
    /// registers "Virtual Memory" next to "Total Memory" with the same sensor names: the physical one wins.
    /// </summary>
    internal static UpstreamIndex.Hw? FindMainMemory(UpstreamIndex up)
    {
        UpstreamIndex.Hw? fallback = null;
        foreach (var h in up.Hardware)
        {
            if (h.Kind != HardwareKind.Memory || !(h.Has(SensorKind.Data, "Memory Used") || h.Has(SensorKind.Load, "Memory"))) continue;
            var key = UpstreamIndex.Key(h.Slug);
            if (key.Contains("virtual", StringComparison.Ordinal)) continue;
            if (key == "totalmemory") return h;
            fallback ??= h;
        }
        return fallback;
    }

    private static bool AnyUpstream(UpstreamIndex up, SensorKind kind, params string[] names)
    {
        foreach (var h in up.Hardware)
        {
            if (h.Kind != HardwareKind.Memory) continue;
            foreach (var n in names)
                if (h.Has(kind, n)) return true;
        }
        return false;
    }

    /// <summary>
    /// The speed to publish: the slowest module's configured speed (the modules run at a common speed), else the slowest
    /// rated one; 0 when SMBIOS says nothing plausible.
    /// </summary>
    internal static int SpeedOf(IReadOnlyList<MemoryModuleInfo> modules)
    {
        var best = 0;
        foreach (var m in modules)
        {
            var s = m.EffectiveSpeedMTs;
            if (!Plausible.MemorySpeedMTs(s)) continue;
            if (best == 0 || s < best) best = s;
        }
        return best;
    }

    public void Release() => _usage = null;

    public void Dispose() => Release();

    /// <summary>GlobalMemoryStatusEx: used / available in GB (1024³) and the used share in percent.</summary>
    private sealed class UsageNode : NativeNode
    {
        public const int Used = 0, Available = 1, Load = 2;
        private const double Gb = 1024.0 * 1024 * 1024;
        private readonly float[] _values = [float.NaN, float.NaN, float.NaN];

        internal override void Refresh(long timestamp)
        {
            var status = new MemoryStatusEx { Length = (uint)sizeof(MemoryStatusEx) };
            if (!Kernel32.GlobalMemoryStatusEx(&status) || status.TotalPhys == 0)
            {
                Fault();
                return;
            }
            var used = status.TotalPhys - Math.Min(status.AvailPhys, status.TotalPhys);
            _values[Used] = (float)(used / Gb);
            _values[Available] = (float)(status.AvailPhys / Gb);
            _values[Load] = (float)(100.0 * used / status.TotalPhys);
        }

        internal override float Read(int slot) => _values[slot];

        internal override void Fault() => Array.Fill(_values, float.NaN);
    }
}
