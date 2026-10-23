using System.Reflection;
using System.Windows.Threading;

namespace BetterVitals.UI.Dev;

/// <summary>
/// Perf probe only: lists the armed <see cref="Timer"/>s (Task.Delay included, with the async method awaiting it) and the
/// dispatcher timers of the UI thread — who wakes the process up. Reflection on runtime internals, best effort: a
/// renamed field gives "?" instead of an exception.
/// </summary>
internal static class TimerDump
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static List<string> Describe(Dispatcher ui)
    {
        var lines = new List<string>();
        try
        {
            var queueType = typeof(Timer).Assembly.GetType("System.Threading.TimerQueue");
            var instances = (queueType?.GetProperty("Instances", All)?.GetValue(null) ?? queueType?.GetField("<Instances>k__BackingField", All)?.GetValue(null)) as Array;
            var timerType = typeof(Timer).Assembly.GetType("System.Threading.TimerQueueTimer");
            if (instances is null || timerType is null) return ["timers: internals not found"];
            var next = timerType.GetField("_next", All);
            var callback = timerType.GetField("_timerCallback", All);
            var state = timerType.GetField("_state", All);
            var period = timerType.GetField("_period", All);
            var due = timerType.GetField("_dueTime", All);
            foreach (var q in instances)
            {
                if (q is null) continue;
                foreach (var list in queueType!.GetFields(All).Where(f => f.FieldType == timerType))
                {
                    for (var t = list.GetValue(q); t is not null; t = next?.GetValue(t))
                    {
                        var cb = callback?.GetValue(t) as Delegate;
                        var st = state?.GetValue(t);
                        var p = period?.GetValue(t);
                        var d = due?.GetValue(t);
                        lines.Add($"  timer  period {Ms(p)}  due {Ms(d)}  {Name(cb)}  state {Describe(st)}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            lines.Add("  timers: " + ex.Message);
        }
        try
        {
            if (typeof(Dispatcher).GetField("_timers", All)?.GetValue(ui) is System.Collections.IEnumerable timers)
            {
                foreach (var o in timers)
                {
                    if (o is not DispatcherTimer dt || !dt.IsEnabled) continue;
                    var tick = typeof(DispatcherTimer).GetField("Tick", All)?.GetValue(dt) as Delegate;
                    lines.Add($"  dispatcher timer {dt.Interval.TotalMilliseconds:0} ms  {Name(tick)}");
                }
            }
        }
        catch (Exception ex)
        {
            lines.Add("  dispatcher timers: " + ex.Message);
        }
        return lines;
    }

    private static string Ms(object? v) => v switch
    {
        uint u when u == uint.MaxValue => "-",
        uint u => $"{u} ms",
        _ => "?",
    };

    private static string Name(Delegate? d)
    {
        if (d is null) return "?";
        var m = d.GetInvocationList()[0].Method;
        return $"{m.DeclaringType?.FullName}.{m.Name}";
    }

    /// <summary>The timer's state; for Task.Delay the async method that awaits it.</summary>
    private static string Describe(object? state)
    {
        if (state is null) return "null";
        var type = state.GetType();
        if (state is Task task)
        {
            var cont = typeof(Task).GetField("m_continuationObject", All)?.GetValue(task);
            return $"{type.Name} → {ContinuationName(cont)}";
        }
        if (state is WeakReference wr) return "weak " + (wr.Target?.GetType().FullName ?? "null");
        var target = type.GetField("_target", All)?.GetValue(state) ?? type.GetField("<Target>k__BackingField", All)?.GetValue(state);
        return target is null ? type.FullName ?? type.Name : $"{type.Name}({target.GetType().FullName})";
    }

    private static string ContinuationName(object? cont)
    {
        switch (cont)
        {
            case null:
                return "none";
            case Delegate d:
                return d.Target?.GetType().FullName ?? d.Method.Name;
            case System.Collections.IEnumerable list:
                return string.Join(" | ", list.Cast<object?>().Select(ContinuationName));
        }
        var t = cont.GetType();
        // AsyncStateMachineBox<TResult, TStateMachine>: the state machine names the async method
        var sm = t.IsGenericType ? t.GetGenericArguments().LastOrDefault() : null;
        if (sm is not null && sm.Name.Contains('<')) return sm.FullName ?? sm.Name;
        var inner = t.GetField("m_action", All)?.GetValue(cont) ?? t.GetField("_continuation", All)?.GetValue(cont);
        return inner is null ? t.FullName ?? t.Name : ContinuationName(inner);
    }
}
