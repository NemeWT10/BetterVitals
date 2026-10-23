using System.Globalization;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Settings;
using BetterVitals.Overlay;
using BetterVitals.Overlay.Rendering;
using Xunit;
using static BetterVitals.Tests.Overlay.OverlayFixtures;

namespace BetterVitals.Tests.Overlay;

/// <summary>Offscreen renders (Direct2D → WIC → PNG under tests/output/overlay); never shown on screen.</summary>
public class OverlayRenderTests
{
    private static readonly Offscreen.Backdrop GameBackdrop = new(new Rgba(0x3A, 0x55, 0x7A), new Rgba(0xC9, 0x8B, 0x5A), 24);

    private static PreviewSensor[] SimpleValues =>
    [
        new(FpsSensor, 143.6f),
        new(CpuTemp, 64.4f),
        new(GpuTemp, 71.2f),
        new(HotSpot, 93.4f),
    ];

    private static OverlayLayout SimpleLayout(OverlayArrangement arrangement = OverlayArrangement.Vertical) => new()
    {
        Style = new OverlayStyle { Arrangement = arrangement },
        Items = [Item(FpsSensor), Item(CpuTemp), Item(GpuTemp), Item(HotSpot)],
    };

    private static void Save(OverlayBitmap bmp, string name) => Offscreen.WritePng(bmp, Path.Combine(OutputDir, name));

    [Fact]
    public void Simple_layout_renders()
    {
        var bmp = Offscreen.Render(SimpleLayout(), OverlayTheme.Default, SimpleValues, 1080, CultureInfo.InvariantCulture, TemperatureUnit.Celsius, null);
        Save(bmp, "simple-1080.png");
        Save(Offscreen.Render(scene: Build(SimpleLayout(), SimpleValues, 1080), false, GameBackdrop), "simple-1080-ingame.png");
        Assert.InRange(bmp.Width, 80, 400);
        Assert.InRange(bmp.Height, 60, 200);
        // panel is opaque-ish inside, transparent outside the rounded corner
        Assert.Equal(0, bmp.Pixels[3]);
        Assert.True(bmp.Pixels[(bmp.Height / 2) * bmp.Stride + (bmp.Width / 2) * 4 + 3] > 150);
    }

    [Fact]
    public void Simple_layout_4k_is_twice_as_large()
    {
        var hd = Offscreen.Render(SimpleLayout(), OverlayTheme.Default, SimpleValues, 1080, null, TemperatureUnit.Celsius, null);
        var uhd = Offscreen.Render(SimpleLayout(), OverlayTheme.Default, SimpleValues, 2160, null, TemperatureUnit.Celsius, null);
        Save(uhd, "simple-2160.png");
        Save(Offscreen.Render(Build(SimpleLayout(), SimpleValues, 2160), false, GameBackdrop with { Margin = 48 }), "simple-2160-ingame.png");
        Assert.InRange(uhd.Width / (double)hd.Width, 1.85, 2.15);
        Assert.InRange(uhd.Height / (double)hd.Height, 1.85, 2.15);
    }

    [Fact]
    public void Compact_and_horizontal_render()
    {
        Save(Offscreen.Render(Build(SimpleLayout(OverlayArrangement.Compact), SimpleValues, 1080), false, GameBackdrop), "compact-1080-ingame.png");
        Save(Offscreen.Render(Build(SimpleLayout(OverlayArrangement.Horizontal), SimpleValues, 1080), false, GameBackdrop), "horizontal-1080-ingame.png");
        Save(Offscreen.Render(Build(SimpleLayout(OverlayArrangement.Compact), SimpleValues, 2160), false, GameBackdrop with { Margin = 40 }), "compact-2160-ingame.png");
        Save(Offscreen.Render(Build(SimpleLayout(OverlayArrangement.Horizontal), SimpleValues, 2160), false, GameBackdrop with { Margin = 40 }), "horizontal-2160-ingame.png");
        var compact = Offscreen.Render(SimpleLayout(OverlayArrangement.Compact), OverlayTheme.Default, SimpleValues, 1080, null, TemperatureUnit.Celsius, null);
        Save(compact, "compact-1080.png");
        Assert.True(compact.Width > compact.Height * 4);
    }

    [Fact]
    public void Light_theme_renders()
    {
        var light = Offscreen.Render(SimpleLayout(), Light, SimpleValues, 1440, new CultureInfo("pl-PL"), TemperatureUnit.Celsius, null);
        Save(light, "light-1440.png");
        var scene = Build(SimpleLayout(), SimpleValues, 1440, Light);
        Save(Offscreen.Render(scene, false, GameBackdrop with { Top = new Rgba(0xE8, 0xEE, 0xF5), Bottom = new Rgba(0x9F, 0xB4, 0xC8) }), "light-1440-ingame.png");
    }

    [Fact]
    public void Advanced_layout_renders()
    {
        var cores = CoreClocks(16);
        var loads = CoreLoads(16);
        var values = new List<PreviewSensor>
        {
            new(FpsSensor, 141, Wave(141, 12)),
            new(Low1, 98),
            new(FrameTime, 7.1f, Wave(7.1f, 1.2f, seed: 3)),
            new(CpuTemp, 76.5f, Wave(72, 6, seed: 2)),
            new(CpuLoad, 38, Wave(38, 20, seed: 5)),
            new(CpuPower, 88.4f),
            new(GpuTemp, 82.1f),
            new(HotSpot, 101.3f),
            new(MemJunction, 86),
            new(GpuClock, 2842),
            new(Vram, 11234),
            new(Ram, 18.7f),
            new(Nvme, 63),
        };
        var r = new Random(7);
        values.AddRange(cores.Select((c, i) => new PreviewSensor(c, 4800 + r.Next(0, 650) - (i % 3) * 300)));
        values.AddRange(loads.Select((c, i) => new PreviewSensor(c, (float)(r.NextDouble() * 100))));

        var layout = new OverlayLayout
        {
            Style = new OverlayStyle(),
            Items =
            [
                new() { SensorIds = [FpsSensor.Id], ShowGraph = true },
                Item(Low1),
                new() { SensorIds = [FrameTime.Id], ShowGraph = true },
                new() { SensorIds = [CpuTemp.Id], ShowGraph = true, Aggregation = ValueAggregation.Max, AggregationSeconds = 10 },
                new() { SensorIds = [CpuLoad.Id], ShowGraph = true },
                Item(CpuPower),
                new() { SensorIds = [.. cores.Select(c => c.Id)], MultiDisplay = MultiValueDisplay.Bars },
                new() { SensorIds = [.. loads.Select(c => c.Id)], MultiDisplay = MultiValueDisplay.Bars, Label = "Load" },
                Item(GpuTemp),
                Item(HotSpot),
                Item(MemJunction),
                Item(GpuClock),
                Item(Vram),
                Item(Ram),
                Item(Nvme),
            ],
        };
        Save(Offscreen.Render(Build(layout, values, 1080), false, GameBackdrop), "advanced-1080-ingame.png");
        Save(Offscreen.Render(Build(layout, values, 2160), false, GameBackdrop with { Margin = 40 }), "advanced-2160-ingame.png");
        layout.Style.Arrangement = OverlayArrangement.Compact;
        Save(Offscreen.Render(Build(layout, values, 1440), false, GameBackdrop), "advanced-compact-1440-ingame.png");
        layout.Style.Arrangement = OverlayArrangement.Horizontal;
        Save(Offscreen.Render(Build(layout, values, 1440), false, GameBackdrop), "advanced-horizontal-1440-ingame.png");
        layout.Style.Arrangement = OverlayArrangement.Vertical;
        Save(Offscreen.Render(layout, OverlayTheme.Default, values, 1440, null, TemperatureUnit.Celsius, null), "advanced-1440.png");

        var grid = new OverlayLayout
        {
            Items =
            [
                new() { SensorIds = [.. cores.Select(c => c.Id)], MultiDisplay = MultiValueDisplay.Grid },
                new() { SensorIds = [.. cores.Take(4).Select(c => c.Id)], MultiDisplay = MultiValueDisplay.Rows },
                Item(FpsSensor),
            ],
        };
        Save(Offscreen.Render(Build(grid, values, 1080), false, GameBackdrop), "multi-grid-rows-1080-ingame.png");
    }

    [Fact]
    public void Every_font_renders()
    {
        foreach (var family in BetterVitals.Overlay.Fonts.BundledFonts.AllFamilies)
        {
            var layout = SimpleLayout();
            layout.Style.FontFamily = family;
            var bmp = Offscreen.Render(Build(layout, SimpleValues, 1080), false, GameBackdrop with { Margin = 8 });
            Save(bmp, $"font-{family.Replace(' ', '-').ToLowerInvariant()}.png");
            Assert.True(bmp.Width > 60);
        }
    }

    /// <summary>The main box of a layout as the host builds it (a normalized copy: a flat item list becomes rows by its arrangement).</summary>
    internal static Scene Build(OverlayLayout layout, IReadOnlyList<PreviewSensor> values, int height, OverlayTheme? theme = null, string boxId = OverlayLayout.MainBoxId)
    {
        var lookup = new PreviewLookup(values);
        var copy = BetterVitals.Overlay.Host.LayoutCopy.Clone(layout);
        var scene = new Scene(new SceneInput(copy, theme ?? OverlayTheme.Default, lookup, CultureInfo.InvariantCulture, TemperatureUnit.Celsius, null,
            OverlayPlacementMath.Scale(copy.Style, height), boxId));
        lookup.Feed(scene);
        return scene;
    }
}
