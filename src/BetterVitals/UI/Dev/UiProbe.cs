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
    private static bool _flyoutOnly;

    public static void Start()
    {
        var args = Environment.GetCommandLineArgs();
        var at = Array.FindIndex(args, a => a.Equals("--ui-probe", StringComparison.OrdinalIgnoreCase));
        var window = at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], NumberStyles.Integer, Inv, out var s) ? s : 20;
        var w = Array.FindIndex(args, a => a.Equals("--ui-probe-wide", StringComparison.OrdinalIgnoreCase));
        var wide = !(w >= 0 && w + 1 < args.Length && args[w + 1] == "0");
        _flyoutOnly = args.Any(a => a.Equals("--ui-probe-flyout", StringComparison.OrdinalIgnoreCase));
        if (_flyoutOnly) wide = false;
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
            await FlyoutEquality(r);
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
        if (_flyoutOnly) return;
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

    /// <summary>
    /// The flyout as shipped (glass + shadow in their own cached layer) against the same flyout rebuilt the old way (one
    /// card with the shadow on it), both rendered at the window's own scale: the pixels must match.
    /// </summary>
    private static async Task FlyoutEquality(List<string> r)
    {
        var history = AppServices.History;
        AppServices.History = null; // no prefill from the live buffer: both windows show the same values
        Motion.I.Update(false);
        var windows = new List<Flyout.FlyoutWindow>();
        try
        {
            foreach (var legacy in new[] { false, true })
            {
                var w = new Flyout.FlyoutWindow(new Flyout.FlyoutViewModel(AppServices.Hub)) { Offscreen = true };
                if (legacy) SingleLayer(w);
                FloatingPlacement.ShowOffscreen(w);
                windows.Add(w);
            }
            await Task.Delay(500);
            await Idle();
            var scale = VisualTreeHelper.GetDpi(windows[0]).DpiScaleX;
            var a = Snapshot(windows[0], scale);
            var b = Snapshot(windows[1], scale);
            var (max, over2) = Compare(a, b);
            foreach (var (bmp, name) in new[] { (a, "flyout-layered.png"), (b, "flyout-one-card.png") })
            {
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using var fs = File.Create(Path.Combine(AppEnv.LogDir, name));
                enc.Save(fs);
            }
            r.Add(string.Create(Inv, $"flyout layering vs one card: {a.PixelWidth}x{a.PixelHeight} px at {scale:0.##}x, max channel difference {max}, pixels differing by more than 2: {over2}"));
            // a capture at another scale (snapshots, README pictures) draws the glass directly (FlyoutWindow.ForCapture)
            windows[0].GlassHost.CacheMode = null;
            var capture = scale == 1.25 ? 1.5 : 1.25;
            var (maxC, overC) = Compare(Snapshot(windows[0], capture), Snapshot(windows[1], capture));
            r.Add(string.Create(Inv, $"  captured at {capture:0.##}x without the cache: max channel difference {maxC}, pixels differing by more than 2: {overC}"));
        }
        catch (Exception ex)
        {
            r.Add("flyout equality failed: " + ex.Message);
        }
        finally
        {
            foreach (var w in windows) w.Close();
            AppServices.History = history;
            Motion.I.Update(true);
        }
    }

    /// <summary>The flyout card as it was before the glass layer: background, border and shadow on the content's own border.</summary>
    private static void SingleLayer(Flyout.FlyoutWindow w)
    {
        var effect = w.Glass.Effect;
        w.Glass.Effect = null;
        w.GlassHost.Visibility = Visibility.Collapsed;
        w.Card.CornerRadius = new CornerRadius(16);
        w.Card.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Brush.Panel");
        w.Card.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "Brush.PanelBorder");
        w.Card.Effect = effect;
    }

    private static System.Windows.Media.Imaging.RenderTargetBitmap Snapshot(Window w, double scale)
    {
        w.UpdateLayout();
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(w.ActualWidth * scale), (int)Math.Ceiling(w.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(w);
        return bmp;
    }

    private static (int Max, int Over2) Compare(System.Windows.Media.Imaging.BitmapSource a, System.Windows.Media.Imaging.BitmapSource b)
    {
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight) return (255, -1);
        var stride = a.PixelWidth * 4;
        var pa = new byte[stride * a.PixelHeight];
        var pb = new byte[pa.Length];
        a.CopyPixels(pa, stride, 0);
        b.CopyPixels(pb, stride, 0);
        int max = 0, over = 0;
        for (var i = 0; i < pa.Length; i += 4)
        {
            var d = 0;
            for (var k = 0; k < 4; k++) d = Math.Max(d, Math.Abs(pa[i + k] - pb[i + k]));
            max = Math.Max(max, d);
            if (d > 2) over++;
        }
        return (max, over);
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

    /// <summary>CompositionTarget.Rendering handlers (reflection, best effort): who keeps WPF rendering frames.</summary>
    private static List<string> RenderLoop()
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var lines = new List<string>();
        try
        {
            var mcType = typeof(CompositionTarget).Assembly.GetType("System.Windows.Media.MediaContext")!;
            var mc = mcType.GetMethod("From", all)!.Invoke(null, [Dispatcher.CurrentDispatcher]);
            var rendering = mcType.GetField("_rendering", all)?.GetValue(mc) as Delegate;
            lines.Add($"Rendering handlers: {rendering?.GetInvocationList().Length ?? 0} {string.Join(", ", rendering?.GetInvocationList().Select(d => d.Method.DeclaringType?.Name + "." + d.Method.Name) ?? [])}");
        }
        catch (Exception ex)
        {
            lines.Add("? " + ex.GetType().Name + ": " + ex.Message);
        }
        return lines;
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
        // what the UI thread runs: dispatcher operations by priority and method
        var ops = new Dictionary<string, int>(StringComparer.Ordinal);
        var methodField = typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);
        void OnOp(object? s, DispatcherHookEventArgs e)
        {
            var m = methodField?.GetValue(e.Operation) as Delegate;
            var key = $"{e.Operation.Priority} {m?.Method.DeclaringType?.Name}.{m?.Method.Name}";
            ops[key] = ops.GetValueOrDefault(key) + 1;
        }
        var hooks = Dispatcher.CurrentDispatcher.Hooks;
        hooks.OperationCompleted += OnOp;
        var a = ProcessStats.Take();
        await Task.Delay(window);
        var b = ProcessStats.Take();
        hooks.OperationCompleted -= OnOp;
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
        {
            foreach (var line in TimerDump.Describe(Dispatcher.CurrentDispatcher)) r.Add("   timer: " + line);
            foreach (var line in RenderLoop()) r.Add("   render: " + line);
            foreach (var (key, n) in ops.OrderByDescending(x => x.Value).Take(6)) r.Add(string.Create(Inv, $"   op: {n / secs,7:0.0}/s {key}"));
        }
        Log.Info(string.Create(Inv, $"ui probe: {title}: CPU {cycleMs / 10 / secs:0.000} % core, wake-ups {wake:0}/s, private WS {Mb(b.PrivateWorkingSet)} MB"));
    }
}
