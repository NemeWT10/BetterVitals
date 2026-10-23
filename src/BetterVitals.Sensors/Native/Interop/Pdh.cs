using System.Runtime.InteropServices;

namespace BetterVitals.Sensors.Native.Interop;

/// <summary>pdh.dll: performance counters by their English names (locale independent).</summary>
internal static unsafe partial class Pdh
{
    public const uint FmtDouble = 0x00000200;
    public const uint FmtNoCap100 = 0x00008000;
    public const uint MoreData = 0x800007D2;

    [LibraryImport("pdh.dll")]
    public static partial uint PdhOpenQueryW(nint dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize, out uint itemCount, void* buffer);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCloseQuery(nint query);
}

/// <summary>PDH_FMT_COUNTERVALUE_ITEM_W for PDH_FMT_DOUBLE (24 bytes); the instance name lives in the same buffer.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PdhCounterItem
{
    public char* Name;
    public uint Status;
    public double Value;
}
