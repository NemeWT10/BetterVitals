using BetterVitals.Core.Overlay;

namespace BetterVitals.Overlay.Host;

/// <summary>Deep copies of the (mutable) layout classes, so the overlay thread never shares them with the UI.</summary>
internal static class LayoutCopy
{
    public static OverlayLayout Clone(OverlayLayout src)
    {
        var l = new OverlayLayout
        {
            Enabled = src.Enabled,
            Visibility = src.Visibility,
            HideFromCapture = src.HideFromCapture,
            Advanced = src.Advanced,
            DefaultsApplied = src.DefaultsApplied,
            RefreshMs = src.RefreshMs,
            Grouping = src.Grouping,
            Placement = Clone(src.Placement ?? new OverlayPlacement()),
            Style = Clone(src.Style ?? new OverlayStyle()),
            Rows = Clone(src.Rows),
            Boxes = (src.Boxes ?? []).Where(b => b is not null).Select(b => new OverlayBox
            {
                Id = b.Id,
                Name = b.Name,
                Placement = Clone(b.Placement ?? new OverlayPlacement()),
                Rows = Clone(b.Rows),
            }).ToList(),
            Items = src.Items?.Where(i => i is not null).Select(Clone).ToList(),
        };
        l.Normalize();
        return l;
    }

    private static List<OverlayRow> Clone(List<OverlayRow>? rows)
        => (rows ?? []).Where(r => r is not null).Select(r => new OverlayRow
        {
            Id = r.Id,
            Label = r.Label,
            Items = (r.Items ?? []).Where(i => i is not null).Select(Clone).ToList(),
        }).ToList();

    public static OverlayPlacement Clone(OverlayPlacement p) => new()
    {
        MonitorMode = p.MonitorMode,
        MonitorKey = p.MonitorKey,
        Anchor = p.Anchor,
        OffsetX = p.OffsetX,
        OffsetY = p.OffsetY,
        Margin = p.Margin,
    };

    public static OverlayStyle Clone(OverlayStyle s) => new()
    {
        FontFamily = s.FontFamily,
        ValueFontFamily = s.ValueFontFamily,
        AutoScale = s.AutoScale,
        ScalePercent = s.ScalePercent,
        Arrangement = s.Arrangement,
        LabelSize = s.LabelSize,
        ValueSize = s.ValueSize,
        UnitSize = s.UnitSize,
        LabelWeight = s.LabelWeight,
        ValueWeight = s.ValueWeight,
        BackgroundColor = s.BackgroundColor,
        BackgroundOpacity = s.BackgroundOpacity,
        CornerRadius = s.CornerRadius,
        Padding = s.Padding,
        Spacing = s.Spacing,
        Border = s.Border,
        TextShadow = s.TextShadow,
        ShowUnits = s.ShowUnits,
        LabelColor = s.LabelColor,
        ValueColor = s.ValueColor,
        UnitColor = s.UnitColor,
    };

    public static OverlayItem Clone(OverlayItem i) => new()
    {
        Id = i.Id,
        SensorIds = [.. i.SensorIds ?? []],
        Label = i.Label,
        LabelColor = i.LabelColor,
        ValueColor = i.ValueColor,
        Decimals = i.Decimals,
        ShowUnit = i.ShowUnit,
        ShowGraph = i.ShowGraph,
        MultiDisplay = i.MultiDisplay,
        Aggregation = i.Aggregation,
        AggregationSeconds = i.AggregationSeconds,
        Thresholds = i.Thresholds?.Where(t => t is not null).Select(t => new OverlayThreshold { Value = t.Value, Color = t.Color }).ToList(),
        FontFamily = i.FontFamily,
        ValueSize = i.ValueSize,
    };
}
