using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using BetterVitals.Core;
using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Lhm;

namespace SensorProbe;

/// <summary>
/// Runs <see cref="LhmSource"/> like the engine would (unelevated, no UI) and writes a Markdown report:
/// hardware tree, every sensor, discovery time, update time per hardware node, allocations per tick, private working set.
/// Usage: SensorProbe [ticks=30] [intervalMs=1000]
/// </summary>
internal static partial class Program
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static int Main(string[] args)
    {
        var ticks = args.Length > 0 && int.TryParse(args[0], out var t) ? t : 30;
        var intervalMs = args.Length > 1 && int.TryParse(args[1], out var ms) ? ms : 1000;
        var root = FindRepoRoot();
        var outDir = Path.Combine(root, "tests", "output", "sensorprobe");
        Directory.CreateDirectory(outDir);
        AppEnv.Init(testMode: true, homeOverride: Path.Combine(outDir, "home"));
        Thread.CurrentThread.Name = "engine";

        var r = new StringBuilder();
        var wsStart = PrivateWorkingSet();

        using var source = new LhmSource();
        var discoverWatch = Stopwatch.StartNew();
        var inventory = source.Discover();
        discoverWatch.Stop();
        var wsAfterDiscover = PrivateWorkingSet();

        AssignSlots(inventory.Sensors, 0);
        var all = inventory.Sensors.ToArray();
        var values = new float[all.Length];
        values.AsSpan().Fill(float.NaN);

        // ── phase A: everything, honoring MinInterval like the engine ──
        var lastRead = new long[all.Length];
        Array.Fill(lastRead, long.MinValue / 2);
        var due = new SensorDescriptor[all.Length];
        var tickRows = new List<(int Tick, int Requested, double Ms, long Alloc, long LhmAlloc)>();
        var cpuA0 = Process.GetCurrentProcess().TotalProcessorTime;
        var wallA = Stopwatch.StartNew();
        for (var tick = 0; tick < ticks; tick++)
        {
            var now = Stopwatch.GetTimestamp();
            var n = 0;
            for (var i = 0; i < all.Length; i++)
            {
                if (Stopwatch.GetElapsedTime(lastRead[i], now) < all[i].MinInterval) continue;
                lastRead[i] = now;
                due[n++] = all[i];
            }
            var sw = Stopwatch.StartNew();
            source.Update(due.AsSpan(0, n), values);
            sw.Stop();
            tickRows.Add((tick + 1, n, sw.Elapsed.TotalMilliseconds, source.LastUpdateAllocatedBytes, source.LastUpdateLhmAllocatedBytes));
            Thread.Sleep(Math.Max(0, intervalMs - (int)sw.ElapsedMilliseconds));
        }
        wallA.Stop();
        var valuesA = values.ToArray();
        var cpuA = Process.GetCurrentProcess().TotalProcessorTime - cpuA0;
        var nodeStatsA = source.GetNodeStats();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var wsAfterTicks = PrivateWorkingSet();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        var wsAfterAggressive = PrivateWorkingSet();

        // ── phase D: GPU cost with / without the D3D + NVML PCIe parts ──
        var gpuRows = new List<string>();
        foreach (var gpu in inventory.Hardware.Where(h => h.Kind == HardwareKind.Gpu))
        {
            var allGpu = all.Where(d => d.HardwareId == gpu.Id).ToArray();
            var core = allGpu.Where(d => !d.Name.StartsWith("D3D ", StringComparison.Ordinal) && !d.Name.StartsWith("GPU PCIe ", StringComparison.Ordinal)).ToArray();
            foreach (var (label, set) in new[] { ("core sensors (no D3D, no PCIe)", core), ("all sensors", allGpu) })
            {
                var (wall, cpu, alloc) = Measure(source, set, values, 20);
                gpuRows.Add(string.Create(Inv, $"| {gpu.Name} | {label} | {set.Length} | {wall:F2} | {cpu:F2} | {alloc} |"));
            }
        }

        // ── phase B: GPU + memory subset only (shows that unrequested nodes are not refreshed) ──
        var subset = all.Where(d => d.HardwareKind is HardwareKind.Gpu or HardwareKind.Memory && d.MinInterval == TimeSpan.Zero).ToArray();
        var before = source.GetNodeStats().ToDictionary(s => s.HardwareId, s => s.Updates);
        var subsetRows = new List<(double Ms, long Alloc, long LhmAlloc)>();
        for (var i = 0; i < 10; i++)
        {
            var sw = Stopwatch.StartNew();
            source.Update(subset, values);
            sw.Stop();
            subsetRows.Add((sw.Elapsed.TotalMilliseconds, source.LastUpdateAllocatedBytes, source.LastUpdateLhmAllocatedBytes));
            Thread.Sleep(Math.Max(0, Math.Min(intervalMs, 250) - (int)sw.ElapsedMilliseconds));
        }
        var touchedInB = source.GetNodeStats().Where(s => s.Updates != before.GetValueOrDefault(s.HardwareId)).Select(s => s.Name).ToList();

        // ── phase C: driver pause round trip (ids must stay stable) ──
        var idsBefore = inventory.Sensors.Select(s => s.Id).ToHashSet();
        source.SetDriverPaused(true);
        source.Update(all, values);
        var statusPaused = source.Status;
        var pausedInventory = source.Discover();
        var pausedIds = pausedInventory.Sensors.Select(s => s.Id).ToHashSet();
        source.SetDriverPaused(false);
        var resumedInventory = source.Discover();
        var resumedIds = resumedInventory.Sensors.Select(s => s.Id).ToHashSet();

        // ── report ──
        r.AppendLine("# SensorProbe report");
        r.AppendLine();
        r.AppendLine(Inv, $"- Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}, machine: {Environment.MachineName}, OS: {RuntimeInformation.OSDescription}");
        r.AppendLine(Inv, $"- Elevated: {source.IsElevated}, PawnIO: {source.PawnIoVersion?.ToString() ?? "not installed"}, status: {source.Status.State} ({source.Status.MessageKey ?? "-"})");
        r.AppendLine(Inv, $"- Hardware nodes: {inventory.Hardware.Count}, sensors: {inventory.Sensors.Count}");
        r.AppendLine(Inv, $"- Discover: {discoverWatch.Elapsed.TotalMilliseconds:F0} ms (source-reported {source.LastDiscoverDuration.TotalMilliseconds:F0} ms)");
        r.AppendLine(Inv, $"- Private working set: start {Mb(wsStart)}, after Discover {Mb(wsAfterDiscover)}, after {ticks} ticks + GC {Mb(wsAfterTicks)}, after an aggressive compacting GC {Mb(wsAfterAggressive)}; managed heap {Mb(GC.GetTotalMemory(false))}");
        r.AppendLine(Inv, $"- Process CPU during phase A: {cpuA.TotalMilliseconds:F0} ms over {wallA.Elapsed.TotalSeconds:F1} s = {cpuA.TotalMilliseconds / wallA.Elapsed.TotalMilliseconds * 100:F2} % of one core, {cpuA.TotalMilliseconds / wallA.Elapsed.TotalMilliseconds * 100 / Environment.ProcessorCount:F3} % of the machine ({Environment.ProcessorCount} logical CPUs)");
        r.AppendLine(Inv, $"- Update plans built: {source.PlanBuilds}");
        r.AppendLine();

        r.AppendLine("## Ticks (phase A: all sensors, MinInterval honored)");
        r.AppendLine();
        r.AppendLine("| tick | requested | update ms | allocated B (total) | allocated B (inside LHM) | source overhead B |");
        r.AppendLine("|---:|---:|---:|---:|---:|---:|");
        foreach (var row in tickRows)
            r.AppendLine(Inv, $"| {row.Tick} | {row.Requested} | {row.Ms:F2} | {row.Alloc} | {row.LhmAlloc} | {row.Alloc - row.LhmAlloc} |");
        var steady = tickRows.Skip(1).ToList();
        if (steady.Count > 0)
        {
            r.AppendLine();
            r.AppendLine(Inv, $"Steady state (ticks 2…{ticks}): update avg {steady.Average(x => x.Ms):F2} ms, max {steady.Max(x => x.Ms):F2} ms; " +
                              $"allocated avg {steady.Average(x => x.Alloc):F0} B/tick (LHM {steady.Average(x => x.LhmAlloc):F0} B, source {steady.Average(x => x.Alloc - x.LhmAlloc):F0} B).");
        }
        r.AppendLine();

        r.AppendLine("## Update time per hardware node (phase A)");
        r.AppendLine();
        r.AppendLine("| hardware | kind | updates | avg ms | max ms | alloc B / update | asleep skips | failures | power gate | state | rotating |");
        r.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---|---|---|");
        foreach (var s in nodeStatsA)
        {
            var avg = s.Updates > 0 ? s.TotalMilliseconds / s.Updates : 0;
            var alloc = s.Updates > 0 ? s.AllocatedBytes / s.Updates : 0;
            r.AppendLine(Inv, $"| {s.Name} | {s.Kind} | {s.Updates} | {avg:F3} | {s.MaxMilliseconds:F3} | {alloc} | {s.SkippedAsleep} | {s.Failures} | {s.PowerGate ?? "-"} | {s.PowerState?.ToString() ?? "-"} | {s.Rotating?.ToString() ?? "-"} |");
        }
        r.AppendLine();

        r.AppendLine("## Phase D: GPU update cost (20 calls each; CPU from process times, ±1 ms resolution)");
        r.AppendLine();
        r.AppendLine("| GPU | requested | sensors | wall ms / call | CPU ms / call | alloc B / call |");
        r.AppendLine("|---|---|---:|---:|---:|---:|");
        foreach (var row in gpuRows) r.AppendLine(row);
        r.AppendLine();

        r.AppendLine("## Phase B: GPU + memory subset");
        r.AppendLine();
        r.AppendLine(Inv, $"- {subset.Length} sensors, 10 calls: avg {subsetRows.Average(x => x.Ms):F2} ms, allocated avg {subsetRows.Average(x => x.Alloc):F0} B (LHM {subsetRows.Average(x => x.LhmAlloc):F0} B)");
        r.AppendLine(Inv, $"- Nodes refreshed: {string.Join(", ", touchedInB)}");
        r.AppendLine();

        r.AppendLine("## Phase C: driver pause round trip");
        r.AppendLine();
        r.AppendLine(Inv, $"- Status while paused: {statusPaused.State} ({statusPaused.MessageKey}); PawnIO modules closed by reflection: {source.LastPauseClosedModules}");
        r.AppendLine(Inv, $"- Ids before: {idsBefore.Count}, while paused: {pausedIds.Count} (missing {idsBefore.Except(pausedIds).Count()}), after resume: {resumedIds.Count} (missing {idsBefore.Except(resumedIds).Count()}, new {resumedIds.Except(idsBefore).Count()})");
        r.AppendLine(Inv, $"- Status after resume: {source.Status.State} ({source.Status.MessageKey ?? "-"})");
        r.AppendLine();

        r.AppendLine("## Hardware tree");
        r.AppendLine();
        foreach (var h in inventory.Hardware.Where(h => h.ParentId is null)) WriteTree(r, inventory, h, 0);
        r.AppendLine();

        r.AppendLine("## Sensors (value after phase A)");
        r.AppendLine();
        r.AppendLine("| id | kind | hardware | name | value | unit | min interval | note |");
        r.AppendLine("|---|---|---|---|---:|---|---:|---|");
        var hwNames = inventory.Hardware.ToDictionary(h => h.Id, h => h.Name);
        foreach (var d in all)
        {
            var v = valuesA[d.Index];
            var text = float.IsNaN(v) ? "NaN" : v.ToString("0.###", Inv);
            r.AppendLine(Inv, $"| `{d.Id}` | {d.Kind} | {hwNames[d.HardwareId]} | {d.Name} | {text} | {d.DisplayUnit} | {d.MinInterval.TotalSeconds:0} s | {d.NoteKey ?? ""} |");
        }

        var path = Path.Combine(outDir, "report.md");
        File.WriteAllText(path, r.ToString(), Encoding.UTF8);
        Console.WriteLine($"report: {path}");
        Console.WriteLine($"hardware {inventory.Hardware.Count}, sensors {inventory.Sensors.Count}, discover {discoverWatch.Elapsed.TotalMilliseconds:F0} ms, status {source.Status}");
        return 0;
    }

    private static (double WallMs, double CpuMs, long Alloc) Measure(LhmSource source, SensorDescriptor[] set, float[] values, int calls)
    {
        source.Update(set, values);
        var p = Process.GetCurrentProcess();
        var cpu0 = p.TotalProcessorTime;
        long alloc = 0;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < calls; i++)
        {
            source.Update(set, values);
            alloc += source.LastUpdateAllocatedBytes;
        }
        sw.Stop();
        p.Refresh();
        return (sw.Elapsed.TotalMilliseconds / calls, (p.TotalProcessorTime - cpu0).TotalMilliseconds / calls, alloc / calls);
    }

    private static void WriteTree(StringBuilder r, SourceInventory inv, HardwareDescriptor h, int depth)
    {
        var count = inv.Sensors.Count(s => s.HardwareId == h.Id);
        r.AppendLine(Inv, $"{new string(' ', depth * 2)}- **{h.Name}** [{h.Kind}] `{h.Id}` — {count} sensors");
        foreach (var c in inv.Hardware.Where(x => x.ParentId == h.Id)) WriteTree(r, inv, c, depth + 1);
    }

    /// <summary>The hub assigns slots; the probe stands in for it (internal setter, so reflection).</summary>
    private static void AssignSlots(IReadOnlyList<SensorDescriptor> sensors, int first)
    {
        var prop = typeof(SensorDescriptor).GetProperty(nameof(SensorDescriptor.Index), BindingFlags.Instance | BindingFlags.Public)!;
        for (var i = 0; i < sensors.Count; i++) prop.SetValue(sensors[i], first + i);
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "BetterVitals.sln"))) return dir.FullName;
        return Environment.CurrentDirectory;
    }

    private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("F1", Inv) + " MB";

    private static long PrivateWorkingSet()
    {
        var c = new ProcessMemoryCountersEx2 { Cb = (uint)Marshal.SizeOf<ProcessMemoryCountersEx2>() };
        return GetProcessMemoryInfo(GetCurrentProcess(), ref c, c.Cb) ? (long)c.PrivateWorkingSetSize : -1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx2
    {
        public uint Cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("psapi.dll", EntryPoint = "GetProcessMemoryInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(nint process, ref ProcessMemoryCountersEx2 counters, uint size);
}
