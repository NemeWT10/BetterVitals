using System.Globalization;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;

namespace BetterVitals.Overlay;

/// <summary>Everything the overlay needs from the app besides layout and theme.</summary>
public sealed record OverlayContext
{
    /// <summary>Live values. The overlay registers its own demand ("overlay") while it is visible.</summary>
    public required ISensorHub Hub { get; init; }

    /// <summary>Number formatting (UI language; PL = decimal comma).</summary>
    public CultureInfo Culture { get; init; } = CultureInfo.InvariantCulture;

    public TemperatureUnit TemperatureUnit { get; init; } = TemperatureUnit.Celsius;

    /// <summary>
    /// Label for an item without its own <see cref="OverlayItem.Label"/> (e.g. the editor's friendly parameter
    /// names). Return null to use <see cref="DefaultLabels"/>. Called on the overlay thread when a layout is applied.
    /// </summary>
    public Func<OverlayItem, string?>? LabelResolver { get; init; }

    /// <summary>Fade on show / hide (the app's "Animations" setting).</summary>
    public bool Animations { get; init; } = true;
}

/// <summary>A sensor with a fixed value for offscreen renders (tests, snapshots, the editor preview).</summary>
/// <param name="Sensor">Descriptor (kind, hardware kind, name, unit); <see cref="SensorDescriptor.Id"/> is matched against item sensor ids.</param>
/// <param name="Value">Value in base units (NaN = no value).</param>
/// <param name="History">Older values, oldest first, for sparklines and Max / Average aggregation.</param>
public sealed record PreviewSensor(SensorDescriptor Sensor, float Value, IReadOnlyList<float>? History = null);

/// <summary>Premultiplied BGRA pixels of an offscreen render (WPF: <c>PixelFormats.Pbgra32</c>).</summary>
public sealed record OverlayBitmap(int Width, int Height, int Stride, byte[] Pixels);
