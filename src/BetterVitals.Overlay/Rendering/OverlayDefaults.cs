using BetterVitals.Core.Sensors;

namespace BetterVitals.Overlay.Rendering;

/// <summary>Overlay-specific number conventions (the panel shows more decimals than an OSD needs).</summary>
internal static class OverlayDefaults
{
    /// <summary>Decimals on the overlay; null = the formatter decides (throughput auto-scales).</summary>
    public static int? Decimals(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => 0,
        SensorKind.Load => 0,
        SensorKind.Clock => 0,
        SensorKind.Power => 0,
        SensorKind.Fan => 0,
        SensorKind.Control => 0,
        SensorKind.Level => 0,
        SensorKind.Fps => 0,
        SensorKind.FrameTime => 1,
        SensorKind.Voltage => 3,
        SensorKind.Current => 1,
        SensorKind.Data => 1,
        SensorKind.SmallData => 1,
        SensorKind.Throughput => null,
        SensorKind.Frequency => 0,
        _ => 1,
    };

    /// <summary>A value with the typical number of digits, for stable column widths before real values arrive.</summary>
    public static float Seed(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => 88,
        SensorKind.Load or SensorKind.Level or SensorKind.Control or SensorKind.Humidity => 100,
        SensorKind.Clock or SensorKind.Fan => 8888,
        SensorKind.Fps => 888,
        SensorKind.FrameTime => 88.8f,
        SensorKind.Power => 888,
        SensorKind.Voltage => 1.888f,
        SensorKind.Data => 88.8f,
        SensorKind.SmallData => 88.8f * 1024,
        SensorKind.Throughput => 888.8e6f,
        _ => 88.8f,
    };

    /// <summary>Lower / upper bound for bar heights by kind (null upper = running max).</summary>
    public static (float Min, float? Max) BarRange(SensorKind kind) => kind switch
    {
        SensorKind.Load or SensorKind.Level or SensorKind.Control or SensorKind.Humidity => (0, 100),
        SensorKind.Temperature => (20, null),
        _ => (0, null),
    };
}
