using System.Globalization;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;

namespace BetterVitals.Core.Formatting;

/// <summary>
/// Turns base-unit values into display text without allocating (span-based), for the overlay hot path and the UI.
/// Culture follows the UI language (PL: decimal comma). Units are never translated; throughput scales B/s → GB/s;
/// temperatures convert to °F when the user chose it.
/// </summary>
public static class ValueFormat
{
    /// <summary>
    /// Formats <paramref name="value"/> into <paramref name="dest"/>; returns the characters written (0 when it does
    /// not fit) and the unit to show. NaN → "—".
    /// </summary>
    public static int Format(SensorKind kind, float value, Span<char> dest, CultureInfo culture, out string unit,
        int? decimals = null, TemperatureUnit temperature = TemperatureUnit.Celsius, string? baseUnit = null)
    {
        unit = baseUnit is { Length: > 0 } ? baseUnit : SensorUnits.Unit(kind);
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            if (dest.Length == 0) return 0;
            dest[0] = '—';
            return 1;
        }

        double v = value;
        var d = decimals ?? SensorUnits.DefaultDecimals(kind);
        switch (kind)
        {
            case SensorKind.Temperature when temperature == TemperatureUnit.Fahrenheit:
                v = v * 9 / 5 + 32;
                unit = "°F";
                break;
            case SensorKind.Throughput:
                (v, unit) = ScaleBytes(v);
                d = decimals ?? (v >= 100 ? 0 : 1);
                break;
            // VRAM and similar: "9285 MB" reads better as "9,1 GB"
            case SensorKind.SmallData when unit == "MB" && Math.Abs(v) >= 1024:
                v /= 1024;
                unit = "GB";
                d = decimals ?? 1;
                break;
        }

        return v.TryFormat(dest, out var written, Fixed(d), culture) ? written : 0;
    }

    /// <summary>Convenience for the UI (allocates the string).</summary>
    public static string ToText(SensorKind kind, float value, CultureInfo culture, out string unit,
        int? decimals = null, TemperatureUnit temperature = TemperatureUnit.Celsius, string? baseUnit = null)
    {
        Span<char> buf = stackalloc char[32];
        var n = Format(kind, value, buf, culture, out unit, decimals, temperature, baseUnit);
        return new string(buf[..n]);
    }

    private static (double Value, string Unit) ScaleBytes(double bytesPerSecond)
    {
        var a = Math.Abs(bytesPerSecond);
        if (a >= 1e9) return (bytesPerSecond / 1e9, "GB/s");
        if (a >= 1e6) return (bytesPerSecond / 1e6, "MB/s");
        if (a >= 1e3) return (bytesPerSecond / 1e3, "KB/s");
        return (bytesPerSecond, "B/s");
    }

    private static string Fixed(int decimals) => decimals switch
    {
        <= 0 => "F0",
        1 => "F1",
        2 => "F2",
        _ => "F3",
    };
}
