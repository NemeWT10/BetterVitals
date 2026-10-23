using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using BetterVitals.Core;
using BetterVitals.Platform;

namespace BetterVitals.Services;

/// <summary>
/// A newer exe started while BetterVitals is installed (a new download, a test build): without this, the start would
/// hand over to the old installed copy. Asks once, closes the running copy, puts this exe in place of the installed one
/// (one UAC prompt unless elevated) and starts it. Runs before the single-instance check and before WPF loads.
/// </summary>
internal static class InstalledUpdate
{
    /// <summary>Exit code when this process handed over to the updated copy; null = start normally.</summary>
    public static int? TryRun()
    {
        try
        {
            if (!Installer.IsInstalled || Installer.IsRunningInstalled) return null;
            var installed = InstalledVersion();
            if (!BuildVersion.ShouldReplace(AppEnv.Version, installed)) return null;
            if (!Ask(installed)) return null; // keeps the installed copy: the normal start hands over to it

            QuitRunningCopy();
            var code = Elevation.IsElevated
                ? PlatformCommands.Update()
                : Elevation.RunElevated(Environment.ProcessPath!, PlatformCommands.UpdateArgs, TimeSpan.FromMinutes(2));
            if (code != PlatformCommands.Ok)
            {
                Log.Warn($"update: the installed copy was not replaced (code {code?.ToString(CultureInfo.InvariantCulture) ?? "declined"})");
                return null;
            }
            Log.Info($"update: installed copy replaced ({installed} → {AppEnv.Version})");
            // started like from the Start menu: it picks full mode (launcher task) or basic mode by itself
            Process.Start(new ProcessStartInfo(Installer.InstalledExe) { UseShellExecute = true })?.Dispose();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("update: replacing the installed copy failed", ex);
            return null;
        }
    }

    private static string? InstalledVersion()
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(Installer.InstalledExe).ProductVersion;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static bool Ask(string? installed)
    {
        var culture = CultureInfo.CurrentUICulture;
        var pl = culture.TwoLetterISOLanguageName == "pl";
        var old = BuildVersion.Display(installed, culture);
        var now = BuildVersion.Display(AppEnv.Version, culture);
        var text = pl
            ? $"Zainstalowany BetterVitals to wersja {old}.\nTen plik to wersja {now}.\n\nZastąpić zainstalowaną wersję tą? Ustawienia i historia zostają. Windows może poprosić o zgodę."
            : $"The installed BetterVitals is version {old}.\nThis file is version {now}.\n\nReplace the installed version with this one? Settings and history stay. Windows may ask for permission.";
        return MessageBoxW(IntPtr.Zero, text, "BetterVitals", 0x00000004 /* YESNO */ | 0x00000020 /* ICONQUESTION */ | 0x00010000 /* SETFOREGROUND */) == 6;
    }

    /// <summary>The installed copy may be in the tray: it exits (saving history) so its files can be replaced.</summary>
    private static void QuitRunningCopy()
    {
        using (var probe = new SingleInstance(AppEnv.InstanceKey))
        {
            if (probe.IsPrimary) return;
            if (!probe.SendToPrimary("quit")) Log.Warn("update: the running copy did not answer");
        }
        var deadline = Environment.TickCount64 + 8000;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(250);
            using var probe = new SingleInstance(AppEnv.InstanceKey);
            if (probe.IsPrimary) return;
        }
        Log.Warn("update: the running copy did not exit in time");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);
}
