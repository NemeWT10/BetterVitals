using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Overlay.Interop;

namespace BetterVitals.Overlay.Rendering;

/// <summary>Brushes and stroke styles of one render target (recreated with the device).</summary>
internal sealed unsafe class DeviceResources : IDisposable
{
    private void* _brush;
    private void* _round;
    private void* _dash;
    private void* _sheen;
    private float _sheenHeight = -1;

    /// <param name="rt">Render target / device context that owns the brushes.</param>
    /// <param name="factory">The ID2D1Factory of <paramref name="rt"/> (geometries, stroke styles); not owned.</param>
    public DeviceResources(void* rt, void* factory)
    {
        Rt = rt;
        Factory = factory;
        Com.Check(RT.CreateSolidColorBrush(rt, new D2DColor(1, 1, 1, 1), out _brush), "CreateSolidColorBrush");
        var round = new D2DStrokeStyleProps { StartCap = 2, EndCap = 2, DashCap = 2, LineJoin = 2, MiterLimit = 10 };
        Com.Check(D2D.CreateStrokeStyle(factory, round, null, 0, out _round), "CreateStrokeStyle");
        var dashes = stackalloc float[] { 3f, 2.5f };
        var dash = new D2DStrokeStyleProps { StartCap = 0, EndCap = 0, DashCap = 0, LineJoin = 0, MiterLimit = 10, DashStyle = 5 };
        Com.Check(D2D.CreateStrokeStyle(factory, dash, dashes, 2, out _dash), "CreateStrokeStyle");
    }

    public void* Rt { get; }

    public void* Factory { get; }

    public void* RoundStroke => _round;

    public void* DashStroke => _dash;

    /// <summary>The shared solid brush, recolored before each primitive.</summary>
    public void* Solid(Rgba c, float alpha)
    {
        Brush.SetColor(_brush, SceneRenderer.Color(c, alpha));
        return _brush;
    }

    /// <summary>Top-down highlight for the panel (white, fading out), cached per panel height.</summary>
    public void* Sheen(float height, float opacity)
    {
        if (_sheen == null || MathF.Abs(_sheenHeight - height) > 0.5f)
        {
            Com.Release(ref _sheen);
            var stops = stackalloc D2DGradientStop[3];
            stops[0] = new D2DGradientStop { Position = 0, Color = new D2DColor(1, 1, 1, 0.07f) };
            stops[1] = new D2DGradientStop { Position = 0.45f, Color = new D2DColor(1, 1, 1, 0.015f) };
            stops[2] = new D2DGradientStop { Position = 1, Color = new D2DColor(1, 1, 1, 0) };
            Com.Check(RT.CreateGradientStopCollection(Rt, stops, 3, out var coll), "CreateGradientStopCollection");
            var hr = RT.CreateLinearGradientBrush(Rt, new D2DPoint(0, 0), new D2DPoint(0, height), coll, out _sheen);
            Com.Release(ref coll);
            Com.Check(hr, "CreateLinearGradientBrush");
            _sheenHeight = height;
        }
        Brush.SetOpacity(_sheen, opacity);
        return _sheen;
    }

    public void Dispose()
    {
        Com.Release(ref _sheen);
        Com.Release(ref _dash);
        Com.Release(ref _round);
        Com.Release(ref _brush);
    }
}

/// <summary>Draws a <see cref="Scene"/> into a Direct2D render target (swap chain or offscreen WIC bitmap).</summary>
internal static unsafe class SceneRenderer
{
    public static D2DColor Color(Rgba c, float alpha) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f * alpha);

    /// <summary>Draws the whole panel. Call between BeginDraw / EndDraw.</summary>
    /// <param name="alpha">Fade multiplier 0–1.</param>
    /// <param name="edit">Edit-mode chrome (accent dashed outline).</param>
    /// <param name="editEmphasis">1 = the box the keys move, less = the other boxes in edit mode.</param>
    public static void Draw(DeviceResources res, Scene scene, float alpha, bool edit, float editEmphasis = 1)
    {
        RT.Clear(res.Rt, new D2DColor(0, 0, 0, 0));
        DrawContent(res, scene, alpha, edit, editEmphasis);
    }

    /// <summary>Draws the panel over whatever is in the target (no clear).</summary>
    public static void DrawContent(DeviceResources res, Scene scene, float alpha, bool edit, float editEmphasis = 1)
    {
        var rt = res.Rt;
        RT.SetTextAntialiasMode(rt, 2);
        RT.SetAntialiasMode(rt, 0);
        float w = scene.Width, h = scene.Height;

        var b = scene.BorderWidth;
        var rr = new D2DRoundedRect
        {
            Rect = new D2DRect(b / 2, b / 2, w - b / 2, h - b / 2),
            RadiusX = scene.Radius,
            RadiusY = scene.Radius,
        };
        if (scene.Background.A > 0)
        {
            RT.FillRoundedRectangle(rt, rr, res.Solid(scene.Background, alpha));
            RT.FillRoundedRectangle(rt, rr, res.Sheen(h, alpha * (scene.Background.A / 255f)));
        }
        if (scene.HasBorder) RT.DrawRoundedRectangle(rt, rr, res.Solid(scene.BorderColor, alpha), b);

        foreach (var d in scene.Dividers)
            RT.DrawLine(rt, new D2DPoint(d.X, d.Top), new D2DPoint(d.X, d.Bottom), res.Solid(scene.BorderColor, alpha * 0.9f), MathF.Max(1, MathF.Round(scene.S)));

        if (scene.Arrangement == OverlayArrangement.Vertical)
        {
            foreach (var row in scene.Rows)
            {
                if (row.Label is { } label) Text(res, scene, label, row.LabelX, row.Baseline, row.LabelColor, alpha);
                if (row.Dots.Count > 0) Dots(res, scene, row, alpha);
                foreach (var item in row.Items) DrawItem(res, scene, row, item, alpha);
            }
        }
        else
        {
            foreach (var row in scene.Rows)
                foreach (var item in row.Items) DrawItem(res, scene, row, item, alpha);
        }

        if (edit)
        {
            var accent = scene.Theme.CategoryColor(HardwareKind.Cpu);
            var e = MathF.Max(1.5f, 1.5f * scene.S);
            var er = new D2DRoundedRect
            {
                Rect = new D2DRect(e / 2, e / 2, w - e / 2, h - e / 2),
                RadiusX = scene.Radius,
                RadiusY = scene.Radius,
            };
            RT.FillRoundedRectangle(rt, er, res.Solid(accent, alpha * 0.10f * editEmphasis));
            RT.DrawRoundedRectangle(rt, er, res.Solid(accent, alpha * editEmphasis), e, res.DashStroke);
        }
    }

    /// <summary>The small dots between the values of a Vertical row, centered on the digits.</summary>
    private static void Dots(DeviceResources res, Scene scene, SceneRow row, float alpha)
    {
        var d = scene.DotSize;
        var first = row.Items[0];
        var cy = row.Baseline - scene.CapPx(first, first.ValuePx) / 2;
        foreach (var x in row.Dots)
        {
            var r = new D2DRoundedRect { Rect = new D2DRect(x - d / 2, cy - d / 2, x + d / 2, cy + d / 2), RadiusX = d / 2, RadiusY = d / 2 };
            RT.FillRoundedRectangle(res.Rt, r, res.Solid(scene.UnitColor, alpha * 0.55f));
        }
    }

    private static void DrawItem(DeviceResources res, Scene scene, SceneRow row, SceneItem it, float alpha)
    {
        var rt = res.Rt;
        var horizontal = scene.Arrangement == OverlayArrangement.Horizontal;
        var vertical = scene.Arrangement == OverlayArrangement.Vertical;
        var rows = it.IsMulti && it.Display == MultiValueDisplay.Rows;

        // Vertical: the row draws the label column; an item only its own small label when it differs
        if (horizontal) Text(res, scene, it.Label, it.LabelX, it.LabelBaseline, it.LabelColor, alpha);
        else if (vertical) { if (it.Tag is { } tag) Text(res, scene, tag, it.TagX, it.Baseline, it.LabelColor, alpha * 0.9f); }
        else if (!rows) Text(res, scene, it.Label, it.LabelX, it.Baseline, it.LabelColor, alpha);

        var rowH = Scene.RowHeight(it.ValuePx);
        if (!it.IsMulti || it.Display == MultiValueDisplay.Bars)
        {
            if (it.IsMulti) Bars(res, scene, it, alpha, rowH);
            Value(res, scene, it.Main, it.ValueRight - it.Main.Width, it.Baseline, it.ValueColor, alpha);
            Unit(res, scene, it, it.Baseline, alpha);
        }
        else if (it.Display == MultiValueDisplay.Grid)
        {
            var cols = Scene.GridCols(it);
            var subH = Scene.RowHeight(it.SubPx);
            var cap = scene.CapPx(it, it.SubPx);
            var gap = MathF.Round(7 * scene.S);
            var firstBaseline = MathF.Round(it.Top + (subH + cap) / 2);
            if (!horizontal)
            {
                // label + unit sit on the first grid row
                firstBaseline = it.Baseline - MathF.Round((rowH - subH) / 2);
            }
            for (var k = 0; k < it.Cells!.Length; k++)
            {
                var col = k % cols;
                var line = k / cols;
                var right = it.GridX + (col + 1) * (it.CellColW + gap) - gap;
                var baseline = firstBaseline + line * subH;
                var cell = it.Cells[k];
                Value(res, scene, cell, MathF.Round(right - cell.Width), baseline, it.CellColors![k], alpha);
            }
            Unit(res, scene, it, firstBaseline, alpha);
        }
        else
        {
            var subH = Scene.RowHeight(it.SubPx);
            var cap = scene.CapPx(it, it.SubPx);
            for (var k = 0; k < it.Cells!.Length; k++)
            {
                var baseline = MathF.Round(it.Top + k * subH + (subH + cap) / 2);
                var label = it.SubLabels![k];
                var x = horizontal ? it.SubLabelX : vertical && !row.SubLabelsInColumn ? it.TagX : it.LabelX;
                Text(res, scene, label, x, baseline, it.LabelColor, alpha * 0.9f);
                var cell = it.Cells[k];
                Value(res, scene, cell, it.ValueRight - cell.Width, baseline, it.CellColors![k], alpha);
                Unit(res, scene, it, baseline, alpha);
            }
        }

        if (it.Spark is not null && it.SparkCount >= 2) Spark(res, scene, it, alpha, rowH);
    }

    private static void Unit(DeviceResources res, Scene scene, SceneItem it, float baseline, float alpha)
    {
        if (it.UnitLine is { } u) Text(res, scene, u, it.UnitX, baseline, scene.UnitColor, alpha);
    }

    /// <summary>Outline / shadow passes (dx, dy in multiples of the scale, alpha).</summary>
    private static readonly (float Dx, float Dy, float A)[] ShadowPasses =
        [(-1, 0, 0.5f), (1, 0, 0.5f), (0, -1, 0.5f), (0, 1, 0.6f), (0.5f, 1.5f, 0.35f)];

    private static void Text(DeviceResources res, Scene scene, TextLine line, float x, float baseline, Rgba color, float alpha)
    {
        x = MathF.Round(x);
        if (scene.TextShadow)
        {
            var o = MathF.Max(1, MathF.Round(scene.S));
            foreach (var (dx, dy, a) in ShadowPasses)
                line.Draw(res.Rt, x + dx * o, baseline + dy * o, res.Solid(scene.ShadowColor, alpha * a));
        }
        line.Draw(res.Rt, x, baseline, res.Solid(color, alpha));
    }

    private static void Value(DeviceResources res, Scene scene, ValueText text, float x, float baseline, Rgba color, float alpha)
    {
        x = MathF.Round(x);
        if (scene.TextShadow)
        {
            var o = MathF.Max(1, MathF.Round(scene.S));
            foreach (var (dx, dy, a) in ShadowPasses)
                text.Draw(res.Rt, x + dx * o, baseline + dy * o, res.Solid(scene.ShadowColor, alpha * a));
        }
        text.Draw(res.Rt, x, baseline, res.Solid(color, alpha));
    }

    private static void Bars(DeviceResources res, Scene scene, SceneItem it, float alpha, float rowH)
    {
        var s = scene.S;
        var barW = MathF.Max(2, MathF.Round(3 * s));
        var gap = MathF.Max(1, MathF.Round(2 * s));
        var barH = MathF.Round(rowH * 0.62f);
        var bottom = it.Baseline + MathF.Round(1 * s);
        var top = bottom - barH;
        var radius = MathF.Min(barW / 2, 1.5f * s);
        var track = scene.Theme.Text;
        for (var k = 0; k < it.Slot.Length; k++)
        {
            var x = it.BarsX + k * (barW + gap);
            var r = new D2DRoundedRect { Rect = new D2DRect(x, top, x + barW, bottom), RadiusX = radius, RadiusY = radius };
            RT.FillRoundedRectangle(res.Rt, r, res.Solid(track, alpha * 0.13f));
            var v = it.Cur[k];
            if (float.IsNaN(v)) continue;
            var f = Scene.BarFraction(it, v);
            var fh = MathF.Max(MathF.Round(f * barH), MathF.Max(1, MathF.Round(s)));
            var picked = Scene.Pick(it, v);
            var color = picked == it.BaseValueColor ? it.LabelColor : picked;
            var fr = new D2DRoundedRect { Rect = new D2DRect(x, bottom - fh, x + barW, bottom), RadiusX = radius, RadiusY = radius };
            RT.FillRoundedRectangle(res.Rt, fr, res.Solid(color, alpha * 0.95f));
        }
    }

    private static void Spark(DeviceResources res, Scene scene, SceneItem it, float alpha, float rowH)
    {
        var spark = it.Spark!;
        var n = it.SparkCount;
        var start = (it.SparkHead - n + spark.Length) % spark.Length;
        float min = float.MaxValue, max = float.MinValue;
        var valid = 0;
        for (var i = 0; i < n; i++)
        {
            var v = spark[(start + i) % spark.Length];
            if (float.IsNaN(v)) continue;
            valid++;
            min = MathF.Min(min, v);
            max = MathF.Max(max, v);
        }
        if (valid < 2) return;
        var (rmin, rmax) = OverlayDefaults.BarRange(it.Kind);
        if (rmax is { } fixedMax)
        {
            min = rmin;
            max = fixedMax;
        }
        else
        {
            var span = max - min;
            var pad = MathF.Max(span * 0.15f, MathF.Max(MathF.Abs(max) * 0.02f, 0.5f));
            min -= pad;
            max += pad;
        }
        var h = MathF.Round(rowH * 0.56f);
        var bottom = it.Baseline + MathF.Round(1 * scene.S);
        var top = bottom - h;
        var x0 = it.GraphX;
        var step = it.GraphW / (spark.Length - 1);
        // right-aligned: newest sample at the right edge
        var xStart = x0 + it.GraphW - (n - 1) * step;

        var pts = stackalloc D2DPoint[spark.Length + 2];
        var count = 0;
        for (var i = 0; i < n; i++)
        {
            var v = spark[(start + i) % spark.Length];
            if (float.IsNaN(v)) v = min;
            var y = bottom - (v - min) / (max - min) * h;
            pts[count++] = new D2DPoint(xStart + i * step, Math.Clamp(y, top, bottom));
        }

        var color = it.LabelColor;
        // area
        if (D2D.CreatePathGeometry(res.Factory, out var area) >= 0)
        {
            if (D2D.OpenGeometry(area, out var sink) >= 0)
            {
                D2D.BeginFigure(sink, new D2DPoint(pts[0].X, bottom), 0);
                D2D.AddLines(sink, pts, (uint)count);
                var last = new D2DPoint(pts[count - 1].X, bottom);
                D2D.AddLines(sink, &last, 1);
                D2D.EndFigure(sink, 1);
                D2D.CloseSink(sink);
                Com.Release(ref sink);
                RT.FillGeometry(res.Rt, area, res.Solid(color, alpha * 0.16f));
            }
            Com.Release(ref area);
        }
        // line
        if (D2D.CreatePathGeometry(res.Factory, out var line) >= 0)
        {
            if (D2D.OpenGeometry(line, out var sink) >= 0)
            {
                D2D.BeginFigure(sink, pts[0], 1);
                D2D.AddLines(sink, pts + 1, (uint)(count - 1));
                D2D.EndFigure(sink, 0);
                D2D.CloseSink(sink);
                Com.Release(ref sink);
                RT.DrawGeometry(res.Rt, line, res.Solid(color, alpha * 0.95f), MathF.Max(1, 1.25f * scene.S), res.RoundStroke);
            }
            Com.Release(ref line);
        }
    }
}
