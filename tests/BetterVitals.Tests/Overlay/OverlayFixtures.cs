using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Overlay;

namespace BetterVitals.Tests.Overlay;

/// <summary>Sensors, layouts and themes shared by the overlay tests.</summary>
internal static class OverlayFixtures
{
    public const string CpuHw = "cpu/amd-ryzen-7-9800x3d/0";
    public const string GpuHw = "gpu/nvidia-geforce-rtx-5080/0";
    public const string GameHw = "game/foreground/0";

    public static SensorDescriptor Sensor(string hw, HardwareKind hk, SensorKind kind, string name, string unit = "")
        => new()
        {
            Id = SensorIds.Sensor(hw, kind, name),
            HardwareId = hw,
            Name = name,
            Kind = kind,
            HardwareKind = hk,
            Unit = unit,
        };

    public static readonly SensorDescriptor Fps = Sensor(GameHw, HardwareKind.Game, SensorKind.Fps, "FPS");
    public static readonly SensorDescriptor Low1 = Sensor(GameHw, HardwareKind.Game, SensorKind.Fps, "1% Low");
    public static readonly SensorDescriptor FrameTime = Sensor(GameHw, HardwareKind.Game, SensorKind.FrameTime, "Frametime");
    public static readonly SensorDescriptor CpuTemp = Sensor(CpuHw, HardwareKind.Cpu, SensorKind.Temperature, "Core (Tctl/Tdie)");
    public static readonly SensorDescriptor CpuLoad = Sensor(CpuHw, HardwareKind.Cpu, SensorKind.Load, "CPU Total");
    public static readonly SensorDescriptor CpuPower = Sensor(CpuHw, HardwareKind.Cpu, SensorKind.Power, "Package");
    public static readonly SensorDescriptor GpuTemp = Sensor(GpuHw, HardwareKind.Gpu, SensorKind.Temperature, "GPU Core");
    public static readonly SensorDescriptor HotSpot = Sensor(GpuHw, HardwareKind.Gpu, SensorKind.Temperature, "GPU Hot Spot");
    public static readonly SensorDescriptor MemJunction = Sensor(GpuHw, HardwareKind.Gpu, SensorKind.Temperature, "GPU Memory Junction");
    public static readonly SensorDescriptor GpuLoad = Sensor(GpuHw, HardwareKind.Gpu, SensorKind.Load, "GPU Core");
    public static readonly SensorDescriptor GpuClock = Sensor(GpuHw, HardwareKind.Gpu, SensorKind.Clock, "GPU Core");
    public static readonly SensorDescriptor Vram = Sensor(GpuHw, HardwareKind.Gpu, SensorKind.SmallData, "GPU Memory Used");
    public static readonly SensorDescriptor Ram = Sensor("memory/generic-memory/0", HardwareKind.Memory, SensorKind.Data, "Memory Used");
    public static readonly SensorDescriptor Nvme = Sensor("storage/kingston-skc3000d2048g/0", HardwareKind.Storage, SensorKind.Temperature, "Composite Temperature");

    public static SensorDescriptor[] CoreClocks(int n) =>
        Enumerable.Range(1, n).Select(i => Sensor(CpuHw, HardwareKind.Cpu, SensorKind.Clock, $"Core #{i}")).ToArray();

    public static SensorDescriptor[] CoreLoads(int n) =>
        Enumerable.Range(1, n).Select(i => Sensor(CpuHw, HardwareKind.Cpu, SensorKind.Load, $"Core #{i}")).ToArray();

    public static OverlayItem Item(params SensorDescriptor[] sensors) => new() { SensorIds = [.. sensors.Select(s => s.Id)] };

    /// <summary>A deterministic wavy history around <paramref name="center"/>.</summary>
    public static float[] Wave(float center, float amplitude, int n = 40, int seed = 1)
    {
        var r = new Random(seed);
        var a = new float[n];
        for (var i = 0; i < n; i++)
            a[i] = center + amplitude * (float)(Math.Sin(i * 0.35 + seed) * 0.7 + (r.NextDouble() - 0.5) * 0.6);
        return a;
    }

    public static OverlayTheme Light { get; } = new()
    {
        Background = new Rgba(0xF7, 0xF8, 0xFB),
        Border = new Rgba(0x10, 0x18, 0x28, 0x24),
        Text = new Rgba(0x14, 0x18, 0x24),
        TextDim = new Rgba(0x5B, 0x64, 0x78),
        Warning = new Rgba(0xD9, 0x77, 0x06),
        Danger = new Rgba(0xDC, 0x26, 0x26),
        Category = new Dictionary<HardwareKind, Rgba>
        {
            [HardwareKind.Cpu] = new(0x6D, 0x4A, 0xFF),
            [HardwareKind.Gpu] = new(0x08, 0x91, 0xB2),
            [HardwareKind.Memory] = new(0xDB, 0x27, 0x77),
            [HardwareKind.Motherboard] = new(0xB4, 0x53, 0x09),
            [HardwareKind.Storage] = new(0x4D, 0x7C, 0x0F),
            [HardwareKind.Network] = new(0x25, 0x63, 0xEB),
            [HardwareKind.Game] = new(0x05, 0x96, 0x69),
        },
    };

    /// <summary>tests/output/overlay (gitignored), found from the test assembly location.</summary>
    public static string OutputDir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BetterVitals.sln"))) dir = dir.Parent;
            var root = dir?.FullName ?? AppContext.BaseDirectory;
            var path = Path.Combine(root, "tests", "output", "overlay");
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
