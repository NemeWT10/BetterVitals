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
/// The on-screen overlay: one native top-most, click-through, never-activating popup per box (the main box and every
/// detached one; not in Alt+Tab or on the taskbar, excluded from capture), each exactly as big as its content, all drawn
/// with one shared D3D11 device + Direct2D context + DirectComposition device (a DXGI composition swap chain per window),
/// on one per-monitor-DPI-aware thread with its own message loop. Anti-cheat safe: no injection, no hooks into other
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
    private const uint WindowExStyle = Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TRANSPARENT
                                       | Win32.WS_EX_LAYERED | Win32.WS_EX_NOREDIRECTIONBITMAP;
    private static readonly Lock ClassGate = new();
    private static bool _classRegistered;

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly OverlayHostOptions _options;
    private readonly Action _onInventory;
    private Thread? _thread;

    /// <summary>The main box's window; it also gets the thread's timers and posted commands.</summary>
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

    /// <summary>One window per box; [0] is the main box (<see cref="_hwnd"/>), the others follow the layout's order.</summary>
    private readonly List<BoxWindow> _boxes = [];

    private GpuDevice? _device;
    private IDemand? _demand;
    private ForegroundWatcher? _watcher;
    private ForegroundState _fg;
    private nint _exclusiveNotified;
    private List<OverlayMonitor> _monitors = [];
    private bool _shown;
    private float _alpha;
    private float _fadeFrom, _fadeTo;
    private long _fadeStart;
    private bool _fading;
    private int _retries;
    private bool _refreshRunning;
    private int _refreshInterval;
    private bool _warnedRefresh;
    private int _ticks;
    private int _renders;

    // edit mode
    private bool _editing;
    private Action<IReadOnlyDictionary<string, OverlayPlacement>>? _commit;
    private Action? _cancel;
    private Dictionary<string, OverlayPlacement>? _editStart;
    private BoxWindow? _active;
    private BoxWindow? _dragBox;
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

    /// <summary>Starts the overlay thread and creates the (hidden) main window. Idempotent.</summary>
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
    /// <see cref="OverlayLayout.Visibility"/>; creates and destroys the windows of detached boxes.
    /// </summary>
    public void Apply(OverlayLayout layout, OverlayTheme theme, OverlayContext ctx)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(ctx);
        var copy = LayoutCopy.Clone(layout); // normalized: whoever built the layout, the windows only ever see sane numbers
        Post(() => ApplyCore(copy, theme, ctx));
    }

    /// <summary>Temporarily hides (false) or allows (true, default) the overlay on top of the layout's own rules.</summary>
    public void SetVisible(bool visible) => Post(() =>
    {
        _userVisible = visible;
        Reconcile();
    });

    /// <summary>
    /// Makes every box of the real overlay hit-testable for exact positioning: drag a box with the mouse (snaps to
    /// work-area edges, the default margin, the center and the other boxes; hold Shift for no snapping), arrow keys move
    /// the selected box 1 px (Shift: 10 px), Tab selects the next box, Enter commits, Esc cancels. Keys work only while
    /// a box has focus in this mode — never global hotkeys. The overlay is shown during edit mode even when the layout
    /// would hide it. <paramref name="commit"/> receives the placement of every box by id
    /// (<see cref="OverlayLayout.MainBoxId"/> for the main one; anchor = the ninth of the screen the box ended in);
    /// both callbacks run on the overlay thread.
    /// </summary>
    public void EnterEditMode(Action<IReadOnlyDictionary<string, OverlayPlacement>> commit, Action cancel)
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
    /// Renders one box of the overlay offscreen (Direct2D → WIC, software, never on screen) for tests, snapshots and the
    /// WPF editor preview. <paramref name="monitorHeightPx"/> drives the automatic scale (1080 → ×1, 2160 → ×2).
    /// Returns premultiplied BGRA (WPF: <c>PixelFormats.Pbgra32</c>). Any thread.
    /// </summary>
    public static OverlayBitmap RenderToBitmap(OverlayLayout layout, OverlayTheme theme, IReadOnlyList<PreviewSensor> values, int monitorHeightPx,
        CultureInfo? culture = null, TemperatureUnit temperatureUnit = TemperatureUnit.Celsius, Func<OverlayItem, string?>? labelResolver = null,
        string boxId = OverlayLayout.MainBoxId)
        => Offscreen.Render(layout, theme, values, monitorHeightPx, culture, temperatureUnit, labelResolver, boxId: boxId);

    /// <summary>
    /// <see cref="RenderToBitmap"/> for every box that has something to show (main box first), each as the window of
    /// that box would draw it. Any thread.
    /// </summary>
    public static IReadOnlyList<(string BoxId, OverlayBitmap Bitmap)> RenderBoxesToBitmaps(OverlayLayout layout, OverlayTheme theme, IReadOnlyList<PreviewSensor> values,
        int monitorHeightPx, CultureInfo? culture = null, TemperatureUnit temperatureUnit = TemperatureUnit.Celsius, Func<OverlayItem, string?>? labelResolver = null)
    {
        var copy = LayoutCopy.Clone(layout);
        var result = new List<(string, OverlayBitmap)>();
        foreach (var box in copy.AllBoxes())
        {
            if (box.Rows.Count == 0) continue;
            result.Add((box.Id, Offscreen.Render(copy, theme, values, monitorHeightPx, culture, temperatureUnit, labelResolver, boxId: box.Id)));
        }
        return result;
    }

    /// <summary>As <see cref="RenderToBitmap"/>, written to a PNG file (straight alpha). Returns the image size.</summary>
    public static (int Width, int Height) RenderToPng(OverlayLayout layout, OverlayTheme theme, IReadOnlyList<PreviewSensor> values, int monitorHeightPx,
        string path, CultureInfo? culture = null, TemperatureUnit temperatureUnit = TemperatureUnit.Celsius, Func<OverlayItem, string?>? labelResolver = null)
    {
        var bmp = RenderToBitmap(layout, theme, values, monitorHeightPx, culture, temperatureUnit, labelResolver);
        Offscreen.WritePng(bmp, path);
        return (bmp.Width, bmp.Height);
    }

    /// <summary>Stops the overlay thread and releases the windows and all graphics resources.</summary>
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

    /// <summary>The main box's window and the shared state (plus every box in <see cref="OverlayDiagnostics.Boxes"/>).</summary>
    internal OverlayDiagnostics Diagnostics() => Invoke(() =>
    {
        var main = _boxes[0];
        var d = BoxDiagnosticsOf(main);
        return new OverlayDiagnostics(_hwnd, _shown, d.WindowVisible, d.WindowRect, d.ExStyle, d.DisplayAffinity,
            d.SceneWidth, d.SceneHeight, main.Scale, main.Surface?.Ready ?? false, _device?.DeviceCreations ?? 0, _device?.IsWarp ?? false,
            _editing, main.Monitor?.Key, main.Area, _demand is not null, _refreshRunning, _ticks, _renders,
            [.. _boxes.Select(BoxDiagnosticsOf)], _active?.Id);
    });

    private BoxDiagnostics BoxDiagnosticsOf(BoxWindow b)
    {
        RECT r;
        Win32.GetWindowRect(b.Hwnd, &r);
        var ex = (uint)(long)Win32.GetWindowLongPtr(b.Hwnd, Win32.GWL_EXSTYLE);
        uint affinity = 0;
        GetWindowDisplayAffinity(b.Hwnd, &affinity);
        return new BoxDiagnostics(b.Id, b.Hwnd, _shown && Drawable(b), Win32.IsWindowVisible(b.Hwnd), new PixelRect(r.Left, r.Top, r.Right, r.Bottom), ex, affinity,
            b.Scene?.Width ?? 0, b.Scene?.Height ?? 0, b.Scale, b.Surface?.Ready ?? false, b.Monitor?.Key, b.Area);
    }

    /// <summary>The value text of every item of every box as drawn right now ("—" = no value), for tests.</summary>
    internal string[] ShownValues() => Invoke(() => _boxes.Where(b => b.Scene is not null).SelectMany(b => b.Scene!.Items).Select(i => new string(i.Main.Text)).ToArray());

    /// <summary>The swap chain's back buffer of a box (what its window shows), for tests.</summary>
    internal OverlayBitmap? ReadBack(string boxId = OverlayLayout.MainBoxId) => Invoke(() =>
    {
        var b = _boxes.Find(x => x.Id == boxId);
        if (b?.Scene is null || b.Surface is not { Ready: true } s) return null;
        if (s.Render(b.Scene, _alpha, _editing, Emphasis(b), present: false) < 0) return null;
        var bmp = s.ReadBack();
        RenderAll();
        return bmp;
    });

    /// <summary>One refresh tick right now (tests / measurements).</summary>
    internal bool RefreshNow() => Invoke(RefreshTick);

    /// <summary>Simulates a lost device (tests): drops the device and recovers like after a TDR.</summary>
    internal bool SimulateDeviceLost() => Invoke(() =>
    {
        if (_device is null) return false;
        foreach (var b in _boxes) b.Surface?.Release();
        _device.Release();
        RenderAll();
        return _device.Ready && _boxes.All(b => b.Scene is null || b.TooBig || b.Surface is { Ready: true });
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
            _hwnd = CreateOverlayWindow();
            if (_hwnd == 0) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()})");
            _boxes.Add(new BoxWindow(OverlayLayout.MainBoxId, _hwnd));
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

    /// <summary>A box window: top-most, tool window, click-through (except in edit mode), layered for DirectComposition content.</summary>
    private nint CreateOverlayWindow()
    {
        var ex = _editing ? WindowExStyle & ~(Win32.WS_EX_TRANSPARENT | Win32.WS_EX_NOACTIVATE) : WindowExStyle;
        nint hwnd;
        fixed (char* cls = ClassName)
        fixed (char* title = "BetterVitals Overlay")
            hwnd = Win32.CreateWindowEx(ex, cls, title, Win32.WS_POPUP, 0, 0, 1, 1, 0, 0, Win32.GetModuleHandle(null), GCHandle.ToIntPtr(_self));
        if (hwnd == 0) return 0;
        // Layered + transparent = click-through for a top-level window; alpha 255 lets DirectComposition content show.
        Win32.SetLayeredWindowAttributes(hwnd, 0, 255, Win32.LWA_ALPHA);
        if (_layout is not null) SetAffinity(hwnd);
        return hwnd;
    }

    private void SetAffinity(nint hwnd)
    {
        if (!Win32.SetWindowDisplayAffinity(hwnd, _layout!.HideFromCapture ? Win32.WDA_EXCLUDEFROMCAPTURE : Win32.WDA_NONE))
            Log.Warn($"overlay: SetWindowDisplayAffinity failed ({Marshal.GetLastPInvokeError()})");
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
            foreach (var b in _boxes)
            {
                b.Scene?.Dispose();
                b.Scene = null;
                b.Surface?.Dispose();
                b.Surface = null;
            }
            _boxes.Clear();
            _device?.Dispose();
            _device = null;
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

    private BoxWindow? BoxOf(nint hwnd) => _boxes.Find(b => b.Hwnd == hwnd);

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
                if (BoxOf(hwnd) is { } pressed) BeginDrag(pressed);
                return true;
            case Win32.WM_MOUSEMOVE when _dragBox is not null:
                Drag();
                return true;
            case Win32.WM_LBUTTONUP when _dragBox is not null:
                EndDrag();
                return true;
            case Win32.WM_CAPTURECHANGED when _dragBox is not null:
                _dragBox = null;
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
                // only the host closes its windows (Dispose → the main window); a box window ignores it
                if (hwnd == _hwnd)
                {
                    for (var i = _boxes.Count - 1; i >= 1; i--) DestroyBox(_boxes[i]);
                    Win32.DestroyWindow(hwnd);
                }
                return true;
            case Win32.WM_DESTROY:
                Win32.SetWindowLongPtr(hwnd, Win32.GWLP_USERDATA, 0);
                if (hwnd == _hwnd) Win32.PostQuitMessage(0);
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
        // While editing, keep the positions the user is moving (the app may re-apply styles meanwhile).
        if (_editing && _layout is not null)
        {
            layout.Placement = _layout.Placement;
            foreach (var box in layout.Boxes)
                if (_layout.Boxes.Find(b => b.Id == box.Id) is { } old) box.Placement = old.Placement;
        }
        _layout = layout;
        _theme = theme;
        _ctx = ctx;
        _lookup = new HubLookup(ctx.Hub);
        SyncWindows();
        foreach (var b in _boxes)
        {
            SetAffinity(b.Hwnd);
            b.Scene?.Dispose();
            b.Scene = null;
        }
        Reconcile();
    }

    /// <summary>One window per box of the layout: new boxes get one, removed boxes lose theirs.</summary>
    private void SyncWindows()
    {
        var order = _layout!.Boxes.Select(b => b.Id).ToList();
        for (var i = _boxes.Count - 1; i >= 1; i--)
            if (!order.Contains(_boxes[i].Id)) DestroyBox(_boxes[i]);
        foreach (var id in order)
        {
            if (_boxes.Exists(b => b.Id == id)) continue;
            var hwnd = CreateOverlayWindow();
            if (hwnd == 0)
            {
                Log.Warn($"overlay: box window failed ({Marshal.GetLastPInvokeError()})");
                continue;
            }
            _boxes.Add(new BoxWindow(id, hwnd));
        }
        // the layout's order (main first): edit-mode Tab and the diagnostics follow it
        _boxes.Sort((a, b) => Order(a).CompareTo(Order(b)));
        int Order(BoxWindow w) => w.Id == OverlayLayout.MainBoxId ? -1 : order.IndexOf(w.Id);
    }

    private void DestroyBox(BoxWindow b)
    {
        if (_dragBox == b)
        {
            _dragBox = null;
            Win32.ReleaseCapture();
            _guides?.Hide();
        }
        if (_active == b) _active = _boxes[0];
        b.Scene?.Dispose();
        b.Scene = null;
        b.Surface?.Dispose();
        b.Surface = null;
        _boxes.Remove(b);
        Win32.DestroyWindow(b.Hwnd);
    }

    private bool WantVisible()
    {
        if (_layout is null || _ctx is null || _disposed) return false;
        if (_editing) return true;
        if (!_layout.Enabled || !_userVisible || _layout.IsEmpty) return false;
        return _layout.Visibility == OverlayVisibility.Always || _fg.Fullscreen;
    }

    /// <summary>A box with content, a surface and a sane size: its window can show.</summary>
    private static bool Drawable(BoxWindow b) => b.Scene is not null && !b.TooBig && b.Surface is { Ready: true };

    /// <summary>Brings windows, scenes, surfaces, demand and timers in line with the current state.</summary>
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
        foreach (var b in _boxes)
        {
            if (_layout!.RowsOf(b.Id) is not { Count: > 0 })
            {
                DropScene(b);
                continue;
            }
            if (b != _dragBox) ResolveMonitor(b);
            var scale = OverlayPlacementMath.Scale(_layout!.Style, b.Monitor?.Bounds.Height ?? 1080);
            if (b.Scene is null || Math.Abs(scale - b.Scale) > 1e-6) BuildScene(b, scale);
        }
        if (!EnsureGraphics()) return; // the retry timer is running
        if (!_boxes.Exists(Drawable))
        {
            // more rows than any screen could show: nothing to read for it until a layout change makes it smaller
            StopRefresh();
            _demand?.Dispose();
            _demand = null;
            if (_shown) BeginFade(0);
            return;
        }
        foreach (var b in _boxes)
            if (Drawable(b) && b != _dragBox) Position(b);
        UpdateDemand();
        StartRefresh();
        if (!_shown)
        {
            _alpha = _ctx!.Animations ? 0 : 1;
            RenderAll();
            Show();
            if (_ctx.Animations) BeginFade(1);
        }
        else
        {
            if (_fading && _fadeTo < 1) BeginFade(1);
            RenderAll();
            UpdateWindows();
        }
    }

    private void DropScene(BoxWindow b)
    {
        b.Scene?.Dispose();
        b.Scene = null;
        b.Surface?.Release();
        if (b.Visible)
        {
            Win32.ShowWindow(b.Hwnd, Win32.SW_HIDE);
            b.Visible = false;
        }
    }

    private List<OverlayMonitor> EnumerateMonitors() => _options.MonitorSource is { } source ? [.. source()] : Monitors.Enumerate();

    private void ResolveMonitor(BoxWindow b)
    {
        if (_monitors.Count == 0) _monitors = EnumerateMonitors();
        string? gameKey = null;
        if (_fg.Fullscreen && _fg.MonitorDevice is { } dev)
            gameKey = _monitors.FirstOrDefault(m => string.Equals(m.DeviceName, dev, StringComparison.OrdinalIgnoreCase))?.Key;
        b.Monitor = OverlayPlacementMath.Resolve(PlacementOf(b), _monitors, gameKey);
        b.Area = AreaOf(b.Monitor);
    }

    private OverlayPlacement PlacementOf(BoxWindow b) => _layout!.PlacementOf(b.Id) ?? _layout!.Placement;

    private void SetPlacement(string boxId, OverlayPlacement p)
    {
        if (boxId == OverlayLayout.MainBoxId) _layout!.Placement = p;
        else if (_layout!.Boxes.Find(x => x.Id == boxId) is { } box) box.Placement = p;
    }

    /// <summary>Work area normally; the full monitor while a full-screen app is in front on it (no taskbar there).</summary>
    private PixelRect AreaOf(OverlayMonitor? m)
    {
        if (m is null) return new PixelRect(0, 0, 1920, 1080);
        var fullscreenHere = _fg.Fullscreen && string.Equals(_fg.MonitorDevice, m.DeviceName, StringComparison.OrdinalIgnoreCase);
        return fullscreenHere ? m.Bounds : m.WorkArea;
    }

    private void BuildScene(BoxWindow b, double scale)
    {
        b.Scene?.Dispose();
        b.Scene = null;
        var ctx = _ctx!;
        b.Scene = new Scene(new SceneInput(_layout!, _theme, _lookup!, ctx.Culture, ctx.TemperatureUnit, ctx.LabelResolver, scale, b.Id));
        b.Scale = scale;
        b.Scene.Update(LatestValues());
    }

    private ReadOnlySpan<float> LatestValues()
    {
        var frame = _ctx?.Hub.Latest;
        if (frame is null) return default;
        return frame.Values.AsSpan(0, Math.Min(frame.Count, frame.Values.Length));
    }

    /// <summary>The shared device and a swap chain of the right size for every box with content.</summary>
    private bool EnsureGraphics()
    {
        try
        {
            _device ??= new GpuDevice(_options.PreferWarp);
            if (!_device.Ready)
            {
                foreach (var b in _boxes) b.Surface?.Release();
                _device.Create();
                _retries = 0;
            }
            foreach (var b in _boxes)
            {
                if (b.Scene is null)
                {
                    b.Surface?.Release();
                    continue;
                }
                int w = b.Scene.Width, h = b.Scene.Height;
                if (w > MaxSurface || h > MaxSurface)
                {
                    // more rows than any screen could show: no retry loop, it is drawn again once a layout change makes it smaller
                    if (!b.TooBig) Log.Warn($"overlay: {w}x{h} px is too big to draw");
                    b.TooBig = true;
                    b.Surface?.Release();
                    continue;
                }
                b.TooBig = false;
                b.Surface ??= new SwapSurface(_device, b.Hwnd);
                if (!b.Surface.Ready)
                {
                    b.Surface.Create(w, h);
                }
                else if (b.Surface.Width != w || b.Surface.Height != h)
                {
                    var hr = b.Surface.Resize(w, h);
                    if (hr < 0)
                    {
                        Log.Warn($"overlay: ResizeBuffers 0x{hr:X8}");
                        b.Surface.Create(w, h);
                    }
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("overlay: graphics init failed", ex);
            ReleaseGraphics(dispose: false);
            ScheduleRetry();
            return false;
        }
    }

    /// <summary>Every surface and the device; <paramref name="dispose"/> also forgets the device (idle).</summary>
    private void ReleaseGraphics(bool dispose)
    {
        foreach (var b in _boxes)
        {
            b.Surface?.Dispose();
            b.Surface = null;
        }
        if (dispose)
        {
            _device?.Dispose();
            _device = null;
        }
        else
        {
            _device?.Release();
        }
    }

    private void ScheduleRetry()
    {
        _retries++;
        var delay = _retries <= 3 ? 500u : 5000u;
        Win32.SetTimer(_hwnd, TimerRetry, delay, 0);
    }

    private float Emphasis(BoxWindow b) => !_editing || b == _active || _boxes.Count(Drawable) <= 1 ? 1 : 0.45f;

    private void RenderAll()
    {
        if (_device is not { Ready: true } || _boxes.Exists(b => b.Scene is not null && !b.TooBig && b.Surface is not { Ready: true }))
            if (!EnsureGraphics()) return;
        foreach (var b in _boxes)
            if (!RenderBox(b)) return; // the device was recreated and everything drawn again
    }

    /// <summary>Draws one box; false when that failed and the device was recovered (every box drawn) or given up.</summary>
    private bool RenderBox(BoxWindow b)
    {
        if (!Drawable(b)) return true;
        _renders++;
        var hr = b.Surface!.Render(b.Scene!, _alpha, _editing, Emphasis(b));
        if (hr >= 0) return true;
        Log.Warn($"overlay: render failed 0x{hr:X8}");
        RecoverDevice();
        return false;
    }

    /// <summary>A failed draw or present (device removed / reset): a new device, new surfaces, everything drawn again.</summary>
    private void RecoverDevice()
    {
        try
        {
            Log.Warn("overlay: graphics device lost, recreating");
            foreach (var b in _boxes) b.Surface?.Release();
            _device!.Create();
            foreach (var b in _boxes)
            {
                if (b.Scene is null || b.TooBig || b.Surface is null) continue;
                b.Surface.Create(b.Scene.Width, b.Scene.Height);
                var hr = b.Surface.Render(b.Scene, _alpha, _editing, Emphasis(b));
                if (hr < 0) throw new OverlayGraphicsException("render after reset", hr);
            }
            _retries = 0;
        }
        catch (Exception ex)
        {
            Log.Error("overlay: device recovery failed", ex);
            ReleaseGraphics(dispose: false);
            ScheduleRetry();
        }
    }

    private void Position(BoxWindow b)
    {
        if (b.Scene is null || _layout is null) return;
        var (x, y) = OverlayPlacementMath.Position(PlacementOf(b), b.Area, b.Scene.Width, b.Scene.Height, b.Scale);
        b.X = x;
        b.Y = y;
        Win32.SetWindowPos(b.Hwnd, Win32.HWND_TOPMOST, x, y, b.Scene.Width, b.Scene.Height, Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
    }

    private void Show()
    {
        _shown = true;
        UpdateWindows();
        AssertTopmost();
    }

    /// <summary>Shows the windows of the boxes that can draw, hides the others (while the overlay is shown).</summary>
    private void UpdateWindows()
    {
        foreach (var b in _boxes)
        {
            var want = _shown && Drawable(b);
            if (want == b.Visible) continue;
            b.Visible = want;
            if (!want) Win32.ShowWindow(b.Hwnd, Win32.SW_HIDE);
            else if (!_options.NeverShow) Win32.ShowWindow(b.Hwnd, Win32.SW_SHOWNOACTIVATE);
        }
    }

    private void Hide()
    {
        _shown = false;
        foreach (var b in _boxes)
        {
            b.Visible = false;
            Win32.ShowWindow(b.Hwnd, Win32.SW_HIDE);
        }
        // Free the GPU device if the overlay stays hidden (e.g. "only in games" with no game running).
        Win32.SetTimer(_hwnd, TimerIdle, 30_000, 0);
    }

    private void AssertTopmost()
    {
        if (!_shown || _options.NeverShow) return;
        foreach (var b in _boxes)
            if (b.Visible)
                Win32.SetWindowPos(b.Hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
    }

    private void BeginFade(float target)
    {
        if (_ctx is null || !_ctx.Animations)
        {
            _fading = false;
            _alpha = target;
            if (target <= 0) Hide();
            else RenderAll();
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
        RenderAll();
    }

    private void UpdateDemand()
    {
        if (_ctx is null || _layout is null) return;
        var ids = _layout.AllItems().SelectMany(i => i.SensorIds).Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal).ToArray();
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

    /// <summary>New values into every box; redraws only the boxes whose picture changed. Allocation-free when nothing grows.</summary>
    private bool RefreshTick()
    {
        _ticks++;
        try
        {
            var values = LatestValues();
            var any = false;
            var resized = false;
            foreach (var b in _boxes)
            {
                b.Dirty = false;
                if (b.Scene is not { } scene) continue;
                int w = scene.Width, h = scene.Height;
                if (!scene.Update(values)) continue;
                any = b.Dirty = true;
                if (scene.Width != w || scene.Height != h) resized = true;
            }
            if (resized)
            {
                if (!EnsureGraphics()) return false;
                foreach (var b in _boxes)
                    if (Drawable(b) && b != _dragBox) Position(b);
                UpdateWindows();
            }
            if (!_fading)
                foreach (var b in _boxes)
                    if (b.Dirty && !RenderBox(b)) break;
            return any;
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
                if (!_shown) ReleaseGraphics(dispose: true);
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
        if (_device is { Ready: true } d && !d.CheckComposition())
        {
            Log.Warn("overlay: composition device invalid after display change");
            ReleaseGraphics(dispose: false);
        }
        Reconcile();
    }

    private void OnInventoryChanged()
    {
        if (_layout is null || !_boxes.Exists(b => b.Scene is { HasUnresolved: true })) return;
        foreach (var b in _boxes)
            if (b.Scene is not null) BuildScene(b, b.Scale);
        Reconcile();
    }

    // ── edit mode ───────────────────────────────────────────────────────────────────────────────────────────────

    private void EnterEditCore(Action<IReadOnlyDictionary<string, OverlayPlacement>> commit, Action cancel)
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
        _editStart = _layout.AllBoxes().ToDictionary(b => b.Id, b => LayoutCopy.Clone(b.Placement), StringComparer.Ordinal);
        foreach (var b in _boxes) SetClickThrough(b.Hwnd, false);
        Reconcile();
        _active = _boxes.Find(Drawable) ?? _boxes[0];
        if (!_options.NeverShow)
        {
            Win32.SetForegroundWindow(_active.Hwnd);
            Win32.SetFocus(_active.Hwnd);
        }
        RenderAll();
    }

    private static void SetClickThrough(nint hwnd, bool on)
    {
        var ex = (uint)(long)Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
        ex = on ? ex | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_NOACTIVATE : ex & ~(Win32.WS_EX_TRANSPARENT | Win32.WS_EX_NOACTIVATE);
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, (nint)ex);
    }

    private void ExitEditCore(bool commit)
    {
        if (!_editing) return;
        if (_dragBox is not null)
        {
            _dragBox = null;
            Win32.ReleaseCapture();
        }
        _guides?.Dispose();
        _guides = null;
        Dictionary<string, OverlayPlacement>? result = null;
        if (commit && _boxes.Exists(Drawable))
        {
            result = new Dictionary<string, OverlayPlacement>(StringComparer.Ordinal);
            foreach (var box in _layout!.AllBoxes())
            {
                var w = _boxes.Find(b => b.Id == box.Id);
                result[box.Id] = w is not null && Drawable(w) ? PlacementFromWindow(w) : LayoutCopy.Clone(box.Placement);
            }
        }
        foreach (var (id, p) in result ?? _editStart ?? [])
            SetPlacement(id, LayoutCopy.Clone(p));
        foreach (var b in _boxes) SetClickThrough(b.Hwnd, true);
        _editing = false;
        _active = null;
        var onCommit = _commit;
        var onCancel = _cancel;
        _commit = null;
        _cancel = null;
        _editStart = null;
        Reconcile();
        if (result is not null && onCommit is not null)
        {
            var copy = result.ToDictionary(kv => kv.Key, kv => LayoutCopy.Clone(kv.Value), StringComparer.Ordinal);
            Safe(() => onCommit(copy));
        }
        else if (onCancel is not null)
        {
            Safe(onCancel);
        }
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

    /// <summary>The placement that reproduces a box's current position (monitor under its center).</summary>
    private OverlayPlacement PlacementFromWindow(BoxWindow b)
    {
        var w = b.Scene!.Width;
        var h = b.Scene.Height;
        var (m, area) = MonitorAt(b.X + w / 2, b.Y + h / 2);
        var cur = PlacementOf(b);
        var p = OverlayPlacementMath.FromPosition(b.X, b.Y, w, h, area, b.Scale, cur);
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
        OverlayMonitor? m;
        if (_options.MonitorSource is not null)
        {
            // tests: the synthetic monitors, not the real desktop
            m = _monitors.FirstOrDefault(s => s.Bounds.Contains(x, y)) ?? _monitors.FirstOrDefault(s => s.IsPrimary) ?? _monitors.FirstOrDefault();
        }
        else
        {
            var handle = Win32.MonitorFromPoint(new POINT { X = x, Y = y }, Win32.MONITOR_DEFAULTTONEAREST);
            m = Monitors.Describe(handle);
        }
        return (m, AreaOf(m));
    }

    private void BeginDrag(BoxWindow b)
    {
        if (b.Scene is null) return;
        if (_active != b)
        {
            _active = b;
            RenderAll(); // the selected box stands out
        }
        POINT p;
        Win32.GetCursorPos(&p);
        _dragCursor = p;
        _dragX = b.X;
        _dragY = b.Y;
        _dragBox = b;
        Win32.SetCapture(b.Hwnd);
        _guides ??= new GuideWindows(_theme.CategoryColor(HardwareKind.Cpu), _layout?.HideFromCapture ?? true);
    }

    private void Drag()
    {
        if (_dragBox is not { } b) return;
        POINT p;
        Win32.GetCursorPos(&p);
        MoveTo(b, _dragX + p.X - _dragCursor.X, _dragY + p.Y - _dragCursor.Y, snap: !Win32.ShiftDown);
    }

    private void EndDrag()
    {
        _dragBox = null;
        Win32.ReleaseCapture();
        _guides?.Hide();
    }

    private void HandleKey(int vk, bool shift)
    {
        if (!_editing) return;
        var b = _active is not null && Drawable(_active) ? _active : _boxes.Find(Drawable);
        var step = shift ? 10 : 1;
        switch (vk)
        {
            case Win32.VK_LEFT when b is not null:
                MoveTo(b, b.X - step, b.Y, false);
                break;
            case Win32.VK_RIGHT when b is not null:
                MoveTo(b, b.X + step, b.Y, false);
                break;
            case Win32.VK_UP when b is not null:
                MoveTo(b, b.X, b.Y - step, false);
                break;
            case Win32.VK_DOWN when b is not null:
                MoveTo(b, b.X, b.Y + step, false);
                break;
            case Win32.VK_TAB:
                SelectNext(b, shift ? -1 : 1);
                break;
            case Win32.VK_RETURN:
                ExitEditCore(true);
                break;
            case Win32.VK_ESCAPE:
                ExitEditCore(false);
                break;
        }
    }

    /// <summary>Tab / Shift+Tab in edit mode: the next box the arrow keys move.</summary>
    private void SelectNext(BoxWindow? current, int dir)
    {
        var boxes = _boxes.Where(Drawable).ToList();
        if (boxes.Count < 2) return;
        var i = current is null ? -1 : boxes.IndexOf(current);
        _active = boxes[((i + dir) % boxes.Count + boxes.Count) % boxes.Count];
        if (!_options.NeverShow) Win32.SetFocus(_active.Hwnd);
        RenderAll();
    }

    /// <summary>Moves a box in edit mode (follows a monitor change: rescale), optionally snapping.</summary>
    private void MoveTo(BoxWindow b, int x, int y, bool snap)
    {
        if (b.Scene is null || _layout is null) return;
        var (m, area) = MonitorAt(x + b.Scene.Width / 2, y + b.Scene.Height / 2);
        if (m is not null && !string.Equals(m.DeviceName, b.Monitor?.DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            b.Monitor = m;
            b.Area = area;
            var scale = OverlayPlacementMath.Scale(_layout.Style, m.Bounds.Height);
            if (Math.Abs(scale - b.Scale) > 1e-6)
            {
                BuildScene(b, scale);
                if (!EnsureGraphics()) return;
            }
        }
        else
        {
            b.Area = area;
        }
        int w = b.Scene!.Width, h = b.Scene.Height;
        int? gx = null, gy = null;
        if (snap)
        {
            // the other boxes on this monitor are snap targets too (edges in line, side by side)
            var others = _boxes.Where(o => o != b && Drawable(o) && o.Monitor?.DeviceName == b.Monitor?.DeviceName)
                .Select(o => new PixelRect(o.X, o.Y, o.X + o.Scene!.Width, o.Y + o.Scene.Height)).ToList();
            var s = OverlayPlacementMath.Snap(x, y, w, h, b.Area, (int)Math.Round(PlacementOf(b).Margin * b.Scale), (int)Math.Round(10 * b.Scale), others, (int)Math.Round(8 * b.Scale));
            (x, y, gx, gy) = (s.X, s.Y, s.GuideX, s.GuideY);
        }
        x = Math.Clamp(x, b.Area.Left, Math.Max(b.Area.Left, b.Area.Right - w));
        y = Math.Clamp(y, b.Area.Top, Math.Max(b.Area.Top, b.Area.Bottom - h));
        b.X = x;
        b.Y = y;
        Win32.SetWindowPos(b.Hwnd, Win32.HWND_TOPMOST, x, y, w, h, Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
        // Keep the placement in sync, so anything that re-lays out meanwhile (value growth, display change) keeps the spot.
        SetPlacement(b.Id, PlacementFromWindow(b));
        if (_dragBox == b) _guides?.Show(gx, gy, b.Area, (int)Math.Max(1, Math.Round(b.Scale)), _options.NeverShow);
        UpdateWindows();
        RenderBox(b);
    }

    /// <summary>A box's window and what it shows (overlay thread).</summary>
    private sealed class BoxWindow(string id, nint hwnd)
    {
        public string Id { get; } = id;

        public nint Hwnd { get; } = hwnd;

        public Scene? Scene;
        public double Scale;
        public SwapSurface? Surface;
        public OverlayMonitor? Monitor;
        public PixelRect Area;
        public int X, Y;

        /// <summary>The window is shown right now.</summary>
        public bool Visible;

        /// <summary>Bigger than a texture can be: not drawn.</summary>
        public bool TooBig;

        /// <summary>Changed in this refresh tick.</summary>
        public bool Dirty;
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
    /// <summary>Never call ShowWindow: the windows exist and render / present, but are never visible (tests).</summary>
    public bool NeverShow { get; init; }

    /// <summary>Tests: the monitor list (synthetic monitors, removal, resolution changes) instead of the real displays.</summary>
    public Func<IReadOnlyList<OverlayMonitor>>? MonitorSource { get; init; }

    /// <summary>
    /// Render with the WARP software device (default) instead of the GPU; the other one is the fallback.
    /// BETTERVITALS_OVERLAY_GPU=1 switches the default to the GPU (troubleshooting).
    /// </summary>
    public bool PreferWarp { get; init; } = Environment.GetEnvironmentVariable("BETTERVITALS_OVERLAY_GPU") != "1";
}

/// <summary>Snapshot of the host state for tests: the main box's window and the shared state, every box in <see cref="Boxes"/>.</summary>
internal sealed record OverlayDiagnostics(
    nint Hwnd, bool Shown, bool WindowVisible, PixelRect WindowRect, uint ExStyle, uint DisplayAffinity,
    int SceneWidth, int SceneHeight, double Scale, bool SurfaceReady, int DeviceCreations, bool Warp,
    bool Editing, string? MonitorKey, PixelRect Area, bool HasDemand, bool RefreshRunning, int Ticks, int Renders,
    IReadOnlyList<BoxDiagnostics> Boxes, string? ActiveBox);

/// <summary>One box window for tests.</summary>
internal sealed record BoxDiagnostics(
    string Id, nint Hwnd, bool Shown, bool WindowVisible, PixelRect WindowRect, uint ExStyle, uint DisplayAffinity,
    int SceneWidth, int SceneHeight, double Scale, bool SurfaceReady, string? MonitorKey, PixelRect Area);
