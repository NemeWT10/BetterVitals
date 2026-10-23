namespace BetterVitals.Core.Sensors;

/// <summary>
/// Units and numeric conventions per <see cref="SensorKind"/>. Values travel through the app in these base units:
/// temperatures in °C (°F only at display), throughput in bytes/s (scaled at display), Data in GB, SmallData in MB
/// (LibreHardwareMonitor conventions). Units are never translated.
/// </summary>
public static class SensorUnits
{
    /// <summary>Base unit text for display; Throughput is scaled by the formatter (B/s … GB/s).</summary>
    public static string Unit(SensorKind kind) => kind switch
    {
        SensorKind.Voltage => "V",
        SensorKind.Current => "A",
        SensorKind.Power => "W",
        SensorKind.Clock => "MHz",
        SensorKind.Temperature => "°C",
        SensorKind.Load => "%",
        SensorKind.Frequency => "Hz",
        SensorKind.Fan => "RPM",
        SensorKind.Flow => "L/h",
        SensorKind.Control => "%",
        SensorKind.Level => "%",
        SensorKind.Factor => "",
        SensorKind.Data => "GB",
        SensorKind.SmallData => "MB",
        SensorKind.Throughput => "B/s",
        SensorKind.TimeSpan => "s",
        SensorKind.Timing => "ns",
        SensorKind.Energy => "mWh",
        SensorKind.Noise => "dBA",
        SensorKind.Conductivity => "µS/cm",
        SensorKind.Humidity => "%",
        SensorKind.Fps => "FPS",
        SensorKind.FrameTime => "ms",
        _ => "",
    };

    /// <summary>Decimals shown by default in the panel (the overlay may override per item).</summary>
    public static int DefaultDecimals(SensorKind kind) => kind switch
    {
        SensorKind.Voltage => 3,
        SensorKind.Current => 2,
        SensorKind.Power => 1,
        SensorKind.Clock => 0,
        SensorKind.Temperature => 1,
        SensorKind.Load => 1,
        SensorKind.Frequency => 1,
        SensorKind.Fan => 0,
        SensorKind.Flow => 1,
        SensorKind.Control => 0,
        SensorKind.Level => 1,
        SensorKind.Factor => 2,
        SensorKind.Data => 1,
        SensorKind.SmallData => 0,
        SensorKind.Throughput => 1,
        SensorKind.TimeSpan => 0,
        SensorKind.Timing => 1,
        SensorKind.Energy => 0,
        SensorKind.Noise => 1,
        SensorKind.Conductivity => 1,
        SensorKind.Humidity => 0,
        SensorKind.Fps => 0,
        SensorKind.FrameTime => 1,
        _ => 1,
    };

    /// <summary>
    /// Storage resolution for history (stored integer = round(value / quantum)). Finer than any display precision,
    /// so min / avg / max computed from history equal what the live view showed.
    /// </summary>
    public static double Quantum(SensorKind kind) => kind switch
    {
        SensorKind.Voltage => 0.0005,
        SensorKind.Current => 0.001,
        SensorKind.Power => 0.01,
        SensorKind.Clock => 0.1,
        SensorKind.Temperature => 0.05,
        SensorKind.Load => 0.05,
        SensorKind.Frequency => 0.1,
        SensorKind.Fan => 1,
        SensorKind.Flow => 0.1,
        SensorKind.Control => 0.1,
        SensorKind.Level => 0.05,
        SensorKind.Factor => 0.001,
        SensorKind.Data => 0.001,
        SensorKind.SmallData => 0.1,
        SensorKind.Throughput => 1,
        SensorKind.TimeSpan => 1,
        SensorKind.Timing => 0.1,
        SensorKind.Energy => 1,
        SensorKind.Noise => 0.1,
        SensorKind.Conductivity => 0.1,
        SensorKind.Humidity => 0.1,
        SensorKind.Fps => 0.1,
        SensorKind.FrameTime => 0.01,
        _ => 0.01,
    };
}
