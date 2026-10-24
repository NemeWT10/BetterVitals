using System.Diagnostics;
using BetterVitals.Overlay.Host;
using BetterVitals.Overlay.Interop;
using Xunit;

namespace BetterVitals.Tests.Overlay;

/// <summary>
/// The foreground watcher's location-change hook: paused after every event (a game's cursor or a caret sends hundreds a
/// second), resumed on the owner's timer, and a change of the foreground window during the pause is reported on resume.
/// Events are fed directly; the "foreground window" is a never-shown window of this process, watched as if it belonged
/// to another process (the hook itself is real, out-of-context, and its events are never pumped here).
/// </summary>
public unsafe class ForegroundWatcherTests
{
    private const int ObjIdCursor = -9;

    private static nint HiddenWindow()
    {
        fixed (char* cls = "STATIC")
        fixed (char* title = "watcher test")
            return Win32.CreateWindowEx(Win32.WS_EX_TOOLWINDOW, cls, title, Win32.WS_POPUP, -4000, -4000, 120, 40, 0, 0, Win32.GetModuleHandle(null), 0);
    }

    private static uint OtherPid() => (uint)(Process.GetProcessesByName("explorer").Select(p => p.Id).FirstOrDefault(id => id != Environment.ProcessId) is var id and > 0 ? id : 4);

    [Fact]
    public void Location_events_pause_the_hook_and_a_change_during_the_pause_is_reported_on_resume()
    {
        var hwnd = HiddenWindow();
        Assert.NotEqual(0, hwnd);
        var events = 0;
        var scheduled = new List<uint>();
        var w = new ForegroundWatcher(() => events++, ms => scheduled.Add(ms));
        try
        {
            w.Start();
            w.TrackForTest(hwnd, OtherPid());
            var hook = w.LocationHook;
            Assert.NotEqual(0, hook);

            // the cursor moves: no evaluation, the hook pauses, the owner is asked to resume it later
            w.OnEvent(hook, Win32.EVENT_OBJECT_LOCATIONCHANGE, 0, ObjIdCursor);
            Assert.Equal(0, events);
            Assert.True(w.LocationPaused);
            Assert.Equal(0, w.LocationHook);
            Assert.Equal([ForegroundWatcher.LocationPauseMs], scheduled);

            // events still queued from the removed hook change nothing
            for (var i = 0; i < 100; i++) w.OnEvent(hook, Win32.EVENT_OBJECT_LOCATIONCHANGE, 0, ObjIdCursor);
            Assert.Equal(0, events);
            Assert.Equal(1, w.LocationEvents);
            Assert.Single(scheduled);

            // resume with the window where it was: hooked again, nothing to report
            w.ResumeLocation();
            Assert.False(w.LocationPaused);
            hook = w.LocationHook;
            Assert.NotEqual(0, hook);
            Assert.Equal(0, events);

            // the window changes while the hook is paused (a game going full screen while its cursor moves): reported on resume
            w.OnEvent(hook, Win32.EVENT_OBJECT_LOCATIONCHANGE, 0, ObjIdCursor);
            Assert.True(w.LocationPaused);
            Assert.True(Win32.SetWindowPos(hwnd, 0, -4000, -4000, 300, 90, Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER));
            w.ResumeLocation();
            Assert.Equal(1, events);

            // the window itself moves: evaluated right away, and the hook pauses too
            hook = w.LocationHook;
            w.OnEvent(hook, Win32.EVENT_OBJECT_LOCATIONCHANGE, hwnd, Win32.OBJID_WINDOW);
            Assert.Equal(2, events);
            Assert.True(w.LocationPaused);

            // a second resume (a stale timer) does nothing; a style change during the pause is reported too
            w.ResumeLocation();
            w.ResumeLocation();
            Assert.Equal(2, events);
            w.OnEvent(w.LocationHook, Win32.EVENT_OBJECT_LOCATIONCHANGE, 0, ObjIdCursor);
            var style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE);
            Win32.SetWindowLongPtr(hwnd, Win32.GWL_STYLE, style | (nint)Win32.WS_CAPTION);
            w.ResumeLocation();
            Assert.Equal(3, events);
        }
        finally
        {
            w.Dispose();
            Win32.DestroyWindow(hwnd);
        }
    }

    [Fact]
    public void Another_process_in_front_ends_the_pause_and_stop_removes_every_hook()
    {
        var hwnd = HiddenWindow();
        var events = 0;
        var w = new ForegroundWatcher(() => events++, _ => { });
        try
        {
            w.Start();
            Assert.True(w.Running);
            w.TrackForTest(hwnd, OtherPid());
            w.OnEvent(w.LocationHook, Win32.EVENT_OBJECT_LOCATIONCHANGE, 0, ObjIdCursor);
            Assert.True(w.LocationPaused);

            // a process switch hooks the new process at once (here: back to "nothing" first, then the other process)
            w.TrackForTest(0, 0);
            Assert.False(w.LocationPaused);
            Assert.Equal(0, w.LocationHook);
            w.TrackForTest(hwnd, OtherPid());
            Assert.NotEqual(0, w.LocationHook);

            w.Stop();
            Assert.False(w.Running);
            Assert.Equal(0, w.LocationHook);
            w.ResumeLocation(); // a timer that was already due: no hook comes back
            Assert.Equal(0, w.LocationHook);
            Assert.Equal(0, events);

            w.Start();
            Assert.True(w.Running);
        }
        finally
        {
            w.Dispose();
            Win32.DestroyWindow(hwnd);
        }
    }
}
