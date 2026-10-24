using System.Runtime.InteropServices;
using BetterVitals.Overlay.Interop;

namespace BetterVitals.Overlay.Host;

/// <summary>What is in front right now.</summary>
/// <param name="Hwnd">Foreground window (0 = none).</param>
/// <param name="ProcessId">Its process (from <c>GetWindowThreadProcessId</c>; never opened).</param>
/// <param name="Fullscreen">A game / full-screen or borderless app covers its monitor.</param>
/// <param name="Exclusive">The shell reports exclusive Direct3D full screen (an overlay window may be invisible there).</param>
/// <param name="MonitorDevice">GDI name of the foreground window's monitor.</param>
internal readonly record struct ForegroundState(nint Hwnd, uint ProcessId, bool Fullscreen, bool Exclusive, string? MonitorDevice);

/// <summary>Pure classification of a foreground window (unit-tested).</summary>
internal static class ForegroundClassifier
{
    /// <summary>Desktop / shell surfaces that are never "a game", even when they cover the screen.</summary>
    public static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "MultitaskingViewFrame", "TaskSwitcherWnd", "ForegroundStaging",
        "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow", "Shell_InputSwitchTopLevelWindow",
        "LockScreenControllerProxyWindow", "ApplicationFrameWindow_Splash",
    };

    /// <param name="className">Window class of the foreground window.</param>
    /// <param name="style">GWL_STYLE.</param>
    /// <param name="window">Window rectangle.</param>
    /// <param name="monitor">Its monitor's full rectangle.</param>
    /// <param name="quns">SHQueryUserNotificationState result (0 when unknown).</param>
    /// <param name="ignored">Own process, cloaked, minimized or not visible.</param>
    public static (bool Fullscreen, bool Exclusive) Classify(string className, uint style, PixelRect window, PixelRect monitor, int quns, bool ignored)
    {
        if (ignored || ShellClasses.Contains(className)) return (false, false);
        var covers = window.Left <= monitor.Left && window.Top <= monitor.Top && window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;
        if (quns == Win32.QUNS_RUNNING_D3D_FULL_SCREEN) return (true, true);
        if (!covers) return (false, false);
        // An ordinary captioned window (even maximized) is not a full-screen app.
        if ((style & Win32.WS_CAPTION) == Win32.WS_CAPTION) return (false, false);
        return (true, false);
    }
}

/// <summary>
/// Event-driven foreground tracking (no polling): WinEvent hooks for foreground changes and minimize, plus a
/// location-change hook scoped to the foreground process (a game going full screen after it became foreground).
/// Events are coalesced by the owner (one evaluation shortly after the last event). No handles to other processes.
/// <para>
/// The location-change hook sees everything that moves in that process — its mouse cursor and text caret too: a game
/// with a system cursor sends hundreds of them a second while the mouse moves (measured in a real game: ~600 wake-ups/s
/// of the overlay thread). So after any one of them the hook is paused for <see cref="LocationPauseMs"/>; when it
/// resumes, the foreground window's rectangle and style are compared with the ones at the pause, and a change is
/// reported then (nothing that happened in the pause is lost, it is only seen up to that much later). Bounded to a few
/// wake-ups a second whatever the process does.
/// </para>
/// Overlay thread only (out-of-context hooks are delivered through its message loop).
/// </summary>
internal sealed unsafe class ForegroundWatcher : IDisposable
{
    /// <summary>How long the location-change hook stays off after an event.</summary>
    public const uint LocationPauseMs = 500;

    [ThreadStatic] private static ForegroundWatcher? _current;

    private readonly Action _onEvent;
    private readonly Action<uint> _scheduleResume;
    private nint _fgHook;
    private nint _minHook;
    private nint _locHook;
    private uint _locPid;
    private nint _fgHwnd;
    private bool _paused;
    private RECT _pausedRect;
    private nint _pausedStyle;

    /// <summary>Location-change events delivered to the hook (diagnostics / tests).</summary>
    public int LocationEvents { get; private set; }

    /// <summary>The hooks are installed (<see cref="Start"/> … <see cref="Stop"/>).</summary>
    public bool Running => _fgHook != 0;

    /// <summary>The location-change hook is paused after an event (tests).</summary>
    public bool LocationPaused => _paused;

    /// <param name="onEvent">Called on the overlay thread for every relevant event (the owner debounces).</param>
    /// <param name="scheduleResume">
    /// Asks the owner to call <see cref="ResumeLocation"/> once after this many ms (a window timer on the overlay thread).
    /// </param>
    public ForegroundWatcher(Action onEvent, Action<uint> scheduleResume)
    {
        _onEvent = onEvent;
        _scheduleResume = scheduleResume;
    }

    /// <summary>Installs the hooks and starts tracking the current foreground window. Idempotent.</summary>
    public void Start()
    {
        if (Running) return;
        _current = this;
        // Own process included: when the app's own window comes to the front, "a game is in front" must end too.
        _fgHook = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, 0, &OnWinEvent, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);
        _minHook = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_MINIMIZESTART, Win32.EVENT_SYSTEM_MINIMIZEEND, 0, &OnWinEvent, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);
        TrackForegroundProcess(Win32.GetForegroundWindow());
    }

    /// <summary>Removes every hook: nothing wakes the thread until <see cref="Start"/> (the overlay is off).</summary>
    public void Stop()
    {
        if (_fgHook != 0) Win32.UnhookWinEvent(_fgHook);
        if (_minHook != 0) Win32.UnhookWinEvent(_minHook);
        if (_locHook != 0) Win32.UnhookWinEvent(_locHook);
        _fgHook = _minHook = _locHook = 0;
        _locPid = 0;
        _fgHwnd = 0;
        _paused = false;
        if (ReferenceEquals(_current, this)) _current = null;
    }

    [UnmanagedCallersOnly]
    private static void OnWinEvent(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            _current?.OnEvent(hook, evt, hwnd, idObject);
        }
        catch
        {
            // never throw across the native boundary
        }
    }

    /// <summary>One WinEvent (internal: tests feed events directly).</summary>
    internal void OnEvent(nint hook, uint evt, nint hwnd, int idObject)
    {
        if (evt == Win32.EVENT_OBJECT_LOCATIONCHANGE)
        {
            // events already queued when the hook was removed (paused, or another process came to the front)
            if (hook != _locHook || hook == 0) return;
            LocationEvents++;
            PauseLocation();
            if (idObject != Win32.OBJID_WINDOW || hwnd != _fgHwnd) return;
        }
        else if (evt == Win32.EVENT_SYSTEM_FOREGROUND)
        {
            TrackForegroundProcess(hwnd);
        }
        _onEvent();
    }

    /// <summary>Tests / measurements: track this window as if it had come to the front.</summary>
    internal void TrackForTest(nint hwnd) => TrackForegroundProcess(hwnd);

    /// <summary>Tests: watch <paramref name="hwnd"/> as the foreground window of process <paramref name="pid"/>.</summary>
    internal void TrackForTest(nint hwnd, uint pid)
    {
        if (_locHook != 0) Win32.UnhookWinEvent(_locHook);
        _locHook = 0;
        _paused = false;
        _fgHwnd = hwnd;
        _locPid = pid;
        HookLocation();
    }

    /// <summary>The location-change hook now (0 = none or paused; tests).</summary>
    internal nint LocationHook => _locHook;

    private void TrackForegroundProcess(nint hwnd)
    {
        _fgHwnd = hwnd;
        uint pid = 0;
        if (hwnd != 0) Win32.GetWindowThreadProcessId(hwnd, &pid);
        if (pid == _locPid) return; // (while paused: the resume hooks it again and compares the new window)
        if (_locHook != 0) Win32.UnhookWinEvent(_locHook);
        _locHook = 0;
        _paused = false;
        _locPid = pid;
        HookLocation();
    }

    private void HookLocation()
    {
        if (_locPid != 0 && _locPid != (uint)Environment.ProcessId)
            _locHook = Win32.SetWinEventHook(Win32.EVENT_OBJECT_LOCATIONCHANGE, Win32.EVENT_OBJECT_LOCATIONCHANGE, 0, &OnWinEvent, _locPid, 0, Win32.WINEVENT_OUTOFCONTEXT);
    }

    private void PauseLocation()
    {
        if (_paused || _locHook == 0) return;
        Win32.UnhookWinEvent(_locHook);
        _locHook = 0;
        _paused = true;
        (_pausedRect, _pausedStyle) = Shape(_fgHwnd);
        _scheduleResume(LocationPauseMs);
    }

    /// <summary>The owner's timer after a pause: hook again and report a foreground window that moved, resized or changed style meanwhile.</summary>
    public void ResumeLocation()
    {
        if (!_paused || !Running) return;
        _paused = false;
        HookLocation();
        var (rect, style) = Shape(_fgHwnd);
        if (style != _pausedStyle || rect.Left != _pausedRect.Left || rect.Top != _pausedRect.Top || rect.Right != _pausedRect.Right || rect.Bottom != _pausedRect.Bottom)
            _onEvent();
    }

    private static (RECT Rect, nint Style) Shape(nint hwnd)
    {
        RECT r = default;
        if (hwnd == 0 || !Win32.GetWindowRect(hwnd, &r)) return (default, 0);
        return (r, Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE));
    }

    /// <summary>Classifies the current foreground window.</summary>
    public static ForegroundState Evaluate()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == 0) return default;
        uint pid = 0;
        Win32.GetWindowThreadProcessId(hwnd, &pid);
        var monitor = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFOEXW { cbSize = (uint)sizeof(MONITORINFOEXW) };
        if (!Win32.GetMonitorInfo(monitor, &mi)) return new ForegroundState(hwnd, pid, false, false, null);
        var device = new string(mi.szDevice);
        var nul = device.IndexOf('\0');
        if (nul >= 0) device = device[..nul];

        var cls = stackalloc char[256];
        var n = Win32.GetClassName(hwnd, cls, 256);
        var className = n > 0 ? new string(cls, 0, n) : "";
        RECT r;
        Win32.GetWindowRect(hwnd, &r);
        var style = (uint)(long)Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE);
        int cloaked = 0;
        Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, &cloaked, 4);
        var ignored = pid == (uint)Environment.ProcessId || cloaked != 0 || Win32.IsIconic(hwnd) || !Win32.IsWindowVisible(hwnd)
                      || hwnd == Win32.GetShellWindow() || hwnd == Win32.GetDesktopWindow();
        int quns = 0;
        if (Win32.SHQueryUserNotificationState(&quns) != 0) quns = 0;
        var (full, exclusive) = ForegroundClassifier.Classify(className, style,
            new PixelRect(r.Left, r.Top, r.Right, r.Bottom),
            new PixelRect(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom), quns, ignored);
        return new ForegroundState(hwnd, pid, full, exclusive, device);
    }

    public void Dispose() => Stop();
}
