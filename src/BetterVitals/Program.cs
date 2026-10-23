using BetterVitals.Core;
using BetterVitals.Core.Settings;
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
        Log.Info($"start {AppEnv.Version} test={AppEnv.TestMode}");

        using var instance = new SingleInstance(AppEnv.InstanceKey);
        if (!instance.IsPrimary)
        {
            // a second launch asks the running app to open the panel and exits before loading any UI
            if (instance.SendToPrimary("open:" + (options.OpenPage ?? ""))) return 0;
            Log.Warn("single instance: the primary did not answer, starting anyway");
        }

        SettingsStore.Init(AppEnv.SettingsFile);
        var app = new App(options, instance);
        app.InitializeComponent();
        var code = app.Run();
        SettingsStore.I.Flush();
        return code;
    }
}
