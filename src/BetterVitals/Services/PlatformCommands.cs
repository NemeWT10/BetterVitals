using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using BetterVitals.Core;
using BetterVitals.Platform;

namespace BetterVitals.Services;

/// <summary>
/// The exe's helper modes, run straight from Main before any WPF type is touched (an elevated helper must never load
/// WPF's native libraries from a user-writable folder): <c>--install</c>, <c>--install-pawnio</c>,
/// <c>--set-autostart</c>, <c>--uninstall</c>. Each does one job and exits with a code the caller understands.
/// </summary>
internal static class PlatformCommands
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Corrupted = 2;
    public const int NotElevated = 5;
    public const int Cancelled = 1223;
    public const int OkRestartNeeded = 3010;

    public static string InstallArgs(string sid, bool autostart, bool pawnIo)
        => $"--install --user-sid {sid} --autostart {(autostart ? 1 : 0)} --pawnio {(pawnIo ? 1 : 0)}";

    public const string InstallPawnIoArgs = "--install-pawnio";

    public static string AutostartArgs(string sid, bool enabled) => $"--set-autostart {(enabled ? 1 : 0)} --user-sid {sid}";

    /// <summary>Exit code when <paramref name="args"/> asks for a helper mode, null for a normal start.</summary>
    public static int? TryRun(IReadOnlyList<string> args)
    {
        string? Value(string name)
        {
            for (var i = 0; i < args.Count - 1; i++)
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
        bool Has(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (Has("--install"))
            return Install(Value("--user-sid") ?? Elevation.CurrentUserSid, Value("--autostart") == "1", Value("--pawnio") == "1");
        if (Has("--install-pawnio")) return InstallPawnIo();
        if (Value("--set-autostart") is { } on)
        {
            if (!Elevation.IsElevated) return NotElevated;
            return Installer.SetAutostart(Value("--user-sid") ?? Elevation.CurrentUserSid, on == "1") ? Ok : Failed;
        }
        if (Has("--uninstall")) return Uninstall();
        return null;
    }

    public static int Install(string sid, bool autostart, bool pawnIo)
    {
        if (!Elevation.IsElevated) return NotElevated;
        var r = Installer.Install(new InstallOptions(sid, autostart, pawnIo), AppEnv.ShortVersion, typeof(PlatformCommands).Assembly);
        if (!r.Ok) return Failed;
        return r.PawnIo == PawnIoInstallOutcome.RestartRequired ? OkRestartNeeded : Ok;
    }

    public static int InstallPawnIo()
    {
        if (!Elevation.IsElevated) return NotElevated;
        Directory.CreateDirectory(Installer.InstallDir);
        return PawnIo.Install(Installer.InstallDir) switch
        {
            PawnIoInstallOutcome.Installed => Ok,
            PawnIoInstallOutcome.RestartRequired => OkRestartNeeded,
            PawnIoInstallOutcome.Corrupted => Corrupted,
            _ => Failed,
        };
    }

    /// <summary>"Apps" → Uninstall: one question (keep history and settings?), then everything else goes.</summary>
    private static int Uninstall()
    {
        if (!Elevation.IsElevated) return Elevation.RunElevated(Environment.ProcessPath!, "--uninstall", TimeSpan.FromMinutes(5)) ?? Cancelled;

        var pl = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pl";
        var answer = MessageBoxW(IntPtr.Zero,
            pl ? "Odinstalować BetterVitals?\n\nTak — usuń też zapisaną historię i ustawienia.\nNie — zostaw je (np. na później).\nAnuluj — nic nie rób."
               : "Uninstall BetterVitals?\n\nYes — also delete the recorded history and settings.\nNo — keep them (for later).\nCancel — do nothing.",
            "BetterVitals", 0x00000003 /* YESNOCANCEL */ | 0x00000020 /* ICONQUESTION */ | 0x00010000 /* SETFOREGROUND */);
        if (answer is not (6 or 7)) return Cancelled; // IDYES / IDNO

        // the running copy exits first (it may hold the history files open)
        using (var instance = new SingleInstance(AppEnv.InstanceKey))
        {
            if (!instance.IsPrimary && instance.SendToPrimary("quit")) Thread.Sleep(2500);
        }
        Installer.Uninstall(removeData: answer == 6, removePawnIo: false, [AppEnv.RoamingDir, AppEnv.LocalDir]);
        return Ok;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);
}
