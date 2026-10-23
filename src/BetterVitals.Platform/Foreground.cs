using System.Runtime.InteropServices;

namespace BetterVitals.Platform;

/// <summary>
/// "Is a game / full-screen app in front?" — for update checks (never in games), the anti-cheat guard and tips.
/// Reads only the foreground window (class, style, rectangle, owning process id); never opens another process.
/// </summary>
public static class Foreground
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
        "LockScreenInputOcclusionFrame",
    };

    private const int QUNS_BUSY = 2;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;
    private const int GWL_STYLE = -16;
    private const long WS_CAPTION = 0x00C00000;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>True when the foreground window is a full-screen or borderless app covering its monitor (games, videos).</summary>
    public static bool IsFullscreenAppActive()
    {
        try
        {
            if (SHQueryUserNotificationState(out var state) == 0 && state is QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE) return true;
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == (uint)Environment.ProcessId) return false;
            Span<char> buf = stackalloc char[256];
            var n = GetClassNameW(hwnd, ref MemoryMarshal.GetReference(buf), buf.Length);
            if (n > 0 && ShellClasses.Contains(new string(buf[..n]))) return false;
            if ((GetWindowLongPtrW(hwnd, GWL_STYLE).ToInt64() & WS_CAPTION) == WS_CAPTION) return state == QUNS_BUSY && IsCovering(hwnd);
            return IsCovering(hwnd);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCovering(IntPtr hwnd)
    {
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi)) return false;
        if (!GetWindowRect(hwnd, out var r)) return false;
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, ref char className, int maxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
