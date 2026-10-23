using BetterVitals.Fps.Interop;

namespace BetterVitals.Fps;

/// <summary>
/// Finds the process of the foreground window: GetForegroundWindow + GetWindowThreadProcessId, name from a Toolhelp32
/// snapshot. Never opens a handle to that process (anti-cheat safety). The shell (desktop, taskbar) and our own process
/// count as "no app". Engine thread only.
/// </summary>
internal sealed unsafe class ForegroundTracker
{
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private nint _hwnd = -1;
    private uint _pid;

    /// <summary>PID of the foreground app, 0 = none (desktop, taskbar, our own window, lock screen).</summary>
    public uint Pid => _pid;

    /// <summary>Re-reads the foreground window; returns the app PID (0 = none). Cheap when nothing changed.</summary>
    public uint Refresh()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == _hwnd) return _pid;
        _hwnd = hwnd;
        _pid = ResolvePid(hwnd);
        return _pid;
    }

    private uint ResolvePid(nint hwnd)
    {
        if (hwnd == 0 || hwnd == Win32.GetShellWindow()) return 0;
        Span<char> cls = stackalloc char[64];
        int len;
        fixed (char* p = cls) len = Win32.GetClassName(hwnd, p, cls.Length);
        var name = cls[..Math.Max(0, len)];
        if (name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return 0;

        var target = hwnd;
        if (name is "ApplicationFrameWindow")
        {
            // UWP: the frame belongs to ApplicationFrameHost, the content (and its presents) to the app
            var core = Win32.FindWindowEx(hwnd, 0, "Windows.UI.Core.CoreWindow", null);
            if (core != 0) target = core;
        }
        Win32.GetWindowThreadProcessId(target, out var pid);
        return pid == _ownPid ? 0 : pid;
    }

    /// <summary>Executable name of <paramref name="pid"/> ("game.exe") from a Toolhelp32 snapshot, or null.</summary>
    public static string? ProcessName(uint pid)
    {
        if (pid == 0) return null;
        var snap = Win32.CreateToolhelp32Snapshot(Win32.Th32csSnapProcess, 0);
        if (snap == 0 || snap == -1) return null;
        try
        {
            ProcessEntry32 e = default;
            e.Size = (uint)sizeof(ProcessEntry32);
            if (!Win32.Process32First(snap, &e)) return null;
            do
            {
                if (e.ProcessId == pid) return new string(e.ExeFile);
            }
            while (Win32.Process32Next(snap, &e));
            return null;
        }
        finally
        {
            Win32.CloseHandle(snap);
        }
    }
}
