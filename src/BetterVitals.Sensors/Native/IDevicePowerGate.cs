using BetterVitals.Sensors.Native.Interop;

namespace BetterVitals.Sensors.Native;

/// <summary>
/// Tells whether a device may be touched without waking it. A laptop dGPU that sleeps in D3 must not get a driver call
/// (NVAPI / ADL / D3DKMT queries power it up); the check itself only asks the PnP manager and never reaches the device.
/// </summary>
public interface IDevicePowerGate
{
    /// <summary>
    /// False when the device (PnP device instance id, "PCI\VEN_10DE&amp;DEV_…\…") is in power state D1–D3. True when it is in D0
    /// and also when the state cannot be determined (the caller then behaves as on a desktop card).
    /// </summary>
    bool IsAwake(string deviceInstanceId);
}

/// <summary>
/// Default gate: <c>CM_Get_DevNode_Property(DEVPKEY_Device_PowerData)</c> → <c>CM_POWER_DATA.PD_MostRecentPowerState</c>.
/// Allocation-free after the first call per device (the devnode handle is cached).
/// </summary>
public sealed unsafe class CmDevicePowerGate : IDevicePowerGate
{
    private readonly Dictionary<string, uint> _devNodes = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public bool IsAwake(string deviceInstanceId)
    {
        try
        {
            if (!_devNodes.TryGetValue(deviceInstanceId, out var devInst))
            {
                if (CfgMgr.CM_Locate_DevNodeW(out devInst, deviceInstanceId, CfgMgr.LocateDevNodeNormal) != CfgMgr.CrSuccess) return true;
                _devNodes[deviceInstanceId] = devInst;
            }
            var key = new DevPropKey { Fmtid = CfgMgr.DeviceKeyGroup, Pid = CfgMgr.PidPowerData };
            uint* buffer = stackalloc uint[16]; // CM_POWER_DATA is 60 bytes
            uint size = 64;
            if (CfgMgr.CM_Get_DevNode_PropertyW(devInst, &key, out var type, buffer, ref size, 0) != CfgMgr.CrSuccess
                || type != CfgMgr.DevPropTypeBinary || size < 8)
            {
                _devNodes.Remove(deviceInstanceId); // stale devnode (device re-enumerated): locate again next time
                return true;
            }
            return State(buffer[1]);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>PD_MostRecentPowerState: 0 = unspecified, 1 = D0, 2 = D1, 3 = D2, 4 = D3. Awake = unspecified or D0.</summary>
    internal static bool State(uint mostRecentPowerState) => mostRecentPowerState <= 1 || mostRecentPowerState > 4;
}
