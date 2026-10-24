using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Overlay;
using BetterVitals.Overlay.Host;
using BetterVitals.Overlay.Rendering;

// Overlay cost probe. Usage:
//   OverlayHarness [--gpu] [--seconds N] [--windows 1|2] [--out file] [--readback file.png] [--storm]
//   Runs the real overlay host (window + D3D11 + DirectComposition + Direct2D) with fake sensors at the default
//   1 s refresh, values changing every tick, and reports the process working set / private bytes and threads before and
//   after, the overlay thread's CPU time and wake-ups, the whole process's CPU and the overlay thread's allocations per
//   frame. The windows are created but never shown. --windows 2 moves the last three of the six items into the second
//   window (two windows on the one shared device). --readback writes what the main window's swap chain holds (fixed
//   values) to a PNG: compare two builds for pixel identity.
//   --storm (hidden desktop only): a helper process moves a child window of its own ~500 times a second while the
//   overlay watches it as "the foreground window" — the location-change storm a game's cursor or a text caret causes —
//   and reports the overlay thread's wake-ups during it.
//   --show exists only for a person to look at the overlay on their own desktop; automated runs never use it.

if (args.Contains("--storm-child")) return StormChild.Run();

var warp = !args.Contains("--gpu");
var show = args.Contains("--show");
var storm = args.Contains("--storm");
var seconds = ArgInt("--seconds", 20);
var windows = Math.Clamp(ArgInt("--windows", 1), 1, OverlayLayout.MaxWindows);
var outFile = ArgStr("--out");
var readback = ArgStr("--readback");
if (storm && !Desk.IsPrivate())
{
    Console.Error.WriteLine("--storm creates windows: run it on a hidden desktop (HiddenDesktopRunner)");
    return 3;
}

var hub = new HarnessHub();
var fps = hub.Add("game/foreground/0", HardwareKind.Game, SensorKind.Fps, "FPS");
var cpuT = hub.Add("cpu/ryzen/0", HardwareKind.Cpu, SensorKind.Temperature, "Core (Tctl/Tdie)");
var gpuT = hub.Add("gpu/rtx/0", HardwareKind.Gpu, SensorKind.Temperature, "GPU Core");
var hot = hub.Add("gpu/rtx/0", HardwareKind.Gpu, SensorKind.Temperature, "GPU Hot Spot");
var load = hub.Add("cpu/ryzen/0", HardwareKind.Cpu, SensorKind.Load, "CPU Total");
var vram = hub.Add("gpu/rtx/0", HardwareKind.Gpu, SensorKind.SmallData, "GPU Memory Used");
// --style vertical (default) | horizontal | compact | rich (shadowed text, two columns, multi-value bars / grid / rows)
var style = ArgStr("--style") ?? "vertical";
var cores = Enumerable.Range(0, 8).Select(i => hub.Add("cpu/ryzen/0", HardwareKind.Cpu, SensorKind.Load, $"CPU Core #{i + 1}")).ToArray();
var clocks = Enumerable.Range(0, 8).Select(i => hub.Add("cpu/ryzen/0", HardwareKind.Cpu, SensorKind.Clock, $"Core #{i + 1}")).ToArray();
var fans = Enumerable.Range(0, 3).Select(i => hub.Add("mb/x870e/0", HardwareKind.Motherboard, SensorKind.Fan, $"Fan #{i + 1}")).ToArray();
var layout = new OverlayLayout { Enabled = true };
if (style == "rich")
{
    OverlayItem Cell(int row, int col, params SensorDescriptor[] s) => new() { SensorIds = [.. s.Select(d => d.Id)], Row = row, Column = col };
    layout.Style = new OverlayStyle { TextShadow = true };
    layout.Cells =
    [
        Cell(0, 0, fps), Cell(0, 1, cpuT), Cell(1, 0, gpuT), Cell(1, 1, hot),
        Cell(2, 0, cores), Cell(3, 0, clocks), Cell(4, 0, load), Cell(4, 1, vram), Cell(5, 0, fans),
    ];
    layout.Cells[0].ShowGraph = true;
    layout.Cells[5].MultiDisplay = MultiValueDisplay.Grid;
    layout.Cells[8].MultiDisplay = MultiValueDisplay.Rows;
    layout.Normalize();
}
else
{
    layout.Style = new OverlayStyle
    {
        Arrangement = style switch { "horizontal" => OverlayArrangement.Horizontal, "compact" => OverlayArrangement.Compact, _ => OverlayArrangement.Vertical },
    };
    layout.Items = [.. new[] { fps, cpuT, gpuT, hot, load, vram }.Select(s => new OverlayItem { SensorIds = [s.Id] })];
    layout.Normalize(); // a row per item (vertical), one row of cells otherwise
    layout.Cells[0].ShowGraph = true;
}
if (windows == 2)
{
    // the second window takes the last three rows
    layout.SetSecondWindow(true);
    foreach (var cell in layout.Cells.Where(c => c.Row >= 3).ToList())
        layout.MoveCell(cell.Id, new GridDrop(OverlayLayout.SecondWindowId, GridDropKind.NewRow, OverlayGrid.RowCount(layout.Second.Cells), 0));
}

var tick = 0;
void Step()
{
    tick++;
    hub.Set(fps, 140 + tick % 9);
    hub.Set(cpuT, 60 + tick % 7);
    hub.Set(gpuT, 70 + tick % 5);
    hub.Set(hot, 85 + tick % 11);
    hub.Set(load, 30 + tick % 13);
    hub.Set(vram, 9000 + tick * 37 % 900);
    for (var i = 0; i < 8; i++)
    {
        hub.Set(cores[i], (tick * (i + 3) * 7 + i * 11) % 100);
        hub.Set(clocks[i], 4500 + (tick * 13 + i * 37) % 900);
    }
    for (var i = 0; i < 3; i++) hub.Set(fans[i], 900 + (tick * 17 + i * 101) % 600);
}
Step();

// Warm the runtime bits the app has anyway (BCL, Core, LINQ) before the baseline.
_ = layout.AllItems().Select(i => i.Id).ToList();
_ = string.Format(CultureInfo.InvariantCulture, "{0}", 1.5);
var proc = Process.GetCurrentProcess();
Settle();
proc.Refresh();
var ws0 = proc.WorkingSet64;
var pb0 = proc.PrivateMemorySize64;
var pws0 = PrivateWorkingSet();
var threads0 = Threads.Read().Count;

var host = new OverlayHost(new OverlayHostOptions { NeverShow = !show, PreferWarp = warp });
host.Start();
var overlayTid = host.Invoke(() => (long)Native.GetCurrentThreadId());
host.Apply(layout, OverlayTheme.Default, new OverlayContext { Hub = hub, Animations = false });
// --state on (default) | hidden ("only in games", no game in front) | off (switched off after it was on)
var state = ArgStr("--state") ?? "on";
if (state != "on")
{
    Thread.Sleep(1500); // it was on and drawn
    var changed = LayoutCopy.Clone(layout);
    if (state == "hidden") changed.Visibility = OverlayVisibility.GamesOnly;
    else changed.Enabled = false;
    host.Apply(changed, OverlayTheme.Default, new OverlayContext { Hub = hub, Animations = false });
}

// Thread CPU in cycles (exact, unlike tick-granular thread times), converted with a calibration spin on that thread.
var cyclesPerMs = host.Invoke(() =>
{
    var c0 = Cycles();
    var t = Stopwatch.StartNew();
    while (t.ElapsedMilliseconds < 200) { }
    return (long)((Cycles() - c0) / t.Elapsed.TotalMilliseconds);
});
long ThreadCpu100ns() => (long)(host.Invoke(() => (long)Cycles()) / (double)cyclesPerMs * 1e4);
double ProcessCpuMs() => ProcessCycles() / (double)cyclesPerMs;

// Steady state at 1 s: values change every second like a real engine tick.
using var timer = new Timer(_ => Step(), null, 1000, 1000);
Thread.Sleep(3000);
var cpuStart = ThreadCpu100ns();
var r0 = host.Diagnostics().Renders;
var procCpu0 = ProcessCpuMs();
var sw0 = Threads.Read();
var cy0 = Threads.AllCycles();
var wall = Stopwatch.StartNew();
Thread.Sleep(Math.Max(5, seconds) * 1000);
var sw1 = Threads.Read();
var cpuEnd = ThreadCpu100ns();
wall.Stop();
var procCpuMs = ProcessCpuMs() - procCpu0;
var steadyRenders = host.Diagnostics().Renders - r0;
var threads1 = sw1.Count;
var (overlayWakes, otherWakes, workerLines) = Threads.Wakeups(sw0, sw1, overlayTid, wall.Elapsed.TotalSeconds, cyclesPerMs, cy0);
Settle();
proc.Refresh();
var ws1 = proc.WorkingSet64;
var pb1 = proc.PrivateMemorySize64;
var pws1 = PrivateWorkingSet();

// Cost of one refresh (every one dirty: format + draw + present), all on the overlay thread: its CPU, the whole
// process's CPU (WARP worker threads included) and the overlay thread's managed allocations.
timer.Change(Timeout.Infinite, Timeout.Infinite);
var diagEnd = host.Diagnostics();
// (by name: the harness also builds against older sources for before / after numbers)
var hooks = diagEnd.GetType().GetProperty("ForegroundHooks")?.GetValue(diagEnd) ?? "always (older build)";
var stateLine = $"state {state}: shown={diagEnd.Shown}, surface ready={diagEnd.SurfaceReady}, foreground hooks={hooks}, refresh timer={diagEnd.RefreshRunning}";
if (state != "on")
{
    var quick = string.Join(Environment.NewLine,
    [
        $"device: {(warp ? "WARP" : "hardware")}, windows: {windows}, style {style}, {stateLine}, idle run {wall.Elapsed.TotalSeconds:F1} s",
        $"private working set: {Mb(pws0)} → {Mb(pws1)} MB (+{Mb(pws1 - pws0)} MB) · private bytes +{Mb(pb1 - pb0)} MB · threads {threads0} → {threads1}",
        $"overlay thread: {overlayWakes / wall.Elapsed.TotalSeconds:F2} wake-ups/s, {(cpuEnd - cpuStart) / 1e4:F1} ms CPU · whole process {procCpuMs:F1} ms CPU",
        "threads that woke (wake-ups/s, CPU ms/s):",
        .. workerLines,
    ]);
    Console.WriteLine(quick);
    if (outFile is not null) File.WriteAllText(outFile, quick + Environment.NewLine);
    host.Dispose();
    return 0;
}
const int n = 300;
var tickFn = typeof(OverlayHost).GetMethod("RefreshTick", BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Func<bool>>(host);
host.Invoke(() => { tickFn(); return 0L; }); // warm
var f0 = Threads.Read();
var fc0 = Threads.AllCycles();
var (frameThreadMs, frameProcMs, frameAlloc) = host.Invoke(() =>
{
    var a0 = GC.GetAllocatedBytesForCurrentThread();
    var c0 = Cycles();
    var p0 = ProcessCycles();
    for (var i = 0; i < n; i++)
    {
        Step();
        tickFn();
    }
    var c1 = Cycles();
    var p1 = ProcessCycles();
    var a1 = GC.GetAllocatedBytesForCurrentThread();
    return ((c1 - c0) / (double)cyclesPerMs / n, (p1 - p0) / (double)cyclesPerMs / n, (a1 - a0) / (double)n);
});

var f1 = Threads.Read();
var (_, _, frameLines) = Threads.Wakeups(f0, f1, overlayTid, n, cyclesPerMs, fc0);
string? stormLine = null;
if (storm) stormLine = Storm.Measure(host, overlayTid, cyclesPerMs);

if (readback is not null)
{
    // fresh scenes (no sparkline history, column widths from scratch) and a fixed run of values: two builds draw the same picture
    // (one step on the overlay thread: no refresh timer tick can add a sparkline sample in between)
    var applyCore = typeof(OverlayHost).GetMethod("ApplyCore", BindingFlags.NonPublic | BindingFlags.Instance)!;
    var (bmp, second) = host.Invoke(() =>
    {
        tick = 999;
        Step(); // the values the new scenes start from
        applyCore.Invoke(host, [LayoutCopy.Clone(layout), OverlayTheme.Default, new OverlayContext { Hub = hub, Animations = false }]);
        for (var k = 0; k < 12; k++)
        {
            tick = 1000 + k;
            Step();
            hub.Set(cpuT, 63.4f + k % 5);
            tickFn();
        }
        return (host.ReadBackOnThread(OverlayLayout.MainWindowId), windows == 2 ? host.ReadBackOnThread(OverlayLayout.SecondWindowId) : null);
    });
    if (bmp is null) Console.Error.WriteLine("readback failed");
    else Offscreen.WritePng(bmp, readback);
    if (second is not null) Offscreen.WritePng(second, Path.ChangeExtension(readback, null) + "-second.png");
}

var cpus = Environment.ProcessorCount;
var steadyCpuMs = (cpuEnd - cpuStart) / 1e4;
var secs = wall.Elapsed.TotalSeconds;
var lines = new List<string>
{
    $"device: {(warp ? "WARP" : "hardware")}, cores: {cpus}, windows: {windows} ({string.Join(" + ", layout.Windows().Select(w => w.Cells.Count))} items), steady run: {secs:F1} s at 1 s refresh, style {style}",
    $"private working set (Task Manager 'Memory'): {Mb(pws0)} → {Mb(pws1)} MB (+{Mb(pws1 - pws0)} MB)",
    $"working set (incl. shared DLL pages): {Mb(ws0)} → {Mb(ws1)} MB (+{Mb(ws1 - ws0)} MB)",
    $"private bytes: {Mb(pb0)} → {Mb(pb1)} MB (+{Mb(pb1 - pb0)} MB)",
    $"threads: {threads0} → {threads1} (+{threads1 - threads0}; the overlay thread + graphics workers + the sensor timer)",
    $"frames rendered in the steady run: {steadyRenders}",
    $"overlay thread CPU, steady: {steadyCpuMs:F1} ms over {secs:F1} s = {steadyCpuMs / wall.Elapsed.TotalMilliseconds * 100:F3} % of one core = {steadyCpuMs / wall.Elapsed.TotalMilliseconds * 100 / cpus:F4} % of the machine",
    $"whole process CPU, steady (all threads incl. WARP workers and the fake-sensor timer): {procCpuMs:F1} ms = {procCpuMs / wall.Elapsed.TotalMilliseconds * 100 / cpus:F4} % of the machine",
    $"wake-ups, steady: overlay thread {overlayWakes / secs:F2}/s, all other threads {otherWakes / secs:F2}/s",
    $"one dirty refresh (format + draw + present, on the overlay thread): {frameThreadMs:F3} ms overlay thread CPU, {frameProcMs:F3} ms whole process CPU, {frameAlloc:F0} B allocated",
};
if (stormLine is not null) lines.Add(stormLine);
lines.Add("threads that woke in the steady run (wake-ups/s, CPU ms/s):");
lines.AddRange(workerLines);
lines.Add($"threads that woke during {n} back-to-back dirty refreshes (wake-ups and CPU ms per frame):");
lines.AddRange(frameLines);
var report = string.Join(Environment.NewLine, lines);
Console.WriteLine(report);
if (outFile is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
    File.WriteAllText(outFile, report + Environment.NewLine);
}
host.Dispose();
return 0;

static long PrivateWorkingSet()
{
    var c = new Native.MemoryCountersEx2 { Cb = (uint)Marshal.SizeOf<Native.MemoryCountersEx2>() };
    unsafe
    {
        Native.K32GetProcessMemoryInfo(Process.GetCurrentProcess().Handle, &c, c.Cb);
    }
    return (long)c.PrivateWorkingSetSize;
}

static ulong Cycles()
{
    ulong c;
    unsafe
    {
        Native.QueryThreadCycleTime(Native.GetCurrentThread(), &c);
    }
    return c;
}

static ulong ProcessCycles()
{
    ulong c;
    unsafe
    {
        Native.QueryProcessCycleTime(Native.GetCurrentProcess(), &c);
    }
    return c;
}

static void Settle()
{
    for (var i = 0; i < 3; i++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
    Thread.Sleep(500);
}

static string Mb(long bytes) => (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture);

int ArgInt(string name, int def)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : def;
}

string? ArgStr(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

/// <summary>The location-change storm: a helper process on this (hidden) desktop moves a child window of its own.</summary>
internal static class Storm
{
    public static string Measure(OverlayHost host, long overlayTid, long cyclesPerMs)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!, "--storm-child 14") { RedirectStandardOutput = true, UseShellExecute = false };
        using var child = Process.Start(psi)!;
        try
        {
            var ready = child.StandardOutput.ReadLine();
            if (ready is null || !ready.StartsWith("READY ", StringComparison.Ordinal)) return $"storm: helper failed ({ready ?? "no output"})";
            var hwnd = (nint)long.Parse(ready[6..], CultureInfo.InvariantCulture);
            var e0 = host.TrackForegroundForTest(hwnd);
            Thread.Sleep(1000);
            var e1 = host.TrackForegroundForTest(0);
            var t0 = Threads.Read();
            var c0 = host.Invoke(() => (long)Cycles());
            var sw = Stopwatch.StartNew();
            Thread.Sleep(10_000);
            var t1 = Threads.Read();
            var c1 = host.Invoke(() => (long)Cycles());
            var e2 = host.TrackForegroundForTest(0);
            var secs = sw.Elapsed.TotalSeconds;
            var (wakes, _, _) = Threads.Wakeups(t0, t1, overlayTid, secs);
            var cpuMs = (c1 - c0) / (double)cyclesPerMs;
            var done = child.StandardOutput.ReadLine();
            return string.Create(CultureInfo.InvariantCulture,
                $"location-change storm ({done ?? "?"}): overlay thread {wakes / secs:F1} wake-ups/s, {cpuMs / secs / 10:F3} % of one core, {(e2 - e1) / secs:F0} events/s delivered to the hook (first second {e1 - e0})");
        }
        finally
        {
            if (!child.HasExited) child.Kill();
        }
    }

    private static ulong Cycles()
    {
        ulong c;
        unsafe
        {
            Native.QueryThreadCycleTime(Native.GetCurrentThread(), &c);
        }
        return c;
    }
}

/// <summary>The storm helper process: a popup with a child window that moves ~500 times a second.</summary>
internal static unsafe class StormChild
{
    public static int Run()
    {
        if (!Desk.IsPrivate()) return 3; // never on the user's desktop
        var seconds = 14;
        var inst = Native.GetModuleHandle(null);
        nint top, kid;
        fixed (char* cls = "STATIC")
        fixed (char* title = "storm")
        {
            top = Native.CreateWindowEx(0x80 /* TOOLWINDOW */, cls, title, 0x90000000 /* POPUP | VISIBLE */, 0, 0, 200, 100, 0, 0, inst, 0);
            kid = Native.CreateWindowEx(0, cls, title, 0x50000000 /* CHILD | VISIBLE */, 0, 0, 20, 20, top, 0, inst, 0);
        }
        if (top == 0 || kid == 0) return 4;
        Console.WriteLine($"READY {(long)top}");
        Console.Out.Flush();
        var sw = Stopwatch.StartNew();
        long moves = 0;
        var msg = stackalloc byte[64];
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            Native.SetWindowPos(kid, 0, (int)(moves % 150), 10, 0, 0, 0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE
            moves++;
            while (Native.PeekMessage(msg, 0, 0, 0, 1)) Native.DispatchMessage(msg);
            var next = moves * 2.0; // 2 ms
            var wait = next - sw.Elapsed.TotalMilliseconds;
            if (wait >= 1) Thread.Sleep((int)wait);
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{moves / sw.Elapsed.TotalSeconds:F0} moves/s"));
        Native.DestroyWindow(top);
        return 0;
    }
}

/// <summary>The thread's desktop is not the user's "Default" desktop.</summary>
internal static unsafe class Desk
{
    public static bool IsPrivate()
    {
        var desk = Native.GetThreadDesktop(Native.GetCurrentThreadId());
        var name = stackalloc char[256];
        uint needed;
        if (desk == 0 || !Native.GetUserObjectInformation(desk, 2 /* UOI_NAME */, name, 512, &needed)) return false;
        return !string.Equals(new string(name), "Default", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Per-thread CPU and context switches (= wake-ups) of this process from NtQuerySystemInformation.</summary>
internal static unsafe class Threads
{
    public static Dictionary<long, (long Cpu, long Switches)> Read()
    {
        var result = new Dictionary<long, (long, long)>();
        var pid = Environment.ProcessId;
        var size = 4 << 20;
        for (var attempt = 0; attempt < 6; attempt++, size *= 2)
        {
            var buffer = NativeMemory.Alloc((nuint)size);
            try
            {
                var status = Native.NtQuerySystemInformation(5, buffer, size, out _);
                if (status == unchecked((int)0xC0000004)) continue;
                if (status < 0) return result;
                var p = (byte*)buffer;
                while (true)
                {
                    var next = *(uint*)p;
                    if ((long)*(nint*)(p + 80) == pid)
                    {
                        var count = *(uint*)(p + 4);
                        var t = p + 256;
                        for (var i = 0; i < count; i++, t += 80)
                            result[(long)*(nint*)(t + 48)] = (*(long*)t + *(long*)(t + 8), *(uint*)(t + 64));
                        return result;
                    }
                    if (next == 0) return result;
                    p += next;
                }
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }
        return result;
    }

    /// <summary>
    /// Wake-ups per thread between two snapshots (per <paramref name="per"/>: seconds or frames) and, when
    /// <paramref name="cyclesPerMs"/> is given, each thread's exact CPU from its cycle counter (sampled now: a thread's
    /// cycles in the window are read as its total minus the first snapshot's).
    /// </summary>
    public static (long Overlay, long Others, List<string> Lines) Wakeups(Dictionary<long, (long Cpu, long Switches)> a, Dictionary<long, (long Cpu, long Switches)> b,
        long overlayTid, double per, long cyclesPerMs = 0, Dictionary<long, ulong>? cycles0 = null)
    {
        long overlay = 0, others = 0;
        var lines = new List<string>();
        foreach (var (tid, now) in b.OrderByDescending(kv => kv.Value.Switches - a.GetValueOrDefault(kv.Key).Switches))
        {
            var old = a.GetValueOrDefault(tid);
            var s = now.Switches - old.Switches;
            if (tid == overlayTid) overlay += s;
            else others += s;
            if (s <= 0) continue;
            var cpu = cycles0 is not null && cyclesPerMs > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{(CyclesOf(tid) - cycles0.GetValueOrDefault(tid)) / (double)cyclesPerMs / per,8:F3} ms")
                : string.Create(CultureInfo.InvariantCulture, $"{(now.Cpu - old.Cpu) / 1e4,7:F1} ms");
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"  #{tid,-6} {s / per,7:F2} {cpu}  {(tid == overlayTid ? "overlay thread" : NameOf(tid))}"));
        }
        return (overlay, others, lines);
    }

    /// <summary>Cycle counters of every thread of this process now.</summary>
    public static Dictionary<long, ulong> AllCycles() => Read().Keys.ToDictionary(t => t, CyclesOf);

    public static ulong CyclesOf(long tid)
    {
        var h = Native.OpenThread(0x0800, false, (uint)tid);
        if (h == 0) return 0;
        try
        {
            ulong c;
            return Native.QueryThreadCycleTime(h, &c) ? c : 0;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    /// <summary>Thread description or the module its start address lies in.</summary>
    public static string NameOf(long tid)
    {
        var h = Native.OpenThread(0x0040 | 0x0800, false, (uint)tid);
        if (h == 0) return "?";
        try
        {
            var name = "";
            if (Native.GetThreadDescription(h, out var desc) >= 0 && desc != 0)
            {
                name = Marshal.PtrToStringUni(desc) ?? "";
                Native.LocalFree(desc);
            }
            nint start = 0;
            if (Native.NtQueryInformationThread(h, 9, &start, sizeof(nint), 0) >= 0 && start != 0 && Native.GetModuleHandleEx(4 | 2, start, out var module) && module != 0)
            {
                var chars = stackalloc char[260];
                var n = Native.GetModuleFileName(module, chars, 260);
                var file = Path.GetFileName(new string(chars, 0, (int)n));
                name = name.Length == 0 ? file : $"{name} [{file}]";
            }
            return name.Length == 0 ? "?" : name;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }
}

internal static unsafe partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryCountersEx2
    {
        public uint Cb, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage,
            PrivateWorkingSetSize, SharedCommitUsage;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool K32GetProcessMemoryInfo(nint process, MemoryCountersEx2* counters, uint cb);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryThreadCycleTime(nint thread, ulong* cycles);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryProcessCycleTime(nint process, ulong* cycles);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandle(string? name);

    [LibraryImport("kernel32.dll")]
    public static partial nint OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);

    [LibraryImport("kernel32.dll")]
    public static partial int GetThreadDescription(nint thread, out nint description);

    [LibraryImport("kernel32.dll")]
    public static partial nint LocalFree(nint mem);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetModuleHandleEx(uint flags, nint address, out nint module);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleFileNameW")]
    public static partial uint GetModuleFileName(nint module, char* name, uint size);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQueryInformationThread(nint thread, int infoClass, void* buffer, int length, nint returned);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQuerySystemInformation(int infoClass, void* buffer, int length, out int needed);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true)]
    public static partial nint CreateWindowEx(uint exStyle, char* className, char* windowName, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessage(void* msg, nint hwnd, uint min, uint max, uint remove);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(void* msg);

    [LibraryImport("user32.dll")]
    public static partial nint GetThreadDesktop(uint threadId);

    [LibraryImport("user32.dll", EntryPoint = "GetUserObjectInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetUserObjectInformation(nint obj, int index, void* info, uint length, uint* needed);
}

/// <summary>Fake sensor hub (values written straight into the frame).</summary>
internal sealed class HarnessHub : ISensorHub
{
    private static readonly PropertyInfo CountProp = typeof(SensorFrame).GetProperty(nameof(SensorFrame.Count))!;
    private static readonly Action<SensorFrame, int> SetSequence = typeof(SensorFrame).GetProperty(nameof(SensorFrame.Sequence))!.GetSetMethod(true)!.CreateDelegate<Action<SensorFrame, int>>();
    private static readonly PropertyInfo IndexProp = typeof(SensorDescriptor).GetProperty(nameof(SensorDescriptor.Index))!;
    private readonly List<SensorDescriptor> _sensors = [];
    private readonly SensorFrame _frame = new(64);

    public SensorDescriptor Add(string hw, HardwareKind hk, SensorKind kind, string name)
    {
        var d = new SensorDescriptor { Id = SensorIds.Sensor(hw, kind, name), HardwareId = hw, Name = name, Kind = kind, HardwareKind = hk };
        IndexProp.SetValue(d, _sensors.Count);
        _sensors.Add(d);
        CountProp.SetValue(_frame, _sensors.Count);
        return d;
    }

    /// <summary>Allocation-free (no reflection boxing): the per-frame allocation count measures the overlay alone.</summary>
    public void Set(SensorDescriptor d, float v)
    {
        _frame.Values[d.Index] = v;
        SetSequence(_frame, _frame.Sequence + 1);
    }

    public IReadOnlyList<HardwareDescriptor> Hardware => [];
    public IReadOnlyList<SensorDescriptor> Sensors => _sensors;
    public bool IsPresent(int index) => index < _sensors.Count;
    public SensorDescriptor? Find(string sensorId) => _sensors.FirstOrDefault(s => s.Id == sensorId);
    public HardwareDescriptor? FindHardware(string hardwareId) => null;
    public SensorFrame Latest => _frame;
    public SessionStats Stats { get; } = new();
    public IDemand Demand(string consumer, IEnumerable<string> sensorIds, TimeSpan interval) => new NoDemand();
    public event Action<SensorFrame>? FrameReady { add { } remove { } }
    public event Action? InventoryChanged { add { } remove { } }
    public IReadOnlyList<(string Source, SourceStatus Status)> SourceStatuses => [];

    private sealed class NoDemand : IDemand
    {
        public void Update(IEnumerable<string> sensorIds, TimeSpan interval) { }
        public void Dispose() { }
    }
}
