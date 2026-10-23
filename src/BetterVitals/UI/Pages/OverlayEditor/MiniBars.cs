using System.Windows;
using System.Windows.Media;
using BetterVitals.Core.Sensors;
using BetterVitals.Services;

namespace BetterVitals.UI.Pages.OverlayEditor;

/// <summary>
/// A tiny bar per value (a multi item's "every core" preview on its tile): heights by value (loads 0–100 %, temperatures
/// from 20 °C, anything else relative to the highest), colored like the overlay's core bar (heat for temperatures).
/// </summary>
internal sealed class MiniBars : FrameworkElement
{
    private float[] _values = [];
    private SensorDescriptor? _sensor;

    public MiniBars()
    {
        IsHitTestVisible = false;
        Height = 24;
        SnapsToDevicePixels = true;
    }

    public void SetValues(SensorDescriptor? sensor, float[] values)
    {
        _sensor = sensor;
        _values = values;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? Math.Max(24, _values.Length * 6) : availableSize.Width, Height);

    protected override void OnRender(DrawingContext dc)
    {
        var n = _values.Length;
        if (n == 0 || ActualWidth <= 0) return;
        var gap = n > 12 ? 1.5 : 2.5;
        var w = Math.Max(1.5, Math.Min(10, (ActualWidth - gap * (n - 1)) / n));
        var kind = _sensor?.Kind ?? SensorKind.Load;
        var (lo, hi) = kind switch
        {
            SensorKind.Load or SensorKind.Control or SensorKind.Level => (0f, 100f),
            SensorKind.Temperature => (20f, Math.Max(90f, Max())),
            _ => (0f, Math.Max(1e-3f, Max())),
        };
        var track = (Brush)(TryFindResource("Brush.SurfaceHover") ?? Brushes.Gray);
        var accent = (Brush)(TryFindResource("Brush.AccentText") ?? Brushes.SlateBlue);
        for (var i = 0; i < n; i++)
        {
            var x = i * (w + gap);
            dc.DrawRoundedRectangle(track, null, new Rect(x, 0, w, ActualHeight), w / 3, w / 3);
            var v = _values[i];
            if (float.IsNaN(v)) continue;
            var t = Math.Clamp((v - lo) / (hi - lo), 0.06, 1);
            var h = ActualHeight * t;
            var brush = accent;
            if (_sensor is not null && kind == SensorKind.Temperature)
            {
                var heat = SensorPresentation.Heat(_sensor, v);
                if (heat != HeatLevel.Normal) brush = (Brush)(TryFindResource(SensorPresentation.HeatBrushKey(heat)) ?? accent);
            }
            dc.DrawRoundedRectangle(brush, null, new Rect(x, ActualHeight - h, w, h), w / 3, w / 3);
        }
    }

    private float Max()
    {
        var m = float.MinValue;
        foreach (var v in _values)
            if (!float.IsNaN(v) && v > m) m = v;
        return m == float.MinValue ? 1 : m;
    }
}
