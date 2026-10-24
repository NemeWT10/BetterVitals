using BetterVitals.Core.Sensors;

namespace BetterVitals.UI.Dev;

/// <summary>
/// A big fake PC for the UI probe (~370 sensors + derived, like an elevated desktop with a 32-core CPU, two GPUs and four
/// drives): the cost of the pages and the panel's memory at the sensor counts real machines report. Values are smooth
/// waves of time only. Allocation-free updates.
/// </summary>
internal sealed class WideFakeSource : ISensorSource
{
    private sealed record Wave(float Base, float Amp, float Period, float Phase);

    public string Name => "fake";

    public SourceStatus Status => new(SourceState.Ok);

    public event Action? InventoryChanged
    {
        add { }
        remove { }
    }

    public SourceInventory Discover()
    {
        var hw = new List<HardwareDescriptor>();
        var sensors = new List<SensorDescriptor>();
        var order = 0;
        string id = "";
        HardwareKind kind = default;

        void Hw(HardwareKind k, string name, int ordinal = 0)
        {
            kind = k;
            id = SensorIds.Hardware(k, name, ordinal);
            hw.Add(new HardwareDescriptor { Id = id, Name = name, Kind = k, Source = "fake" });
        }

        void S(SensorKind k, string name, float b, float amp, float period = 30)
        {
            sensors.Add(new SensorDescriptor
            {
                Id = SensorIds.Sensor(id, k, name),
                HardwareId = id,
                HardwareKind = kind,
                Name = name,
                Kind = k,
                Order = order,
                SourceTag = new Wave(b, amp, period + order % 7, order * 0.37f),
            });
            order++;
        }

        Hw(HardwareKind.Cpu, "Probe Threadripper 32-Core");
        S(SensorKind.Temperature, "Core (Tctl/Tdie)", 62, 9);
        for (var i = 1; i <= 4; i++) S(SensorKind.Temperature, $"CCD{i} (Tdie)", 58, 8);
        for (var i = 1; i <= 32; i++)
        {
            S(SensorKind.Clock, $"Core #{i}", 4800, 300, 9);
            S(SensorKind.Clock, $"Core #{i} (Effective)", 4100, 600, 11);
            S(SensorKind.Load, $"CPU Core #{i}", 30, 25, 7);
            S(SensorKind.Voltage, $"Core #{i} VID", 1.2f, 0.07f, 13);
            S(SensorKind.Temperature, $"Core #{i}", 60, 10, 17);
        }
        for (var i = 1; i <= 64; i++) S(SensorKind.Load, $"CPU Core #{(i + 1) / 2} Thread #{(i - 1) % 2 + 1}", 30, 25, 6);
        S(SensorKind.Load, "CPU Total", 30, 20);
        S(SensorKind.Power, "Package", 180, 60);
        S(SensorKind.Power, "Cores", 140, 50);
        S(SensorKind.Power, "SoC", 25, 3);
        S(SensorKind.Voltage, "Core (SVI3 TFN)", 1.2f, 0.05f);
        S(SensorKind.Voltage, "SoC (SVI3 TFN)", 1.05f, 0.01f);
        S(SensorKind.Clock, "Bus Speed", 100, 0.1f);

        for (var g = 0; g < 2; g++)
        {
            Hw(HardwareKind.Gpu, "Probe GeForce RTX 9090", g);
            foreach (var n in new[] { "GPU Core", "GPU Hot Spot", "GPU Memory Junction" }) S(SensorKind.Temperature, n, 65, 10);
            for (var i = 1; i <= 6; i++) S(SensorKind.Temperature, $"GPU Hot Spot #{i}", 75, 8);
            foreach (var n in new[] { "GPU Core", "GPU Memory", "GPU Video", "GPU Shader" }) S(SensorKind.Clock, n, 2600, 200);
            foreach (var n in new[] { "GPU Core", "GPU Memory Controller", "GPU Video Engine", "GPU Bus", "D3D 3D", "D3D Copy" }) S(SensorKind.Load, n, 50, 40);
            foreach (var n in new[] { "GPU Memory Used", "GPU Memory Free", "GPU Memory Total" }) S(SensorKind.SmallData, n, 9000, 2000);
            S(SensorKind.Power, "GPU Package", 300, 80);
            S(SensorKind.Voltage, "GPU Core", 0.95f, 0.05f);
            for (var i = 1; i <= 3; i++)
            {
                S(SensorKind.Fan, $"GPU Fan {i}", 1500, 300);
                S(SensorKind.Control, $"GPU Fan {i}", 45, 10);
            }
        }

        Hw(HardwareKind.Motherboard, "Probe Board X999");
        for (var i = 1; i <= 10; i++) S(SensorKind.Temperature, $"System #{i}", 40, 4, 120);
        for (var i = 1; i <= 8; i++) S(SensorKind.Fan, $"System Fan #{i}", 900, 100, 90);
        foreach (var n in new[] { "Vcore", "+3.3V", "+5V", "+12V", "DRAM", "VBAT", "VSOC", "VDDIO", "VDDP", "VDDG CCD", "VDDG IOD", "CPU VDD18" })
            S(SensorKind.Voltage, n, 1.5f, 0.02f, 120);

        Hw(HardwareKind.Memory, "Generic Memory");
        S(SensorKind.Data, "Memory Used", 30, 4, 120);
        S(SensorKind.Data, "Memory Available", 34, 4, 120);
        S(SensorKind.Load, "Memory", 47, 5, 120);
        S(SensorKind.Data, "Virtual Memory Used", 40, 5, 120);
        S(SensorKind.Load, "Virtual Memory", 50, 5, 120);
        for (var i = 1; i <= 4; i++) S(SensorKind.Temperature, $"DIMM #{i}", 42, 2, 90);

        for (var d = 0; d < 4; d++)
        {
            Hw(HardwareKind.Storage, "Probe NVMe 4TB", d);
            S(SensorKind.Temperature, "Composite Temperature", 45, 4, 120);
            S(SensorKind.Temperature, "Temperature #2", 50, 4, 120);
            S(SensorKind.Load, "Total Activity", 5, 5, 15);
            S(SensorKind.Throughput, "Read Rate", 4e7f, 4e7f, 15);
            S(SensorKind.Throughput, "Write Rate", 6e6f, 6e6f, 15);
            S(SensorKind.Level, "Available Spare", 100, 0, 600);
            S(SensorKind.Level, "Percentage Used", 3, 0, 600);
        }

        for (var n = 0; n < 3; n++)
        {
            Hw(HardwareKind.Network, "Probe Ethernet", n);
            S(SensorKind.Throughput, "Download Speed", 3e6f, 3e6f, 20);
            S(SensorKind.Throughput, "Upload Speed", 2e5f, 2e5f, 20);
            S(SensorKind.Load, "Network Utilization", 2, 2, 20);
            S(SensorKind.Data, "Data Downloaded", 12, 0.1f, 600);
        }
        return new SourceInventory(hw, sensors);
    }

    public void Update(ReadOnlySpan<SensorDescriptor> requested, Span<float> values)
    {
        var t = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        foreach (var d in requested)
        {
            if (d.SourceTag is not Wave w)
            {
                values[d.Index] = float.NaN;
                continue;
            }
            values[d.Index] = (float)Math.Max(0, w.Base + w.Amp * Math.Sin(t * 2 * Math.PI / w.Period + w.Phase));
        }
    }

    public void Dispose()
    {
    }
}
