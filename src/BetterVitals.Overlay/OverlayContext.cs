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

/// <summary>
/// A window's grid as drawn, in pixels of its render: the extents of every column's content (index = grid column) and
/// of every row (index = grid row), and the cell of every item. Columns and rows are separated by gaps.
/// </summary>
public sealed record OverlayGridGeometry(
    int Width,
    int Height,
    IReadOnlyList<float> ColumnLeft,
    IReadOnlyList<float> ColumnRight,
    IReadOnlyList<float> RowTop,
    IReadOnlyList<float> RowBottom,
    IReadOnlyList<OverlayGridCell> Cells)
{
    public int Columns => ColumnLeft.Count;

    public int Rows => RowTop.Count;
}

/// <summary>Where an item sits in an <see cref="OverlayGridGeometry"/>; <paramref name="Right"/>: where its own content ends, px (a sparkline may run past its column).</summary>
public readonly record struct OverlayGridCell(string ItemId, int Row, int Column, float Right = 0);
