using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Overlay;

namespace BetterVitals.UI.Pages.OverlayEditor;

/// <summary>How an item's value color reacts to the value: the sensor's defaults, own rows, or never.</summary>
internal enum ThresholdMode : byte
{
    /// <summary><see cref="OverlayItem.Thresholds"/> = null: defaults per sensor (CPU 80 / 90 °C…).</summary>
    Default,

    /// <summary>Own value → color rows.</summary>
    Custom,

    /// <summary>Empty list: the value keeps its color.</summary>
    Off,
}

/// <summary>
/// The thresholds editor's rules. Values are stored in base units (°C); the editor shows them in the user's unit.
/// Every edit returns a new list (the working copy and the settings never share a list instance).
/// </summary>
internal static class ThresholdEditing
{
    public static ThresholdMode Mode(OverlayItem item) => item.Thresholds switch
    {
        null => ThresholdMode.Default,
        { Count: 0 } => ThresholdMode.Off,
        _ => ThresholdMode.Custom,
    };

    /// <summary>The defaults of a sensor as editable rows (empty when it has none, e.g. loads).</summary>
    public static List<OverlayThreshold> Defaults(SensorDescriptor? sensor, OverlayTheme theme)
        => sensor is not null && DefaultThresholds.For(sensor) is { } pair ? DefaultThresholds.AsList(pair, theme) : [];

    /// <summary>Rows to start "Custom" with: the sensor's defaults, or two sensible rows for its kind.</summary>
    public static List<OverlayThreshold> Seed(SensorDescriptor? sensor, OverlayTheme theme)
    {
        var d = Defaults(sensor, theme);
        if (d.Count > 0) return d;
        var (warn, danger) = sensor?.Kind switch
        {
            SensorKind.Load or SensorKind.Control or SensorKind.Level => (80f, 95f),
            SensorKind.Temperature => (70f, 85f),
            SensorKind.Fps => (60f, 144f),
            SensorKind.FrameTime => (16.7f, 33.3f),
            _ => (80f, 90f),
        };
        return [new() { Value = warn, Color = theme.Warning.ToString() }, new() { Value = danger, Color = theme.Danger.ToString() }];
    }

    /// <summary>The rows the editor shows for an item (null → defaults).</summary>
    public static List<OverlayThreshold> Effective(OverlayItem item, SensorDescriptor? sensor, OverlayTheme theme)
        => item.Thresholds is { } own ? Copy(own) : Defaults(sensor, theme);

    /// <summary>One more row above the highest: +10 (or +10 %), in the danger color.</summary>
    public static List<OverlayThreshold> Add(IReadOnlyList<OverlayThreshold> rows, OverlayTheme theme)
    {
        var list = Copy(rows);
        var top = list.Count == 0 ? 80f : list.Max(r => r.Value);
        var step = Math.Abs(top) >= 200 ? MathF.Round(Math.Abs(top) * 0.1f) : 10f;
        list.Add(new OverlayThreshold { Value = list.Count == 0 ? top : top + step, Color = (list.Count == 0 ? theme.Warning : theme.Danger).ToString() });
        return list;
    }

    public static List<OverlayThreshold> Remove(IReadOnlyList<OverlayThreshold> rows, int index)
    {
        var list = Copy(rows);
        if ((uint)index < (uint)list.Count) list.RemoveAt(index);
        return list;
    }

    public static List<OverlayThreshold> SetValue(IReadOnlyList<OverlayThreshold> rows, int index, float value)
    {
        var list = Copy(rows);
        if ((uint)index < (uint)list.Count && float.IsFinite(value)) list[index].Value = value;
        return list;
    }

    public static List<OverlayThreshold> SetColor(IReadOnlyList<OverlayThreshold> rows, int index, string color)
    {
        var list = Copy(rows);
        if ((uint)index < (uint)list.Count && ColorText.Normalize(color) is { } c) list[index].Color = c;
        return list;
    }

    /// <summary>Ascending by value (the renderer's "last match wins" then reads naturally top to bottom).</summary>
    public static List<OverlayThreshold> Sorted(IReadOnlyList<OverlayThreshold> rows) => Copy(rows).OrderBy(r => r.Value).ToList();

    /// <summary>The color a value gets with these rows, or null (keeps the value color).</summary>
    public static string? ColorFor(IReadOnlyList<OverlayThreshold> rows, float value)
    {
        string? color = null;
        foreach (var r in rows.OrderBy(r => r.Value))
            if (value >= r.Value) color = r.Color;
        return color;
    }

    public static List<OverlayThreshold> Copy(IEnumerable<OverlayThreshold> rows) => rows.Select(r => new OverlayThreshold { Value = r.Value, Color = r.Color }).ToList();

    // ── units: temperatures are stored in °C and shown in the user's unit ──

    public static double ToDisplay(float value, SensorKind kind, TemperatureUnit unit)
        => kind == SensorKind.Temperature && unit == TemperatureUnit.Fahrenheit ? value * 9.0 / 5 + 32 : value;

    public static float FromDisplay(double value, SensorKind kind, TemperatureUnit unit)
        => (float)(kind == SensorKind.Temperature && unit == TemperatureUnit.Fahrenheit ? (value - 32) * 5 / 9 : value);
}
