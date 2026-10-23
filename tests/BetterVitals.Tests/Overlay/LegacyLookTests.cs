using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Settings;
using BetterVitals.Overlay;
using BetterVitals.Overlay.Rendering;
using Xunit;
using static BetterVitals.Tests.Overlay.OverlayFixtures;

namespace BetterVitals.Tests.Overlay;

/// <summary>
/// Overlay layouts in the flat format of settings schema 1 / profile v1, rendered offscreen. Written to
/// tests/output/compat/current; when tests/output/compat/golden exists (renders made by an older build) every picture
/// must match it pixel for pixel: old settings look exactly as before.
/// </summary>
public class LegacyLookTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly CultureInfo Pl = new("pl-PL");

    private static List<PreviewSensor> Values()
    {
        var cores = CoreClocks(8);
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
        values.AddRange(cores.Select((c, i) => new PreviewSensor(c, 4800 + i * 61 - (i % 3) * 300)));
        return values;
    }

    private static string Ids(params BetterVitals.Core.Sensors.SensorDescriptor[] s) => string.Join(",", s.Select(x => "\"" + x.Id + "\""));

    private static IEnumerable<(string Name, string Json)> Layouts()
    {
        var cores = Ids(CoreClocks(8));
        var six = $$"""
            [
              { "SensorIds": [{{Ids(FpsSensor)}}], "ShowGraph": true },
              { "SensorIds": [{{Ids(CpuTemp)}}] },
              { "SensorIds": [{{cores}}] },
              { "SensorIds": [{{Ids(GpuTemp)}}] },
              { "SensorIds": [{{Ids(HotSpot)}}] },
              { "SensorIds": [{{Ids(GpuClock)}}] }
            ]
            """;
        var mixed = $$"""
            [
              { "SensorIds": [{{Ids(FpsSensor)}}], "ShowGraph": true, "Label": "Frames" },
              { "SensorIds": [{{Ids(Low1)}}] },
              { "SensorIds": [{{Ids(FrameTime)}}], "ShowGraph": true, "Decimals": 2 },
              { "SensorIds": [{{Ids(CpuTemp)}}], "Aggregation": "Max", "AggregationSeconds": 10, "Thresholds": [ { "Value": 70, "Color": "#22C55E" }, { "Value": 75, "Color": "#F43F5E" } ] },
              { "SensorIds": [{{Ids(CpuLoad)}}], "ShowUnit": false, "ValueColor": "#38BDF8" },
              { "SensorIds": [{{cores}}], "MultiDisplay": "Grid" },
              { "SensorIds": [{{Ids(CoreClocks(4))}}], "MultiDisplay": "Rows" },
              { "SensorIds": [{{Ids(GpuTemp)}}], "LabelColor": "#FBBF24", "ValueSize": 22 },
              { "SensorIds": [{{Ids(Vram)}}] },
              { "SensorIds": [{{Ids(Ram)}}] },
              { "SensorIds": [{{Ids(Nvme)}}], "FontFamily": "JetBrains Mono" }
            ]
            """;
        foreach (var arr in new[] { "Vertical", "Horizontal", "Compact" })
        {
            yield return ($"six-{arr}", $$"""{ "Style": { "Arrangement": "{{arr}}" }, "Items": {{six}} }""");
            yield return ($"mixed-{arr}", $$"""{ "Style": { "Arrangement": "{{arr}}" }, "Items": {{mixed}} }""");
            yield return ($"one-{arr}", $$"""{ "Style": { "Arrangement": "{{arr}}" }, "Items": [ { "SensorIds": [{{Ids(GpuTemp)}}] } ] }""");
        }
        yield return ("styled-Vertical", $$"""
            { "Style": { "FontFamily": "Orbitron", "ValueFontFamily": "Rajdhani", "ScalePercent": 135, "BackgroundOpacity": 0, "Border": false, "TextShadow": true,
                         "Padding": 6, "Spacing": 9, "CornerRadius": 2, "ShowUnits": false, "LabelColor": "#FFFFFF" }, "Items": {{six}} }
            """);
        yield return ("empty-Vertical", """{ "Style": { "Arrangement": "Vertical" }, "Items": [] }""");
        yield return ("default-items", $$"""{ "Items": [ { "SensorIds": [{{Ids(FpsSensor)}}] }, { "SensorIds": [{{Ids(CpuTemp)}}] }, { "SensorIds": [{{Ids(GpuTemp)}}] } ] }""");
    }

    [Fact]
    public void Flat_layouts_of_schema_1_render_as_they_always_did()
    {
        var root = Path.Combine(Path.GetDirectoryName(OutputDir)!, "compat");
        var current = Path.Combine(root, "current");
        var golden = Path.Combine(root, "golden");
        Directory.CreateDirectory(current);
        var values = Values();
        var mismatches = new List<string>();
        var compared = 0;
        foreach (var (name, json) in Layouts())
        {
            foreach (var (height, theme, themeName, culture) in new[] { (1080, OverlayTheme.Default, "dark", CultureInfo.InvariantCulture), (1440, Light, "light", Pl), (2160, OverlayTheme.Default, "dark", Pl) })
            {
                var layout = JsonSerializer.Deserialize<OverlayLayout>(json, Json)!;
                var bmp = OverlayHost.RenderToBitmap(layout, theme, values, height, culture, TemperatureUnit.Celsius);
                var file = $"{name}-{height}-{themeName}";
                Offscreen.WritePng(bmp, Path.Combine(current, file + ".png"));
                File.WriteAllBytes(Path.Combine(current, file + ".raw"), Raw(bmp));
                var goldenRaw = Path.Combine(golden, file + ".raw");
                if (!File.Exists(goldenRaw)) continue;
                compared++;
                if (!File.ReadAllBytes(goldenRaw).AsSpan().SequenceEqual(Raw(bmp))) mismatches.Add(file);
            }
        }
        Assert.True(mismatches.Count == 0, $"{mismatches.Count} of {compared} differ: {string.Join(", ", mismatches)}");
    }

    private static byte[] Raw(OverlayBitmap bmp)
    {
        var head = BitConverter.GetBytes(bmp.Width).Concat(BitConverter.GetBytes(bmp.Height)).ToArray();
        return [.. head, .. bmp.Pixels];
    }
}
