using System.Runtime;
using System.Windows.Threading;
using BetterVitals.Core;
using BetterVitals.Core.Settings;
using BetterVitals.UI.Explain;

namespace BetterVitals.UI.Tray;

/// <summary>
/// What the app does while it waits in the tray (ported from BetterNotepad). On the way in: settings are written, the
/// explanation bubble window is destroyed and the live texts' cache emptied (every other window is already closed and released). Then memory goes back to
/// Windows: a compacting full GC and a working-set trim once the dispatcher is idle, again after the work started by
/// closing has settled (~4 s: the render thread freeing its buffers) and after idle thread-pool threads have retired
/// (~25 s, trim only). After that nothing runs — no timer, no polling — until a window shows up again.
/// </summary>
internal static class TrayRest
{
    private static readonly (TimeSpan After, bool Gc)[] LateTrims = [(TimeSpan.FromSeconds(4), true), (TimeSpan.FromSeconds(25), false)];

    private static DispatcherTimer? _timer;
    private static int _generation;

    /// <summary>Trims done so far (tests, probe).</summary>
    internal static int Trims { get; private set; }

    /// <summary>A later trim is still scheduled.</summary>
    internal static bool Pending => _timer is not null;

    /// <summary>Is any app window on screen? (Set by the shell; a window in use would just page everything back in.)</summary>
    internal static Func<bool> AnyWindowVisible { get; set; } = static () => false;

    /// <summary>The last window closed.</summary>
    public static void Enter()
    {
        try
        {
            SettingsStore.I?.Flush();
        }
        catch (Exception ex)
        {
            Log.Error("tray: settings flush failed", ex);
        }
        ExplainService.I.Release();
        Controls.LiveText.ClearCache();
        Settle();
    }

    /// <summary>Schedules the trims (also at start straight into the tray). Cancelled when a window shows.</summary>
    public static void Settle()
    {
        var generation = ++_generation;
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => TrimIfStill(generation, gc: true));

        _timer?.Stop();
        var timer = _timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher) { Interval = LateTrims[0].After };
        var next = 0;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_timer != timer) return;
            TrimIfStill(generation, LateTrims[next].Gc);
            next++;
            if (next < LateTrims.Length && generation == _generation)
            {
                timer.Interval = LateTrims[next].After - LateTrims[next - 1].After;
                timer.Start();
            }
            else
            {
                _timer = null;
            }
        };
        timer.Start();
    }

    /// <summary>A window is about to show: pending trims are dropped.</summary>
    public static void Leave()
    {
        _generation++;
        _timer?.Stop();
        _timer = null;
    }

    private static void TrimIfStill(int generation, bool gc)
    {
        if (generation != _generation || AnyWindowVisible()) return;
        Trim(gc);
    }

    /// <summary>Compacting full GC (large objects too) with finalizers, then the working set goes back to Windows.</summary>
    internal static void Trim(bool gc)
    {
        if (gc)
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
        Native.EmptyWorkingSet(Native.CurrentProcess);
        Trims++;
    }
}
