using System.Globalization;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Overlay;
using BetterVitals.Overlay.Fonts;
using BetterVitals.Overlay.Host;
using BetterVitals.Overlay.Interop;
using BetterVitals.Overlay.Rendering;
using Xunit;
using static BetterVitals.Tests.Overlay.OverlayFixtures;

namespace BetterVitals.Tests.Overlay;

public class OverlayLogicTests
{
    [Theory]
    [InlineData(HardwareKind.Cpu, SensorKind.Temperature, "Core (Tctl/Tdie)", "CPU")]
    [InlineData(HardwareKind.Cpu, SensorKind.Temperature, "CPU Package", "CPU")]
    [InlineData(HardwareKind.Cpu, SensorKind.Temperature, "Core Max", "CPU")]
    [InlineData(HardwareKind.Cpu, SensorKind.Temperature, "CCD1 (Tdie)", "CCD1")]
    [InlineData(HardwareKind.Cpu, SensorKind.Temperature, "Core #3", "Core 3")]
    [InlineData(HardwareKind.Cpu, SensorKind.Load, "CPU Total", "CPU")]
    [InlineData(HardwareKind.Cpu, SensorKind.Clock, "Core #12", "Core 12")]
    [InlineData(HardwareKind.Cpu, SensorKind.Power, "Package", "CPU")]
    [InlineData(HardwareKind.Cpu, SensorKind.Voltage, "Core (SVI3 TFN)", "Vcore")]
    [InlineData(HardwareKind.Gpu, SensorKind.Temperature, "GPU Core", "GPU")]
    [InlineData(HardwareKind.Gpu, SensorKind.Temperature, "GPU Hot Spot", "Hot Spot")]
    [InlineData(HardwareKind.Gpu, SensorKind.Temperature, "GPU Hot Spot #2", "Hot Spot 2")]
    [InlineData(HardwareKind.Gpu, SensorKind.Temperature, "GPU Memory Junction", "VRAM Tj")]
    [InlineData(HardwareKind.Gpu, SensorKind.Temperature, "GPU Memory #7", "VRAM 7")]
    [InlineData(HardwareKind.Gpu, SensorKind.SmallData, "GPU Memory Used", "VRAM")]
    [InlineData(HardwareKind.Gpu, SensorKind.SmallData, "D3D Dedicated Memory Used", "VRAM")]
    [InlineData(HardwareKind.Gpu, SensorKind.Clock, "GPU Memory", "VRAM")]
    [InlineData(HardwareKind.Gpu, SensorKind.Clock, "GPU Core", "GPU")]
    [InlineData(HardwareKind.Gpu, SensorKind.Load, "GPU Core", "GPU")]
    [InlineData(HardwareKind.Gpu, SensorKind.Power, "GPU Package", "GPU")]
    [InlineData(HardwareKind.Gpu, SensorKind.Fan, "GPU Fan 1", "GPU Fan")]
    [InlineData(HardwareKind.Memory, SensorKind.Data, "Memory Used", "RAM")]
    [InlineData(HardwareKind.Memory, SensorKind.Load, "Memory", "RAM")]
    [InlineData(HardwareKind.Memory, SensorKind.Temperature, "DIMM #2", "DIMM 2")]
    [InlineData(HardwareKind.Game, SensorKind.Fps, "FPS", "FPS")]
    [InlineData(HardwareKind.Game, SensorKind.Fps, "1% Low", "1% low")]
    [InlineData(HardwareKind.Game, SensorKind.Fps, "0.1% Low", "0.1% low")]
    [InlineData(HardwareKind.Game, SensorKind.FrameTime, "Frametime", "Frametime")]
    [InlineData(HardwareKind.Network, SensorKind.Throughput, "Download Speed", "Down")]
    [InlineData(HardwareKind.Network, SensorKind.Throughput, "Upload Speed", "Up")]
    [InlineData(HardwareKind.Motherboard, SensorKind.Temperature, "VRM MOS", "VRM MOS")]
    [InlineData(HardwareKind.Motherboard, SensorKind.Fan, "Fan #2", "Fan 2")]
    public void Default_labels(HardwareKind hw, SensorKind kind, string name, string expected)
        => Assert.Equal(expected, DefaultLabels.For(hw, kind, name));

    [Theory]
    [InlineData("KINGSTON SKC3000D2048G", "Composite Temperature", "NVMe")]
    [InlineData("Samsung SSD 870 EVO 1TB", "Temperature", "SSD")]
    [InlineData("ST2000DM008-2FR102", "Temperature", "HDD")]
    [InlineData("Some Drive", "Temperature", "Disk")]
    public void Storage_labels(string drive, string sensor, string expected)
        => Assert.Equal(expected, DefaultLabels.For(HardwareKind.Storage, SensorKind.Temperature, sensor, drive));

    [Fact]
    public void Labels_from_ids_of_sensors_not_present_yet()
    {
        Assert.Equal("1% low", DefaultLabels.ForId(Low1.Id));
        Assert.Equal("FPS", DefaultLabels.ForId(Fps.Id));
        Assert.Equal("Frametime", DefaultLabels.ForId(FrameTime.Id));
        Assert.Equal("Hot Spot", DefaultLabels.ForId(HotSpot.Id));
        Assert.Equal("0.1% low", DefaultLabels.ForId(SensorIds.Sensor(GameHw, SensorKind.Fps, "0.1% Low")));
        Assert.Equal("—", DefaultLabels.ForId("garbage"));
    }

    [Fact]
    public void Group_labels()
    {
        Assert.Equal("Cores", DefaultLabels.ForGroup(CoreClocks(16)));
        Assert.Equal("GPU", DefaultLabels.ForGroup([GpuTemp, HotSpot]));
        Assert.Equal("3", DefaultLabels.Sub(CoreClocks(4)[2], 2));
    }

    [Theory]
    [InlineData(HardwareKind.Cpu, "Core (Tctl/Tdie)", 80f, 90f)]
    [InlineData(HardwareKind.Gpu, "GPU Core", 80f, 88f)]
    [InlineData(HardwareKind.Gpu, "GPU Hot Spot", 90f, 100f)]
    [InlineData(HardwareKind.Gpu, "GPU Memory Junction", 90f, 100f)]
    [InlineData(HardwareKind.Gpu, "GPU Memory #3", 90f, 100f)]
    [InlineData(HardwareKind.Storage, "Composite Temperature", 60f, 70f)]
    public void Default_thresholds(HardwareKind hw, string name, float warn, float danger)
    {
        var t = DefaultThresholds.For(hw, SensorKind.Temperature, name);
        Assert.Equal(new ThresholdPair(warn, danger), t);
    }

    [Fact]
    public void No_default_thresholds_for_other_kinds()
    {
        Assert.Null(DefaultThresholds.For(HardwareKind.Cpu, SensorKind.Load, "CPU Total"));
        Assert.Null(DefaultThresholds.For(HardwareKind.Motherboard, SensorKind.Temperature, "System"));
        var list = DefaultThresholds.AsList(DefaultThresholds.GpuHotSpot, OverlayTheme.Default);
        Assert.Equal(2, list.Count);
        Assert.Equal(OverlayTheme.Default.Danger.ToString(), list[1].Color);
    }

    [Theory]
    [InlineData("UnrealWindow", 0x14000000u, 0, 0, 2560, 1440, 0, true, false)] // borderless covering the monitor
    [InlineData("Chrome_WidgetWin_1", 0x16CF0000u, -8, -8, 2568, 1400, 0, false, false)] // maximized captioned window
    [InlineData("Progman", 0x96000000u, 0, 0, 2560, 1440, 0, false, false)] // desktop
    [InlineData("SDL_app", 0x14000000u, 100, 100, 900, 700, 0, false, false)] // windowed, no caption
    [InlineData("D3DWindow", 0x94000000u, 0, 0, 2560, 1440, 3, true, true)] // exclusive D3D full screen
    public void Foreground_classification(string cls, uint style, int l, int t, int r, int b, int quns, bool full, bool exclusive)
    {
        var res = ForegroundClassifier.Classify(cls, style, new PixelRect(l, t, r, b), new PixelRect(0, 0, 2560, 1440), quns, ignored: false);
        Assert.Equal((full, exclusive), res);
        Assert.Equal((false, false), ForegroundClassifier.Classify(cls, style, new PixelRect(l, t, r, b), new PixelRect(0, 0, 2560, 1440), quns, ignored: true));
    }

    [Fact]
    public void Tabular_digits_have_equal_width_in_every_font()
    {
        foreach (var family in BundledFonts.AllFamilies)
        {
            var face = FontLibrary.Face(family, 600);
            var a = ValueText.Measure(face, 16, "111");
            var b = ValueText.Measure(face, 16, "888");
            var c = ValueText.Measure(face, 16, "407");
            Assert.Equal(a, b, 3);
            Assert.Equal(a, c, 3);
            Assert.True(a > 10, family);
        }
    }

    [Fact]
    public void Bundled_fonts_load_from_memory_with_both_weights()
    {
        foreach (var f in BundledFonts.All)
        {
            var regular = FontLibrary.Face(f.FamilyName, 400);
            var semibold = FontLibrary.Face(f.FamilyName, 600);
            Assert.Equal(f.FamilyName, regular.Family);
            Assert.Equal(400, regular.Weight);
            Assert.Equal(600, semibold.Weight);
            Assert.Contains("SIL Open Font License", f.LicenseText());
            using var s = f.File(600).Open();
            Assert.True(s.Length > 10_000);
        }
        var total = BundledFonts.All.SelectMany(f => f.Files).Sum(file =>
        {
            using var s = file.Open();
            return s.Length;
        });
        Assert.True(total < 1_500_000, $"{total} bytes");
    }

    [Fact]
    public void Unknown_font_falls_back_to_segoe()
    {
        var face = FontLibrary.Face("No Such Font 123", 600);
        Assert.StartsWith("Segoe UI", face.Family);
    }

    private static Scene MakeScene(OverlayLayout layout, PreviewSensor[] values, int height = 1080, OverlayArrangement? arrangement = null)
    {
        if (arrangement is { } a) layout.Style.Arrangement = a;
        return OverlayRenderTests.Build(layout, values, height);
    }

    [Theory]
    [InlineData(OverlayArrangement.Vertical)]
    [InlineData(OverlayArrangement.Horizontal)]
    [InlineData(OverlayArrangement.Compact)]
    public void Layout_scales_with_the_monitor(OverlayArrangement arrangement)
    {
        PreviewSensor[] values = [new(Fps, 144), new(CpuTemp, 65), new(HotSpot, 91)];
        var layout = new OverlayLayout { Items = [Item(Fps), Item(CpuTemp), Item(HotSpot)] };
        using var hd = MakeScene(layout, values, 1080, arrangement);
        using var qhd = MakeScene(layout, values, 1440, arrangement);
        using var uhd = MakeScene(layout, values, 2160, arrangement);
        using var fiveK = MakeScene(layout, values, 2880, arrangement);
        Assert.InRange(uhd.Width / (double)hd.Width, 1.85, 2.15);
        Assert.InRange(uhd.Height / (double)hd.Height, 1.85, 2.15);
        Assert.InRange(qhd.Width / (double)hd.Width, 1.22, 1.45);
        Assert.InRange(fiveK.Height / (double)hd.Height, 2.45, 2.85);
    }

    [Fact]
    public void Vertical_layout_measures_like_the_spec()
    {
        PreviewSensor[] values = [new(Fps, 144), new(CpuTemp, 65), new(GpuTemp, 70), new(HotSpot, 91)];
        using var s = MakeScene(new OverlayLayout { Items = [Item(Fps), Item(CpuTemp), Item(GpuTemp), Item(HotSpot)] }, values);
        // 4 rows of 22 px + 3 gaps of 4 px + vertical padding, label 12 / value 16 / unit 11 px.
        Assert.InRange(s.Height, 4 * 22 + 3 * 4 + 2 * 5, 4 * 22 + 3 * 4 + 2 * 12);
        Assert.InRange(s.Width, 110, 220);
        Assert.Equal(22, BetterVitals.Overlay.Rendering.Scene.RowHeight(16));
    }

    [Fact]
    public void Values_never_change_the_width_except_growing_once()
    {
        using var s = MakeScene(new OverlayLayout { Items = [Item(CpuTemp), Item(CpuLoad)] }, [new(CpuTemp, 64), new(CpuLoad, 5)]);
        var w = s.Width;
        s.Update([65f, 100f]); // loads are seeded with 3 digits
        Assert.Equal(w, s.Width);
        s.Update([float.NaN, 1f]);
        Assert.Equal(w, s.Width);

        using var t = MakeScene(new OverlayLayout { Items = [Item(CpuTemp)] }, [new(CpuTemp, 64)]);
        var tw = t.Width;
        t.Update([100f]); // a 3-digit temperature grows the column once…
        var grown = t.Width;
        Assert.True(grown > tw);
        t.Update([64f]); // …and it never shrinks back (no jitter)
        Assert.Equal(grown, t.Width);
    }

    [Fact]
    public void Thresholds_recolor_the_value()
    {
        using var s = MakeScene(new OverlayLayout { Items = [Item(HotSpot)] }, [new(HotSpot, 70)]);
        var item = s.Items[0];
        Assert.Equal(OverlayTheme.Default.Text, item.ValueColor);
        s.Update([95f]);
        Assert.Equal(OverlayTheme.Default.Warning, item.ValueColor);
        s.Update([100f]);
        Assert.Equal(OverlayTheme.Default.Danger, item.ValueColor);

        var custom = new OverlayItem { SensorIds = [CpuLoad.Id], Thresholds = [new() { Value = 50, Color = "#00FF00" }] };
        using var s2 = MakeScene(new OverlayLayout { Items = [custom] }, [new(CpuLoad, 60)]);
        Assert.Equal(new Rgba(0, 255, 0), s2.Items[0].ValueColor);
    }

    [Fact]
    public void Colors_default_to_category_text_and_dim()
    {
        using var s = MakeScene(new OverlayLayout { Items = [Item(CpuTemp), Item(GpuTemp), new() { SensorIds = [Fps.Id], LabelColor = "#112233", ValueColor = "#445566" }] },
            [new(CpuTemp, 50), new(GpuTemp, 50), new(Fps, 60)]);
        Assert.Equal(OverlayTheme.Default.CategoryColor(HardwareKind.Cpu), s.Items[0].LabelColor);
        Assert.Equal(OverlayTheme.Default.CategoryColor(HardwareKind.Gpu), s.Items[1].LabelColor);
        Assert.Equal(OverlayTheme.Default.Text, s.Items[0].ValueColor);
        Assert.Equal(OverlayTheme.Default.TextDim, s.UnitColor);
        Assert.Equal(new Rgba(0x11, 0x22, 0x33), s.Items[2].LabelColor);
        Assert.Equal(new Rgba(0x44, 0x55, 0x66), s.Items[2].ValueColor);
    }

    [Fact]
    public void Aggregation_max_and_average()
    {
        var max = new OverlayItem { SensorIds = [CpuTemp.Id], Aggregation = ValueAggregation.Max, AggregationSeconds = 3 };
        var avg = new OverlayItem { SensorIds = [CpuTemp.Id], Aggregation = ValueAggregation.Average, AggregationSeconds = 3 };
        using var s = MakeScene(new OverlayLayout { Items = [max, avg], RefreshMs = 1000 }, [new(CpuTemp, 50)]);
        s.Update([70f]);
        s.Update([60f]);
        Assert.Equal("70", new string(s.Items[0].Main.Text));
        Assert.Equal("60", new string(s.Items[1].Main.Text)); // (50 + 70 + 60) / 3
        s.Update([40f]);
        s.Update([40f]); // window of 3: 60, 40, 40
        Assert.Equal("60", new string(s.Items[0].Main.Text));
        Assert.Equal("47", new string(s.Items[1].Main.Text));
    }

    [Fact]
    public void Formatting_follows_culture_and_units()
    {
        var layout = new OverlayLayout { Items = [Item(FrameTime), Item(Vram), Item(CpuTemp)] };
        var lookup = new PreviewLookup([new(FrameTime, 6.94f), new(Vram, 11234), new(CpuTemp, 50)]);
        using var s = new Scene(new SceneInput(layout, OverlayTheme.Default, lookup, new CultureInfo("pl-PL"), TemperatureUnit.Fahrenheit, null, 1));
        lookup.Feed(s);
        Assert.Equal("6,9", new string(s.Items[0].Main.Text));
        Assert.Equal("ms", s.Items[0].Unit);
        Assert.Equal("11,0", new string(s.Items[1].Main.Text));
        Assert.Equal("GB", s.Items[1].Unit);
        Assert.Equal("122", new string(s.Items[2].Main.Text));
        Assert.Equal("°F", s.Items[2].Unit);
    }

    [Fact]
    public void Compact_joins_the_degree_sign_and_hides_duplicate_units()
    {
        using var s = MakeScene(new OverlayLayout { Items = [Item(CpuTemp), Item(Fps)] }, [new(CpuTemp, 64), new(Fps, 144)], arrangement: OverlayArrangement.Compact);
        Assert.Equal("64°", new string(s.Items[0].Main.Text));
        Assert.Null(s.Items[0].UnitLine);
        Assert.Null(s.Items[1].UnitLine); // "FPS 144", not "FPS 144 FPS"
    }

    [Fact]
    public void Missing_sensor_shows_a_dash_with_its_default_label()
    {
        using var s = MakeScene(new OverlayLayout { Items = [Item(Low1)] }, []);
        Assert.True(s.HasUnresolved);
        Assert.Equal("—", new string(s.Items[0].Main.Text));
        Assert.Equal("1% low", s.Items[0].Label.Text);
    }

    [Fact]
    public void Update_does_not_allocate()
    {
        var cores = CoreClocks(16);
        var values = new List<PreviewSensor> { new(Fps, 144), new(CpuTemp, 60), new(HotSpot, 90), new(Vram, 9000), new(FrameTime, 7) };
        values.AddRange(cores.Select(c => new PreviewSensor(c, 5000)));
        var layout = new OverlayLayout
        {
            Items =
            [
                new() { SensorIds = [Fps.Id], ShowGraph = true },
                new() { SensorIds = [CpuTemp.Id], Aggregation = ValueAggregation.Average },
                Item(HotSpot), Item(Vram), Item(FrameTime),
                new() { SensorIds = [.. cores.Select(c => c.Id)], MultiDisplay = MultiValueDisplay.Bars },
                new() { SensorIds = [.. cores.Select(c => c.Id)], MultiDisplay = MultiValueDisplay.Grid },
            ],
        };
        using var s = MakeScene(layout, [.. values]);
        var frame = new float[values.Count];
        for (var i = 0; i < 50; i++) Fill(frame, i); // warm-up (JIT, unit layouts)
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) Fill(frame, i);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);

        void Fill(float[] f, int i)
        {
            for (var k = 0; k < f.Length; k++) f[k] = values[k].Value + (i * 7 + k * 3) % 23;
            s.Update(f);
        }
    }

    [Fact]
    public void Render_to_png_writes_a_png()
    {
        var path = Path.Combine(OutputDir, "api-render.png");
        var (w, h) = OverlayHost.RenderToPng(new OverlayLayout { Items = [Item(Fps)] }, OverlayTheme.Default, [new PreviewSensor(Fps, 60)], 1080, path);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.True(w > 0 && h > 0);
    }

    [Fact]
    public void Layout_copy_is_deep()
    {
        var layout = new OverlayLayout { Items = [new() { SensorIds = ["a"], Thresholds = [new() { Value = 1 }] }] };
        var copy = LayoutCopy.Clone(layout);
        layout.Items[0].SensorIds.Add("b");
        layout.Items[0].Thresholds![0].Value = 5;
        layout.Style.FontFamily = "Geist";
        Assert.Single(copy.Items[0].SensorIds);
        Assert.Equal(1, copy.Items[0].Thresholds![0].Value);
        Assert.Equal("Inter", copy.Style.FontFamily);
    }

    [Fact]
    public void Vtable_slots_used_by_the_interop_are_stable()
    {
        // Guards against accidental edits of the hand-written COM layer: these slots were checked against the
        // Windows SDK 10.0.26100 headers (MSVC orders overloads in reverse).
        Assert.Equal(87u, new DxgiSwapChainDesc1 { Format = 87 }.Format);
        Assert.Equal(48, System.Runtime.InteropServices.Marshal.SizeOf<DWriteGlyphRun>());
        Assert.Equal(32, System.Runtime.InteropServices.Marshal.SizeOf<D2DBitmapProps1>());
        Assert.Equal(20, System.Runtime.InteropServices.Marshal.SizeOf<DWriteFontMetrics>());
        Assert.Equal(28, System.Runtime.InteropServices.Marshal.SizeOf<DWriteGlyphMetrics>());
        Assert.Equal(36, System.Runtime.InteropServices.Marshal.SizeOf<DWriteTextMetrics>());
        Assert.Equal(24, System.Runtime.InteropServices.Marshal.SizeOf<DWriteLineMetrics>());
        Assert.Equal(48, System.Runtime.InteropServices.Marshal.SizeOf<DxgiSwapChainDesc1>());
    }
}
