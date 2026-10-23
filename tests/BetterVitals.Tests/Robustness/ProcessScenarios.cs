using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using BetterVitals.Core.Settings;
using BetterVitals.History;
using BetterVitals.Services;
using Xunit;
using Xunit.Abstractions;

namespace BetterVitals.Tests.Robustness;

/// <summary>
/// The whole app, as a process: <c>BetterVitals.exe --test-mode --data-dir …</c> (fake PC, no tray icon, no windows) started on
/// tools/HiddenDesktopRunner's private desktop, so nothing can appear on the user's screen. Settings and history go to a
/// scratch folder under tests/output/qa.
/// </summary>
[Collection(HeavyCollection.Name)]
public sealed class ProcessScenarios(ITestOutputHelper output)
{
    private static readonly Lock BuildGate = new();

    private static string Runner()
    {
#if DEBUG
        const string config = "Debug";
#else
        const string config = "Release";
#endif
        var root = QaDir.RepoRoot;
        var project = Path.Combine(root, "tools", "HiddenDesktopRunner", "HiddenDesktopRunner.csproj");
        var exe = Path.Combine(root, "tools", "HiddenDesktopRunner", "bin", config, "net10.0-windows", "win-x64", "HiddenDesktopRunner.exe");
        lock (BuildGate)
        {
            if (File.Exists(exe)) return exe;
            var psi = new ProcessStartInfo("dotnet", $"build \"{project}\" -c {config} -nologo -v q")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var build = Process.Start(psi)!;
            var log = build.StandardOutput.ReadToEnd() + build.StandardError.ReadToEnd();
            build.WaitForExit();
            if (build.ExitCode != 0 || !File.Exists(exe)) throw new InvalidOperationException("Building HiddenDesktopRunner failed: " + log);
            return exe;
        }
    }

    private static string AppExe => Path.Combine(AppContext.BaseDirectory, "BetterVitals.exe");

    /// <summary>The key AppEnv derives from the data folder (the single-instance mutex and pipe of a test instance).</summary>
    private static string InstanceKey(string home)
    {
        var roaming = Path.Combine(Path.GetFullPath(home), "roaming");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(roaming.ToUpperInvariant() + "|test"));
        return "BetterVitals-" + Convert.ToHexString(hash, 0, 8);
    }

    private sealed class AppProcess : IDisposable
    {
        private readonly Process _runner;
        private readonly string _home;

        private AppProcess(string home)
        {
            _home = home;
            var psi = new ProcessStartInfo(Runner())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "--timeout", "150", "--", AppExe, "--test-mode", "--data-dir", home }) psi.ArgumentList.Add(a);
            _runner = new Process { StartInfo = psi };
            _runner.OutputDataReceived += (_, _) => { };
            _runner.ErrorDataReceived += (_, _) => { };
            _runner.Start();
            _runner.BeginOutputReadLine();
            _runner.BeginErrorReadLine();
        }

        public string LogPath => Path.Combine(_home, "local", "logs", "app.log");

        public string[] LogLines
        {
            get
            {
                try
                {
                    using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var sr = new StreamReader(fs);
                    return sr.ReadToEnd().Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
                }
                catch (IOException)
                {
                    return [];
                }
            }
        }

        /// <summary>Starts the app and waits until it says it is ready (the n-th "ui ready" line of the log).</summary>
        public static AppProcess Start(string home, ITestOutputHelper output)
        {
            var before = File.Exists(Path.Combine(home, "local", "logs", "app.log"))
                ? File.ReadAllLines(Path.Combine(home, "local", "logs", "app.log")).Count(l => l.Contains("ui ready", StringComparison.Ordinal))
                : 0;
            var app = new AppProcess(home);
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (app.LogLines.Count(l => l.Contains("ui ready", StringComparison.Ordinal)) <= before)
            {
                if (app._runner.HasExited || DateTime.UtcNow > deadline)
                {
                    output.WriteLine(string.Join(Environment.NewLine, app.LogLines));
                    var code = app._runner.HasExited ? app._runner.ExitCode.ToString() : "still running";
                    app.Dispose();
                    throw new InvalidOperationException($"the app did not get ready (runner: {code})");
                }
                Thread.Sleep(100);
            }
            return app;
        }

        /// <summary>Asks the running instance to quit (the same message an update or the uninstaller sends).</summary>
        public void RequestQuit()
        {
            using var client = new NamedPipeClientStream(".", InstanceKey(_home) + "-pipe", PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(5000);
            client.Write(Encoding.UTF8.GetBytes("quit"));
            client.Flush();
        }

        public int WaitForExit(int seconds)
        {
            Assert.True(_runner.WaitForExit(seconds * 1000), "the app did not exit");
            _runner.WaitForExit();
            return _runner.ExitCode;
        }

        /// <summary>Power cut: the runner dies, its job object takes the app with it.</summary>
        public void Kill()
        {
            _runner.Kill(entireProcessTree: true);
            _runner.WaitForExit(5000);
            // the app is gone when its single-instance mutex can be taken
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                using var probe = new SingleInstance(InstanceKey(_home));
                if (probe.IsPrimary) return;
                Thread.Sleep(100);
            }
            throw new InvalidOperationException("the app survived the kill");
        }

        public void Dispose()
        {
            try
            {
                if (!_runner.HasExited) _runner.Kill(entireProcessTree: true);
                _runner.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
                // already gone
            }
            _runner.Dispose();
        }
    }

    private static string TodayRaw() => $"raw-{DateTime.UtcNow:yyyyMMdd}.bvr";

    [Fact]
    public async Task The_app_starts_over_damaged_files_records_exits_cleanly_survives_a_power_cut_and_starts_again()
    {
        using var dir = new QaDir("process");
        var home = dir.Path;
        var roaming = Path.Combine(home, "roaming");
        var history = Path.Combine(home, "local", "history");
        Directory.CreateDirectory(roaming);
        Directory.CreateDirectory(history);

        // ── what a bad day leaves behind: damaged settings and damaged history ──
        var rnd = new Random(1);
        var garbage = new byte[700];
        rnd.NextBytes(garbage);
        File.WriteAllBytes(Path.Combine(roaming, "settings.json"), garbage);
        foreach (var name in new[] { TodayRaw(), "catalog.bvc", "min-" + DateTime.UtcNow.ToString("yyyyMM") + ".bvm", "hour-" + DateTime.UtcNow.Year + ".bvh" })
        {
            var junk = new byte[rnd.Next(300, 3000)];
            rnd.NextBytes(junk);
            File.WriteAllBytes(Path.Combine(history, name), junk);
        }
        File.WriteAllBytes(Path.Combine(history, "raw-20250101.bvr"), []);

        // ── run 1: starts anyway, records, quits cleanly ──
        var t1 = DateTime.UtcNow.AddSeconds(-1);
        using (var app = AppProcess.Start(home, output))
        {
            Assert.Contains(app.LogLines, l => l.Contains("settings: unreadable", StringComparison.Ordinal));
            Thread.Sleep(7000); // the key preset is recorded: a few samples
            app.RequestQuit();
            Assert.Equal(0, app.WaitForExit(30));
            var lines = app.LogLines;
            output.WriteLine(string.Join(Environment.NewLine, lines.Where(l => !l.StartsWith("   ", StringComparison.Ordinal)).Take(30)));
            Assert.Contains(lines, l => l.Contains("host: stopped", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.Contains("exit 0", StringComparison.Ordinal));
        }
        Assert.Equal(garbage, File.ReadAllBytes(Path.Combine(roaming, "settings.bad.json")));
        var settings = SettingsStore.Load(Path.Combine(roaming, "settings.json"));
        Assert.True(settings.Recording.SensorIds.Count > 10, "the key preset was applied and saved");
        Assert.True(settings.Recording.KeyPresetApplied);
        var id = settings.Recording.SensorIds.Order().First(i => i.Contains("/temperature/", StringComparison.Ordinal));
        var points1 = await ReadBack(history, id, t1, DateTime.UtcNow.AddMinutes(1));
        output.WriteLine($"run 1 recorded {points1} points of {id}");
        Assert.InRange(points1, 2, 12);

        // ── run 2: normal start with repaired files and the overlay on (its window lives on the hidden desktop), then the plug is pulled ──
        var settingsPath = Path.Combine(roaming, "settings.json");
        var store = SettingsStore.CreateDetached(settingsPath, settings);
        store.Mutate("overlay", s => s.Overlay.Enabled = true);
        store.Flush();
        var t2 = DateTime.UtcNow;
        using (var app = AppProcess.Start(home, output))
        {
            Thread.Sleep(4000);
            app.Kill();
        }

        // ── run 3: after the power cut ──
        var t3 = DateTime.UtcNow;
        using (var app = AppProcess.Start(home, output))
        {
            Thread.Sleep(4500);
            app.RequestQuit();
            Assert.Equal(0, app.WaitForExit(30));
            Assert.DoesNotContain(app.LogLines, l => l.Contains("[ERROR]", StringComparison.Ordinal) && !l.Contains("settings: unreadable", StringComparison.Ordinal));
            Assert.Contains(app.LogLines, l => l.Contains("[INFO] overlay: surface", StringComparison.Ordinal)); // the overlay window was created ...
            Assert.DoesNotContain(app.LogLines, l => (l.Contains("[WARN] overlay:", StringComparison.Ordinal) || l.Contains("[ERROR] overlay:", StringComparison.Ordinal))); // ... and had nothing to complain about
        }
        Assert.Equal(garbage, File.ReadAllBytes(Path.Combine(roaming, "settings.bad.json")));
        var again = SettingsStore.Load(Path.Combine(roaming, "settings.json"));
        Assert.Equal(settings.Recording.SensorIds.Count, again.Recording.SensorIds.Count);
        Assert.True(again.Overlay.Enabled);
        var points3 = await ReadBack(history, id, t3, DateTime.UtcNow.AddMinutes(1));
        var pointsAll = await ReadBack(history, id, t1, DateTime.UtcNow.AddMinutes(1));
        output.WriteLine($"run 3 recorded {points3} points; all runs together {pointsAll} (the killed run's unwritten minute may be gone: {t3 - t2})");
        Assert.InRange(points3, 1, 12);
        Assert.True(pointsAll >= points1 + points3, "what was written before stays readable after a power cut and a restart");
    }

    /// <summary>Opens the history folder the way the app does (read-only here) and counts the points of a sensor.</summary>
    private static async Task<int> ReadBack(string historyDir, string sensorId, DateTime from, DateTime to)
    {
        // a copy: the folder may hold the next run's files while we look
        using var scratch = new QaDir("process-read");
        foreach (var f in Directory.GetFiles(historyDir)) File.Copy(f, Path.Combine(scratch.Path, Path.GetFileName(f)));
        using var pc = new SimPc();
        using var svc = new HistoryService(pc.Engine, scratch.Path, new HistoryOptions { AutoFlush = false, BackgroundStart = false });
        var series = await svc.GetSeriesAsync(sensorId, from, to, 100_000);
        return series.Count;
    }
}
