using Xunit;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace BetterVitals.Tests.Ui;

/// <summary>UI tests share static WPF state (Loc, ExplainService, Motion): they run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiCollection
{
    public const string Name = "Ui";
}

/// <summary>Headless WPF: each test body runs on its own STA thread with its own dispatcher; nothing is ever shown.</summary>
internal static class Sta
{
    public static void Run(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>Runs the dispatcher until <paramref name="condition"/> holds (true) or the time is up (false).</summary>
    public static bool PumpUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) return false;
            DoEvents();
            Thread.Sleep(5);
        }
        return true;
    }

    /// <summary>Runs the dispatcher for a while (lets timers that should not fire prove it).</summary>
    public static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            DoEvents();
            Thread.Sleep(5);
        }
    }

    public static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
