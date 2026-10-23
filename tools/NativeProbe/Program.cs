namespace BetterVitals.Tools.NativeProbe;

internal static class Program
{
    private static int Main(string[] args)
    {
        // keep the app log (and nothing else) inside tests\output instead of the user's %LOCALAPPDATA%
        BetterVitals.Core.AppEnv.Init(true, Path.Combine(FindRepoRoot(), "tests", "output", "nativeprobe", "home"));
        if (args.Contains("--raw"))
        {
            RawDump.Run(Console.Out);
            return 0;
        }
        if (args.Contains("--samples"))
        {
            RawDump.Samples(Console.Out);
            return 0;
        }
        if (args.Contains("--nvme"))
        {
            RawDump.NvmeExperiments(Console.Out);
            return 0;
        }
        if (args.Contains("--lhm-names"))
        {
            LhmDump.Run(Console.Out);
            return 0;
        }
        // default: the full report -> tests\output\nativeprobe\report.md (repo root found by walking up to BetterVitals.sln)
        var outPath = args.SkipWhile(a => a != "--out").Skip(1).FirstOrDefault() ?? Path.Combine(FindRepoRoot(), "tests", "output", "nativeprobe", "report.md");
        return Report.Run(outPath, args.Contains("--quick"));
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "BetterVitals.sln"))) return dir.FullName;
        return Directory.GetCurrentDirectory();
    }
}
