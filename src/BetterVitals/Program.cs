using BetterVitals.Core;
using BetterVitals.Core.Settings;
using BetterVitals.Platform;
using BetterVitals.Services;

namespace BetterVitals;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = StartupOptions.Parse(args);
        AppEnv.Init(options.TestMode, options.DataDir);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error("fatal: unhandled exception", e.ExceptionObject as Exception);
            try
            {
                SettingsStore.I?.Flush(); // atomic write: a crash never costs the settings
            }
            catch
            {
                // nothing more to do
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("task: unobserved exception", e.Exception);
            e.SetObserved();
        };

        // helper modes (install / PawnIO / autostart / uninstall) do their job and exit, before anything loads WPF
        if (PlatformCommands.TryRun(args) is { } helperExit) return helperExit;

        Log.Info($"start {AppEnv.Version} test={AppEnv.TestMode} elevated={Elevation.IsElevated}");

        // WPF's native libraries only from a safe folder (never the user's %TEMP% for an elevated process); also before
        // any early exit below: the UI's module initializers have loaded WPF, whose shutdown needs its C runtime
        NativeRuntime.Prepare(typeof(Program).Assembly, AppEnv.ShortVersion);

        var startedByTask = args.Contains(ScheduledTasks.FromTaskArg, StringComparer.OrdinalIgnoreCase);

        // a newer exe next to an installed copy: offer to put it in place of the installed one, then hand over to it
        if (!AppEnv.TestMode && options.DataDir is null && !startedByTask && InstalledUpdate.TryRun() is { } updateExit) return updateExit;

        var instance = AcquireInstance(waitForPrevious: startedByTask);
        if (!instance.IsPrimary)
        {
            // a second launch asks the running app to open the panel and exits before loading any UI
            if (instance.SendToPrimary("open:" + (options.OpenPage ?? ""))) return 0;
            Log.Warn("single instance: the primary did not answer, starting anyway");
        }
        AppHost.Instance = instance;

        SettingsStore.Init(AppEnv.SettingsFile);
        var s = SettingsStore.I.Current;
        var action = Startup.Decide(AppEnv.TestMode, Elevation.IsElevated, s.FirstRunDone, s.Driver.Mode,
            Installer.IsInstalled, !AppEnv.TestMode && Installer.HasLauncher(Elevation.CurrentUserSid), startedByTask);
        if (action == StartAction.RelaunchViaTask)
        {
            // the elevated, installed copy takes over without a UAC prompt
            AppHost.ReleaseSingleInstance();
            if (ScheduledTasks.Start(ScheduledTasks.LauncherName(Elevation.CurrentUserSid))) return 0;
            Log.Warn("startup: the launcher task did not start; running here with limited sensors");
            instance = AcquireInstance(waitForPrevious: false);
            AppHost.Instance = instance;
        }

        var app = new App(options, instance) { NeedsFirstRun = action == StartAction.FirstRun };
        app.InitializeComponent();
        var code = app.Run();
        SettingsStore.I.Flush();
        AppHost.ReleaseSingleInstance();
        return code;
    }

    /// <summary>
    /// The single-instance mutex; after an update or an elevation hand-over the previous copy may still be closing,
    /// so a start from the launcher task waits up to 5 s for it.
    /// </summary>
    private static SingleInstance AcquireInstance(bool waitForPrevious)
    {
        var instance = new SingleInstance(AppEnv.InstanceKey);
        var deadline = Environment.TickCount64 + 5000;
        while (waitForPrevious && !instance.IsPrimary && Environment.TickCount64 < deadline)
        {
            instance.Dispose();
            Thread.Sleep(200);
            instance = new SingleInstance(AppEnv.InstanceKey);
        }
        return instance;
    }
}
