namespace BetterVitals.Sensors.Native;

/// <summary>
/// Sanity filters for raw driver / firmware values. Drivers fill unsupported fields with 0, 0xFFFF…, or nonsense; a value
/// outside these ranges is reported as "no value" (NaN) instead of a wrong number.
/// </summary>
internal static class Plausible
{
    /// <summary>Drive temperature in °C. 0 is what firmware reports when it has no sensor.</summary>
    public static bool DriveTemperature(int celsius) => celsius is >= 1 and <= 125;

    /// <summary>GPU temperature in deci-Celsius (ADAPTERPERFDATA.Temperature): 5 °C … 150 °C.</summary>
    public static bool GpuTemperatureDeci(uint deci) => deci is >= 50 and <= 1500;

    public static bool FanRpm(uint rpm) => rpm <= 20_000;

    /// <summary>Memory / engine clock in Hz: 50 MHz … 40 GHz (covers GDDR7 data-rate style reports).</summary>
    public static bool ClockHz(ulong hz) => hz is >= 50_000_000 and <= 40_000_000_000;

    /// <summary>Engine voltage in milli-volts (NODEPERFDATA.Voltage): 0.2 V … 2.5 V.</summary>
    public static bool VoltageMilli(uint milliVolts) => milliVolts is >= 200 and <= 2_500;

    /// <summary>ADAPTERPERFDATA.Power in tenths of a percent: 0 … 200 %.</summary>
    public static bool PowerTenthsPercent(uint tenths) => tenths <= 2000;

    /// <summary>SMBIOS memory speed in MT/s.</summary>
    public static bool MemorySpeedMTs(int mts) => mts is >= 100 and <= 32_000;
}
