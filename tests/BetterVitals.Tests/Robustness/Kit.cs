using BetterVitals.Core;
using BetterVitals.Core.Catalog;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.History;
using BetterVitals.Sensors.Derived;
using BetterVitals.Sensors.Engine;
using BetterVitals.Sensors.Fake;
using Xunit;

namespace BetterVitals.Tests.Robustness;

// Shared pieces of the robustness scenarios (PROJEKT §12). Everything runs headless: a simulated wall clock drives the real
// sensor engine and the real history service, the overlay window (where used) is never shown.

/// <summary>The scenarios that run the whole engine + history for a long time share one collection (one at a time).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HeavyCollection
{
    public const string Name = "Robustness-heavy";
}

/// <summary>A simulated wall clock (UTC ticks); tests move it forward and backward.</summary>
internal sealed class SimClock(DateTime start)
{
    private long _now = start.Ticks;

    public long Now
    {
        get => Volatile.Read(ref _now);
        set => Volatile.Write(ref _now, value);
    }

    public DateTime Utc => new(Now, DateTimeKind.Utc);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _now, by.Ticks);
}

/// <summary>
/// A <see cref="TimeProvider"/> over a <see cref="SimClock"/>; timers never fire (the tests flush by hand). Elapsed time
/// follows <paramref name="monotonic"/> when given (real time that passed, whatever the wall clock does), else the clock.
/// </summary>
internal sealed class SimTime(SimClock clock, Func<long>? monotonic = null) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(clock.Now, TimeSpan.Zero);

    public override long GetTimestamp() => monotonic?.Invoke() ?? clock.Now;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NullTimer();

    private sealed class NullTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// A sensor source that delegates to another one and counts what the engine does to it.
/// </summary>
internal sealed class SpySource : ISensorSource
{
    private readonly ISensorSource _inner;
    private int _discovers, _updates, _suspends, _resumes, _disposes;

    public SpySource(ISensorSource inner, string? name = null)
    {
        _inner = inner;
        Name = name ?? inner.Name;
        inner.InventoryChanged += () => InventoryChanged?.Invoke();
    }

    public string Name { get; }

    public SourceStatus Status => _inner.Status;

    public event Action? InventoryChanged;

    public int Discovers => Volatile.Read(ref _discovers);

    public int Updates => Volatile.Read(ref _updates);

    public int Suspends => Volatile.Read(ref _suspends);

    public int Resumes => Volatile.Read(ref _resumes);

    public int Disposes => Volatile.Read(ref _disposes);

    public SourceInventory Discover()
    {
        Interlocked.Increment(ref _discovers);
        return _inner.Discover();
    }

    public void Update(ReadOnlySpan<SensorDescriptor> requested, Span<float> values)
    {
        Interlocked.Increment(ref _updates);
        _inner.Update(requested, values);
    }

    public void Suspend()
    {
        Interlocked.Increment(ref _suspends);
        _inner.Suspend();
    }

    public void Resume()
    {
        Interlocked.Increment(ref _resumes);
        _inner.Resume();
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _disposes);
        _inner.Dispose();
    }
}

/// <summary>A tiny scripted source: <c>count</c> sensors on one node, values from a function, failures and unplugging on demand.</summary>
internal sealed class ScriptedSource : ISensorSource
{
    private readonly string _hardwareName;
    private readonly int _count;
    private readonly SensorKind _kind;
    private int _discovers, _updates;
    private readonly int[] _reads;

    public ScriptedSource(string name, string hardwareName, int count, SensorKind kind = SensorKind.Temperature)
    {
        Name = name;
        _hardwareName = hardwareName;
        _count = count;
        _kind = kind;
        _reads = new int[count];
    }

    /// <summary>How many times the sensor with this ordinal was read.</summary>
    public int Reads(int ordinal) => Volatile.Read(ref _reads[ordinal]);

    public string Name { get; }

    public SourceStatus Status => new(SourceState.Ok);

    public event Action? InventoryChanged;

    /// <summary>Value of the sensor with this ordinal.</summary>
    public Func<int, float> Value { get; set; } = i => 40 + i;

    /// <summary>Update throws this (null = works).</summary>
    public volatile Exception? FailUpdate;

    /// <summary>The next calls of Discover throw (counts down).</summary>
    public int FailDiscoveries;

    /// <summary>false = the device is unplugged: Discover reports nothing.</summary>
    public volatile bool Plugged = true;

    public int Discovers => Volatile.Read(ref _discovers);

    public int Updates => Volatile.Read(ref _updates);

    public string HardwareId => SensorIds.Hardware(HardwareKind.Other, _hardwareName, 0);

    public string SensorId(int ordinal) => SensorIds.Sensor(HardwareId, _kind, $"S{ordinal}");

    public void RaiseInventoryChanged() => InventoryChanged?.Invoke();

    public SourceInventory Discover()
    {
        Interlocked.Increment(ref _discovers);
        if (Interlocked.Decrement(ref FailDiscoveries) >= 0) throw new InvalidOperationException("discovery failed (test)");
        Interlocked.Exchange(ref FailDiscoveries, 0);
        if (!Plugged) return SourceInventory.Empty;
        var hw = new HardwareDescriptor { Id = HardwareId, Name = _hardwareName, Kind = HardwareKind.Other, Source = Name };
        var sensors = Enumerable.Range(0, _count).Select(i => new SensorDescriptor
        {
            Id = SensorId(i),
            HardwareId = HardwareId,
            HardwareKind = HardwareKind.Other,
            Name = $"S{i}",
            Kind = _kind,
            Order = i,
        }).ToList();
        return new SourceInventory([hw], sensors);
    }

    public void Update(ReadOnlySpan<SensorDescriptor> requested, Span<float> values)
    {
        Interlocked.Increment(ref _updates);
        if (FailUpdate is { } ex) throw ex;
        foreach (var d in requested)
        {
            Interlocked.Increment(ref _reads[d.Order]);
            values[d.Index] = Value(d.Order);
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Collects the app's log lines (level, message) while it lives. Tests that drive the engine on their own thread read
/// <see cref="Mine"/>: only what was logged on that thread, so parallel tests cannot disturb each other.
/// </summary>
internal sealed class LogCapture : IDisposable
{
    private readonly List<(string Level, string Message, int Thread)> _lines = [];
    private readonly int _owner = Environment.CurrentManagedThreadId;

    public LogCapture() => Log.Written += OnLine;

    private void OnLine(string level, string message)
    {
        lock (_lines) _lines.Add((level, message, Environment.CurrentManagedThreadId));
    }

    /// <summary>Everything logged by any thread.</summary>
    public (string Level, string Message)[] Lines
    {
        get
        {
            lock (_lines) return _lines.Select(l => (l.Level, l.Message)).ToArray();
        }
    }

    /// <summary>What the creating thread logged (the engine runs on it in the simulated scenarios).</summary>
    public (string Level, string Message)[] Mine
    {
        get
        {
            lock (_lines) return _lines.Where(l => l.Thread == _owner).Select(l => (l.Level, l.Message)).ToArray();
        }
    }

    public int MineWith(string level, string text) => Mine.Count(l => l.Level == level && l.Message.Contains(text, StringComparison.Ordinal));

    /// <summary>Lines (any thread) of a level whose text contains <paramref name="text"/>.</summary>
    public int Count(string level, string text) => Lines.Count(l => l.Level == level && l.Message.Contains(text, StringComparison.Ordinal));

    public void Dispose() => Log.Written -= OnLine;
}

/// <summary>A scratch folder under tests/output/qa (git-ignored); removed afterwards unless BV_QA_KEEP=1.</summary>
internal sealed class QaDir : IDisposable
{
    public QaDir(string name)
    {
        Path = System.IO.Path.Combine(Root, $"{name}-{Guid.NewGuid():N}"[..Math.Min(name.Length + 9, 60)]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public static string Root => System.IO.Path.Combine(RepoRoot, "tests", "output", "qa");

    public static string RepoRoot
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
                if (File.Exists(System.IO.Path.Combine(d.FullName, "BetterVitals.sln"))) return d.FullName;
            throw new InvalidOperationException("repository root (BetterVitals.sln) not found");
        }
    }

    public string File_(string name) => System.IO.Path.Combine(Path, name);

    public string[] Files() => Directory.Exists(Path) ? Directory.GetFiles(Path).Select(f => System.IO.Path.GetFileName(f)).Order().ToArray() : [];

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("BV_QA_KEEP") == "1") return;
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
        catch (UnauthorizedAccessException)
        {
            // best effort
        }
    }
}

/// <summary>The real engine over a fake PC on a simulated clock, driven step by step (no engine thread).</summary>
internal sealed class SimPc : IDisposable
{
    public static readonly DateTime DefaultStart = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    private int _frames;

    /// <param name="prepare">Runs before the first discovery (a device that is not plugged in yet…).</param>
    public SimPc(FakeProfile profile = FakeProfile.DesktopAmdNvidia, bool derived = true, DateTime? start = null, Action<FakeSensorSource>? prepare = null)
    {
        Clock = new SimClock(start ?? DefaultStart);
        Fake = new FakeSensorSource(profile, 1, () => Clock.Now);
        prepare?.Invoke(Fake);
        Source = new SpySource(Fake);
        ISensorSource[] sources = derived ? [Source, new DerivedSource()] : [Source];
        Engine = new SensorEngine(sources, () => Clock.Now);
        Engine.FrameReady += _ => Interlocked.Increment(ref _frames);
        Engine.DiscoverNow();
    }

    private SimPc(ISensorSource[] sources, bool custom)
    {
        Clock = new SimClock(DefaultStart);
        Fake = new FakeSensorSource(FakeProfile.Minimal, 1, () => Clock.Now);
        Source = new SpySource(Fake);
        Engine = new SensorEngine(sources, () => Clock.Now);
        Engine.FrameReady += _ => Interlocked.Increment(ref _frames);
        Engine.DiscoverNow();
    }

    /// <summary>An engine over the given sources only (scripted sources, failures…).</summary>
    public static SimPc With(params ISensorSource[] sources) => new(sources, custom: true);

    public SimClock Clock { get; }

    public FakeSensorSource Fake { get; }

    public SpySource Source { get; }

    public SensorEngine Engine { get; }

    /// <summary>Frames published so far.</summary>
    public int Frames => Volatile.Read(ref _frames);

    /// <summary>Engine steps of <paramref name="step"/> each until <paramref name="duration"/> has passed; <paramref name="each"/> runs after every step.</summary>
    public void Run(TimeSpan duration, TimeSpan? step = null, Action? each = null)
    {
        var s = step ?? SensorEngine.BaseTick;
        var end = Clock.Now + duration.Ticks;
        while (Clock.Now < end)
        {
            Clock.Advance(s);
            Engine.Step();
            each?.Invoke();
        }
    }

    /// <summary>A sensor by hardware name, kind and name — also while its hardware is unplugged (the registry keeps it).</summary>
    public SensorDescriptor Sensor(string hardwareName, SensorKind kind, string name)
        => Engine.Sensors.Single(s => s.Name == name && s.Kind == kind && s.HardwareId.Contains("/" + SensorIds.Slug(hardwareName) + "/", StringComparison.Ordinal));

    public string HardwareId(string name) => Engine.Hardware.Single(h => h.Name == name).Id;

    /// <summary>Slot of every sensor id (the slots must never change).</summary>
    public Dictionary<string, int> Slots() => Engine.Sensors.ToDictionary(s => s.Id, s => s.Index, StringComparer.Ordinal);

    public void Dispose() => Engine.Dispose();
}

/// <summary>
/// <see cref="SimPc"/> + the history service wired as <c>AppHost</c> wires them: recording of the key sensors, a flush
/// once a minute (the timer's job), and the same calls before sleep / after resume.
/// </summary>
internal sealed class SimApp : IDisposable
{
    private long _monotonic; // the flush timer counts real elapsed time, whatever the wall clock does
    private long _nextFlush;

    public SimApp(string name, FakeProfile profile = FakeProfile.DesktopAmdNvidia, int intervalSeconds = 2, bool record = true,
        Func<SimPc, IEnumerable<string>>? sensors = null, DateTime? start = null)
    {
        Pc = new SimPc(profile, derived: true, start);
        Dir = new QaDir(name);
        Warnings = [];
        History = new HistoryService(Pc.Engine, System.IO.Path.Combine(Dir.Path, "history"), new HistoryOptions
        {
            Time = new SimTime(Pc.Clock, () => Interlocked.Read(ref _monotonic)),
            AutoFlush = false,
            BackgroundStart = false,
            FreeSpace = _ => null,
            Warn = m =>
            {
                lock (Warnings) Warnings.Add(m);
            },
        });
        Recording = new RecordingSettings
        {
            Enabled = record,
            IntervalSeconds = intervalSeconds,
            SensorIds = new HashSet<string>(sensors?.Invoke(Pc) ?? KeySensors(Pc), StringComparer.Ordinal),
        };
        History.Configure(Recording);
        _nextFlush = TimeSpan.TicksPerMinute;
    }

    public SimPc Pc { get; }

    public QaDir Dir { get; }

    public HistoryService History { get; private set; }

    public RecordingSettings Recording { get; }

    public List<string> Warnings { get; }

    public string HistoryDir => System.IO.Path.Combine(Dir.Path, "history");

    /// <summary>The "Key" preset like the app applies it on first run.</summary>
    public static IEnumerable<string> KeySensors(SimPc pc)
    {
        var items = FriendlyCatalog.Resolve(pc.Engine.Hardware, pc.Engine.Sensors);
        return RecordingPolicy.MissingKeySensors(new RecordingSettings(), items, new HashSet<string>());
    }

    /// <summary>Runs the engine; flushes the history once a minute like the app's timer does.</summary>
    public void Run(TimeSpan duration, TimeSpan? step = null, Action? each = null)
    {
        var s = step ?? TimeSpan.FromSeconds(1);
        Pc.Run(duration, s, () =>
        {
            each?.Invoke();
            _monotonic += s.Ticks;
            if (_monotonic < _nextFlush) return;
            History.Flush();
            _nextFlush = _monotonic + TimeSpan.TicksPerMinute;
        });
    }

    /// <summary>What <c>AppHost.OnSuspending</c> does.</summary>
    public void Sleep()
    {
        Pc.Engine.NotifySuspend();
        History.Suspend();
    }

    /// <summary>What <c>AppHost.OnResumed</c> does.</summary>
    public void Wake()
    {
        Pc.Engine.NotifyResume();
        History.Resume();
        _nextFlush = _monotonic + TimeSpan.TicksPerMinute;
    }

    /// <summary>Series of one sensor over a range.</summary>
    public Task<IReadOnlyList<Core.History.HistoryPoint>> Series(string id, DateTime from, DateTime to, int max = 100_000)
        => History.GetSeriesAsync(id, from, to, max);

    public void Dispose()
    {
        History.Dispose();
        Pc.Dispose();
        Dir.Dispose();
    }
}
