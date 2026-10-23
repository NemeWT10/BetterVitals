using System.Diagnostics;

namespace BetterVitals.Tests.Fps;

/// <summary>Runs tools/FakeGame (hidden desktop, never on screen) and waits for its first present.</summary>
internal sealed class FakeGameProcess : IDisposable
{
    private static readonly Lock BuildGate = new();
    private readonly Process _process;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _output = [];

    private FakeGameProcess(string args)
    {
        var psi = new ProcessStartInfo(ExePath(), args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_output) _output.Add(e.Data);
            if (e.Data.StartsWith("READY", StringComparison.Ordinal)) _ready.TrySetResult(true);
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) lock (_output) _output.Add("ERR " + e.Data);
        };
        _process.Exited += (_, _) => _ready.TrySetResult(false);
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public int Pid => _process.Id;

    public string Output
    {
        get
        {
            lock (_output) return string.Join(Environment.NewLine, _output);
        }
    }

    /// <summary>Starts FakeGame and waits until it presented once.</summary>
    public static FakeGameProcess Start(string args)
    {
        var game = new FakeGameProcess(args);
        if (!game._ready.Task.Wait(TimeSpan.FromSeconds(20)) || !game._ready.Task.Result)
        {
            var output = game.Output;
            game.Dispose();
            throw new InvalidOperationException("FakeGame did not start: " + output);
        }
        return game;
    }

    public bool WaitForExit(TimeSpan timeout)
    {
        if (!_process.WaitForExit(timeout)) return false;
        _process.WaitForExit(); // flush redirected output
        return true;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited) _process.Kill();
            _process.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
            // never started
        }
        _process.Dispose();
    }

    /// <summary>tools/FakeGame/bin/&lt;config&gt;/…/FakeGame.exe; built on first use when missing.</summary>
    private static string ExePath()
    {
#if DEBUG
        const string config = "Debug";
#else
        const string config = "Release";
#endif
        var root = RepoRoot();
        var project = Path.Combine(root, "tools", "FakeGame", "FakeGame.csproj");
        var exe = Path.Combine(root, "tools", "FakeGame", "bin", config, "net10.0-windows", "win-x64", "FakeGame.exe");
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
            if (build.ExitCode != 0 || !File.Exists(exe)) throw new InvalidOperationException("Building FakeGame failed: " + log);
            return exe;
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BetterVitals.sln"))) return dir.FullName;
        }
        throw new InvalidOperationException("Repository root (BetterVitals.sln) not found.");
    }
}
