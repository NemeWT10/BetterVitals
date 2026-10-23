namespace BetterVitals.Sensors.Native;

/// <summary>
/// Hardware names the way LibreHardwareMonitor shows them, so a node created here gets the same id as the one LHM would
/// create (history and settings survive a switch between basic and full mode).
/// </summary>
internal static class LhmNames
{
    // longest first: "16-Core Processor" must go before "6-Core Processor", which it contains
    private static readonly string[] CpuNoise = new[]
    {
        "(R)", "(TM)", "(tm)", "CPU",
        "Dual-Core Processor", "Triple-Core Processor", "Quad-Core Processor", "Six-Core Processor", "Eight-Core Processor",
        "6-Core Processor", "8-Core Processor", "10-Core Processor", "12-Core Processor", "16-Core Processor", "24-Core Processor",
        "32-Core Processor", "64-Core Processor",
    }.OrderByDescending(n => n.Length).ToArray();

    /// <summary>"AMD Ryzen 7 9800X3D 8-Core Processor" → "AMD Ryzen 7 9800X3D"; "Intel(R) Core(TM) i7-12700K" → "Intel Core i7-12700K".</summary>
    public static string Cpu(string brand)
    {
        var name = brand;
        foreach (var noise in CpuNoise) name = name.Replace(noise, "", StringComparison.Ordinal);
        name = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return name.Length == 0 ? "CPU" : name;
    }
}
