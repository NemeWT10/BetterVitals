using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace BetterVitals.Platform;

/// <summary>Administrator rights of this process and the one-time UAC start used by install / uninstall.</summary>
public static class Elevation
{
    private const int ErrorCancelled = 1223;

    /// <summary>The process runs with an elevated (administrator) token.</summary>
    public static bool IsElevated { get; } = Compute();

    /// <summary>SID of the user this process runs as ("S-1-5-21-…").</summary>
    public static string CurrentUserSid { get; } = WindowsIdentity.GetCurrent().User?.Value ?? "";

    private static bool Compute()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Starts <paramref name="exe"/> elevated (the UAC prompt) and waits for it. Returns its exit code, or null when the
    /// user declined the prompt or the start failed.
    /// </summary>
    public static int? RunElevated(string exe, string arguments, TimeSpan timeout)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = true, Verb = "runas" });
            if (p is null) return null;
            if (!p.WaitForExit(timeout)) return null;
            return p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return null;
        }
        catch (Exception ex)
        {
            Core.Log.Warn("elevation: start failed: " + ex.Message);
            return null;
        }
    }
}
