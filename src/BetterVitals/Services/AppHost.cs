using System.Windows.Threading;
using BetterVitals.Core;
using BetterVitals.Core.Catalog;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Fps;
using BetterVitals.History;
using BetterVitals.Platform;
using BetterVitals.Sensors.Derived;
using BetterVitals.Sensors.Engine;
using BetterVitals.Sensors.Fake;
using BetterVitals.Sensors.Lhm;
using BetterVitals.Sensors.Native;
using BetterVitals.UI.Tray;

namespace BetterVitals.Services;

/// <summary>
/// The composition root: builds the sensor sources and the engine, the history, the overlay and the platform services,
/// and wires Windows events into them. Test mode uses a fake PC (no hardware, no ETW, no driver). Everything here runs
/// on the UI thread except the handlers noted otherwise.
/// </summary>
internal sealed class AppHost : IDisposable
{
    public static AppHost? Current { get; private set; }

    /// <summary>The single-instance mutex (Program owns it; released before an elevated copy takes over).</summary>
    public static SingleInstance? Instance { get; set; }

    private readonly Dispatcher _ui;
    private readonly SensorEngine _engine;
    private readonly LhmSource? _lhm;
    private readonly FpsSource? _fps;
    private readonly HistoryService _history;
    private readonly SystemEvents? _events;
    private readonly AntiCheatGuard _guard = new();
    private IDemand? _trayDemand;
    private volatile bool _gameInFront;

    public OverlayController Overlay { get; }

    public PlatformService Platform { get; }

    public ISensorHub Hub => _engine;

    /// <summary>Diagnostics (perf probe): the engine and the LHM source (null with fake sensors).</summary>
    internal SensorEngine Engine => _engine;

    internal LhmSource? Lhm => _lhm;

    private AppHost(Dispatcher ui, bool testMode, bool realSensors)
    {
        _ui = ui;
        var sources = new List<ISensorSource>();
        if (testMode && !realSensors)
        {
            sources.Add(new FakeSensorSource(FakeProfile.DesktopAmdNvidia));
        }
        else
        {
            _lhm = new LhmSource();
            _fps = new FpsSource();
            sources.Add(_lhm);
            sources.Add(_fps);
            // fills what LHM lacks here (drive temperatures without admin, RAM speed, GPU basics); before Derived,
            // which then also sees those values (dependent sources keep their registration order)
            sources.Add(new NativeSource());
        }
        sources.Add(new DerivedSource());
        _engine = new SensorEngine(sources);

        // FPS: the ETW session exists before any game starts (EA AntiCheat refuses new sessions once it runs)
        _fps?.Start();
        _engine.Start();

        _history = new HistoryService(_engine, AppEnv.HistoryDir);
        _history.Configure(SettingsStore.I.Current.Recording);

        AppServices.Hub = _engine;
        AppServices.History = _history;
        AppServices.WatchHistory = ids => _history.Watch(ids);

        Overlay = new OverlayController(_engine, ui);
        AppServices.Overlay = Overlay;

        Platform = new PlatformService(ui, () => _gameInFront);
        AppServices.Platform = Platform;

        _engine.InventoryChanged += OnInventoryChanged;
        _engine.DiscoveryCompleted += OnDiscoveryCompleted;
        SettingsStore.I.Changed += OnSettingsChanged;
        _guard.PausedChanged += paused =>
        {
            _lhm?.SetDriverPaused(paused);
            Platform.AntiCheatPauseReason = _guard.Reason;
        };

        if (!testMode)
        {
            _events = new SystemEvents();
            _events.Suspending += OnSuspending;
            _events.Resumed += OnResumed;
            _events.DevicesChanged += OnDevicesChanged;
            _events.DisplayChanged += Overlay.OnDisplayChanged;
            _events.SessionEnding += FlushAll;
            _events.ForegroundChanged += OnForegroundChanged;
            if (Elevation.IsElevated && Installer.IsRunningInstalled) Installer.CleanupLeftovers();
        }
        Log.Info($"host: started (elevated={Elevation.IsElevated}, installed={Installer.IsInstalled}, pawnio={PawnIo.InstalledVersion?.ToString() ?? "none"})");
    }

    public static AppHost Start(Dispatcher ui, bool testMode, bool realSensors = false) => Current = new AppHost(ui, testMode, realSensors);

    /// <summary>Releases the single-instance mutex before another copy takes over (elevation hand-over, update restart).</summary>
    public static void ReleaseSingleInstance()
    {
        Instance?.Dispose();
        Instance = null;
    }

    /// <summary>Connects the tray icon for quiet notifications.</summary>
    public void AttachTray(TrayIcon tray)
    {
        Overlay.Tip += tray.Notify;
        Platform.Notify = tray.Notify;
    }

    /// <summary>PawnIO was just installed: LHM re-opens and finds the driver-backed sensors.</summary>
    public void OnDriverInstalled()
    {
        _lhm?.NotifyDeviceChange();
        _engine.RequestRediscover();
    }

    /// <summary>
    /// Test / snapshot mode: read every sensor once so pages render real numbers right away (the engine reads on
    /// demand only, and a page's first demand would otherwise race the snapshot).
    /// </summary>
    public void WarmUp(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (_engine.Sensors.Count == 0 && DateTime.UtcNow < deadline) Thread.Sleep(20);
        using var done = new ManualResetEventSlim();
        void OnFrame(SensorFrame f) => done.Set();
        _engine.FrameReady += OnFrame;
        using (_engine.Demand("warm-up", _engine.Sensors.Select(s => s.Id), TimeSpan.FromSeconds(1)))
            done.Wait(deadline - DateTime.UtcNow > TimeSpan.Zero ? deadline - DateTime.UtcNow : TimeSpan.Zero);
        _engine.FrameReady -= OnFrame;
    }

    // ───────────────────────── events ─────────────────────────

    /// <summary>Engine thread: new / vanished hardware.</summary>
    private void OnInventoryChanged() => _ui.BeginInvoke(ApplyRecordingPolicy);

    /// <summary>Key preset on first run and for hardware that shows up later (a new GPU…), never re-adding what the user removed.</summary>
    private void ApplyRecordingPolicy()
    {
        var items = FriendlyCatalog.Resolve(_engine.Hardware, _engine.Sensors, NetworkPrimary.Find());
        var rec = SettingsStore.I.Current.Recording;
        var missing = RecordingPolicy.MissingKeySensors(rec, items, rec.RemovedSensorIds);
        if (missing.Count > 0 || !rec.KeyPresetApplied)
        {
            SettingsStore.I.Mutate("recording", s =>
            {
                s.Recording.SensorIds.UnionWith(missing);
                s.Recording.KeyPresetApplied = true;
            });
        }
        // the tray tooltip shows the key values: keep them fresh at a slow pace even when nothing records them
        var key = SensorPresentation.KeySensors(_engine, SettingsStore.I.Current.Flyout.PinnedSensorIds).Select(s => s.Id).ToArray();
        if (_trayDemand is null) _trayDemand = _engine.Demand("tray", key, TimeSpan.FromSeconds(5));
        else _trayDemand.Update(key, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Engine thread, after every discovery pass (also one that found the same inventory and told nobody): a re-discovery
    /// leaves tens of MB behind (LHM drive / HID enumeration, NVML / ADL init after a re-open): give it back when nothing
    /// is on screen, as after closing the last window.
    /// </summary>
    private void OnDiscoveryCompleted() => _ui.BeginInvoke(static () =>
    {
        if (!TrayRest.AnyWindowVisible()) TrayRest.Settle();
    });

    private void OnSettingsChanged(string section)
    {
        switch (section)
        {
            case "recording":
                _history.Configure(SettingsStore.I.Current.Recording);
                break;
            case "driver":
                _guard.Evaluate(SettingsStore.I.Current.Driver.PauseForAntiCheat, _gameInFront);
                break;
        }
    }

    /// <summary>System events thread.</summary>
    private void OnForegroundChanged()
    {
        _gameInFront = Foreground.IsFullscreenAppActive();
        Overlay.OnForegroundChanged(_gameInFront);
        var pause = SettingsStore.I.Current.Driver.PauseForAntiCheat;
        if (pause || _guard.Paused) _guard.Evaluate(pause, _gameInFront);
    }

    /// <summary>System events thread: flush and pause before sleep.</summary>
    private void OnSuspending()
    {
        _engine.NotifySuspend();
        _history.Suspend();
        SettingsStore.I.Flush();
    }

    private void OnResumed()
    {
        _engine.NotifyResume();
        _history.Resume();
    }

    private void OnDevicesChanged()
    {
        _lhm?.NotifyDeviceChange();
        _engine.RequestRediscover();
    }

    private void FlushAll()
    {
        try { _history.Flush(); } catch (Exception ex) { Log.Error("host: history flush", ex); }
        SettingsStore.I.Flush();
    }

    public void Dispose()
    {
        SettingsStore.I.Changed -= OnSettingsChanged;
        _engine.InventoryChanged -= OnInventoryChanged;
        _engine.DiscoveryCompleted -= OnDiscoveryCompleted;
        _events?.Dispose();
        _trayDemand?.Dispose();
        Overlay.Dispose();
        Platform.Dispose();
        _history.Dispose(); // writes everything
        _engine.Dispose();  // disposes the sources (LHM closes, the ETW session stops)
        Log.Info("host: stopped");
    }
}
