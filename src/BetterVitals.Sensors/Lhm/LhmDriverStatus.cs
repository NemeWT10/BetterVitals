using BetterVitals.Core;
using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Power;
using Microsoft.Win32;

namespace BetterVitals.Sensors.Lhm;

/// <summary>Loc keys of the LHM source status (texts live in the app's Loc tables).</summary>
public static class LhmStatusKeys
{
    /// <summary>Not elevated: PawnIO opens only for administrators, CPU / mainboard / DIMM sensors are missing.</summary>
    public const string NotElevated = "source.lhm.notElevated";

    /// <summary>PawnIO is not installed.</summary>
    public const string NoPawnIo = "source.lhm.noPawnIo";

    /// <summary>PawnIO older than 2.1 (old certificate, blocked by FACEIT) — update recommended.</summary>
    public const string OldPawnIo = "source.lhm.oldPawnIo";

    /// <summary>Installed and elevated, but the PawnIO device cannot be opened (service stopped / blocked).</summary>
    public const string DriverNotRunning = "source.lhm.driverNotRunning";

    /// <summary>Driver sensors released on purpose (anti-cheat game).</summary>
    public const string Paused = "source.lhm.paused";

    /// <summary>LibreHardwareMonitor failed to start.</summary>
    public const string Failed = "source.lhm.failed";
}

/// <summary>PawnIO / elevation facts and the pure status decision.</summary>
internal static class LhmDriverStatus
{
    /// <summary>First PawnIO with the new signing certificate (FACEIT accepts it).</summary>
    public static readonly Version MinimumPawnIo = new(2, 1, 0);

    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";
    private const string DevicePath = @"\\?\GLOBALROOT\Device\PawnIO";

    /// <summary>
    /// Installed PawnIO version from HKLM\…\Uninstall\PawnIO\DisplayVersion (read fresh: LHM's <c>PawnIo.Version</c> is
    /// cached once per process, so it misses an install made while we run), LHM's value as a fallback; null = not installed.
    /// </summary>
    public static Version? DetectPawnIo()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = root.OpenSubKey(UninstallKey);
                if (Version.TryParse(key?.GetValue("DisplayVersion") as string, out var v)) return v;
            }
            catch
            {
                // no access / no key
            }
        }
        try
        {
            if (LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled) return LibreHardwareMonitor.PawnIo.PawnIo.Version;
        }
        catch (Exception ex)
        {
            Log.Warn($"lhm: PawnIo.IsInstalled failed: {ex.Message}");
        }
        return null;
    }

    public static bool IsElevated() => Environment.IsPrivilegedProcess;

    /// <summary>Opens and immediately closes the PawnIO device (elevated callers only; never while paused).</summary>
    public static unsafe bool CanOpenDevice()
    {
        nint h;
        fixed (char* p = DevicePath)
            h = PowerNative.CreateFile(p, PowerNative.GENERIC_READ | PowerNative.GENERIC_WRITE,
                PowerNative.FILE_SHARE_READ | PowerNative.FILE_SHARE_WRITE, 0, PowerNative.OPEN_EXISTING, 0, 0);
        if (h == PowerNative.InvalidHandle || h == 0) return false;
        PowerNative.CloseHandle(h);
        return true;
    }

    /// <summary>
    /// Status precedence: failed → paused → no PawnIO → not elevated → old PawnIO → device not opening → OK.
    /// </summary>
    public static SourceStatus Compute(bool openFailed, bool paused, Version? pawnIo, bool elevated, bool? deviceOpens)
    {
        if (openFailed) return new(SourceState.Unavailable, LhmStatusKeys.Failed);
        if (paused) return new(SourceState.Paused, LhmStatusKeys.Paused);
        if (pawnIo is null) return new(SourceState.Limited, LhmStatusKeys.NoPawnIo);
        if (!elevated) return new(SourceState.Limited, LhmStatusKeys.NotElevated);
        if (pawnIo < MinimumPawnIo) return new(SourceState.Limited, LhmStatusKeys.OldPawnIo);
        if (deviceOpens == false) return new(SourceState.Limited, LhmStatusKeys.DriverNotRunning);
        return new(SourceState.Ok);
    }
}
