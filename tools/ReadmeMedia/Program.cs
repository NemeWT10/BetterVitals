using System.IO;
using System.Windows;
using BetterVitals.Core;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.UI;
using BetterVitals.UI.Explain;
using BetterVitals.UI.Themes;

namespace ReadmeMedia;

/// <summary>
/// <c>ReadmeMedia &lt;out dir&gt; [only=hero,history,…]</c>: the README screenshots, rendered from the app's own windows and
/// pages (never the screen) on a fake PC with a pinned clock, so every run gives the same pictures. English, Aurora.
/// Settings, logs and anything else the app writes go to &lt;out dir&gt;\work. Exit code 0 = every picture written.
/// Windows sit far off screen and are never activated; run it on the hidden desktop (tools\readme-media.ps1).
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: ReadmeMedia <out dir> [only=hero,history,overlay,overlay-editor,sensors]");
            return 2;
        }
        var outDir = Path.GetFullPath(args[0]);
        var only = args.Skip(1).Where(a => a.StartsWith("only=", StringComparison.OrdinalIgnoreCase))
            .SelectMany(a => a[5..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = only.Where(o => !Showcase.Names.Contains(o)).ToList();
        if (unknown.Count > 0)
        {
            Console.Error.WriteLine($"unknown picture(s): {string.Join(", ", unknown)}; pictures: {string.Join(", ", Showcase.Names)}");
            return 2;
        }

        // a fresh, private data folder: the user's settings, history and logs are never touched
        var work = Path.Combine(outDir, "work");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        AppEnv.Init(testMode: true, work);
        SettingsStore.Init(AppEnv.SettingsFile);
        SettingsStore.I.Mutate("general", s =>
        {
            s.General.Language = "en";
            s.General.Theme = "aurora";
            s.General.Mica = false;
            s.General.Animations = false;
            s.General.Explanations = true;
            s.General.TemperatureUnit = TemperatureUnit.Celsius;
        });

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/BetterVitals;component/UI/Themes/Base.xaml", UriKind.Relative)));
        Loc.I.SetLanguage("en");
        ThemeManager.I.Apply("aurora", mica: false);
        Motion.I.Update(false); // end states only, no half-faded entrances
        ExplainService.I.Enabled = true;

        var code = 1;
        app.DispatcherUnhandledException += (_, e) =>
        {
            Console.Error.WriteLine("ui exception: " + e.Exception);
            e.Handled = true;
        };
        app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                code = await Showcase.RunAsync(outDir, only);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                code = 1;
            }
            app.Shutdown(code);
        });
        app.Run();
        SettingsStore.I.Flush();
        return code;
    }
}
