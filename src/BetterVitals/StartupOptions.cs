namespace BetterVitals;

/// <summary>
/// Command line:
/// <list type="bullet">
/// <item><c>--test-mode</c>: no tray icon, the preview hub instead of real sensors, its own single-instance key;</item>
/// <item><c>--data-dir &lt;dir&gt;</c>: settings, logs and history under &lt;dir&gt; (tests never touch the user's data);</item>
/// <item><c>--snapshots &lt;dir&gt; [pl|en|all] [theme|all]</c>: renders every window and page offscreen to PNG + report.txt, then exits
/// (implies --test-mode);</item>
/// <item><c>--tray-probe</c>: opens and closes the panel and flyout offscreen, waits for the tray rest and logs the memory (test mode);</item>
/// <item><c>--open &lt;page&gt;</c>: opens the panel at a page on start (a second launch forwards it to the running instance).</item>
/// </list>
/// </summary>
public sealed record StartupOptions
{
    public bool TestMode { get; init; }

    public string? DataDir { get; init; }

    public string? SnapshotDir { get; init; }

    /// <summary>"pl", "en" or null = both.</summary>
    public string? SnapshotLanguage { get; init; }

    /// <summary>A theme id or null = all five.</summary>
    public string? SnapshotTheme { get; init; }

    public bool TrayProbe { get; init; }

    public string? OpenPage { get; init; }

    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        var o = new StartupOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string? Next() => i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
            switch (a.ToLowerInvariant())
            {
                case "--test-mode":
                    o = o with { TestMode = true };
                    break;
                case "--data-dir":
                    o = o with { DataDir = Next() };
                    break;
                case "--snapshots":
                    var dir = Next();
                    var lang = Next();
                    var theme = Next();
                    o = o with
                    {
                        TestMode = true,
                        SnapshotDir = dir ?? "snapshots",
                        SnapshotLanguage = lang is "pl" or "en" ? lang : null,
                        SnapshotTheme = theme is null or "all" ? null : theme,
                    };
                    break;
                case "--tray-probe":
                    o = o with { TestMode = true, TrayProbe = true };
                    break;
                case "--open":
                    o = o with { OpenPage = Next() ?? "overview" };
                    break;
            }
        }
        return o;
    }
}
