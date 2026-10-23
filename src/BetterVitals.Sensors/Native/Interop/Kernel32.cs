using System.Runtime.InteropServices;

namespace BetterVitals.Sensors.Native.Interop;

/// <summary>kernel32 / ntdll entry points used by the native source (blittable, no per-call allocation).</summary>
internal static unsafe partial class Kernel32
{
    public const uint FileShareRead = 1;
    public const uint FileShareWrite = 2;
    public const uint OpenExisting = 3;
    public static readonly nint InvalidHandle = -1;

    public const uint IoctlStorageQueryProperty = 0x002D1400;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(nint device, uint code, void* input, uint inputSize, void* output, uint outputSize, out uint returned, nint overlapped);

    /// <summary>True when the device is in D0. Does not touch the media (used to leave sleeping HDDs alone).</summary>
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDevicePowerState(nint device, out int on);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetSystemFirmwareTable(uint signature, uint tableId, void* buffer, uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalMemoryStatusEx(MemoryStatusEx* status);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetLogicalProcessorInformationEx(int relationship, void* buffer, ref uint length);

    [LibraryImport("kernel32.dll")]
    public static partial ushort GetActiveProcessorGroupCount();

    [LibraryImport("kernel32.dll")]
    public static partial uint GetActiveProcessorCount(ushort group);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQuerySystemInformation(int infoClass, void* buffer, uint length, out uint returned);

    public const int SystemProcessorPerformanceInformation = 8;

    /// <summary>"RSMB" as the DWORD GetSystemFirmwareTable expects.</summary>
    public const uint RawSmbiosSignature = 0x52534D42;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MemoryStatusEx
{
    public uint Length;
    public uint MemoryLoad;
    public ulong TotalPhys;
    public ulong AvailPhys;
    public ulong TotalPageFile;
    public ulong AvailPageFile;
    public ulong TotalVirtual;
    public ulong AvailVirtual;
    public ulong AvailExtendedVirtual;
}

/// <summary>SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION (x64, 48 bytes). Times in 100 ns units; KernelTime includes IdleTime.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ProcessorPerformance
{
    public long IdleTime;
    public long KernelTime;
    public long UserTime;
    public long DpcTime;
    public long InterruptTime;
    public uint InterruptCount;
}
