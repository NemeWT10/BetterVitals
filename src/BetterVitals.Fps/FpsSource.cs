using System.Diagnostics;
using System.Security.Principal;
using BetterVitals.Core;
using BetterVitals.Core.Sensors;

namespace BetterVitals.Fps;

/// <summary>
/// FPS and frame times of the foreground app, passively from ETW present events (no injection, no hooks, no handles
/// to the game process). One hardware node "Foreground app" with five sensors; see <see cref="FrameStats"/> for the
/// definitions.
/// <para>
/// Lifecycle: <see cref="Start"/> at app start creates the ETW session without providers (anti-cheats such as EA
/// AntiCheat block creating sessions once they run, not enabling providers). Providers are enabled on the first
/// <see cref="Update"/> that asks for an FPS sensor and disabled ~5 s after the last such call, so the cost is zero
/// while nobody watches FPS. <see cref="Discover"/> calls <see cref="Start"/> when the owner did not.
/// </para>
/// </summary>
public sealed class FpsSource : ISensorSource
{
    /// <summary>ETW session name used by the app.</summary>
    public const string DefaultSessionName = "BetterVitals-Present";

    /// <summary>Providers go off after this long without an FPS request.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(5);

    public const string HardwareId = "game/foreground/0";
    public const string StatusNoAccess = "source.fps.noAccess";
    public const string StatusBlocked = "source.fps.blocked";

    private enum Metric { Framerate, FrameTime, Low1, Low01, FrameTimeMax }

    private readonly Lock _gate = new();
    private readonly string _sessionName;
    private readonly ForegroundTracker _foreground = new();
    private readonly SourceInventory _inventory;
    private PresentRing? _ring;
    private PresentSession? _session;
    private FrameStatsCalculator? _calc;
    private long[]? _snapTimes;
    private uint[]? _snapKeys;
    private Timer? _idleTimer;
    private volatile bool _enabled;
    private long _lastDemandQpc;
    private bool _started;
    private bool _disposed;
    private bool _loggedUpdateError;
    private volatile object? _status;
    private volatile string? _processName;
    private int _processId;

    // reset marker: entries before it belong to a previous target / provider run
    private long _fromIndex;
    private long _fromQpc;
    private uint _target;
    private long _cacheIndex = -1;
    private FrameStats _cache = FrameStats.None;

    /// <param name="sessionName">ETW session name; tests use their own so they never touch the app's session.</param>
    public FpsSource(string? sessionName = null)
    {
        // a test-mode instance gets a name of its own data folder: parallel test runs never stop each other's session
        _sessionName = sessionName ?? (AppEnv.TestMode ? $"{DefaultSessionName}-{AppEnv.InstanceKey}" : DefaultSessionName);
        var hw = new HardwareDescriptor { Id = HardwareId, Name = "Foreground app", Kind = HardwareKind.Game, Source = "fps" };
        _inventory = new SourceInventory([hw],
        [
            Sensor("Framerate", SensorKind.Fps, Metric.Framerate),
            Sensor("Frametime", SensorKind.FrameTime, Metric.FrameTime),
            Sensor("1% Low", SensorKind.Fps, Metric.Low1),
            Sensor("0.1% Low", SensorKind.Fps, Metric.Low01),
            Sensor("Frametime Max", SensorKind.FrameTime, Metric.FrameTimeMax),
        ]);
    }

    private static SensorDescriptor Sensor(string name, SensorKind kind, Metric metric) => new()
    {
        Id = SensorIds.Sensor(HardwareId, kind, name),
        HardwareId = HardwareId,
        Name = name,
        Kind = kind,
        HardwareKind = HardwareKind.Game,
        Order = (int)metric,
        SourceTag = metric,
    };

    public string Name => "fps";

    /// <summary>Ok while the session runs; Unavailable with <see cref="StatusNoAccess"/> / <see cref="StatusBlocked"/>.</summary>
    public SourceStatus Status
    {
        get => _status is SourceStatus s ? s : default;
        private set => _status = value; // boxed: readers on any thread never see a torn value
    }

    /// <summary>The inventory is fixed; never raised.</summary>
    public event Action? InventoryChanged { add { } remove { } }

    /// <summary>Executable name of the measured process ("game.exe"), null when there is none. Any thread.</summary>
    public string? ForegroundProcessName => _processName;

    /// <summary>PID of the measured process, 0 when there is none. Any thread.</summary>
    public int ForegroundProcessId => Volatile.Read(ref _processId);

    /// <summary>Test hook: measure this process instead of the foreground one (it need not have a window).</summary>
    internal int? TargetPidOverride { get; set; }

    /// <summary>True while the present providers are enabled.</summary>
    internal bool ProvidersEnabled => _enabled;

    internal PresentSession? Session => _session;

    internal PresentRing? Ring => _ring;

    /// <summary>The fixed inventory without starting the session (tests).</summary>
    internal SourceInventory Inventory => _inventory;

    /// <summary>Test hook: measure from <paramref name="ring"/> without an ETW session (pure logic tests).</summary>
    internal void UseRingForTests(PresentRing ring) => _ring = ring;

    /// <summary>
    /// Creates the ETW session (no providers) and its consumer thread. Call once at app start, before any game runs.
    /// Never throws; failures end in <see cref="SourceState.Unavailable"/>. Later calls do nothing — use
    /// <see cref="RetrySession"/>.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed) return;
            _started = true;
            StartCore();
        }
    }

    /// <summary>Explicit request (e.g. a "Retry" button) to create the session again after it failed.</summary>
    public void RetrySession()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _started = true;
            if (_session is { IsRunning: true } && Status.State == SourceState.Ok) return;
            TeardownSession();
            StartCore();
        }
    }

    private void StartCore()
    {
        try
        {
            _ring ??= new PresentRing();
            var session = new PresentSession(_sessionName, _ring);
            var result = session.Start();
            if (result == SessionStartResult.Ok)
            {
                session.Lost += OnSessionLost;
                _session = session;
                Status = new SourceStatus(SourceState.Ok);
                return;
            }
            Status = new SourceStatus(SourceState.Unavailable, result == SessionStartResult.NoAccess && !HasEtwRights() ? StatusNoAccess : StatusBlocked);
            Log.Warn($"FPS: ETW session '{_sessionName}' failed ({result}, error {session.LastError}).");
        }
        catch (Exception ex)
        {
            Status = new SourceStatus(SourceState.Unavailable, StatusBlocked);
            Log.Error("FPS: ETW session start failed.", ex);
        }
    }

    /// <summary>Administrator or member of "Performance Log Users" (S-1-5-32-559): access denied then means blocked.</summary>
    private static bool HasEtwRights()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var p = new WindowsPrincipal(id);
            return p.IsInRole(WindowsBuiltInRole.Administrator) || p.IsInRole(new SecurityIdentifier("S-1-5-32-559"));
        }
        catch
        {
            return false;
        }
    }

    private void OnSessionLost()
    {
        // consumer thread: ProcessTrace returned on its own (session stopped from outside)
        _enabled = false;
        Status = new SourceStatus(SourceState.Unavailable, StatusBlocked);
        Log.Warn($"FPS: ETW session '{_sessionName}' ended unexpectedly.");
    }

    public SourceInventory Discover()
    {
        if (!_started) Start();
        return _inventory;
    }

    public void Update(ReadOnlySpan<SensorDescriptor> requested, Span<float> values)
    {
        try
        {
            if (requested.IsEmpty) return;
            var now = Stopwatch.GetTimestamp();
            Volatile.Write(ref _lastDemandQpc, now);
            if (!_enabled) EnsureEnabled();

            var stats = _enabled ? Measure(now) : FrameStats.None;
            foreach (var d in requested)
            {
                if ((uint)d.Index >= (uint)values.Length || d.SourceTag is not Metric m) continue;
                values[d.Index] = m switch
                {
                    Metric.Framerate => stats.Framerate,
                    Metric.FrameTime => stats.FrameTimeMs,
                    Metric.Low1 => stats.Low1,
                    Metric.Low01 => stats.Low01,
                    _ => stats.FrameTimeMaxMs,
                };
            }
        }
        catch (Exception ex)
        {
            if (!_loggedUpdateError)
            {
                _loggedUpdateError = true;
                Log.Error("FPS: update failed.", ex);
            }
            foreach (var d in requested)
            {
                if ((uint)d.Index < (uint)values.Length) values[d.Index] = float.NaN;
            }
        }
    }

    /// <summary>Current stats of the target (engine thread). Exposed for tests.</summary>
    internal FrameStats Measure(long nowQpc)
    {
        var ring = _ring!;
        var target = TargetPidOverride is { } o ? (uint)o : _foreground.Refresh();
        if (target != _target) Retarget(target, nowQpc);
        if (target == 0) return FrameStats.None;

        var write = ring.WriteIndex;
        if (write == _cacheIndex)
        {
            // nothing new: only staleness can change
            return _cache.HasValue && LastTimestamp(ring, write) is var last && nowQpc - last <= (long)(FrameStatsCalculator.StaleSeconds * Stopwatch.Frequency)
                ? _cache
                : FrameStats.None;
        }

        _calc ??= new FrameStatsCalculator(ring.Capacity);
        _snapTimes ??= new long[ring.Capacity];
        _snapKeys ??= new uint[ring.Capacity];
        var n = ring.Snapshot(_fromIndex, _snapTimes, _snapKeys, out var end);
        // drop entries older than the reset (a present of the previous target racing the switch)
        var skip = 0;
        while (skip < n && _snapTimes[skip] < _fromQpc) skip++;
        // next time start at the window (+1 s slack for out-of-order delivery): no re-copying of old entries
        if (n > 0)
        {
            var keepFrom = _snapTimes[n - 1] - (long)((FrameStatsCalculator.WindowSeconds + 1) * Stopwatch.Frequency);
            var first = skip;
            while (first < n && _snapTimes[first] < keepFrom) first++;
            _fromIndex = Math.Max(_fromIndex, end - n + first);
        }
        var stats = _calc.Compute(_snapTimes.AsSpan(skip, n - skip), _snapKeys.AsSpan(skip, n - skip), nowQpc, Stopwatch.Frequency);
        _cacheIndex = end;
        _cache = stats;
        return stats;
    }

    private static unsafe long LastTimestamp(PresentRing ring, long write)
        => write == 0 ? long.MinValue / 2 : ring.Shared->Timestamps[(write - 1) & (ring.Capacity - 1)];

    private void Retarget(uint target, long nowQpc)
    {
        _target = target;
        var ring = _ring!;
        ring.TargetPid = target;
        _fromIndex = ring.WriteIndex;
        _fromQpc = nowQpc;
        _cacheIndex = -1;
        _cache = FrameStats.None;
        Volatile.Write(ref _processId, (int)target);
        _processName = ForegroundTracker.ProcessName(target);
    }

    private void EnsureEnabled()
    {
        lock (_gate)
        {
            if (_enabled || _disposed || _session is not { IsRunning: true } session || Status.State != SourceState.Ok) return;
            var rc = session.EnableProviders();
            if (rc != 0)
            {
                Status = new SourceStatus(SourceState.Unavailable, rc == Interop.Etw.ErrorAccessDenied && !HasEtwRights() ? StatusNoAccess : StatusBlocked);
                Log.Warn($"FPS: enabling present providers failed (error {rc}).");
                return;
            }
            // presents from before this run are of unknown targets
            var now = Stopwatch.GetTimestamp();
            _fromIndex = _ring!.WriteIndex;
            _fromQpc = now;
            _cacheIndex = -1;
            _enabled = true;
            _idleTimer ??= new Timer(static s => ((FpsSource)s!).OnIdleTimer(), this, Timeout.Infinite, Timeout.Infinite);
            _idleTimer.Change(IdleTimeout + TimeSpan.FromMilliseconds(250), Timeout.InfiniteTimeSpan);
        }
    }

    private void OnIdleTimer()
    {
        try
        {
            lock (_gate)
            {
                if (!_enabled || _disposed) return;
                var idle = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastDemandQpc));
                if (idle < IdleTimeout)
                {
                    _idleTimer?.Change(IdleTimeout - idle + TimeSpan.FromMilliseconds(250), Timeout.InfiniteTimeSpan);
                    return;
                }
                _session?.DisableProviders();
                _enabled = false;
            }
        }
        catch (Exception ex)
        {
            Log.Error("FPS: idle timer failed.", ex);
        }
    }

    private void TeardownSession()
    {
        _enabled = false;
        if (_session is null) return;
        _session.Lost -= OnSessionLost;
        if (!_session.Stop())
        {
            // the consumer thread is stuck in ProcessTrace: keep its memory alive, never free under it
            Log.Warn("FPS: ETW consumer thread did not exit.");
            _ring = null;
        }
        _session = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _idleTimer?.Dispose();
            _idleTimer = null;
            TeardownSession();
            _ring?.Dispose();
            _ring = null;
        }
    }
}
