using BetterVitals.Sensors.Native.Interop;

namespace BetterVitals.Sensors.Native.Drives;

/// <summary>
/// The operating-system calls a drive node makes, behind an interface so the "never wake a sleeping HDD" and "reopen after
/// failures" logic can be tested without hardware.
/// </summary>
internal interface IStorageIo
{
    /// <summary>Opens <c>\\.\PhysicalDriveN</c> with desiredAccess 0; 0 when it cannot be opened.</summary>
    nint Open(int physicalIndex);

    void Close(nint handle);

    /// <summary>True when the device is in D0. False when it sleeps — and when Windows does not say (then it is left alone).</summary>
    bool IsInD0(nint handle);

    bool TryReadTemperature(nint handle, TemperatureMethod method, out float celsius);
}

/// <summary>The real thing: kernel32 + IOCTL_STORAGE_QUERY_PROPERTY.</summary>
internal sealed class SystemStorageIo : IStorageIo
{
    public static SystemStorageIo Instance { get; } = new();

    public nint Open(int physicalIndex)
    {
        var h = StorageQuery.OpenPhysicalDrive(physicalIndex);
        return StorageQuery.IsValid(h) ? h : 0;
    }

    public void Close(nint handle)
    {
        if (StorageQuery.IsValid(handle)) Kernel32.CloseHandle(handle);
    }

    public bool IsInD0(nint handle) => Kernel32.GetDevicePowerState(handle, out var on) && on != 0;

    public bool TryReadTemperature(nint handle, TemperatureMethod method, out float celsius)
        => StorageQuery.TryReadTemperature(handle, method, out celsius);
}
