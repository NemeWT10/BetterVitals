using Xunit;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BetterVitals.Tests.Ui.Pages;
using BetterVitals.UI;

namespace BetterVitals.Tests.Ui;

[Collection(UiCollection.Name)]
public sealed class MotionLoopTests
{
    [Fact]
    public void A_stopped_loop_leaves_no_clock_ticking() => Sta.Run(() =>
    {
        var dot = new Border { Width = 7, Height = 7, Background = Brushes.Green };
        using var host = new HiddenHost(dot, 20, 20);
        static DoubleAnimation Breath() => Motion.Throttled(new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(900)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        var before = ActiveEndlessClocks();

        var loop = new Motion.Loop(dot, UIElement.OpacityProperty, Breath);
        for (var i = 0; i < 3; i++)
        {
            loop.Start(); // a running loop is not started twice
            Sta.Pump(30);
        }
        Assert.True(loop.IsRunning);
        Assert.Equal(before + 1, ActiveEndlessClocks());
        loop.Stop();
        Sta.DoEvents();
        Assert.False(loop.IsRunning);
        Assert.Equal(before, ActiveEndlessClocks());
        Assert.Equal(1.0, dot.Opacity);

        // what BeginAnimation(dp, null) did: the endless clock keeps ticking until a GC finds it
        dot.BeginAnimation(UIElement.OpacityProperty, Breath());
        Sta.Pump(30);
        dot.BeginAnimation(UIElement.OpacityProperty, null);
        Sta.DoEvents();
        Assert.Equal(before + 1, ActiveEndlessClocks());
    });

    /// <summary>Endless root clocks that are active (each keeps WPF rendering frames).</summary>
    private static int ActiveEndlessClocks()
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var mcType = typeof(CompositionTarget).Assembly.GetType("System.Windows.Media.MediaContext")!;
        var mc = mcType.GetMethod("From", all)!.Invoke(null, [Dispatcher.CurrentDispatcher]);
        var tm = mcType.GetProperty("TimeManager", all)!.GetValue(mc)!;
        var root = tm.GetType().GetField("_timeManagerClock", all)!.GetValue(tm)!;
        var children = (System.Collections.IEnumerable?)root.GetType().GetField("_rootChildren", all)!.GetValue(root);
        var n = 0;
        foreach (var o in children ?? Array.Empty<object>())
            if (o is WeakReference { Target: Clock { CurrentState: ClockState.Active } c } && c.Timeline?.RepeatBehavior == RepeatBehavior.Forever)
                n++;
        return n;
    }
}
