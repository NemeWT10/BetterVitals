using System.Globalization;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Overlay;
using Xunit;
using Xunit.Abstractions;

namespace BetterVitals.Tests.Robustness;

/// <summary>PROJEKT §7: the overlay must not break — whatever the layout file says and whatever numbers the sensors report.</summary>
public sealed class OverlayScenarios(ITestOutputHelper output)
{
    private static readonly double[] Nasty = [double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.MaxValue, double.MinValue, double.Epsilon, -1, 0, 0.5, 1, 11, 4096, 1e9, -1e9];

    private static OverlayLayout RandomLayout(Random rnd, IReadOnlyList<string> sensorIds)
    {
        double D() => rnd.Next(3) == 0 ? Nasty[rnd.Next(Nasty.Length)] : rnd.NextDouble() * rnd.Next(1, 300);
        string? Id() => rnd.Next(10) switch
        {
            0 => null,
            1 => "",
            2 => "no/such/0/temperature/sensor",
            3 => "garbage",
            _ => sensorIds[rnd.Next(sensorIds.Count)],
        };
        string? Color() => rnd.Next(6) switch { 0 => null, 1 => "", 2 => "nonsense", 3 => "#12345", 4 => "#FF00FF", _ => "#80336699" };
        string?[] fonts = ["Inter", "Geist", "Orbitron", "JetBrains Mono", "Rajdhani", "Space Grotesk", "Segoe UI Variable", "Bahnschrift", "No Such Font", "", null];
        var layout = new OverlayLayout
        {
            Enabled = true,
            RefreshMs = rnd.Next(-100, 5000),
            HideFromCapture = rnd.Next(2) == 0,
            Visibility = (OverlayVisibility)rnd.Next(0, 3),
            Placement = new OverlayPlacement
            {
                Anchor = (OverlayAnchor)rnd.Next(0, 12),
                MonitorMode = (OverlayMonitorMode)rnd.Next(0, 4),
                MonitorKey = rnd.Next(2) == 0 ? null : "\\\\?\\DISPLAY#X#4&0&UID1",
                OffsetX = D(),
                OffsetY = D(),
                Margin = D(),
            },
            Style = new OverlayStyle
            {
                FontFamily = fonts[rnd.Next(fonts.Length)]!,
                ValueFontFamily = fonts[rnd.Next(fonts.Length)],
                AutoScale = rnd.Next(2) == 0,
                ScalePercent = D(),
                Arrangement = (OverlayArrangement)rnd.Next(0, 6),
                LabelSize = D(),
                ValueSize = D(),
                UnitSize = D(),
                LabelWeight = rnd.Next(-5, 1500),
                ValueWeight = rnd.Next(-5, 1500),
                BackgroundColor = Color(),
                BackgroundOpacity = D(),
                CornerRadius = D(),
                Padding = D(),
                Spacing = D(),
                Border = rnd.Next(2) == 0,
                TextShadow = rnd.Next(2) == 0,
                ShowUnits = rnd.Next(2) == 0,
                LabelColor = Color(),
                ValueColor = Color(),
                UnitColor = Color(),
            },
        };
        for (var i = rnd.Next(0, 12); i > 0; i--)
        {
            var item = new OverlayItem
            {
                SensorIds = [.. Enumerable.Range(0, rnd.Next(0, 10)).Select(_ => Id()!)],
                Label = rnd.Next(3) == 0 ? new string('W', rnd.Next(0, 200)) : (rnd.Next(2) == 0 ? null : "Label"),
                LabelColor = Color(),
                ValueColor = Color(),
                Decimals = rnd.Next(2) == 0 ? null : rnd.Next(-3, 9),
                ShowUnit = rnd.Next(3) == 0 ? null : rnd.Next(2) == 0,
                ShowGraph = rnd.Next(2) == 0,
                MultiDisplay = (MultiValueDisplay)rnd.Next(0, 4),
                Aggregation = (ValueAggregation)rnd.Next(0, 4),
                AggregationSeconds = rnd.Next(-5, 5000),
                Thresholds = rnd.Next(3) == 0 ? null : [.. Enumerable.Range(0, rnd.Next(0, 5)).Select(_ => rnd.Next(8) == 0 ? null! : new OverlayThreshold { Value = (float)D(), Color = Color()! })],
                FontFamily = fonts[rnd.Next(fonts.Length)],
                ValueSize = rnd.Next(2) == 0 ? null : D(),
            };
            layout.Items.Add(item);
        }
        if (rnd.Next(10) == 0) layout.Items.Add(null!);
        return layout;
    }

    [Fact]
    public void A_layout_made_of_garbage_still_renders_after_normalizing()
    {
        using var pc = new SimPc();
        var ids = pc.Engine.Sensors.Select(s => s.Id).ToArray();
        var rnd = new Random(2610);
        var biggest = 0;
        for (var i = 0; i < 120; i++)
        {
            var layout = RandomLayout(rnd, ids);
            layout.Normalize(); // what loading settings does
            var heights = new[] { 1080, 1440, 2160, 720, 0, -5 };
            var h = heights[rnd.Next(heights.Length)];
            var values = layout.Items.SelectMany(it => it.SensorIds).Distinct().Take(30).Select(id => pc.Engine.Find(id)).Where(d => d is not null)
                .Select(d => new PreviewSensor(d!, (float)Nasty[rnd.Next(Nasty.Length)])).ToList();
            var bmp = OverlayHost.RenderToBitmap(layout, OverlayTheme.Default, values, h, culture: rnd.Next(2) == 0 ? CultureInfo.InvariantCulture : new CultureInfo("pl-PL"),
                temperatureUnit: rnd.Next(2) == 0 ? TemperatureUnit.Celsius : TemperatureUnit.Fahrenheit);
            biggest = Math.Max(biggest, Math.Max(bmp.Width, bmp.Height));
            Assert.True(bmp.Width > 0 && bmp.Height > 0);
        }
        output.WriteLine($"120 random layouts rendered; the biggest edge was {biggest} px"); // a hand-made layout of hundreds of rows is huge, but it renders
    }

    [Fact]
    public void Odd_sensor_values_never_break_the_live_overlay()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var source = new ScriptedSource("odd-" + id, "Odd " + id, 12, SensorKind.Temperature);
        using var pc = SimPc.With(source);
        var layout = new OverlayLayout
        {
            Enabled = true,
            Items =
            [
                .. Enumerable.Range(0, 12).Select(i => new OverlayItem { SensorIds = [source.SensorId(i)], ShowGraph = i % 2 == 0, Aggregation = (ValueAggregation)(i % 3), AggregationSeconds = 3 }),
                new OverlayItem { SensorIds = [.. Enumerable.Range(0, 12).Select(source.SensorId)], MultiDisplay = MultiValueDisplay.Grid },
                new OverlayItem { SensorIds = [.. Enumerable.Range(0, 12).Select(source.SensorId)], MultiDisplay = MultiValueDisplay.Rows },
            ],
        };
        using var host = new OverlayHost(new OverlayHostOptions { NeverShow = true });
        host.Start();
        host.Apply(layout, OverlayTheme.Default, new OverlayContext { Hub = pc.Engine, Animations = false });
        Assert.True(host.Diagnostics().HasDemand);
        var rnd = new Random(9);
        float[] special = [float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.MaxValue, float.MinValue, float.Epsilon, -0f, 0f, -273.15f, 1e-30f, 99999.999f, 123456789f];
        for (var round = 0; round < 60; round++)
        {
            var pick = rnd.Next(3) == 0 ? special : null;
            source.Value = i => pick is null ? (float)(rnd.NextDouble() * 200 - 20) : pick[(i + round) % pick.Length];
            pc.Run(TimeSpan.FromSeconds(2));
            host.RefreshNow();
            var shown = host.ShownValues();
            Assert.All(shown, text => Assert.InRange(text.Length, 1, 24));
            Assert.True(host.IsRunning);
        }
        Assert.True(host.Diagnostics().SurfaceReady);
    }

    [Fact]
    public void A_hundred_layout_changes_in_a_row_leave_the_window_alive_and_the_demand_matching()
    {
        using var pc = new SimPc();
        var ids = pc.Engine.Sensors.Select(s => s.Id).ToArray();
        var rnd = new Random(41);
        using var host = new OverlayHost(new OverlayHostOptions { NeverShow = true });
        host.Start();
        for (var i = 0; i < 100; i++)
        {
            var layout = RandomLayout(rnd, ids);
            if (rnd.Next(3) != 0) layout.Normalize(); // the editor may hand over a layout that was never normalized
            layout.Enabled = rnd.Next(5) != 0;
            host.Apply(layout, OverlayTheme.Default, new OverlayContext { Hub = pc.Engine, Animations = rnd.Next(2) == 0 });
            if (rnd.Next(4) == 0) host.SetVisible(rnd.Next(2) == 0);
            pc.Run(TimeSpan.FromSeconds(1));
            var d = host.Diagnostics();
            Assert.True(host.IsRunning);
            // (a scene beyond 16384 px cannot be drawn; the host then waits for a smaller layout instead of retrying)
            // the overlay asks the engine for exactly what its items show, and for nothing when it is off
            var normalized = BetterVitals.Overlay.Host.LayoutCopy.Clone(layout);
            normalized.Normalize();
            var wanted = normalized.Items.SelectMany(it => it.SensorIds).Distinct().Count();
            var asked = pc.Engine.Demands.Where(x => x.Consumer == "overlay").Sum(x => x.Sensors);
            if (!d.HasDemand) Assert.Equal(0, asked);
            else Assert.Equal(wanted, asked);
        }
        host.SetVisible(true);
    }
}
