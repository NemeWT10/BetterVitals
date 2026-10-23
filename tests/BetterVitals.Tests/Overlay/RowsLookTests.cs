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
/// Layouts in the rows-and-boxes format of settings schema 2 / profile v2, rendered offscreen to
/// tests/output/compat-v2/current. With tests/output/compat-v2/golden (renders of a schema-2 build) every picture listed
/// in <see cref="Same"/> must match pixel for pixel.
/// </summary>
public class RowsLookTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string Ids(params BetterVitals.Core.Sensors.SensorDescriptor[] s) => string.Join(",", s.Select(x => "\"" + x.Id + "\""));

    private static string I(string ids, string extra = "") => $$"""{ "SensorIds": [{{ids}}]{{extra}} }""";

    internal static IEnumerable<(string Name, string Json)> Layouts()
    {
        var cores = Ids(CoreClocks(8));
        var fps = I(Ids(FpsSensor), ", \"ShowGraph\": true");
        var rows = $$"""
            [
              { "Items": [ {{fps}}, {{I(Ids(Low1))}} ] },
              { "Items": [ {{I(Ids(CpuTemp))}}, {{I(cores)}}, {{I(Ids(CpuLoad))}} ] },
              { "Items": [ {{I(Ids(GpuTemp))}}, {{I(Ids(HotSpot))}}, {{I(Ids(GpuClock))}}, {{I(Ids(GpuLoad))}} ] },
              { "Items": [ {{I(Ids(Ram))}} ] }
            ]
            """;
        var plain = $$"""
            [
              { "Items": [ {{I(Ids(FpsSensor))}}, {{I(Ids(Low1))}} ] },
              { "Items": [ {{I(Ids(CpuTemp))}}, {{I(Ids(CpuLoad))}}, {{I(Ids(CpuPower))}} ] },
              { "Items": [ {{I(Ids(GpuTemp))}}, {{I(Ids(HotSpot))}}, {{I(Ids(GpuClock))}} ] }
            ]
            """;
        var list = $$"""
            [
              { "Items": [ {{fps}} ] }, { "Items": [ {{I(Ids(CpuTemp))}} ] }, { "Items": [ {{I(cores, ", \"MultiDisplay\": \"Rows\"")}} ] },
              { "Items": [ {{I(Ids(GpuTemp))}} ] }, { "Items": [ {{I(Ids(HotSpot))}} ] }, { "Items": [ {{I(Ids(Vram))}} ] }
            ]
            """;
        var line = $$"""[ { "Items": [ {{fps}}, {{I(Ids(CpuTemp))}}, {{I(cores)}}, {{I(Ids(GpuTemp))}}, {{I(Ids(HotSpot))}} ] } ]""";
        var named = $$"""
            [
              { "Label": "Graphics", "Items": [ {{I(Ids(GpuTemp))}}, {{I(Ids(HotSpot))}} ] },
              { "Label": "Memory", "Items": [ {{I(Ids(Ram))}} ] }
            ]
            """;
        foreach (var arr in new[] { "Vertical", "Horizontal", "Compact" })
        {
            yield return ($"rows-{arr}", $$"""{ "Style": { "Arrangement": "{{arr}}" }, "Rows": {{rows}} }""");
            yield return ($"plain-{arr}", $$"""{ "Style": { "Arrangement": "{{arr}}" }, "Rows": {{plain}} }""");
            yield return ($"list-{arr}", $$"""{ "Style": { "Arrangement": "{{arr}}" }, "Rows": {{list}} }""");
            yield return ($"line-{arr}", $$"""{ "Style": { "Arrangement": "{{arr}}" }, "Rows": {{line}} }""");
        }
        yield return ("named-Vertical", $$"""{ "Rows": {{named}} }""");
        yield return ("default-rows", $$"""{ "Rows": [ { "Items": [ {{I(Ids(FpsSensor))}} ] }, { "Items": [ {{I(Ids(CpuTemp))}} ] }, { "Items": [ {{I(Ids(GpuTemp))}} ] } ] }""");
        yield return ("boxed", $$"""
            { "Rows": [ { "Items": [ {{fps}} ] }, { "Items": [ {{I(Ids(CpuTemp))}} ] } ],
              "Boxes": [ { "Placement": { "Anchor": "TopRight" }, "Rows": [ { "Items": [ {{I(Ids(GpuTemp))}} ] }, { "Items": [ {{I(Ids(HotSpot))}} ] } ] } ] }
            """);
    }

    /// <summary>
    /// Layouts whose grid is the old picture: the list (rows of one, label beside), one row (any style), a detached box
    /// (it becomes the second window, drawn as before). The others differ on purpose: every column of a table lines up —
    /// schema 2 aligned only the first two columns with the label beside, and none with the label above or compact —
    /// and a row's own name is now the label of its first cell.
    /// </summary>
    private static bool Same(string name) => name == "list-Vertical" || name.StartsWith("line-", StringComparison.Ordinal) || name is "default-rows" or "boxed";

    [Fact]
    public void Row_layouts_of_schema_2_render_as_before_where_the_grid_is_the_same()
    {
        var root = Path.Combine(Path.GetDirectoryName(OutputDir)!, "compat-v2");
        var current = Path.Combine(root, "current");
        var golden = Path.Combine(root, "golden");
        Directory.CreateDirectory(current);
        var values = OverlayGridRenderTests.Values();
        var mismatches = new List<string>();
        var compared = 0;
        foreach (var (name, json) in Layouts())
        {
            foreach (var (height, theme, themeName, culture) in new[] { (1080, OverlayTheme.Default, "dark", CultureInfo.InvariantCulture), (1440, Light, "light", new CultureInfo("pl-PL")) })
            {
                var layout = JsonSerializer.Deserialize<OverlayLayout>(json, Json)!;
                var windows = OverlayHost.RenderWindowsToBitmaps(layout, theme, values, height, culture, TemperatureUnit.Celsius);
                Assert.Equal(name == "boxed" ? 2 : 1, windows.Count);
                for (var i = 0; i < windows.Count; i++)
                {
                    var file = $"{name}-{height}-{themeName}-w{i}";
                    Offscreen.WritePng(windows[i].Bitmap, Path.Combine(current, file + ".png"));
                    File.WriteAllBytes(Path.Combine(current, file + ".raw"), Raw(windows[i].Bitmap));
                    var goldenRaw = Path.Combine(golden, file + ".raw");
                    if (!Same(name) || !File.Exists(goldenRaw)) continue;
                    compared++;
                    if (!File.ReadAllBytes(goldenRaw).AsSpan().SequenceEqual(Raw(windows[i].Bitmap))) mismatches.Add(file);
                }
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
