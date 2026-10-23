using System.Runtime.InteropServices;

namespace BetterVitals.Sensors.Native.Interop;

/// <summary>D3DKMT thunks (gdi32): adapter enumeration and <c>D3DKMTQueryAdapterInfo</c>. All calls work without administrator rights.</summary>
internal static unsafe partial class D3dKmt
{
    // KMTQUERYADAPTERINFOTYPE values used here
    public const int QaiAdapterAddress = 6;
    public const int QaiAdapterRegistryInfo = 8;
    public const int QaiAdapterType = 15;
    public const int QaiNodePerfData = 61;
    public const int QaiAdapterPerfData = 62;
    public const int QaiAdapterPerfDataCaps = 63;

    [LibraryImport("gdi32.dll")]
    public static partial int D3DKMTEnumAdapters2(EnumAdapters2* args);

    [LibraryImport("gdi32.dll")]
    public static partial int D3DKMTOpenAdapterFromLuid(OpenAdapterFromLuid* args);

    [LibraryImport("gdi32.dll")]
    public static partial int D3DKMTCloseAdapter(CloseAdapter* args);

    [LibraryImport("gdi32.dll")]
    public static partial int D3DKMTQueryAdapterInfo(QueryAdapterInfo* args);

    /// <summary>NTSTATUS success?</summary>
    public static bool Ok(int status) => status >= 0;

    /// <summary>Queries one info block of <paramref name="size"/> bytes into <paramref name="data"/>.</summary>
    public static int Query(uint adapter, int type, void* data, uint size)
    {
        var q = new QueryAdapterInfo { Handle = adapter, Type = type, Data = data, Size = size };
        return D3DKMTQueryAdapterInfo(&q);
    }

    public static void Close(uint adapter)
    {
        if (adapter == 0) return;
        var c = new CloseAdapter { Handle = adapter };
        D3DKMTCloseAdapter(&c);
    }

    public static uint OpenLuid(Luid luid)
    {
        var o = new OpenAdapterFromLuid { Luid = luid };
        return D3DKMTOpenAdapterFromLuid(&o) >= 0 ? o.Handle : 0;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct Luid
{
    public uint Low;
    public int High;

    public readonly long Value => ((long)High << 32) | Low;

    public override readonly string ToString() => $"0x{High:X8}_0x{Low:X8}";
}

[StructLayout(LayoutKind.Sequential)]
internal struct KmtAdapterInfo
{
    public uint Handle;
    public Luid Luid;
    public uint NumSources;
    public int PresentMoveRegionsPreferred;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct EnumAdapters2
{
    public uint NumAdapters;
    public KmtAdapterInfo* Adapters;
}

[StructLayout(LayoutKind.Sequential)]
internal struct OpenAdapterFromLuid
{
    public Luid Luid;
    public uint Handle;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CloseAdapter
{
    public uint Handle;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct QueryAdapterInfo
{
    public uint Handle;
    public int Type;
    public void* Data;
    public uint Size;
}

/// <summary>D3DKMT_ADAPTERADDRESS: PCI location.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AdapterAddress
{
    public uint BusNumber;
    public uint DeviceNumber;
    public uint FunctionNumber;
}

/// <summary>D3DKMT_ADAPTER_PERFDATA (64 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AdapterPerfData
{
    public uint PhysicalAdapterIndex;   // in
    public ulong MemoryFrequency;       // Hz
    public ulong MaxMemoryFrequency;    // Hz
    public ulong MaxMemoryFrequencyOC;  // Hz
    public ulong MemoryBandwidth;       // bytes
    public ulong PcieBandwidth;         // bytes
    public uint FanRpm;
    public uint Power;                  // tenths of a percent (not watts)
    public uint Temperature;            // deci-Celsius
    public byte PowerStateOverride;
}

/// <summary>D3DKMT_ADAPTER_PERFDATACAPS (40 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AdapterPerfDataCaps
{
    public uint PhysicalAdapterIndex;   // in
    public ulong MaxMemoryBandwidth;
    public ulong MaxPcieBandwidth;
    public uint MaxFanRpm;
    public uint TemperatureWarning;     // deci-Celsius
    public uint TemperatureOverheat;    // deci-Celsius
}

/// <summary>D3DKMT_NODE_PERFDATA (56 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NodePerfData
{
    public uint NodeOrdinal;            // in
    public uint PhysicalAdapterIndex;   // in
    public ulong Frequency;             // Hz
    public ulong MaxFrequency;          // Hz
    public ulong MaxFrequencyOC;        // Hz
    public uint Voltage;                // milli-volts
    public uint VoltageMax;
    public uint VoltageMaxOC;
    public ulong MaxTransitionLatency;
}

/// <summary>D3DKMT_ADAPTERREGISTRYINFO: four MAX_PATH wide strings.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AdapterRegistryInfo
{
    public fixed char AdapterString[260];
    public fixed char BiosString[260];
    public fixed char DacType[260];
    public fixed char ChipType[260];
}
