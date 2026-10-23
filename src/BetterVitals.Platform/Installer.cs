using System.Diagnostics;
using Microsoft.Win32;

namespace BetterVitals.Platform;

public sealed record InstallOptions(string UserSid, bool Autostart, bool InstallPawnIo);

public sealed record InstallResult(bool Ok, PawnIoInstallOutcome? PawnIo, string? Error = null);

/// <summary>
/// Per-machine install to %ProgramFiles%\BetterVitals (admin-only folder: an exe that runs with administrator rights
/// must not live where an ordinary program could replace it), an "Apps" entry, a Start menu shortcut and the two
/// scheduled tasks. All of it runs once in an elevated helper process (<c>--install</c>) behind a single UAC prompt.
/// </summary>
public static class Installer
{
    public const string ExeName = "BetterVitals.exe";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\BetterVitals";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BetterVitals");

    public static string InstalledExe => Path.Combine(InstallDir, ExeName);

    public static string StartMenuLink => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", "BetterVitals.lnk");

    public static bool IsInstalled => File.Exists(InstalledExe);

    /// <summary>This process is the installed copy.</summary>
    public static bool IsRunningInstalled => string.Equals(
        Path.GetFullPath(Environment.ProcessPath ?? ""), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    /// <summary>The launcher task of this user exists (an unelevated start can relaunch elevated without UAC).</summary>
    public static bool HasLauncher(string userSid) => ScheduledTasks.Exists(ScheduledTasks.LauncherName(userSid));

    /// <summary>
    /// Elevated: performs the whole install. Safe to repeat (repair / re-install / update of tasks).
    /// <paramref name="nativeSource"/> = the app assembly carrying WPF's native libraries (see <see cref="NativeRuntime"/>).
    /// </summary>
    public static InstallResult Install(InstallOptions o, string version, System.Reflection.Assembly? nativeSource = null)
    {
        if (!Elevation.IsElevated) return new InstallResult(false, null, "not elevated");
        try
        {
            Directory.CreateDirectory(InstallDir);
            var source = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("no process path"));
            if (!string.Equals(source, Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase)) CopyExe(source, InstalledExe);
            if (nativeSource is not null) NativeRuntime.WriteBeside(nativeSource, InstallDir);

            WriteUninstallEntry(version);
            try { Shortcut.Create(StartMenuLink, InstalledExe, "BetterVitals"); }
            catch (Exception ex) { Core.Log.Warn("install: shortcut: " + ex.Message); }

            if (!RegisterTasks(o.UserSid, o.Autostart)) return new InstallResult(false, null, "tasks");

            PawnIoInstallOutcome? pawn = null;
            if (o.InstallPawnIo && (!PawnIo.IsInstalled || PawnIo.IsOutdated)) pawn = PawnIo.Install(InstallDir);
            Core.Log.Info($"install: done (autostart={o.Autostart}, pawnio={pawn?.ToString() ?? "skipped"})");
            return new InstallResult(true, pawn);
        }
        catch (Exception ex)
        {
            Core.Log.Error("install failed", ex);
            return new InstallResult(false, null, ex.Message);
        }
    }

    /// <summary>Elevated: (re)creates the launcher and autostart tasks for a user.</summary>
    public static bool RegisterTasks(string userSid, bool autostart)
    {
        var launcher = ScheduledTasks.BuildXml(InstalledExe, ScheduledTasks.FromTaskArg, userSid, atLogon: false, enabled: true,
            "Starts BetterVitals with administrator rights without a prompt (sensor access).");
        var auto = ScheduledTasks.BuildXml(InstalledExe, $"{ScheduledTasks.FromTaskArg} {ScheduledTasks.AutostartArg}", userSid, atLogon: true, enabled: autostart,
            "Starts BetterVitals in the tray when you sign in.");
        return ScheduledTasks.CreateOrReplace(ScheduledTasks.LauncherName(userSid), launcher, InstallDir)
               && ScheduledTasks.CreateOrReplace(ScheduledTasks.AutostartName(userSid), auto, InstallDir);
    }

    /// <summary>Elevated: the "start with Windows" setting.</summary>
    public static bool SetAutostart(string userSid, bool enabled)
    {
        var name = ScheduledTasks.AutostartName(userSid);
        return ScheduledTasks.Exists(name) ? ScheduledTasks.SetEnabled(name, enabled) : RegisterTasks(userSid, enabled);
    }

    /// <summary>
    /// Elevated: removes tasks (all users), shortcut, Apps entry and — after this process exits — the install folder.
    /// User data (%APPDATA% / %LOCALAPPDATA%\BetterVitals) only when <paramref name="removeData"/>. PawnIO stays
    /// (other tools use it) unless <paramref name="removePawnIo"/>.
    /// </summary>
    public static void Uninstall(bool removeData, bool removePawnIo, IEnumerable<string> dataDirs)
    {
        ScheduledTasks.DeleteAll();
        TryDelete(StartMenuLink);
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            hklm.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            Core.Log.Warn("uninstall: registry: " + ex.Message);
        }
        if (removePawnIo) RunPawnIoUninstaller();
        if (removeData)
            foreach (var dir in dataDirs)
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (Exception ex) { Core.Log.Warn("uninstall: data: " + ex.Message); }
        ScheduleFolderRemoval(InstallDir);
    }

    private static void CopyExe(string source, string target)
    {
        var tmp = target + ".new";
        File.Copy(source, tmp, overwrite: true);
        if (File.Exists(target))
        {
            // a running copy cannot be overwritten, but it can be renamed; the .old file goes away at the next start
            var old = target + ".old";
            TryDelete(old);
            File.Move(target, old);
        }
        File.Move(tmp, target);
    }

    /// <summary>Removes leftovers of a previous update / install (call at start).</summary>
    public static void CleanupLeftovers()
    {
        TryDelete(InstalledExe + ".old");
        TryDelete(InstalledExe + ".new");
    }

    private static void WriteUninstallEntry(string version)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "BetterVitals");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "NemeWT10");
        key.SetValue("DisplayIcon", InstalledExe);
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("URLInfoAbout", "https://github.com/NemeWT10/BetterVitals");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        try { key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord); } catch { /* optional */ }
    }

    private static void RunPawnIoUninstaller()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            if (key?.GetValue("UninstallString") is not string cmd || string.IsNullOrWhiteSpace(cmd)) return;
            var (exe, args) = SplitCommand(cmd);
            using var p = Process.Start(new ProcessStartInfo(exe, (args + " -silent").Trim()) { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(TimeSpan.FromMinutes(2));
        }
        catch (Exception ex)
        {
            Core.Log.Warn("uninstall: pawnio: " + ex.Message);
        }
    }

    internal static (string Exe, string Args) SplitCommand(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end < 0 ? (command.Trim('"'), "") : (command[1..end], command[(end + 1)..].Trim());
        }
        var space = command.IndexOf(' ');
        return space < 0 ? (command, "") : (command[..space], command[(space + 1)..].Trim());
    }

    /// <summary>A running exe cannot delete its own folder: a hidden cmd waits a moment and removes it.</summary>
    private static void ScheduleFolderRemoval(string dir)
    {
        try
        {
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
            Process.Start(new ProcessStartInfo(cmd, $"/d /c \"\"{ping}\" -n 4 127.0.0.1 >nul & rmdir /s /q \"{dir}\"\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),
            })?.Dispose();
        }
        catch (Exception ex)
        {
            Core.Log.Warn("uninstall: folder: " + ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* in use or gone */ }
    }
}
