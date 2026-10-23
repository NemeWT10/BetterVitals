using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BetterVitals.Core.Overlay;
using BetterVitals.UI.Pages.OverlayEditor;
using Xunit;
using Xunit.Abstractions;

namespace BetterVitals.Tests.Robustness;

/// <summary>PROJEKT §7 / §8: overlay profiles are files the user saves, copies, shares and edits — the editor must take any of them.</summary>
public sealed class ProfileScenarios(ITestOutputHelper output)
{
    private static OverlayLayout Sample() => new()
    {
        Enabled = true,
        Placement = { Anchor = OverlayAnchor.BottomRight, OffsetX = 12 },
        Items = [new OverlayItem { SensorIds = ["cpu/x/0/temperature/a"], Label = "CPU" }, new OverlayItem { SensorIds = ["gpu/y/0/load/b", "gpu/y/0/load/c"], MultiDisplay = MultiValueDisplay.Grid }],
    };

    [Fact]
    public void Any_mangled_profile_text_is_either_read_or_refused_with_FormatException_never_anything_else()
    {
        var good = LayoutJson.WriteProfile("My HUD", Sample());
        var rnd = new Random(77);
        var read = 0;
        var refused = 0;
        string[] pieces = ["{", "}", "[", "]", "null", "\"", ",", ":", "1e999", "-0", "true", "\"layout\"", "\"items\"", "\"style\"", "99999999999999999999", "\\u0000", "\\", "/*", "//", "\0"];
        for (var i = 0; i < 4000; i++)
        {
            var sb = new StringBuilder(good);
            for (var k = rnd.Next(1, 6); k > 0; k--)
            {
                var pos = rnd.Next(sb.Length + 1);
                switch (rnd.Next(5))
                {
                    case 0:
                        if (pos < sb.Length) sb.Remove(pos, Math.Min(rnd.Next(1, 40), sb.Length - pos));
                        break;
                    case 1:
                        sb.Insert(pos, pieces[rnd.Next(pieces.Length)]);
                        break;
                    case 2:
                        if (pos < sb.Length) sb[pos] = (char)rnd.Next(32, 400);
                        break;
                    case 3:
                        sb.Length = Math.Max(0, pos);
                        break;
                    default:
                        if (pos < sb.Length) sb.Insert(pos, sb.ToString(pos, Math.Min(rnd.Next(1, 200), sb.Length - pos)));
                        break;
                }
            }
            try
            {
                var (_, layout) = LayoutJson.ReadProfile(sb.ToString());
                Assert.NotNull(layout);
                Assert.All(layout.Items, item => Assert.NotNull(item.SensorIds));
                read++;
            }
            catch (FormatException)
            {
                refused++;
            }
        }
        output.WriteLine($"{read} read, {refused} refused");
        Assert.True(read > 100 && refused > 100);

        foreach (var text in new[] { "", " ", "null", "[]", "42", "\"x\"", "{}", "{\"layout\": 5}", "{\"layout\": {\"items\": 5}}", "{\"layout\": {\"items\": [null, 5, \"x\"]}}", "{\"name\": 7, \"layout\": {\"items\": []}}", "{\"overlay\": []}" })
        {
            try
            {
                LayoutJson.ReadProfile(text);
            }
            catch (FormatException)
            {
                // refused: fine
            }
        }
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("Aux")]
    [InlineData("COM1")]
    [InlineData("lpt9")]
    [InlineData("con.")]
    [InlineData("prn ")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData("a:b")]
    [InlineData("<>:\"/\\|?*")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData("żółć gęślą jaźń")]
    [InlineData("名前 \U0001F600")]
    [InlineData("trailing dot.")]
    [InlineData("sub\\dir\\name")]
    [InlineData("C:\\Windows\\evil")]
    public void Any_profile_name_can_be_saved_listed_loaded_and_deleted_without_leaving_the_folder(string name)
    {
        using var dir = new QaDir("profiles");
        var store = new OverlayProfileStore(Path.Combine(dir.Path, "overlay-profiles"));
        store.Save(name, Sample());
        var path = store.PathOf(name);
        Assert.StartsWith(Path.GetFullPath(store.Directory), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase); // never outside its folder
        Assert.Equal(Path.GetFullPath(store.Directory), Path.GetDirectoryName(Path.GetFullPath(path)));
        Assert.True(File.Exists(path));
        Assert.NotEmpty(store.List());
        var loaded = store.Load(name);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Items.Count);
        store.Delete(name);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Damaged_profile_files_in_the_folder_do_not_break_listing_and_are_refused_cleanly_when_opened()
    {
        using var dir = new QaDir("profiles-bad");
        var folder = Path.Combine(dir.Path, "overlay-profiles");
        Directory.CreateDirectory(folder);
        var rnd = new Random(2);
        var junk = new byte[500];
        rnd.NextBytes(junk);
        File.WriteAllBytes(Path.Combine(folder, "random.json"), junk);
        File.WriteAllBytes(Path.Combine(folder, "empty.json"), []);
        File.WriteAllText(Path.Combine(folder, "array.json"), "[1,2]");
        File.WriteAllText(Path.Combine(folder, "cut.json"), LayoutJson.WriteProfile("cut", Sample())[..60]);
        File.WriteAllText(Path.Combine(folder, "wrong-kind.json"), "{\"name\":\"zzz\",\"layout\":{\"items\":\"no\"}}");
        var store = new OverlayProfileStore(folder);
        store.Save("fine", Sample());

        var names = store.List();
        Assert.Contains("fine", names);
        Assert.True(names.Count >= 6);
        foreach (var name in new[] { "random", "empty", "array", "cut", "zzz" })
        {
            try
            {
                store.Load(name);
            }
            catch (FormatException)
            {
                // the editor shows "damaged"
            }
        }
        Assert.NotNull(store.Load("fine"));
        Assert.Null(store.Load("does not exist"));
    }

    [Fact]
    public void A_profile_from_a_newer_version_with_unknown_fields_and_values_is_read_as_far_as_it_is_understood()
    {
        var node = JsonNode.Parse(LayoutJson.WriteProfile("future", Sample()))!.AsObject();
        node["version"] = 7;
        node["layout"]!["Hologram"] = JsonValue.Create(true);
        node["layout"]!["Style"]!["Arrangement"] = 12; // a layout mode this version does not know
        node["layout"]!["Items"]![0]!["MultiDisplay"] = "Constellation";
        var ex = Record.Exception(() => LayoutJson.ReadProfile(node.ToJsonString()));
        Assert.True(ex is null or FormatException);

        node["layout"]!["Items"]![0]!["MultiDisplay"] = 9;
        var (_, layout) = LayoutJson.ReadProfile(node.ToJsonString());
        Assert.Equal(OverlayArrangement.Vertical, layout.Style.Arrangement);
        Assert.Equal(MultiValueDisplay.Bars, layout.Items[0].MultiDisplay);
        Assert.Equal(OverlayAnchor.BottomRight, layout.Placement.Anchor);
    }
}
