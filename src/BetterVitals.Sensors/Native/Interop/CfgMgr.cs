using System.Runtime.InteropServices;

namespace BetterVitals.Sensors.Native.Interop;

/// <summary>cfgmgr32: PnP manager queries (no device I/O, never wakes a device).</summary>
internal static unsafe partial class CfgMgr
{
    public const uint CrSuccess = 0;
    public const uint GetIdListFilterClass = 0x00000200;
    public const uint GetIdListFilterPresent = 0x00000100;
    public const uint LocateDevNodeNormal = 0;
    public const uint DevPropTypeBinary = 0x1003;
    public const uint DevPropTypeUInt32 = 0x7;

    /// <summary>The Display device setup class.</summary>
    public const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";

    // DEVPKEY_Device_* share the fmtid {a45c254e-df1c-4efd-8020-67d146a850e0}
    public static readonly Guid DeviceKeyGroup = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    public const uint PidBusNumber = 23;
    public const uint PidAddress = 30;
    public const uint PidPowerData = 32;

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint CM_Get_Device_ID_List_SizeW(out uint length, string? filter, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint CM_Get_Device_ID_ListW(string? filter, char* buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint CM_Locate_DevNodeW(out uint devInst, string? deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    public static partial uint CM_Get_DevNode_PropertyW(uint devInst, DevPropKey* key, out uint type, void* buffer, ref uint size, uint flags);

    /// <summary>Reads a DWORD devnode property.</summary>
    public static bool TryGetUInt32(uint devInst, uint pid, out uint value)
    {
        value = 0;
        var key = new DevPropKey { Fmtid = DeviceKeyGroup, Pid = pid };
        uint v = 0, size = 4;
        if (CM_Get_DevNode_PropertyW(devInst, &key, out var type, &v, ref size, 0) != CrSuccess || type != DevPropTypeUInt32 || size != 4) return false;
        value = v;
        return true;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct DevPropKey
{
    public Guid Fmtid;
    public uint Pid;
}
