namespace BetterVitals.Sensors.Power;

/// <summary>
/// Power gate for drives: <c>GetDevicePowerState</c> on a handle opened with zero access rights — no I/O reaches the
/// medium, so a spun-down HDD stays asleep. The handle is opened per check and closed right away (a kept handle could
/// block "safely remove" of USB drives). Limitation: a drive that spun itself down by its own firmware timer (APM)
/// while Windows still sees it in D0 reads as awake.
/// </summary>
internal sealed unsafe class DiskPowerGate : PowerGate
{
    private readonly string _path;

    private DiskPowerGate(string path, bool? seekPenalty)
    {
        _path = path;
        IncursSeekPenalty = seekPenalty;
    }

    public override string Target => _path;

    /// <summary>Rotating medium (StorageDeviceSeekPenaltyProperty); null when the drive does not say.</summary>
    public bool? IncursSeekPenalty { get; }

    /// <summary>"\\.\PhysicalDrive2" from the device number, else the device path; null when neither is known.</summary>
    public static DiskPowerGate? TryCreate(uint? deviceNumber, string? devicePath)
    {
        var path = deviceNumber.HasValue ? $@"\\.\PhysicalDrive{deviceNumber.Value}" : devicePath;
        if (string.IsNullOrEmpty(path)) return null;
        return new DiskPowerGate(path, QuerySeekPenalty(path));
    }

    protected override PowerQuery Query()
    {
        var h = PowerNative.OpenNoAccess(_path);
        if (h == PowerNative.InvalidHandle || h == 0) return PowerQuery.Unknown;
        try
        {
            if (!PowerNative.GetDevicePowerState(h, out var on)) return PowerQuery.Unknown;
            return on != 0 ? PowerQuery.Awake : PowerQuery.Asleep;
        }
        finally
        {
            PowerNative.CloseHandle(h);
        }
    }

    /// <summary>IOCTL_STORAGE_QUERY_PROPERTY is FILE_ANY_ACCESS, so a zero-access handle suffices (no admin, no spin-up).</summary>
    private static bool? QuerySeekPenalty(string path)
    {
        var h = PowerNative.OpenNoAccess(path);
        if (h == PowerNative.InvalidHandle || h == 0) return null;
        try
        {
            var query = new PowerNative.StoragePropertyQuery { PropertyId = PowerNative.StorageDeviceSeekPenaltyProperty };
            PowerNative.DeviceSeekPenaltyDescriptor result = default;
            if (!PowerNative.DeviceIoControl(h, PowerNative.IOCTL_STORAGE_QUERY_PROPERTY, &query, (uint)sizeof(PowerNative.StoragePropertyQuery),
                    &result, (uint)sizeof(PowerNative.DeviceSeekPenaltyDescriptor), out var returned, 0) || returned < 9)
                return null;
            return result.IncursSeekPenalty != 0;
        }
        finally
        {
            PowerNative.CloseHandle(h);
        }
    }
}
