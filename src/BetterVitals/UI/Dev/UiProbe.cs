using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BetterVitals.Core;
using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Derived;
using BetterVitals.Sensors.Engine;
using BetterVitals.Services;
using BetterVitals.UI.Pages.Overview;
using BetterVitals.UI.Pages.Shared;
using BetterVitals.UI.Panel;
using BetterVitals.UI.Tray;

namespace BetterVitals.UI.Dev;

/// <summary>
/// <c>--ui-probe [seconds]</c> (test mode, hidden desktop only): the cost of the UI itself. Per state — tray rest, the
/// flyout open, every panel page, the Overview with every card expanded, the tray after the panel closed — CPU (cycles,
/// one core and machine), wake-ups, allocations, GC, memory; open latencies; and whether the closed panel and its
/// pages were collected. Runs once on the default fake PC and once more on <see cref="WideFakeSource"/> (~400 sensors;
/// <c>--ui-probe-wide 0</c> skips it). Writes ui-probe.txt next to the logs, then exits.
/// </summary>
internal static class UiProbe
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void Start()
    {
        var args = Environment.GetCommandLineArgs();
        var at = Array.FindIndex(args, a => a.Equals("--ui-probe", StringComparison.OrdinalIgnoreCase));
        var window = at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], NumberStyles.Integer, Inv, out var s) ? s : 20;
        var w = Array.FindIndex(args, a => a.Equals("--ui-probe-wide", StringComparison.OrdinalIgnoreCase));
        var wide = !(w >= 0 && w + 1 < args.Length && args[w + 1] == "0");
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => Run(TimeSpan.FromSeconds(Math.Clamp(window, 5, 600)), wide));
    }

    private static async void Run(TimeSpan window, bool wide)
    {
        var r = new List<string>();
        SensorEngine? big = null;
        try
        {
            var tier = RenderCapability.Tier >> 16;
            r.Add($"ui probe {AppEnv.Version} · render tier {tier} · animations {Motion.I.Enabled} · {ProcessStats.MachineProcessors} logical processors · window {window.TotalSeconds:0} s");
            TrayRest.Settle();
            await Task.Delay(8000);
            Mark(r, "start, trimmed");
            await Measure(r, "tray rest", window);

            await Pass(r, window, $"default fake PC ({AppServices.Hub.Sensors.Count} sensors)");

            if (wide)
            {
                var original = AppServices.Hub;
                big = new SensorEngine([new WideFakeSource(), new DerivedSource()]);
                big.Start();
                for (var i = 0; i < 50 && big.Sensors.Count == 0; i++) await Task.Delay(100);
                AppServices.Hub = big;
                await Task.Delay(1000);
                await Pass(r, window, $"wide fake PC ({big.Sensors.Count} sensors)");
                AppServices.Hub = original;
            }
        }
        catch (Exception ex)
        {
            r.Add("ui probe failed: " + ex);
            Log.Error("ui probe failed", ex);
        }
        big?.Dispose();
        try
        {
            File.WriteAllLines(Path.Combine(AppEnv.LogDir, "ui-probe.txt"), r, Encoding.UTF8);
        }
        catch
        {
            // the log has the lines
        }
        AppShell.I.Quit();
    }

    private static async Task Pass(List<string> r, TimeSpan window, string title)
    {
        r.Add("");
        r.Add("# " + title);

        // flyout (the second open is the one the budget means; the first loads the templates)
        for (var i = 1; i <= 2; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            AppShell.I.ShowFlyout();
            await Idle();
            r.Add(string.Create(Inv, $"latency: flyout open #{i} {sw.Elapsed.TotalMilliseconds:0} ms"));
            if (i == 1)
            {
                await Task.Delay(1000);
                AppShell.I.CloseAll();
                await Task.Delay(1000);
            }
        }
        await Task.Delay(2000);
        await Measure(r, "flyout open", window);
        // the same with animations off: what the breathing live dot costs
        Motion.I.Update(false);
        await Task.Delay(1000);
        await Measure(r, "flyout open, animations off", window);
        Motion.I.Update(true);
        AppShell.I.CloseAll();
        await Task.Delay(3000);

        // panel: every page, then the Overview with every card expanded (every sensor live)
        var refs = new List<(string Name, WeakReference Ref)>();
        foreach (var page in PageRegistry.All.Where(p => p.InNav))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            AppShell.I.OpenPanel(page.Id);
            await Idle();
            r.Add(string.Create(Inv, $"latency: page {page.Id} {sw.Elapsed.TotalMilliseconds:0} ms{(refs.Count == 0 ? " (panel open)" : "")}"));
            Track(refs, page.Id);
            await Task.Delay(3000);
            await Measure(r, "panel: " + page.Id, window);
        }
        AppShell.I.OpenPanel(PageIds.Overview);
        await Idle();
        var expandWatch = System.Diagnostics.Stopwatch.StartNew();
        var expanded = ExpandAll();
        if (expanded > 0)
        {
            await Idle();
            r.Add(string.Create(Inv, $"latency: expand all {expanded} cards {expandWatch.Elapsed.TotalMilliseconds:0} ms"));
            await Task.Delay(3000);
            await Measure(r, "panel: overview, every card expanded", window);
        }

        AppShell.I.CloseAll();
        await Task.Delay(30000);
        Mark(r, $"panel closed +30 s ({TrayRest.Trims} trims)");
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        var alive = refs.Where(x => x.Ref.IsAlive).Select(x => x.Name).ToList();
        r.Add(alive.Count == 0 ? $"collected after close: all {refs.Count} (panel window + pages)" : "STILL ALIVE after close: " + string.Join(", ", alive));
        r.Add($"shared page theme owners (weak refs kept by Ui.Theme): {ThemeOwners()}");
        TrayRest.Trim(gc: false);
        await Measure(r, "tray after panel", window);
    }

    // no page reference may live in an async state machine (it would keep the closed panel alive)
    private static void Track(List<(string Name, WeakReference Ref)> refs, string pageId)
    {
        if (refs.Count == 0 && AppShell.I.Panel is { } p) refs.Add(("panel window", new WeakReference(p)));
        if (AppShell.I.Panel?.PageElement(pageId) is { } element) refs.Add(("page " + pageId, new WeakReference(element)));
    }

    private static int ExpandAll()
    {
        if (AppShell.I.Panel?.PageElement(PageIds.Overview) is not OverviewPage overview) return 0;
        foreach (var card in overview.Cards.ToList())
            if (!card.IsExpanded) overview.Toggle(card);
        return overview.Cards.Count;
    }

    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle).Task;

    private static string ThemeOwners()
    {
        try
        {
            var field = typeof(ResourceDictionary).GetField("_ownerFEs", BindingFlags.Instance | BindingFlags.NonPublic);
            var list = field?.GetValue(Ui.Theme);
            return list?.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(list)?.ToString() ?? "0";
        }
        catch (Exception ex)
        {
            return "? " + ex.GetType().Name;
        }
    }

    private static void Mark(List<string> r, string what)
    {
        var (priv, ws, bytes) = ProcessStats.Memory();
        r.Add(string.Create(Inv, $"{what,-34} private WS {priv / 1048576.0,6:0.0} MB | WS {ws / 1048576.0,6:0.0} MB | private bytes {bytes / 1048576.0,6:0.0} MB | GC heap {GC.GetGCMemoryInfo().HeapSizeBytes / 1048576.0,5:0.0} MB"));
    }

    private static async Task Measure(List<string> r, string title, TimeSpan window)
    {
        var a = ProcessStats.Take();
        await Task.Delay(window);
        var b = ProcessStats.Take();
        var secs = (b.Timestamp - a.Timestamp) / (double)System.Diagnostics.Stopwatch.Frequency;
        double Cpu(ProcessStats.ThreadInfo t) => (t.Cycles - (a.Threads.TryGetValue(t.Id, out var o) ? Math.Min(o.Cycles, t.Cycles) : 0)) * 1000.0 / ProcessStats.TscHz;
        long Switches(ProcessStats.ThreadInfo t) => (long)t.ContextSwitches - (a.Threads.TryGetValue(t.Id, out var o) ? o.ContextSwitches : 0);
        var cycleMs = (b.Cycles - a.Cycles) * 1000.0 / ProcessStats.TscHz;
        var wake = b.Threads.Values.Sum(Switches) / secs;
        static string Mb(long v) => (v / 1048576.0).ToString("0.0", Inv);
        var top = string.Join(" | ", b.Threads.Values.OrderByDescending(Cpu).Take(4)
            .Select(t => string.Create(Inv, $"{(t.Name.Length > 0 ? t.Name : "#" + t.Id)} {Cpu(t) / 10 / secs:0.00}% {Switches(t) / secs:0}/s")));
        r.Add(string.Create(Inv,
            $"## {title,-38} CPU {cycleMs / 10 / secs,6:0.000} % core ({cycleMs / 10 / secs / ProcessStats.MachineProcessors:0.0000} % machine) · wake-ups {wake,6:0.0}/s · alloc {(b.Allocated - a.Allocated) / secs / 1024,7:0.0} KB/s · GC +{b.Gen0 - a.Gen0}/{b.Gen1 - a.Gen1}/{b.Gen2 - a.Gen2}"));
        r.Add(string.Create(Inv,
            $"   memory: private WS {Mb(b.PrivateWorkingSet)} MB · WS {Mb(b.WorkingSet)} MB · private bytes {Mb(b.PrivateBytes)} MB · GC heap {Mb(b.GcHeap)} MB (committed {Mb(b.GcCommitted)}) · {b.Threads.Count} threads · {b.Handles} handles"));
        r.Add("   top threads: " + top);
        if (wake > 100)
            foreach (var line in TimerDump.Describe(Dispatcher.CurrentDispatcher)) r.Add("   timer: " + line);
        Log.Info(string.Create(Inv, $"ui probe: {title}: CPU {cycleMs / 10 / secs:0.000} % core, wake-ups {wake:0}/s, private WS {Mb(b.PrivateWorkingSet)} MB"));
    }
}
