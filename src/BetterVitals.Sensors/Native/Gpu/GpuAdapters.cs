using BetterVitals.Sensors.Native.Drives;
using BetterVitals.Sensors.Native.Interop;

namespace BetterVitals.Sensors.Native.Gpu;

/// <summary>A hardware GPU adapter as D3DKMT lists it (software / indirect-display adapters are left out).</summary>
internal sealed class GpuAdapterInfo
{
    public Luid Luid { get; init; }

    public string Name { get; init; } = "";

    /// <summary>D3DKMT_ADAPTERTYPE bits.</summary>
    public uint TypeFlags { get; init; }

    public bool HasAddress { get; init; }

    public AdapterAddress Address { get; init; }

    /// <summary>Open D3DKMT adapter handle (0 = closed).</summary>
    public uint Handle { get; set; }

    /// <summary>PnP device instance id of the display device ("PCI\VEN_10DE&amp;DEV_2C02…"), used to ask whether it sleeps.</summary>
    public string? InstanceId { get; set; }

    public bool HybridDiscrete => (TypeFlags & TypeHybridDiscrete) != 0;

    public bool Detachable => (TypeFlags & TypeDetachable) != 0;

    /// <summary>Adapters that can power down get their power state checked before each driver call.</summary>
    public bool NeedsPowerGate => HybridDiscrete || Detachable || TypeFlags == 0;

    public const uint TypeRenderSupported = 1 << 0;
    public const uint TypeDisplaySupported = 1 << 1;
    public const uint TypeSoftwareDevice = 1 << 2;
    public const uint TypeHybridDiscrete = 1 << 4;
    public const uint TypeHybridIntegrated = 1 << 5;
    public const uint TypeIndirectDisplay = 1 << 6;
    public const uint TypeDetachable = 1 << 10;

    /// <summary>Real graphics hardware: renders or displays, and is not a software / indirect-display device.</summary>
    public static bool IsHardware(uint typeFlags)
        => (typeFlags & (TypeSoftwareDevice | TypeIndirectDisplay)) == 0 && (typeFlags & (TypeRenderSupported | TypeDisplaySupported)) != 0;
}

/// <summary>What <c>ADAPTERPERFDATA</c> / <c>NODEPERFDATA</c> answered for an adapter (which sensors exist).</summary>
internal readonly record struct GpuPerfProbe(
    bool Probed, bool Temperature, bool Fan, bool MemoryClock, bool Power, bool CoreClock, bool CoreVoltage, uint NodeOrdinal)
{
    public static GpuPerfProbe None => default;
}

internal static unsafe class GpuAdapters
{
    /// <summary>Enumerates hardware adapters without waking anything (type, name and PCI address are cached by the kernel).</summary>
    public static List<GpuAdapterInfo> Enumerate()
    {
        var result = new List<GpuAdapterInfo>();
        var args = new EnumAdapters2();
        if (!D3dKmt.Ok(D3dKmt.D3DKMTEnumAdapters2(&args)) || args.NumAdapters == 0) return result;

        var infos = new KmtAdapterInfo[args.NumAdapters + 4];
        int status;
        fixed (KmtAdapterInfo* p = infos)
        {
            args.Adapters = p;
            args.NumAdapters = (uint)infos.Length;
            status = D3dKmt.D3DKMTEnumAdapters2(&args);
        }
        if (!D3dKmt.Ok(status)) return result;

        for (var i = 0; i < Math.Min(args.NumAdapters, infos.Length); i++)
        {
            var info = infos[i];
            var keep = false;
            try
            {
                uint type = 0;
                if (!D3dKmt.Ok(D3dKmt.Query(info.Handle, D3dKmt.QaiAdapterType, &type, sizeof(uint))) || !GpuAdapterInfo.IsHardware(type)) continue;
                var registry = new AdapterRegistryInfo();
                var name = D3dKmt.Ok(D3dKmt.Query(info.Handle, D3dKmt.QaiAdapterRegistryInfo, &registry, (uint)sizeof(AdapterRegistryInfo)))
                    ? StorageQuery.Collapse(new string(registry.AdapterString)) : "";
                if (name.Length == 0 || name.Contains("Microsoft Basic Render", StringComparison.OrdinalIgnoreCase)) continue;
                var address = new AdapterAddress();
                var hasAddress = D3dKmt.Ok(D3dKmt.Query(info.Handle, D3dKmt.QaiAdapterAddress, &address, (uint)sizeof(AdapterAddress)));
                result.Add(new GpuAdapterInfo { Luid = info.Luid, Name = name, TypeFlags = type, HasAddress = hasAddress, Address = address, Handle = info.Handle });
                keep = true;
            }
            finally
            {
                if (!keep) D3dKmt.Close(info.Handle);
            }
        }
        return result;
    }

    /// <summary>
    /// Finds the display devnode of an adapter by its PCI location (bus / device / function) among the present Display-class
    /// devices. Null when none matches.
    /// </summary>
    public static string? FindInstanceId(AdapterAddress address)
    {
        if (CfgMgr.CM_Get_Device_ID_List_SizeW(out var length, CfgMgr.DisplayClassGuid, CfgMgr.GetIdListFilterClass | CfgMgr.GetIdListFilterPresent) != CfgMgr.CrSuccess || length < 2)
            return null;
        var buffer = new char[length];
        fixed (char* p = buffer)
        {
            if (CfgMgr.CM_Get_Device_ID_ListW(CfgMgr.DisplayClassGuid, p, length, CfgMgr.GetIdListFilterClass | CfgMgr.GetIdListFilterPresent) != CfgMgr.CrSuccess)
                return null;
        }
        var wantedAddress = (address.DeviceNumber << 16) | (address.FunctionNumber & 0xFFFF);
        foreach (var range in buffer.AsSpan().Split('\0'))
        {
            var id = new string(buffer.AsSpan()[range]);
            if (id.Length == 0) continue;
            if (CfgMgr.CM_Locate_DevNodeW(out var devInst, id, CfgMgr.LocateDevNodeNormal) != CfgMgr.CrSuccess) continue;
            if (CfgMgr.TryGetUInt32(devInst, CfgMgr.PidBusNumber, out var bus) && CfgMgr.TryGetUInt32(devInst, CfgMgr.PidAddress, out var addr)
                && bus == address.BusNumber && addr == wantedAddress)
                return id;
        }
        return null;
    }

    /// <summary>
    /// Asks the driver what it reports (ADAPTERPERFDATA, ADAPTERPERFDATA_CAPS, the 3D node's NODEPERFDATA). Touches the
    /// driver — call only for an adapter that is awake. A field the driver leaves at 0 / nonsense means "no such sensor".
    /// </summary>
    public static GpuPerfProbe Probe(uint adapter)
    {
        var perf = new AdapterPerfData();
        if (!D3dKmt.Ok(D3dKmt.Query(adapter, D3dKmt.QaiAdapterPerfData, &perf, (uint)sizeof(AdapterPerfData))))
            return new GpuPerfProbe(true, false, false, false, false, false, false, 0);

        var caps = new AdapterPerfDataCaps();
        var capsOk = D3dKmt.Ok(D3dKmt.Query(adapter, D3dKmt.QaiAdapterPerfDataCaps, &caps, (uint)sizeof(AdapterPerfDataCaps)));
        var fan = Plausible.FanRpm(perf.FanRpm) && (perf.FanRpm > 0 || (capsOk && caps.MaxFanRpm > 0));

        // The 3D engine is node 0 (NODEMETADATA, which would say so, is rejected by current drivers). A driver that fills the
        // node with garbage (the Radeon iGPU reports its clock in MHz instead of Hz) gets no core clock / voltage sensor.
        var coreClock = false;
        var voltage = false;
        var np = new NodePerfData { NodeOrdinal = 0 };
        if (D3dKmt.Ok(D3dKmt.Query(adapter, D3dKmt.QaiNodePerfData, &np, (uint)sizeof(NodePerfData))))
        {
            coreClock = Plausible.ClockHz(np.Frequency);
            voltage = coreClock && Plausible.VoltageMilli(np.Voltage);
        }

        return new GpuPerfProbe(true,
            Temperature: Plausible.GpuTemperatureDeci(perf.Temperature),
            Fan: fan,
            MemoryClock: Plausible.ClockHz(perf.MemoryFrequency),
            Power: perf.Power > 0 && Plausible.PowerTenthsPercent(perf.Power),
            CoreClock: coreClock,
            CoreVoltage: voltage,
            NodeOrdinal: 0);
    }
}
