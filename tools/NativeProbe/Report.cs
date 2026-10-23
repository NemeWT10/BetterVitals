using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text;
using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Engine;
using BetterVitals.Sensors.Fake;
using BetterVitals.Sensors.Native;

namespace BetterVitals.Tools.NativeProbe;

/// <summary>
/// Runs <see cref="NativeSource"/> on this PC (background, unelevated) in three situations — empty upstream ("LHM failed"),
/// and two fake upstream PCs — and writes what it found, the values, time per Update and allocations to a Markdown report.
/// </summary>
internal static class Report
{
    private sealed class Tee(TextWriter a, StringBuilder b) : TextWriter
    {
        public override Encoding Encoding => a.Encoding;
        public override void Write(char value) { a.Write(value); b.Append(value); }
        public override void Write(string? value) { a.Write(value); b.Append(value); }
        public override void WriteLine(string? value) { a.WriteLine(value); b.AppendLine(value); }
        public override void WriteLine() { a.WriteLine(); b.AppendLine(); }
    }

    public static int Run(string outPath, bool quick)
    {
        var sb = new StringBuilder();
        var w = new Tee(Console.Out, sb);
        var inv = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = inv;

        w.WriteLine("# NativeSource probe");
        w.WriteLine();
        w.WriteLine($"- run: {DateTime.Now:yyyy-MM-dd HH:mm:ss}, {Environment.OSVersion}, .NET {Environment.Version}, {Environment.ProcessorCount} logical processors");
        w.WriteLine($"- elevated: {IsElevated()} (the probe must run unelevated: this is the \"basic mode\" case)");
        w.WriteLine($"- build: {(IsOptimized() ? "Release" : "Debug (timings are pessimistic; run with -c Release for final numbers)")}");
        w.WriteLine("- how to read: every scenario starts a real SensorEngine with the given upstream plus NativeSource, discovers, reads every native sensor, times each hardware node's Update, then lets the engine thread run for a while.");
        w.WriteLine("- the CPU figure is the whole probe process over the interval (a 15.6 ms timer tick is the resolution), so it overstates the source; hard drives answer in ~17 ms because the command reaches the disk.");
        w.WriteLine();

        Scenario(w, "A. Empty upstream (LibreHardwareMonitor failed completely)", () => null, quick);
        Scenario(w, "B. Fake upstream: desktop AMD + NVIDIA (what a working elevated LHM would register)", () => new FakeSensorSource(FakeProfile.DesktopAmdNvidia), quick);
        Scenario(w, "C. Fake upstream: minimal basic-mode PC", () => new FakeSensorSource(FakeProfile.Minimal), quick);
        Scenario(w, "D. Real LibreHardwareMonitor on this PC (unelevated, no driver), frozen as upstream", () =>
        {
            var lhm = LhmUpstream.TryCapture(out var error);
            if (lhm is null) w.WriteLine($"(LHM could not be opened: {error})");
            return lhm;
        }, quick);

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"report written to {outPath}");
        return 0;
    }

    private static void Scenario(TextWriter w, string title, Func<ISensorSource?> upstream, bool quick)
    {
        w.WriteLine($"## {title}");
        w.WriteLine();
        var native = new NativeSource();
        var up = upstream();
        ISensorSource[] sources = up is null ? [native] : [up, native];
        using var engine = new SensorEngine(sources);

        var privateBefore = Process.GetCurrentProcess().PrivateMemorySize64;
        var t0 = Stopwatch.GetTimestamp();
        engine.DiscoverNow();
        var discoverMs = Ms(Stopwatch.GetTimestamp() - t0);
        var privateAfter = Process.GetCurrentProcess().PrivateMemorySize64;

        var all = engine.Sensors;
        var mine = all.Where(s => s.SourceTag is NativeTag).ToArray();
        var upstreamSensors = all.Where(s => s.SourceTag is not NativeTag).ToArray();
        w.WriteLine($"Discover: {discoverMs:F1} ms, private memory {(privateAfter - privateBefore) / 1024.0:F0} KB; status {native.Status.State}; upstream sensors {upstreamSensors.Length}, native sensors {mine.Length}");
        w.WriteLine();

        // ── inventory ──
        var ownHardware = engine.Hardware.Where(h => h.Source == "native").Select(h => h.Id).ToHashSet();
        w.WriteLine("| hardware | native node? | sensor | kind | unit | min interval | id |");
        w.WriteLine("|---|---|---|---|---|---|---|");
        foreach (var g in mine.GroupBy(s => s.HardwareId))
        {
            var hw = engine.FindHardware(g.Key);
            var label = ownHardware.Contains(g.Key) ? $"{hw?.Name} (new)" : $"{g.Key} (upstream card)";
            foreach (var s in g.OrderBy(s => s.Order))
                w.WriteLine($"| {label} | {(ownHardware.Contains(g.Key) ? "yes" : "no")} | {s.Name} | {s.Kind} | {s.DisplayUnit} | {s.MinInterval.TotalSeconds:0.#} s | `{s.Id}` |");
        }
        if (mine.Length == 0) w.WriteLine("| (nothing added) | | | | | | |");
        w.WriteLine();

        var dup = mine.Count(n => upstreamSensors.Any(u => (u.HardwareId == n.HardwareId && u.Kind == n.Kind && u.Name == n.Name) || u.Id == n.Id));
        var idDup = all.GroupBy(s => s.Id).Count(g => g.Count() > 1);
        w.WriteLine($"Duplicates of upstream sensors: **{dup}**; repeated ids overall: {idDup}");
        w.WriteLine();

        if (native.MemoryModules.Count > 0)
        {
            w.WriteLine("Memory modules (SMBIOS type 17):");
            w.WriteLine();
            w.WriteLine("| slot | bank | type | form | MB | speed MT/s | configured MT/s | manufacturer | part number |");
            w.WriteLine("|---|---|---|---|---|---|---|---|---|");
            foreach (var m in native.MemoryModules)
                w.WriteLine($"| {m.DeviceLocator} | {m.BankLocator} | {m.Type} | {m.FormFactor} | {m.CapacityMb} | {m.SpeedMTs} | {m.ConfiguredSpeedMTs} | {m.Manufacturer} | {m.PartNumber} |");
            w.WriteLine();
        }
        if (mine.Length == 0) return;

        // ── values ──
        var values = new float[all.Count];
        Array.Fill(values, float.NaN);
        var requested = mine;
        {
            var privateBeforeFirst = Process.GetCurrentProcess().PrivateMemorySize64;
            var first = Stopwatch.GetTimestamp();
            native.Update(requested, values);
            w.WriteLine($"First Update after Discover: {Ms(Stopwatch.GetTimestamp() - first):F1} ms, private memory +{(Process.GetCurrentProcess().PrivateMemorySize64 - privateBeforeFirst) / 1024} KB "
                        + "(the first read of a GPU load / memory sensor opens the PDH counters; in a process that already used PDH this costs much less, which is why only the first scenario shows it).");
            w.WriteLine();
        }
        Thread.Sleep(1200);
        for (var round = 0; round < 3; round++)
        {
            native.Update(requested, values);
            Thread.Sleep(1000);
        }
        native.Update(requested, values);
        w.WriteLine("Values after four reads, one second apart:");
        w.WriteLine();
        w.WriteLine("| sensor | value |");
        w.WriteLine("|---|---|");
        foreach (var s in requested)
            w.WriteLine($"| {engine.FindHardware(s.HardwareId)?.Name ?? s.HardwareId} / {s.Kind} / {s.Name} | {Format(values[s.Index], s)} |");
        w.WriteLine();

        // ── time and allocations per hardware group ──
        var rounds = quick ? 8 : 30;
        var groups = mine.GroupBy(s => s.HardwareId).Select(g => (Id: g.Key, Sensors: g.ToArray())).ToArray();
        var stats = groups.ToDictionary(g => g.Id, _ => new Stat());
        for (var r = 0; r < rounds; r++)
        {
            foreach (var g in groups)
            {
                var a0 = GC.GetAllocatedBytesForCurrentThread();
                var s0 = Stopwatch.GetTimestamp();
                native.Update(g.Sensors, values);
                var dt = Stopwatch.GetTimestamp() - s0;
                var alloc = GC.GetAllocatedBytesForCurrentThread() - a0;
                if (r >= 2) stats[g.Id].Add(Ms(dt) * 1000, alloc); // first rounds warm JIT and prime the rate counters
            }
            Thread.Sleep(300);
        }
        w.WriteLine($"Time per Update call and allocations ({rounds - 2} measured calls per node, 300 ms apart, every sensor of the node requested):");
        w.WriteLine();
        w.WriteLine("| hardware | sensors | avg us | max us | allocated bytes per call (max) |");
        w.WriteLine("|---|---|---|---|---|");
        foreach (var g in groups)
        {
            var st = stats[g.Id];
            w.WriteLine($"| {engine.FindHardware(g.Id)?.Name ?? g.Id} | {g.Sensors.Length} | {st.Avg:F1} | {st.Max:F1} | {st.MaxAlloc} |");
        }
        var wholeAlloc = long.MaxValue;
        {
            var worst = 0L;
            for (var r = 0; r < 20; r++)
            {
                var a0 = GC.GetAllocatedBytesForCurrentThread();
                native.Update(requested, values);
                worst = Math.Max(worst, GC.GetAllocatedBytesForCurrentThread() - a0);
                Thread.Sleep(r < 2 ? 300 : 50);
            }
            wholeAlloc = worst;
        }
        w.WriteLine();
        w.WriteLine($"All native sensors in one Update call, worst allocation over 20 calls: **{wholeAlloc} bytes**");
        w.WriteLine();

        // ── engine-driven CPU cost ──
        var seconds = quick ? 8 : 20;
        using var demand = engine.Demand("probe", mine.Select(s => s.Id), TimeSpan.FromSeconds(1));
        engine.Start();
        Thread.Sleep(2000);
        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        var cpu0 = proc.TotalProcessorTime;
        var wall0 = Stopwatch.GetTimestamp();
        var frames0 = engine.Latest.Sequence;
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        proc.Refresh();
        var cpu = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
        var wall = Ms(Stopwatch.GetTimestamp() - wall0);
        var frames = engine.Latest.Sequence - frames0;
        w.WriteLine($"Real engine thread, every native sensor demanded at 1 s (drives follow their own minimum interval), {seconds} s: {frames} frames, process CPU {cpu:F1} ms over {wall / 1000:F1} s = **{100 * cpu / wall:F3} % of one core** (whole probe process, includes the engine thread and this sleeping main thread). Private memory now {proc.PrivateMemorySize64 / 1024 / 1024.0:F1} MB, working set {proc.WorkingSet64 / 1024 / 1024.0:F1} MB, GC heap {GC.GetTotalMemory(false) / 1024 / 1024.0:F1} MB.");
        w.WriteLine();
    }

    private sealed class Stat
    {
        private double _sum;
        private int _n;
        public double Max;
        public long MaxAlloc;
        public double Avg => _n == 0 ? 0 : _sum / _n;

        public void Add(double us, long alloc)
        {
            _sum += us;
            _n++;
            Max = Math.Max(Max, us);
            MaxAlloc = Math.Max(MaxAlloc, alloc);
        }
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static string Format(float v, SensorDescriptor s)
    {
        if (float.IsNaN(v)) return "NaN";
        return s.Kind switch
        {
            SensorKind.Throughput => $"{v:F0} B/s",
            SensorKind.Voltage => $"{v:F3} {s.DisplayUnit}",
            SensorKind.Data => $"{v:F2} {s.DisplayUnit}",
            _ => $"{v:F1} {s.DisplayUnit}",
        };
    }

    private static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool IsOptimized()
    {
        var attr = typeof(Report).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false).OfType<DebuggableAttribute>().FirstOrDefault();
        return attr is null || !attr.IsJITOptimizerDisabled;
    }
}
