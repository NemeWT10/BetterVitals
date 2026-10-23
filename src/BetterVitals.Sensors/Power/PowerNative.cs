using System.Runtime.InteropServices;

namespace BetterVitals.Sensors.Power;

/// <summary>Minimal Win32 / CfgMgr32 interop for the power gates. Everything here is allocation-free on the hot path.</summary>
internal static unsafe partial class PowerNative
{
    public const uint CR_SUCCESS = 0;
    public const uint CR_NO_SUCH_DEVNODE = 0x0D;
    public const uint CR_BUFFER_SMALL = 0x1A;
    public const uint CM_LOCATE_DEVNODE_NORMAL = 0;
    public const uint CM_GETIDLIST_FILTER_PRESENT = 0x100;
    public const uint CM_GETIDLIST_FILTER_CLASS = 0x200;

    public const uint FILE_SHARE_READ = 1;
    public const uint FILE_SHARE_WRITE = 2;
    public const uint OPEN_EXISTING = 3;
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    public const int StorageDeviceSeekPenaltyProperty = 7;

    public static readonly nint InvalidHandle = -1;

    /// <summary>Display adapters setup class.</summary>
    public const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>DEVPKEY_Device_PowerData = {a45c254e-df1c-4efd-8020-67d146a850e0}, 32 (CM_POWER_DATA).</summary>
    public static readonly DevPropKey DevPKeyDevicePowerData =
        new(new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0), 32);

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct DevPropKey(Guid fmtid, uint pid)
    {
        public readonly Guid FmtId = fmtid;
        public readonly uint Pid = pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public int AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceSeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        public byte IncursSeekPenalty;
    }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW")]
    public static partial uint CM_Locate_DevNode(out uint devInst, char* deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    public static partial uint CM_Get_DevNode_Property(uint devInst, DevPropKey* key, out uint propertyType, byte* buffer, ref uint bufferSize, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW")]
    public static partial uint CM_Get_Device_ID_List_Size(out uint length, char* filter, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW")]
    public static partial uint CM_Get_Device_ID_List(char* filter, char* buffer, uint length, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true)]
    public static partial nint CreateFile(char* fileName, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDevicePowerState(nint device, out int on);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(nint device, uint code, void* inBuffer, uint inSize, void* outBuffer, uint outSize, out uint returned, nint overlapped);

    /// <summary>Opens a device with zero access rights (metadata / power queries only; never I/O to the medium).</summary>
    public static nint OpenNoAccess(string path)
    {
        fixed (char* p = path)
            return CreateFile(p, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
    }

    /// <summary>Lists present device instance ids of a setup class (discovery only; allocates).</summary>
    public static List<string> GetPresentDeviceIds(string classGuid)
    {
        var result = new List<string>();
        const uint flags = CM_GETIDLIST_FILTER_CLASS | CM_GETIDLIST_FILTER_PRESENT;
        fixed (char* filter = classGuid)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (CM_Get_Device_ID_List_Size(out var length, filter, flags) != CR_SUCCESS || length == 0) return result;
                var buffer = new char[length];
                fixed (char* b = buffer)
                {
                    var cr = CM_Get_Device_ID_List(filter, b, length, flags);
                    if (cr == CR_BUFFER_SMALL) continue;
                    if (cr != CR_SUCCESS) return result;
                }
                var start = 0;
                for (var i = 0; i < buffer.Length; i++)
                {
                    if (buffer[i] != '\0') continue;
                    if (i == start) break;
                    result.Add(new string(buffer, start, i - start));
                    start = i + 1;
                }
                return result;
            }
        }
        return result;
    }
}
