using System.Windows.Threading;
using BetterVitals.Core.Sensors;

namespace BetterVitals.UI.Pages.Shared;

/// <summary>
/// Turns the engine's FrameReady into at most one UI update per <see cref="MinGap"/> (default 1 s). The engine thread
/// only flips a flag and posts once; the UI thread reads <see cref="ISensorHub.Latest"/> right away. Costs nothing while
/// stopped (no subscription, no timer). Start in OnNavigatedTo, stop in OnNavigatedFrom.
/// </summary>
public sealed class LiveTicker
{
    private readonly ISensorHub _hub;
    private readonly Dispatcher _dispatcher;
    private readonly Action _apply;
    private readonly Action? _inventory;
    private DispatcherTimer? _timer;
    private int _posted;
    private int _inventoryPosted;
    private long _lastApply;
    private bool _running;

    public LiveTicker(ISensorHub hub, Dispatcher dispatcher, Action apply, Action? inventoryChanged = null)
    {
        _hub = hub;
        _dispatcher = dispatcher;
        _apply = apply;
        _inventory = inventoryChanged;
    }

    public TimeSpan MinGap { get; init; } = TimeSpan.FromSeconds(1);

    public bool IsRunning => _running;

    /// <summary>Subscribes and applies once right away.</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        _hub.FrameReady += OnFrame;
        if (_inventory is not null) _hub.InventoryChanged += OnInventory;
        Run();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _hub.FrameReady -= OnFrame;
        if (_inventory is not null) _hub.InventoryChanged -= OnInventory;
        _timer?.Stop();
        Interlocked.Exchange(ref _posted, 0);
        Interlocked.Exchange(ref _inventoryPosted, 0);
    }

    // engine thread: never block, never touch the frame here
    private void OnFrame(SensorFrame frame)
    {
        if (Interlocked.Exchange(ref _posted, 1) == 0) _dispatcher.BeginInvoke(DispatcherPriority.Background, Fire);
    }

    private void OnInventory()
    {
        if (Interlocked.Exchange(ref _inventoryPosted, 1) == 0)
            _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                Interlocked.Exchange(ref _inventoryPosted, 0);
                if (_running) _inventory?.Invoke();
            });
    }

    private void Fire()
    {
        if (!_running) return;
        var wait = MinGap - TimeSpan.FromTicks(Environment.TickCount64 * TimeSpan.TicksPerMillisecond - _lastApply);
        if (wait > TimeSpan.FromMilliseconds(15))
        {
            _timer ??= new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
            _timer.Tick -= OnTimer;
            _timer.Tick += OnTimer;
            _timer.Interval = wait;
            _timer.Start();
            return;
        }
        Run();
    }

    private void OnTimer(object? sender, EventArgs e)
    {
        _timer!.Stop();
        if (_running) Run();
    }

    private void Run()
    {
        Interlocked.Exchange(ref _posted, 0);
        _lastApply = Environment.TickCount64 * TimeSpan.TicksPerMillisecond;
        _apply();
    }
}
