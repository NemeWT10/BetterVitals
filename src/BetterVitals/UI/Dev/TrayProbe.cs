using System.IO;
using System.Globalization;
using System.Text;
using System.Windows.Threading;
using BetterVitals.Core;
using BetterVitals.Core.Overlay;
using BetterVitals.Services;
using BetterVitals.UI.Panel;
using BetterVitals.UI.Tray;

namespace BetterVitals.UI.Dev;

/// <summary>
/// <c>--tray-probe</c> (test mode, run it on the hidden desktop): measures the private working set at start, with the
/// panel open (every page visited) and the flyout open, and again after everything closed and the tray rest settled.
/// Writes tray-probe.txt next to the logs and to stdout, then exits.
/// <para>
/// With <c>--perf [seconds]</c> (tools\perf.ps1, usually with <c>--real-sensors</c>) it measures idle windows instead:
/// tray rest with recording (+ the overlay when the settings enable it), the panel open with every sensor live at 1 s
/// (what the Overview / Sensors pages ask for), the flyout open, and the tray again after the panel closed. Per window: CPU (one core and
/// whole machine), wake-ups (context switches) per thread, engine ticks and their cost, allocations, GC, I/O writes,
/// memory, threads, handles, LHM node costs. Writes perf-probe.txt.
/// </para>
/// </summary>
internal static class TrayProbe
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void Start()
    {
        var args = Environment.GetCommandLineArgs();
        var at = Array.FindIndex(args, a => a.Equals("--perf", StringComparison.OrdinalIgnoreCase));
        if (at < 0)
        {
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, Run);
            return;
        }
        var window = at + 1 < args.Length && int.TryParse(args[at + 1], NumberStyles.Integer, Inv, out var s) ? s : 90;
        var trayOnly = args.Any(a => a.Equals("--perf-tray-only", StringComparison.OrdinalIgnoreCase));
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => RunPerf(TimeSpan.FromSeconds(Math.Clamp(window, 10, 3600)), trayOnly));
    }

    private static async void Run()
    {
        var lines = new List<string>();
        void Mark(string what)
        {
            var line = MemoryLine(what);
            lines.Add(line);
            Log.Info("probe: " + line);
        }
        try
        {
            Mark("start");
            TrayRest.Settle(); // what a normal start does
            await Task.Delay(6000); // the start-up trims ran (idle + 4 s)
            Mark("start, trimmed");

            AppShell.I.OpenPanel(PageIds.Overview);
            await Task.Delay(400);
            foreach (var page in PageRegistry.All)
            {
                AppShell.I.OpenPanel(page.Id);
                await Task.Delay(300);
            }
            Mark("panel open, all pages");
            AppShell.I.ShowFlyout();
            await Task.Delay(1500);
            Mark("panel + flyout");

            AppShell.I.CloseAll();
            await Task.Delay(30000); // idle trim, +4 s with GC, +25 s working set only
            Mark($"tray rest ({TrayRest.Trims} trims)");
            await Task.Delay(60000);
            Mark("tray rest +60 s (hub ticking)");
        }
        catch (Exception ex)
        {
            lines.Add("probe failed: " + ex);
            Log.Error("probe failed", ex);
        }
        Write("tray-probe.txt", lines);
        AppShell.I.Quit();
    }

    private static string MemoryLine(string what)
    {
        var (priv, ws, bytes) = Native.MemoryUse();
        return string.Create(Inv,
            $"{what,-30} private WS {priv / 1048576.0,7:0.0} MB | WS {ws / 1048576.0,7:0.0} MB | private bytes {bytes / 1048576.0,7:0.0} MB | managed {GC.GetTotalMemory(false) / 1048576.0,6:0.0} MB | GC committed {GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576.0,6:0.0} MB");
    }

    // ───────────────────────── --perf ─────────────────────────

    private static async void RunPerf(TimeSpan window, bool trayOnly)
    {
        var r = new List<string>();
        void Mark(string what)
        {
            var line = MemoryLine(what);
            r.Add(line);
            Log.Info("perf: " + line);
        }
        try
        {
            var host = AppHost.Current!;
            var overlay = Core.Settings.SettingsStore.I.Current.Overlay.Enabled;
            var boxes = overlay ? await SplitOverlay() : 0;
            r.Add($"perf probe {AppEnv.Version} · {Environment.ProcessorCount} processors visible, {ProcessStats.MachineProcessors} in the machine · TSC {ProcessStats.TscHz / 1e9:0.000} GHz · elevated={BetterVitals.Platform.Elevation.IsElevated} · overlay={overlay}{(overlay ? $" ({boxes} box{(boxes == 1 ? "" : "es")})" : "")} · window {window.TotalSeconds:0} s");
            r.Add($"runtime: tiered compilation={AppContext.GetData("System.Runtime.TieredCompilation") ?? "default"}, tiered PGO={AppContext.GetData("System.Runtime.TieredPGO") ?? "default"}, conserve memory={AppContext.GetData("System.GC.ConserveMemory") ?? "default"}, env DOTNET_TieredCompilation={Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "-"} DOTNET_TieredPGO={Environment.GetEnvironmentVariable("DOTNET_TieredPGO") ?? "-"}");
            r.Add("");
            Mark("start");
            TrayRest.Settle();
            await Task.Delay(6000);
            Mark("start, trimmed");
            await Task.Delay(30000); // the 25 s trim ran, history started, LHM settled
            Mark("start +36 s");
            r.Add("");

            await Measure(r, host, overlay ? "tray + overlay, recording" : "tray rest, recording", window);
            if (trayOnly)
            {
                Write("perf-probe.txt", r);
                AppShell.I.Quit();
                return;
            }

            // panel: every page once (memory), then the Overview with every sensor live at 1 s (what the real pages ask for)
            var open = System.Diagnostics.Stopwatch.StartNew();
            AppShell.I.OpenPanel(PageIds.Overview);
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
            r.Add(string.Create(Inv, $"latency: first panel open {open.Elapsed.TotalMilliseconds:0} ms (until the UI thread is idle)"));
            await Task.Delay(400);
            foreach (var page in PageRegistry.All)
            {
                AppShell.I.OpenPanel(page.Id);
                await Task.Delay(300);
            }
            AppShell.I.OpenPanel(PageIds.Overview);
            using (host.Hub.Demand("probe-panel", host.Hub.Sensors.Select(s => s.Id), TimeSpan.FromSeconds(1)))
            {
                await Task.Delay(5000);
                await Measure(r, host, "panel open (Overview), all sensors 1 s", TimeSpan.FromSeconds(Math.Min(window.TotalSeconds, 60)));
            }
            AppShell.I.CloseAll();
            await Task.Delay(1000);
            for (var i = 1; i <= 2; i++)
            {
                open.Restart();
                AppShell.I.ShowFlyout();
                await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
                r.Add(string.Create(Inv, $"latency: flyout open #{i} {open.Elapsed.TotalMilliseconds:0} ms"));
                if (i == 1)
                {
                    await Task.Delay(1000);
                    AppShell.I.CloseAll();
                    await Task.Delay(1000);
                }
            }
            r.Add("");
            await Task.Delay(3000);
            await Measure(r, host, "flyout open", TimeSpan.FromSeconds(30));
            AppShell.I.CloseAll();
            await Task.Delay(30000);
            Mark($"panel closed +30 s ({TrayRest.Trims} trims)");
            r.Add("");
            await Measure(r, host, "tray after panel", window);

            // a device change (what WM_DEVICECHANGE does): LHM hot-plug groups + NVIDIA re-enumeration, then re-discovery
            var updates0 = host.Lhm?.GetNodeStats().Where(n => n.Kind == Core.Sensors.HardwareKind.Gpu).Sum(n => n.Updates) ?? 0;
            host.OnDriverInstalled();
            await Task.Delay(10000);
            Mark("device change +10 s");
            await Task.Delay(30000);
            var updates1 = host.Lhm?.GetNodeStats().Where(n => n.Kind == Core.Sensors.HardwareKind.Gpu).Sum(n => n.Updates) ?? 0;
            Mark($"device change +40 s ({TrayRest.Trims} trims, GPU updates +{updates1 - updates0})");
            Mark("end");
        }
        catch (Exception ex)
        {
            r.Add("perf probe failed: " + ex);
            Log.Error("perf probe failed", ex);
        }
        Write("perf-probe.txt", r);
        AppShell.I.Quit();
    }

    /// <summary>
    /// <c>--perf-overlay-boxes N</c>: once the default overlay items exist, detaches rows (then items) of the main box
    /// until the overlay has N boxes — the cost of several overlay windows. Returns the number of boxes.
    /// </summary>
    private static async Task<int> SplitOverlay()
    {
        var args = Environment.GetCommandLineArgs();
        var at = Array.FindIndex(args, a => a.Equals("--perf-overlay-boxes", StringComparison.OrdinalIgnoreCase));
        var want = at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], NumberStyles.Integer, Inv, out var n) ? Math.Clamp(n, 1, Core.Overlay.OverlayLayout.MaxBoxes) : 1;
        var store = Core.Settings.SettingsStore.I;
        for (var i = 0; i < 60 && store.Current.Overlay.IsEmpty; i++) await Task.Delay(500); // the defaults come with the sensors
        if (want > 1)
        {
            store.Mutate("overlay", s =>
            {
                var o = s.Overlay;
                while (o.Boxes.Count + 1 < want && o.CanAddBox())
                {
                    if (o.Rows.Count > 1) o.MoveRowToNewBox(o.Rows[^1].Id, Core.Overlay.OverlayLayout.NewId(), o.NewBoxPlacement());
                    else if (o.Rows.Count == 1 && o.Rows[0].Items.Count > 1) o.MoveItemToNewBox(o.Rows[0].Items[^1].Id, Core.Overlay.OverlayLayout.NewId(), Core.Overlay.OverlayLayout.NewId(), o.NewBoxPlacement());
                    else break;
                }
            });
            await Task.Delay(3000);
        }
        var overlay = store.Current.Overlay;
        Log.Info($"perf: overlay with {overlay.Boxes.Count + 1} boxes, {overlay.AllItems().Count()} items");
        return overlay.Boxes.Count + 1;
    }

    private static async Task Measure(List<string> r, AppHost host, string title, TimeSpan window)
    {
        var lhm0 = host.Lhm?.GetNodeStats().ToDictionary(n => n.HardwareId);
        var seq0 = host.Hub.Latest.Sequence;
        var a = ProcessStats.Take();
        var samples = new List<long>();
        var end = DateTime.UtcNow + window;
        while (DateTime.UtcNow < end)
        {
            var left = end - DateTime.UtcNow;
            await Task.Delay(left < TimeSpan.FromSeconds(15) ? left : TimeSpan.FromSeconds(15));
            samples.Add(ProcessStats.Memory().PrivateWorkingSet);
        }
        var b = ProcessStats.Take();
        var seq1 = host.Hub.Latest.Sequence;
        var lhm1 = host.Lhm?.GetNodeStats();

        var secs = (b.Timestamp - a.Timestamp) / (double)System.Diagnostics.Stopwatch.Frequency;
        var cpuMs = (b.CpuTicks - a.CpuTicks) / 10_000.0;
        var ticks = seq1 - seq0;
        long Switches(ProcessStats.ThreadInfo t) => (long)t.ContextSwitches - (a.Threads.TryGetValue(t.Id, out var o) ? o.ContextSwitches : 0);
        // per-thread CPU from cycle counters (exact; the tick-sampled thread times are 15.6 ms coarse)
        double Cpu(ProcessStats.ThreadInfo t) => (t.Cycles - (a.Threads.TryGetValue(t.Id, out var o) ? Math.Min(o.Cycles, t.Cycles) : 0)) * 1000.0 / ProcessStats.TscHz;
        var wakeups = b.Threads.Values.Sum(Switches);
        var engine = b.Threads.Values.FirstOrDefault(t => t.Name.StartsWith("BetterVitals sampling", StringComparison.Ordinal));

        static string Mb(long v) => (v / 1048576.0).ToString("0.0", Inv);
        r.Add($"## {title} ({secs:0} s)");
        r.Add(string.Create(Inv, $"memory (end)      private WS {Mb(b.PrivateWorkingSet)} MB (window min {Mb(samples.DefaultIfEmpty(0).Min())}, max {Mb(samples.DefaultIfEmpty(0).Max())}) · WS {Mb(b.WorkingSet)} MB · private bytes {Mb(b.PrivateBytes)} MB"));
        r.Add(string.Create(Inv, $"GC                heap {Mb(b.GcHeap)} MB · committed {Mb(b.GcCommitted)} MB · collections gen0 +{b.Gen0 - a.Gen0} gen1 +{b.Gen1 - a.Gen1} gen2 +{b.Gen2 - a.Gen2} · allocated {(b.Allocated - a.Allocated) / secs / 1024:0.0} KB/s"));
        var cycleMs = (b.Cycles - a.Cycles) * 1000.0 / ProcessStats.TscHz;
        r.Add(string.Create(Inv, $"CPU               {cycleMs:0} ms (cycles; {cpuMs:0} ms tick-sampled) = {cycleMs / 10 / secs:0.000} % of one core = {cycleMs / 10 / secs / ProcessStats.MachineProcessors:0.0000} % of the machine ({ProcessStats.MachineProcessors} logical)"));
        r.Add(string.Create(Inv, $"wake-ups          {wakeups / secs:0.0} /s (context switches, all threads)"));
        r.Add(string.Create(Inv, $"engine            {ticks} ticks = {ticks / secs:0.00} /s · engine thread {(engine is null ? 0 : Cpu(engine)):0} ms CPU = {(ticks > 0 && engine is not null ? Cpu(engine) / ticks : 0):0.00} ms per tick · {(engine is null ? 0 : Switches(engine)) / secs:0.0} wake-ups/s"));
        r.Add(string.Create(Inv, $"disk              {(b.WriteOps - a.WriteOps) / secs * 60:0.0} write ops/min · {(b.WriteBytes - a.WriteBytes) / secs * 60 / 1024:0.0} KB/min · reads {(b.ReadOps - a.ReadOps) / secs * 60:0} /min · other I/O {(b.OtherOps - a.OtherOps) / secs * 60:0} /min"));
        r.Add(string.Create(Inv, $"process           {b.Threads.Count} threads · {b.Handles} handles"));
        r.Add("demands           " + string.Join(", ", host.Engine.Demands.Select(d => string.Create(Inv, $"{d.Consumer} {d.Sensors}@{d.Interval.TotalSeconds:0.##}s"))));
        r.Add("threads (CPU ms / wake-ups per s in the window):");
        foreach (var t in b.Threads.Values.OrderByDescending(Cpu).ThenByDescending(Switches).Take(14))
            r.Add(string.Create(Inv, $"  {Cpu(t),8:0.0} ms {Switches(t) / secs,7:0.00}/s  #{t.Id,-6} {t.Name}"));
        var idle = b.Threads.Values.Count(t => Switches(t) == 0);
        r.Add($"  … {idle} threads did not wake at all");
        if (lhm0 is not null && lhm1 is not null)
        {
            r.Add("LHM nodes updated in the window (updates, ms each, total ms, alloc B each):");
            foreach (var n in lhm1)
            {
                var o = lhm0.GetValueOrDefault(n.HardwareId);
                var u = n.Updates - o.Updates;
                if (u <= 0) continue;
                var ms = n.TotalMilliseconds - o.TotalMilliseconds;
                r.Add(string.Create(Inv, $"  {u,5} × {ms / u,7:0.000} ms = {ms,8:0.0} ms  {(n.AllocatedBytes - o.AllocatedBytes) / u,7} B  {n.Name}{(n.SkippedAsleep > o.SkippedAsleep ? $" (asleep {n.SkippedAsleep - o.SkippedAsleep}×)" : "")}"));
            }
        }
        r.Add("private working set by owner:");
        r.AddRange(ProcessStats.PrivateBreakdown());
        r.Add("armed timers:");
        r.AddRange(TimerDump.Describe(Dispatcher.CurrentDispatcher));
        r.Add("");
        Log.Info(string.Create(Inv, $"perf: {title}: CPU {cycleMs / 10 / secs:0.000} % core, wake-ups {wakeups / secs:0.0}/s, private WS {Mb(b.PrivateWorkingSet)} MB"));
    }

    private static void Write(string file, List<string> lines)
    {
        try
        {
            File.WriteAllLines(Path.Combine(AppEnv.LogDir, file), lines, Encoding.UTF8);
            foreach (var l in lines) Console.WriteLine(l);
        }
        catch
        {
            // the log has it
        }
    }
}
