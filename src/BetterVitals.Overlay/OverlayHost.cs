using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using BetterVitals.Core;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Overlay.Host;
using BetterVitals.Overlay.Interop;
using BetterVitals.Overlay.Rendering;

namespace BetterVitals.Overlay;

/// <summary>
/// The on-screen overlay: a native top-most, click-through, never-activating popup (not in Alt+Tab or on the
/// taskbar) rendered with D3D11 + DXGI composition swap chain + DirectComposition + Direct2D / DirectWrite, on its own
/// per-monitor-DPI-aware thread with its own message loop. Anti-cheat safe: no injection, no hooks into other
/// processes' code (only out-of-context WinEvents), no handles to other processes.
/// All public members are thread-safe; work is posted to the overlay thread. Callbacks and events are raised on the
/// overlay thread — marshal to the UI thread yourself.
/// </summary>
public sealed unsafe partial class OverlayHost : IDisposable
{
    private const nuint TimerRefresh = 1, TimerFade = 2, TimerDisplay = 3, TimerForeground = 4, TimerRetry = 5, TimerIdle = 6;
    private const uint WmInvoke = Win32.WM_APP + 1, WmInventory = Win32.WM_APP + 2;
    private const string ClassName = "BetterVitals.Overlay";
    private const float FadeMs = 160;
    private const int MaxSurface = 16384; // the largest texture Direct3D 11 guarantees
    private static readonly Lock ClassGate = new();
    private static bool _classRegistered;

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly OverlayHostOptions _options;
    private readonly Action _onInventory;
    private Thread? _thread;
    private nint _hwnd;
    private GCHandle _self;
    private volatile bool _disposed;
    private readonly ManualResetEventSlim _ready = new();
    private Exception? _startError;

    // ── overlay-thread state ──
    private OverlayLayout? _layout;
    private OverlayTheme _theme = OverlayTheme.Default;
    private OverlayContext? _ctx;
    private ISensorHub? _subscribedHub;
    private HubLookup? _lookup;
    private bool _userVisible = true;
    private Scene? _scene;
    private double _sceneScale;
    private GpuSurface? _surface;
    private IDemand? _demand;
    private ForegroundWatcher? _watcher;
    private ForegroundState _fg;
    private nint _exclusiveNotified;
    private List<OverlayMonitor> _monitors = [];
    private OverlayMonitor? _monitor;
    private PixelRect _area;
    private int _x, _y;
    private bool _shown;
    private float _alpha;
    private float _fadeFrom, _fadeTo;
    private long _fadeStart;
    private bool _fading;
    private int _retries;
    private bool _refreshRunning;
    private int _refreshInterval;
    private bool _warnedRefresh;
    private bool _warnedHuge;
    private int _ticks;
    private int _renders;

    // edit mode
    private bool _editing;
    private Action<OverlayPlacement>? _commit;
    private Action? _cancel;
    private OverlayPlacement? _editStart;
    private bool _dragging;
    private POINT _dragCursor;
    private int _dragX, _dragY;
    private GuideWindows? _guides;

    /// <summary>Creates the host; call <see cref="Start"/> to create the overlay thread and window.</summary>
    public OverlayHost() : this(new OverlayHostOptions())
    {
    }

    internal OverlayHost(OverlayHostOptions options)
    {
        _options = options;
        _onInventory = () =>
        {
            var h = _hwnd;
            if (h != 0) Win32.PostMessage(h, WmInventory, 0, 0);
        };
    }

    /// <summary>
    /// The shell reports exclusive Direct3D full screen in the foreground (an old DX9 / DX11 game with full-screen
    /// optimizations off). A separate window cannot draw over it without injection, so the app may show its one-time
    /// "use borderless / windowed full screen" tip. Argument: the game's process id. Raised once per window, on the
    /// overlay thread.
    /// </summary>
    public event Action<uint>? ExclusiveFullscreenDetected;

    /// <summary>The overlay thread is running.</summary>
    public bool IsRunning => _thread is { IsAlive: true } && !_disposed;

    // ── public API ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Starts the overlay thread and creates the (hidden) window. Idempotent.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null) return;
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "BetterVitals overlay" };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("overlay thread did not start");
        if (_startError is not null) throw new InvalidOperationException("overlay failed to start", _startError);
    }

    /// <summary>
    /// Applies a layout (copied; later changes to the object have no effect until the next call), theme colors and
    /// context. Shows / hides the overlay according to <see cref="OverlayLayout.Enabled"/> and
    /// <see cref="OverlayLayout.Visibility"/>.
    /// </summary>
    public void Apply(OverlayLayout layout, OverlayTheme theme, OverlayContext ctx)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(ctx);
        var copy = LayoutCopy.Clone(layout);
        copy.Normalize(); // whoever built the layout, the window only ever sees sane numbers
        Post(() => ApplyCore(copy, theme, ctx));
    }

    /// <summary>Temporarily hides (false) or allows (true, default) the overlay on top of the layout's own rules.</summary>
    public void SetVisible(bool visible) => Post(() =>
    {
        _userVisible = visible;
        Reconcile();
    });

    /// <summary>
    /// Makes the real overlay hit-testable for exact positioning: drag with the mouse (snaps to work-area edges, the
    /// default margin and the center; hold Shift for no snapping), arrow keys move 1 px (Shift: 10 px), Enter commits,
    /// Esc cancels. Keys work only while the overlay has focus in this mode — never global hotkeys. The overlay is
    /// shown during edit mode even when the layout would hide it. <paramref name="commit"/> receives the new placement
    /// (anchor = the ninth of the screen the overlay ended in); both callbacks run on the overlay thread.
    /// </summary>
    public void EnterEditMode(Action<OverlayPlacement> commit, Action cancel)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(cancel);
        Post(() => EnterEditCore(commit, cancel));
    }

    /// <summary>Ends edit mode from the app (its "Done" / "Cancel" buttons): same as Enter / Esc.</summary>
    public void ExitEditMode(bool commit) => Post(() => ExitEditCore(commit));

    /// <summary>All monitors in physical pixels with stable keys (for the editor's monitor picker).</summary>
    public static IReadOnlyList<OverlayMonitor> GetMonitors()
    {
        var old = Win32.SetThreadDpiAwarenessContext(Win32.DPI_PMV2);
        try
        {
            return Monitors.Enumerate();
        }
        finally
        {
            if (old != 0) Win32.SetThreadDpiAwarenessContext(old);
        }
    }

    /// <summary>
    /// Renders the overlay offscreen (Direct2D → WIC, software, never on screen) for tests, snapshots and the WPF
    /// editor preview. <paramref name="monitorHeightPx"/> drives the automatic scale (1080 → ×1, 2160 → ×2).
    /// Returns premultiplied BGRA (WPF: <c>PixelFormats.Pbgra32</c>). Any thread.
    /// </summary>
    public static OverlayBitmap RenderToBitmap(OverlayLayout layout, OverlayTheme theme, IReadOnlyList<PreviewSensor> values, int monitorHeightPx,
        CultureInfo? culture = null, TemperatureUnit temperatureUnit = TemperatureUnit.Celsius, Func<OverlayItem, string?>? labelResolver = null)
    {
        var copy = LayoutCopy.Clone(layout);
        copy.Normalize();
        return Offscreen.Render(copy, theme, values, monitorHeightPx, culture, temperatureUnit, labelResolver);
    }

    /// <summary>As <see cref="RenderToBitmap"/>, written to a PNG file (straight alpha). Returns the image size.</summary>
    public static (int Width, int Height) RenderToPng(OverlayLayout layout, OverlayTheme theme, IReadOnlyList<PreviewSensor> values, int monitorHeightPx,
        string path, CultureInfo? culture = null, TemperatureUnit temperatureUnit = TemperatureUnit.Celsius, Func<OverlayItem, string?>? labelResolver = null)
    {
        var bmp = RenderToBitmap(layout, theme, values, monitorHeightPx, culture, temperatureUnit, labelResolver);
        Offscreen.WritePng(bmp, path);
        return (bmp.Width, bmp.Height);
    }

    /// <summary>Stops the overlay thread and releases the window and all graphics resources.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var h = _hwnd;
        if (h != 0) Win32.PostMessage(h, Win32.WM_CLOSE, 0, 0);
        if (_thread is not null && _thread != Thread.CurrentThread) _thread.Join(TimeSpan.FromSeconds(5));
        _ready.Dispose();
    }

    // ── test / diagnostics hooks ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Runs <paramref name="f"/> on the overlay thread and waits for the result.</summary>
    internal T Invoke<T>(Func<T> f, int timeoutMs = 10000)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                tcs.SetResult(f());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        if (!tcs.Task.Wait(timeoutMs)) throw new TimeoutException("overlay thread did not respond");
        return tcs.Task.Result;
    }

    internal OverlayDiagnostics Diagnostics() => Invoke(() =>
    {
        RECT r;
        Win32.GetWindowRect(_hwnd, &r);
        var ex = (uint)(long)Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE);
        uint affinity = 0;
        GetWindowDisplayAffinity(_hwnd, &affinity);
        return new OverlayDiagnostics(_hwnd, _shown, Win32.IsWindowVisible(_hwnd), new PixelRect(r.Left, r.Top, r.Right, r.Bottom), ex, affinity,
            _scene?.Width ?? 0, _scene?.Height ?? 0, _sceneScale, _surface?.Ready ?? false, _surface?.DeviceCreations ?? 0, _surface?.IsWarp ?? false,
            _editing, _monitor?.Key, _area, _demand is not null, _refreshRunning, _ticks, _renders);
    });

    /// <summary>The value text of every item as drawn right now ("—" = no value), for tests.</summary>
    internal string[] ShownValues() => Invoke(() => _scene?.Items.Select(i => new string(i.Main.Text)).ToArray() ?? []);

    /// <summary>The swap chain's back buffer (what the window shows), for tests.</summary>
    internal OverlayBitmap? ReadBack() => Invoke(() =>
    {
        if (_surface is null || _scene is null) return null;
        if (_surface.Render(_scene, _alpha, _editing, present: false) < 0) return null;
        var bmp = _surface.ReadBack();
        Render();
        return bmp;
    });

    /// <summary>One refresh tick right now (tests / measurements).</summary>
    internal bool RefreshNow() => Invoke(RefreshTick);

    /// <summary>Simulates a lost device (tests): drops the device and recovers like after a TDR.</summary>
    internal bool SimulateDeviceLost() => Invoke(() =>
    {
        if (_surface is null) return false;
        _surface.Release();
        Render();
        return _surface.Ready;
    });

    /// <summary>Runs the display-change handling now (tests; the real trigger is WM_DISPLAYCHANGE / WM_DPICHANGED + a 250 ms timer).</summary>
    internal bool SimulateDisplayChange() => Invoke(() =>
    {
        OnDisplayChanged();
        return true;
    });

    /// <summary>Feeds a key to edit mode (tests; real keys arrive as WM_KEYDOWN).</summary>
    internal void EditKey(int vk, bool shift) => Invoke(() =>
    {
        HandleKey(vk, shift);
        return 0;
    });

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowDisplayAffinity(nint hwnd, uint* affinity);

    // ── thread ──────────────────────────────────────────────────────────────────────────────────────────────────

    private void Post(Action a)
    {
        if (_disposed) return;
        _queue.Enqueue(a);
        var h = _hwnd;
        if (h != 0) Win32.PostMessage(h, WmInvoke, 0, 0);
    }

    private void ThreadMain()
    {
        try
        {
            Win32.SetThreadDpiAwarenessContext(Win32.DPI_PMV2);
            RegisterClass();
            _self = GCHandle.Alloc(this);
            const uint ex = Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TRANSPARENT
                            | Win32.WS_EX_LAYERED | Win32.WS_EX_NOREDIRECTIONBITMAP;
            fixed (char* cls = ClassName)
            fixed (char* title = "BetterVitals Overlay")
                _hwnd = Win32.CreateWindowEx(ex, cls, title, Win32.WS_POPUP, 0, 0, 1, 1, 0, 0, Win32.GetModuleHandle(null), GCHandle.ToIntPtr(_self));
            if (_hwnd == 0) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()})");
            // Layered + transparent = click-through for a top-level window; alpha 255 lets DirectComposition content show.
            Win32.SetLayeredWindowAttributes(_hwnd, 0, 255, Win32.LWA_ALPHA);
            _monitors = EnumerateMonitors();
            _watcher = new ForegroundWatcher(OnForegroundEvent);
            _watcher.Start();
            _fg = ForegroundWatcher.Evaluate();
        }
        catch (Exception ex)
        {
            _startError = ex;
            Log.Error("overlay: start failed", ex);
            _ready.Set();
            return;
        }
        _ready.Set();
        // Anything posted before the window existed.
        Drain();

        MSG msg;
        while (Win32.GetMessage(&msg, 0, 0, 0) > 0)
        {
            Win32.TranslateMessage(&msg);
            Win32.DispatchMessage(&msg);
        }
        Cleanup();
    }

    private static void RegisterClass()
    {
        lock (ClassGate)
        {
            if (_classRegistered) return;
            fixed (char* name = ClassName)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = &WndProc,
                    hInstance = Win32.GetModuleHandle(null),
                    lpszClassName = name,
                };
                if (Win32.RegisterClassEx(&wc) == 0) throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastPInvokeError()})");
            }
            _classRegistered = true;
        }
    }

    private void Cleanup()
    {
        try
        {
            KillAllTimers();
            _demand?.Dispose();
            _demand = null;
            if (_subscribedHub is not null) _subscribedHub.InventoryChanged -= _onInventory;
            _subscribedHub = null;
            _guides?.Dispose();
            _guides = null;
            _watcher?.Dispose();
            _scene?.Dispose();
            _scene = null;
            _surface?.Dispose();
            _surface = null;
        }
        catch (Exception ex)
        {
            Log.Error("overlay: cleanup", ex);
        }
        if (_self.IsAllocated) _self.Free();
        _hwnd = 0;
    }

    private void KillAllTimers()
    {
        for (nuint t = 1; t <= 6; t++) Win32.KillTimer(_hwnd, t);
        _refreshRunning = false;
    }

    private void Drain()
    {
        while (_queue.TryDequeue(out var a))
        {
            try
            {
                a();
            }
            catch (Exception ex)
            {
                Log.Error("overlay: command failed", ex);
            }
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            OverlayHost? host = null;
            if (msg == Win32.WM_NCCREATE)
            {
                var cs = (CREATESTRUCTW*)lParam;
                Win32.SetWindowLongPtr(hwnd, Win32.GWLP_USERDATA, cs->lpCreateParams);
            }
            var data = Win32.GetWindowLongPtr(hwnd, Win32.GWLP_USERDATA);
            if (data != 0) host = GCHandle.FromIntPtr(data).Target as OverlayHost;
            if (host is not null && host.HandleMessage(hwnd, msg, wParam, lParam, out var result)) return result;
        }
        catch (Exception ex)
        {
            Log.Error($"overlay: message 0x{msg:X4}", ex);
        }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private bool HandleMessage(nint hwnd, uint msg, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        switch (msg)
        {
            case WmInvoke:
                Drain();
                return true;
            case WmInventory:
                OnInventoryChanged();
                return true;
            case Win32.WM_TIMER:
                OnTimer((nuint)wParam);
                return true;
            case Win32.WM_ERASEBKGND:
                result = 1;
                return true;
            case Win32.WM_NCHITTEST:
                result = _editing ? Win32.HTCLIENT : Win32.HTTRANSPARENT;
                return true;
            case Win32.WM_MOUSEACTIVATE:
                result = _editing ? 1 : Win32.MA_NOACTIVATE;
                return true;
            case Win32.WM_SETCURSOR when _editing:
                Win32.SetCursor(Win32.LoadCursor(0, Win32.IDC_SIZEALL));
                result = 1;
                return true;
            case Win32.WM_LBUTTONDOWN when _editing:
                BeginDrag();
                return true;
            case Win32.WM_MOUSEMOVE when _dragging:
                Drag();
                return true;
            case Win32.WM_LBUTTONUP when _dragging:
                EndDrag();
                return true;
            case Win32.WM_CAPTURECHANGED when _dragging:
                _dragging = false;
                _guides?.Hide();
                return true;
            case Win32.WM_KEYDOWN when _editing:
            case Win32.WM_SYSKEYDOWN when _editing:
                HandleKey((int)wParam, Win32.ShiftDown);
                return true;
            case Win32.WM_DISPLAYCHANGE:
            case Win32.WM_DPICHANGED:
            case Win32.WM_DWMCOMPOSITIONCHANGED:
                ScheduleDisplayUpdate();
                return msg == Win32.WM_DPICHANGED; // ignore the suggested rectangle: we place in physical pixels ourselves
            case Win32.WM_SETTINGCHANGE when (int)wParam == Win32.SPI_SETWORKAREA:
                ScheduleDisplayUpdate();
                return false;
            case Win32.WM_CLOSE:
                Win32.DestroyWindow(hwnd);
                return true;
            case Win32.WM_DESTROY:
                Win32.SetWindowLongPtr(hwnd, Win32.GWLP_USERDATA, 0);
                Win32.PostQuitMessage(0);
                return true;
        }
        return false;
    }

    // ── apply / visibility ──────────────────────────────────────────────────────────────────────────────────────

    private void ApplyCore(OverlayLayout layout, OverlayTheme theme, OverlayContext ctx)
    {
        if (!ReferenceEquals(_subscribedHub, ctx.Hub))
        {
            if (_subscribedHub is not null) _subscribedHub.InventoryChanged -= _onInventory;
            _demand?.Dispose();
            _demand = null;
            _subscribedHub = ctx.Hub;
            _subscribedHub.InventoryChanged += _onInventory;
        }
        // While editing, keep the position the user is moving (the app may re-apply styles meanwhile).
        if (_editing && _layout is not null) layout.Placement = _layout.Placement;
        _layout = layout;
        _theme = theme;
        _ctx = ctx;
        _lookup = new HubLookup(ctx.Hub);
        if (!Win32.SetWindowDisplayAffinity(_hwnd, layout.HideFromCapture ? Win32.WDA_EXCLUDEFROMCAPTURE : Win32.WDA_NONE))
            Log.Warn($"overlay: SetWindowDisplayAffinity failed ({Marshal.GetLastPInvokeError()})");
        _scene?.Dispose();
        _scene = null;
        Reconcile();
    }

    private bool WantVisible()
    {
        if (_layout is null || _ctx is null || _disposed) return false;
        if (_editing) return true;
        if (!_layout.Enabled || !_userVisible || _layout.Items.Count == 0) return false;
        return _layout.Visibility == OverlayVisibility.Always || _fg.Fullscreen;
    }

    /// <summary>Brings window, scene, surface, demand and timers in line with the current state.</summary>
    private void Reconcile()
    {
        if (!WantVisible())
        {
            StopRefresh();
            _demand?.Dispose();
            _demand = null;
            if (_shown) BeginFade(0);
            return;
        }
        Win32.KillTimer(_hwnd, TimerIdle);
        if (!_dragging) ResolveMonitor();
        var scale = OverlayPlacementMath.Scale(_layout!.Style, _monitor?.Bounds.Height ?? 1080);
        if (_scene is null || Math.Abs(scale - _sceneScale) > 1e-6) BuildScene(scale);
        if (!EnsureSurface())
        {
            if (!_warnedHuge) return; // graphics are not ready yet: the retry timer is running
            StopRefresh(); // too big to ever be shown: nothing to read for it
            _demand?.Dispose();
            _demand = null;
            if (_shown) BeginFade(0);
            return;
        }
        if (!_dragging) Position();
        UpdateDemand();
        StartRefresh();
        if (!_shown)
        {
            _alpha = _ctx!.Animations ? 0 : 1;
            Render();
            Show();
            if (_ctx.Animations) BeginFade(1);
        }
        else
        {
            if (_fading && _fadeTo < 1) BeginFade(1);
            Render();
        }
    }

    private List<OverlayMonitor> EnumerateMonitors() => _options.MonitorSource is { } source ? [.. source()] : Monitors.Enumerate();

    private void ResolveMonitor()
    {
        if (_monitors.Count == 0) _monitors = EnumerateMonitors();
        string? gameKey = null;
        if (_fg.Fullscreen && _fg.MonitorDevice is { } dev)
            gameKey = _monitors.FirstOrDefault(m => string.Equals(m.DeviceName, dev, StringComparison.OrdinalIgnoreCase))?.Key;
        _monitor = OverlayPlacementMath.Resolve(_layout!.Placement, _monitors, gameKey);
        _area = AreaOf(_monitor);
    }

    /// <summary>Work area normally; the full monitor while a full-screen app is in front on it (no taskbar there).</summary>
    private PixelRect AreaOf(OverlayMonitor? m)
    {
        if (m is null) return new PixelRect(0, 0, 1920, 1080);
        var fullscreenHere = _fg.Fullscreen && string.Equals(_fg.MonitorDevice, m.DeviceName, StringComparison.OrdinalIgnoreCase);
        return fullscreenHere ? m.Bounds : m.WorkArea;
    }

    private void BuildScene(double scale)
    {
        _scene?.Dispose();
        _scene = null;
        var ctx = _ctx!;
        _scene = new Scene(new SceneInput(_layout!, _theme, _lookup!, ctx.Culture, ctx.TemperatureUnit, ctx.LabelResolver, scale));
        _sceneScale = scale;
        _scene.Update(LatestValues());
    }

    private ReadOnlySpan<float> LatestValues()
    {
        var frame = _ctx?.Hub.Latest;
        if (frame is null) return default;
        return frame.Values.AsSpan(0, Math.Min(frame.Count, frame.Values.Length));
    }

    private bool EnsureSurface()
    {
        var w = _scene!.Width;
        var h = _scene.Height;
        if (w > MaxSurface || h > MaxSurface)
        {
            // more rows than any screen could show: no retry loop, it is drawn again as soon as a layout change makes it smaller
            if (!_warnedHuge) Log.Warn($"overlay: {w}x{h} px is too big to draw");
            _warnedHuge = true;
            return false;
        }
        _warnedHuge = false;
        try
        {
            _surface ??= new GpuSurface(_hwnd, _options.PreferWarp);
            if (!_surface.Ready)
            {
                _surface.Create(w, h);
                _retries = 0;
            }
            else if (_surface.Width != w || _surface.Height != h)
            {
                var hr = _surface.Resize(w, h);
                if (hr < 0)
                {
                    Log.Warn($"overlay: ResizeBuffers 0x{hr:X8}");
                    _surface.Create(w, h);
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("overlay: graphics init failed", ex);
            _surface?.Release();
            ScheduleRetry();
            return false;
        }
    }

    private void ScheduleRetry()
    {
        _retries++;
        var delay = _retries <= 3 ? 500u : 5000u;
        Win32.SetTimer(_hwnd, TimerRetry, delay, 0);
    }

    private void Render()
    {
        if (_scene is null || _surface is null) return;
        if (!_surface.Ready && !EnsureSurface()) return;
        _renders++;
        var hr = _surface.Render(_scene, _alpha, _editing);
        if (hr >= 0) return;
        Log.Warn($"overlay: render failed 0x{hr:X8}");
        try
        {
            _surface.Reset();
            _ = _surface.Resize(_scene.Width, _scene.Height);
            hr = _surface.Render(_scene, _alpha, _editing);
            if (hr < 0) throw new OverlayGraphicsException("render after reset", hr);
            _retries = 0;
        }
        catch (Exception ex)
        {
            Log.Error("overlay: device recovery failed", ex);
            _surface.Release();
            ScheduleRetry();
        }
    }

    private void Position()
    {
        if (_scene is null || _layout is null) return;
        var (x, y) = OverlayPlacementMath.Position(_layout.Placement, _area, _scene.Width, _scene.Height, _sceneScale);
        _x = x;
        _y = y;
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, x, y, _scene.Width, _scene.Height, Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
    }

    private void Show()
    {
        _shown = true;
        if (_options.NeverShow) return;
        Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
        AssertTopmost();
    }

    private void Hide()
    {
        _shown = false;
        Win32.ShowWindow(_hwnd, Win32.SW_HIDE);
        // Free the GPU device if the overlay stays hidden (e.g. "only in games" with no game running).
        Win32.SetTimer(_hwnd, TimerIdle, 30_000, 0);
    }

    private void AssertTopmost()
    {
        if (_shown && !_options.NeverShow)
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
    }

    private void BeginFade(float target)
    {
        if (_ctx is null || !_ctx.Animations)
        {
            _fading = false;
            _alpha = target;
            if (target <= 0) Hide();
            else Render();
            return;
        }
        _fadeFrom = _alpha;
        _fadeTo = target;
        _fadeStart = Stopwatch.GetTimestamp();
        _fading = true;
        Win32.SetTimer(_hwnd, TimerFade, 15, 0);
    }

    private void FadeTick()
    {
        var t = (float)Math.Min(1, Stopwatch.GetElapsedTime(_fadeStart).TotalMilliseconds / FadeMs);
        var eased = 1 - (1 - t) * (1 - t) * (1 - t);
        _alpha = _fadeFrom + (_fadeTo - _fadeFrom) * eased;
        if (t >= 1)
        {
            _alpha = _fadeTo;
            _fading = false;
            Win32.KillTimer(_hwnd, TimerFade);
            if (_fadeTo <= 0)
            {
                Hide();
                return;
            }
        }
        Render();
    }

    private void UpdateDemand()
    {
        if (_ctx is null || _layout is null) return;
        var ids = _layout.Items.SelectMany(i => i.SensorIds).Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal).ToArray();
        var interval = TimeSpan.FromMilliseconds(_layout.RefreshMs);
        try
        {
            if (_demand is null) _demand = _ctx.Hub.Demand("overlay", ids, interval);
            else _demand.Update(ids, interval);
        }
        catch (Exception ex)
        {
            Log.Error("overlay: demand", ex);
        }
    }

    private void StartRefresh()
    {
        var ms = _layout?.RefreshMs ?? 1000;
        if (_refreshRunning && _refreshInterval == ms) return;
        // Coalescable: the system may batch our wake-up with others (tolerance 10 %).
        Win32.SetCoalescableTimer(_hwnd, TimerRefresh, (uint)ms, 0, (uint)Math.Max(10, ms / 10));
        _refreshRunning = true;
        _refreshInterval = ms;
    }

    private void StopRefresh()
    {
        if (!_refreshRunning) return;
        Win32.KillTimer(_hwnd, TimerRefresh);
        _refreshRunning = false;
    }

    private bool RefreshTick()
    {
        _ticks++;
        if (_scene is null) return false;
        try
        {
            int w = _scene.Width, h = _scene.Height;
            if (!_scene.Update(LatestValues())) return false;
            if (_scene.Width != w || _scene.Height != h)
            {
                if (!EnsureSurface()) return false;
                if (!_dragging) Position();
            }
            if (!_fading) Render();
            return true;
        }
        catch (Exception ex)
        {
            if (!_warnedRefresh) Log.Error("overlay: refresh", ex);
            _warnedRefresh = true;
            return false;
        }
    }

    private void OnTimer(nuint id)
    {
        switch (id)
        {
            case TimerRefresh:
                RefreshTick();
                break;
            case TimerFade:
                FadeTick();
                break;
            case TimerDisplay:
                Win32.KillTimer(_hwnd, TimerDisplay);
                OnDisplayChanged();
                break;
            case TimerForeground:
                Win32.KillTimer(_hwnd, TimerForeground);
                OnForegroundChanged();
                break;
            case TimerRetry:
                Win32.KillTimer(_hwnd, TimerRetry);
                Reconcile();
                break;
            case TimerIdle:
                Win32.KillTimer(_hwnd, TimerIdle);
                if (!_shown)
                {
                    _surface?.Dispose();
                    _surface = null;
                }
                break;
        }
    }

    // ── events ──────────────────────────────────────────────────────────────────────────────────────────────────

    private void OnForegroundEvent() => Win32.SetTimer(_hwnd, TimerForeground, 120, 0);

    private void OnForegroundChanged()
    {
        var fg = ForegroundWatcher.Evaluate();
        var relevant = fg.Fullscreen != _fg.Fullscreen || fg.Exclusive != _fg.Exclusive
                       || (fg.Fullscreen && !string.Equals(fg.MonitorDevice, _fg.MonitorDevice, StringComparison.OrdinalIgnoreCase));
        _fg = fg;
        if (fg.Exclusive && fg.Hwnd != _exclusiveNotified)
        {
            _exclusiveNotified = fg.Hwnd;
            try
            {
                ExclusiveFullscreenDetected?.Invoke(fg.ProcessId);
            }
            catch (Exception ex)
            {
                Log.Error("overlay: ExclusiveFullscreenDetected handler", ex);
            }
        }
        if (relevant) Reconcile();
        AssertTopmost();
    }

    private void ScheduleDisplayUpdate() => Win32.SetTimer(_hwnd, TimerDisplay, 250, 0);

    private void OnDisplayChanged()
    {
        _monitors = EnumerateMonitors();
        _fg = ForegroundWatcher.Evaluate();
        if (_surface is { Ready: true } s && !s.CheckComposition())
        {
            Log.Warn("overlay: composition device invalid after display change");
            s.Release();
        }
        Reconcile();
    }

    private void OnInventoryChanged()
    {
        if (_scene is { HasUnresolved: true } && _layout is not null)
        {
            BuildScene(_sceneScale);
            Reconcile();
        }
    }

    // ── edit mode ───────────────────────────────────────────────────────────────────────────────────────────────

    private void EnterEditCore(Action<OverlayPlacement> commit, Action cancel)
    {
        if (_layout is null)
        {
            Safe(cancel);
            return;
        }
        if (_editing) ExitEditCore(false);
        _editing = true;
        _commit = commit;
        _cancel = cancel;
        _editStart = LayoutCopy.Clone(_layout.Placement);
        var ex = (uint)(long)Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE);
        ex &= ~(Win32.WS_EX_TRANSPARENT | Win32.WS_EX_NOACTIVATE);
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, (nint)ex);
        Reconcile();
        if (!_options.NeverShow)
        {
            Win32.SetForegroundWindow(_hwnd);
            Win32.SetFocus(_hwnd);
        }
        Render();
    }

    private void ExitEditCore(bool commit)
    {
        if (!_editing) return;
        if (_dragging)
        {
            _dragging = false;
            Win32.ReleaseCapture();
        }
        _guides?.Dispose();
        _guides = null;
        OverlayPlacement? result = null;
        if (commit && _scene is not null) result = PlacementFromWindow();
        _layout!.Placement = result ?? _editStart ?? _layout.Placement;
        var ex = (uint)(long)Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE);
        ex |= Win32.WS_EX_TRANSPARENT | Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, (nint)ex);
        _editing = false;
        var onCommit = _commit;
        var onCancel = _cancel;
        _commit = null;
        _cancel = null;
        Reconcile();
        if (result is not null && onCommit is not null) Safe(() => onCommit(LayoutCopy.Clone(result)));
        else if (onCancel is not null) Safe(onCancel);
    }

    private static void Safe(Action a)
    {
        try
        {
            a();
        }
        catch (Exception ex)
        {
            Log.Error("overlay: edit callback", ex);
        }
    }

    /// <summary>The placement that reproduces the window's current position (monitor under its center).</summary>
    private OverlayPlacement PlacementFromWindow()
    {
        var w = _scene!.Width;
        var h = _scene.Height;
        var (m, area) = MonitorAt(_x + w / 2, _y + h / 2);
        var cur = _layout!.Placement;
        var p = OverlayPlacementMath.FromPosition(_x, _y, w, h, area, _sceneScale, cur);
        if (m is null || cur.MonitorMode == OverlayMonitorMode.FollowGame) return p;
        var same = cur.MonitorMode switch
        {
            OverlayMonitorMode.Primary => m.IsPrimary,
            _ => string.Equals(cur.MonitorKey, m.Key, StringComparison.OrdinalIgnoreCase),
        };
        if (!same)
        {
            p.MonitorMode = m.IsPrimary ? OverlayMonitorMode.Primary : OverlayMonitorMode.Specific;
            p.MonitorKey = m.IsPrimary ? null : m.Key;
        }
        return p;
    }

    private (OverlayMonitor? Monitor, PixelRect Area) MonitorAt(int x, int y)
    {
        var handle = Win32.MonitorFromPoint(new POINT { X = x, Y = y }, Win32.MONITOR_DEFAULTTONEAREST);
        var m = Monitors.Describe(handle);
        return (m, AreaOf(m));
    }

    private void BeginDrag()
    {
        POINT p;
        Win32.GetCursorPos(&p);
        _dragCursor = p;
        _dragX = _x;
        _dragY = _y;
        _dragging = true;
        Win32.SetCapture(_hwnd);
        _guides ??= new GuideWindows(_theme.CategoryColor(HardwareKind.Cpu), _layout?.HideFromCapture ?? true);
    }

    private void Drag()
    {
        POINT p;
        Win32.GetCursorPos(&p);
        MoveTo(_dragX + p.X - _dragCursor.X, _dragY + p.Y - _dragCursor.Y, snap: !Win32.ShiftDown);
    }

    private void EndDrag()
    {
        _dragging = false;
        Win32.ReleaseCapture();
        _guides?.Hide();
    }

    private void HandleKey(int vk, bool shift)
    {
        if (!_editing) return;
        var step = shift ? 10 : 1;
        switch (vk)
        {
            case Win32.VK_LEFT:
                MoveTo(_x - step, _y, false);
                break;
            case Win32.VK_RIGHT:
                MoveTo(_x + step, _y, false);
                break;
            case Win32.VK_UP:
                MoveTo(_x, _y - step, false);
                break;
            case Win32.VK_DOWN:
                MoveTo(_x, _y + step, false);
                break;
            case Win32.VK_RETURN:
                ExitEditCore(true);
                break;
            case Win32.VK_ESCAPE:
                ExitEditCore(false);
                break;
        }
    }

    /// <summary>Moves the overlay in edit mode (follows a monitor change: rescale), optionally snapping.</summary>
    private void MoveTo(int x, int y, bool snap)
    {
        if (_scene is null || _layout is null) return;
        var (m, area) = MonitorAt(x + _scene.Width / 2, y + _scene.Height / 2);
        if (m is not null && !string.Equals(m.DeviceName, _monitor?.DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            _monitor = m;
            _area = area;
            var scale = OverlayPlacementMath.Scale(_layout.Style, m.Bounds.Height);
            if (Math.Abs(scale - _sceneScale) > 1e-6)
            {
                BuildScene(scale);
                EnsureSurface();
            }
        }
        else
        {
            _area = area;
        }
        int w = _scene.Width, h = _scene.Height;
        int? gx = null, gy = null;
        if (snap)
        {
            var s = OverlayPlacementMath.Snap(x, y, w, h, _area, (int)Math.Round(_layout.Placement.Margin * _sceneScale), (int)Math.Round(10 * _sceneScale));
            (x, y, gx, gy) = (s.X, s.Y, s.GuideX, s.GuideY);
        }
        x = Math.Clamp(x, _area.Left, Math.Max(_area.Left, _area.Right - w));
        y = Math.Clamp(y, _area.Top, Math.Max(_area.Top, _area.Bottom - h));
        _x = x;
        _y = y;
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, x, y, w, h, Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
        // Keep the placement in sync, so anything that re-lays out meanwhile (value growth, display change) keeps the spot.
        _layout.Placement = PlacementFromWindow();
        if (_dragging) _guides?.Show(gx, gy, _area, (int)Math.Max(1, Math.Round(_sceneScale)), _options.NeverShow);
        Render();
    }

    private sealed class HubLookup(ISensorHub hub) : ISensorLookup
    {
        public SensorDescriptor? Find(string sensorId) => hub.Find(sensorId);

        public int Slot(SensorDescriptor sensor) => sensor.Index;
    }
}

/// <summary>Internal options (tests and measurements).</summary>
internal sealed record OverlayHostOptions
{
    /// <summary>Never call ShowWindow: the window exists and renders / presents, but is never visible (tests).</summary>
    public bool NeverShow { get; init; }

    /// <summary>Tests: the monitor list (synthetic monitors, removal, resolution changes) instead of the real displays.</summary>
    public Func<IReadOnlyList<OverlayMonitor>>? MonitorSource { get; init; }

    /// <summary>
    /// Render with the WARP software device (default) instead of the GPU; the other one is the fallback.
    /// BETTERVITALS_OVERLAY_GPU=1 switches the default to the GPU (troubleshooting).
    /// </summary>
    public bool PreferWarp { get; init; } = Environment.GetEnvironmentVariable("BETTERVITALS_OVERLAY_GPU") != "1";
}

/// <summary>Snapshot of the host state for tests.</summary>
internal sealed record OverlayDiagnostics(
    nint Hwnd, bool Shown, bool WindowVisible, PixelRect WindowRect, uint ExStyle, uint DisplayAffinity,
    int SceneWidth, int SceneHeight, double Scale, bool SurfaceReady, int DeviceCreations, bool Warp,
    bool Editing, string? MonitorKey, PixelRect Area, bool HasDemand, bool RefreshRunning, int Ticks, int Renders);
