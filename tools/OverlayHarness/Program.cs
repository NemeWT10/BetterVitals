using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Overlay;

// Overlay cost probe. Usage: OverlayHarness [--gpu] [--seconds N] [--windows 1|2] [--out file]
//   Runs the real overlay host (window + D3D11 + DirectComposition + Direct2D) with fake sensors at the default
//   1 s refresh, values changing every tick, and reports the process working set / private bytes before and after,
//   plus the overlay thread's CPU time. The windows are created but never shown. --windows 2 moves the last three of the
//   six items into the second window (two windows on the one shared device).
//   --show exists only for a person to look at the overlay on their own desktop; automated runs never use it.

var warp = !args.Contains("--gpu");
var show = args.Contains("--show");
var seconds = ArgInt("--seconds", 20);
var windows = Math.Clamp(ArgInt("--windows", 1), 1, OverlayLayout.MaxWindows);
var outFile = ArgStr("--out");

var hub = new HarnessHub();
var fps = hub.Add("game/foreground/0", HardwareKind.Game, SensorKind.Fps, "FPS");
var cpuT = hub.Add("cpu/ryzen/0", HardwareKind.Cpu, SensorKind.Temperature, "Core (Tctl/Tdie)");
var gpuT = hub.Add("gpu/rtx/0", HardwareKind.Gpu, SensorKind.Temperature, "GPU Core");
var hot = hub.Add("gpu/rtx/0", HardwareKind.Gpu, SensorKind.Temperature, "GPU Hot Spot");
var load = hub.Add("cpu/ryzen/0", HardwareKind.Cpu, SensorKind.Load, "CPU Total");
var vram = hub.Add("gpu/rtx/0", HardwareKind.Gpu, SensorKind.SmallData, "GPU Memory Used");
var layout = new OverlayLayout
{
    Enabled = true,
    Items = [.. new[] { fps, cpuT, gpuT, hot, load, vram }.Select(s => new OverlayItem { SensorIds = [s.Id] })],
};
layout.Normalize(); // a row per item
layout.Cells[0].ShowGraph = true;
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

var options = typeof(OverlayHost).Assembly.GetType("BetterVitals.Overlay.OverlayHostOptions")!;
var opt = Activator.CreateInstance(options)!;
options.GetProperty("NeverShow")!.SetValue(opt, !show);
options.GetProperty("PreferWarp")!.SetValue(opt, warp);
var host = (OverlayHost)typeof(OverlayHost).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, [options])!.Invoke([opt]);
host.Start();
host.Apply(layout, OverlayTheme.Default, new OverlayContext { Hub = hub, Animations = false });

var invoke = typeof(OverlayHost).GetMethod("Invoke", BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(typeof(long));
long OnOverlay(Func<long> f) => (long)invoke.Invoke(host, [f, 10000])!;
// Thread CPU in cycles (exact, unlike tick-granular thread times), converted with a calibration spin on that thread.
var cyclesPerMs = OnOverlay(() =>
{
    var c0 = Cycles();
    var t = Stopwatch.StartNew();
    while (t.ElapsedMilliseconds < 200) { }
    return (long)((Cycles() - c0) / t.Elapsed.TotalMilliseconds);
});
long ThreadCpu100ns() => (long)(OnOverlay(() => (long)Cycles()) / (double)cyclesPerMs * 1e4);
var diag = typeof(OverlayHost).GetMethod("Diagnostics", BindingFlags.NonPublic | BindingFlags.Instance)!;
int Renders() => (int)diag.Invoke(host, null)!.GetType().GetProperty("Renders")!.GetValue(diag.Invoke(host, null))!;

// Steady state at 1 s: values change every second like a real engine tick.
using var timer = new Timer(_ => Step(), null, 1000, 1000);
Thread.Sleep(3000);
var cpuStart = ThreadCpu100ns();
var r0 = Renders();
proc.Refresh();
var procCpu0 = proc.TotalProcessorTime;
var wall = Stopwatch.StartNew();
Thread.Sleep(Math.Max(5, seconds) * 1000);
var cpuEnd = ThreadCpu100ns();
wall.Stop();
proc.Refresh();
var procCpuMs = (proc.TotalProcessorTime - procCpu0).TotalMilliseconds;
var steadyRenders = Renders() - r0;
Settle();
proc.Refresh();
var ws1 = proc.WorkingSet64;
var pb1 = proc.PrivateMemorySize64;
var pws1 = PrivateWorkingSet();

// Cost of one refresh (every one dirty: format + draw + present), measured on the overlay thread.
var refresh = typeof(OverlayHost).GetMethod("RefreshNow", BindingFlags.NonPublic | BindingFlags.Instance)!;
timer.Change(Timeout.Infinite, Timeout.Infinite);
const int n = 300;
var c0 = ThreadCpu100ns();
var sw = Stopwatch.StartNew();
for (var i = 0; i < n; i++)
{
    Step();
    refresh.Invoke(host, null);
}
sw.Stop();
var c1 = ThreadCpu100ns();

var cores = Environment.ProcessorCount;
var steadyCpuMs = (cpuEnd - cpuStart) / 1e4;
var report = string.Join(Environment.NewLine,
[
    $"device: {(warp ? "WARP" : "hardware")}, cores: {cores}, windows: {windows} ({string.Join(" + ", layout.Windows().Select(w => w.Cells.Count))} items), steady run: {wall.Elapsed.TotalSeconds:F1} s at 1 s refresh",
    $"private working set (Task Manager 'Memory'): {Mb(pws0)} → {Mb(pws1)} MB (+{Mb(pws1 - pws0)} MB)",
    $"working set (incl. shared DLL pages): {Mb(ws0)} → {Mb(ws1)} MB (+{Mb(ws1 - ws0)} MB)",
    $"private bytes: {Mb(pb0)} → {Mb(pb1)} MB (+{Mb(pb1 - pb0)} MB)",
    $"frames rendered in the steady run: {steadyRenders}",
    $"overlay thread CPU, steady: {steadyCpuMs:F1} ms over {wall.Elapsed.TotalSeconds:F1} s = {steadyCpuMs / wall.Elapsed.TotalMilliseconds * 100:F3} % of one core = {steadyCpuMs / wall.Elapsed.TotalMilliseconds * 100 / cores:F4} % of the machine",
    $"whole process CPU, steady (all threads incl. WARP workers and the fake-sensor timer): {procCpuMs:F0} ms = {procCpuMs / wall.Elapsed.TotalMilliseconds * 100 / cores:F4} % of the machine",
    $"one dirty refresh (format + draw + present): {(c1 - c0) / 1e4 / n:F3} ms CPU, {sw.Elapsed.TotalMilliseconds / n:F3} ms wall (incl. cross-thread invoke)",
]);
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
    public static partial nint GetCurrentThread();
}

/// <summary>Fake sensor hub (values written straight into the frame).</summary>
internal sealed class HarnessHub : ISensorHub
{
    private static readonly PropertyInfo CountProp = typeof(SensorFrame).GetProperty(nameof(SensorFrame.Count))!;
    private static readonly PropertyInfo SeqProp = typeof(SensorFrame).GetProperty(nameof(SensorFrame.Sequence))!;
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

    public void Set(SensorDescriptor d, float v)
    {
        _frame.Values[d.Index] = v;
        SeqProp.SetValue(_frame, _frame.Sequence + 1);
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
