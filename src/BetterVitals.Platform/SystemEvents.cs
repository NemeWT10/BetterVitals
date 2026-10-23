using System.Runtime.InteropServices;

namespace BetterVitals.Platform;

/// <summary>
/// Windows notifications the app reacts to, from one hidden top-level window on its own thread (broadcasts such as
/// WM_DISPLAYCHANGE / WM_DEVICECHANGE never reach message-only windows): sleep / resume (also for modern standby),
/// device changes, display changes, session lock / unlock and sign-out / shutdown. Events fire on that thread —
/// handlers must be quick (signal the engine, marshal to the UI). Nothing polls.
/// </summary>
public sealed class SystemEvents : IDisposable
{
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_QUERYENDSESSION = 0x0011;
    private const uint WM_ENDSESSION = 0x0016;
    private const uint WM_DISPLAYCHANGE = 0x007E;
    private const uint WM_POWERBROADCAST = 0x0218;
    private const uint WM_DEVICECHANGE = 0x0219;
    private const uint WM_WTSSESSION_CHANGE = 0x02B1;
    private const uint WM_CLOSE = 0x0010;
    private const int PBT_APMSUSPEND = 0x4;
    private const int PBT_APMRESUMESUSPEND = 0x7;
    private const int PBT_APMRESUMEAUTOMATIC = 0x12;
    private const int DBT_DEVNODES_CHANGED = 0x7;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int WTS_SESSION_LOCK = 0x7;
    private const int WTS_SESSION_UNLOCK = 0x8;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly WndProc _proc; // kept alive: the window calls it
    private readonly WinEventProc _winEvent; // kept alive: the hook calls it
    private IntPtr _foregroundHook;
    private IntPtr _hwnd;
    private IntPtr _suspendRegistration;
    private bool _suspended;

    /// <summary>Going to sleep / hibernate: flush and pause.</summary>
    public event Action? Suspending;

    /// <summary>Back from sleep / hibernate (fired once).</summary>
    public event Action? Resumed;

    /// <summary>A device was added / removed (USB, drives, GPU driver install…). Not debounced — consumers debounce.</summary>
    public event Action? DevicesChanged;

    /// <summary>Resolution, monitor layout or refresh rate changed.</summary>
    public event Action? DisplayChanged;

    /// <summary>true = locked, false = unlocked.</summary>
    public event Action<bool>? SessionLockChanged;

    /// <summary>Sign-out / shutdown / restart: save everything now (the process may be killed right after).</summary>
    public event Action? SessionEnding;

    /// <summary>Another window came to the foreground (game started, Alt+Tab…). Event-driven, no polling.</summary>
    public event Action? ForegroundChanged;

    public SystemEvents()
    {
        _proc = WindowProc;
        _winEvent = OnWinEvent;
        _thread = new Thread(Run) { IsBackground = true, Name = "BetterVitals system events" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    private void Run()
    {
        try
        {
            var hInstance = GetModuleHandleW(null);
            var cls = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = hInstance,
                lpszClassName = "BetterVitals.SystemEvents",
            };
            RegisterClassExW(ref cls);
            // a real (hidden) top-level window: it receives broadcasts; never shown, not in the taskbar / Alt+Tab
            _hwnd = CreateWindowExW(0x00000080 /* WS_EX_TOOLWINDOW */, cls.lpszClassName, "BetterVitals", 0x80000000 /* WS_POPUP */,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (_hwnd != IntPtr.Zero)
            {
                _suspendRegistration = RegisterSuspendResumeNotification(_hwnd, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
                WTSRegisterSessionNotification(_hwnd, 0 /* NOTIFY_FOR_THIS_SESSION */);
            }
            // EVENT_SYSTEM_FOREGROUND, out of context: the callback runs on this thread's message loop
            _foregroundHook = SetWinEventHook(0x0003, 0x0003, IntPtr.Zero, _winEvent, 0, 0, 0);
        }
        catch (Exception ex)
        {
            Core.Log.Error("system events: window failed", ex);
        }
        finally
        {
            _ready.Set();
        }

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_POWERBROADCAST:
                    switch ((int)wParam)
                    {
                        case PBT_APMSUSPEND:
                            if (!_suspended)
                            {
                                _suspended = true;
                                Suspending?.Invoke();
                            }
                            break;
                        case PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND:
                            if (_suspended)
                            {
                                _suspended = false;
                                Resumed?.Invoke();
                            }
                            break;
                    }
                    return 1;
                case WM_DEVICECHANGE when (int)wParam is DBT_DEVNODES_CHANGED or DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE:
                    DevicesChanged?.Invoke();
                    return 1;
                case WM_DISPLAYCHANGE:
                    DisplayChanged?.Invoke();
                    break;
                case WM_WTSSESSION_CHANGE when (int)wParam is WTS_SESSION_LOCK or WTS_SESSION_UNLOCK:
                    SessionLockChanged?.Invoke((int)wParam == WTS_SESSION_LOCK);
                    break;
                case WM_QUERYENDSESSION:
                    return 1;
                case WM_ENDSESSION when wParam != IntPtr.Zero:
                    SessionEnding?.Invoke();
                    return 0;
                case WM_DESTROY:
                    if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
                    _foregroundHook = IntPtr.Zero;
                    PostQuitMessage(0);
                    return 0;
            }
        }
        catch (Exception ex)
        {
            Core.Log.Error("system events: handler failed", ex);
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            ForegroundChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Core.Log.Error("system events: foreground handler failed", ex);
        }
    }

    public void Dispose()
    {
        if (_suspendRegistration != IntPtr.Zero) UnregisterSuspendResumeNotification(_suspendRegistration);
        if (_hwnd != IntPtr.Zero)
        {
            WTSUnRegisterSessionNotification(_hwnd);
            PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    // ───────────────────────── interop ─────────────────────────

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW cls);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int code);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterSuspendResumeNotification(IntPtr recipient, int flags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterSuspendResumeNotification(IntPtr handle);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSRegisterSessionNotification(IntPtr hwnd, int flags);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);
}
