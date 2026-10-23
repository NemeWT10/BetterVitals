using LibreHardwareMonitor.Hardware;

namespace BetterVitals.Tools.NativeProbe;

/// <summary>
/// "--lhm-names": opens LibreHardwareMonitor unelevated (no driver) and lists the hardware names and sensors it gives, to check that
/// the names NativeSource builds ids from are the same, and to see which gaps exist in basic mode.
/// </summary>
internal static class LhmDump
{
    public static void Run(TextWriter w)
    {
        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsStorageEnabled = true,
            IsNetworkEnabled = true,
            IsMotherboardEnabled = false,
            IsControllerEnabled = false,
            IsBatteryEnabled = false,
            IsPsuEnabled = false,
        };
        try
        {
            computer.Open();
            for (var pass = 0; pass < 2; pass++)
            {
                foreach (var hw in computer.Hardware) hw.Update();
                Thread.Sleep(1000);
            }
            foreach (var hw in computer.Hardware)
            {
                Print(w, hw, 0);
            }
        }
        catch (Exception ex)
        {
            w.WriteLine($"LHM failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { computer.Close(); } catch { /* ignored */ }
        }
    }

    private static void Print(TextWriter w, IHardware hw, int depth)
    {
        var pad = new string(' ', depth * 2);
        w.WriteLine($"{pad}[{hw.HardwareType}] '{hw.Name}'");
        foreach (var s in hw.Sensors)
            w.WriteLine($"{pad}  {s.SensorType} '{s.Name}' = {(s.Value.HasValue ? s.Value.Value.ToString("F1") : "null")}");
        foreach (var sub in hw.SubHardware) Print(w, sub, depth + 1);
    }
}
