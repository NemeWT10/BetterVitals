using BetterVitals.Core;

namespace BetterVitals;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var testMode = args.Contains("--test-mode", StringComparer.OrdinalIgnoreCase);
        string? home = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--data-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) home = args[++i];
        }
        AppEnv.Init(testMode, home);
        Log.Info($"start {AppEnv.Version} test={AppEnv.TestMode}");

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
