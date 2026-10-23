using System.IO;
using Xunit;
using System.Globalization;
using System.Text.RegularExpressions;
using BetterVitals.Core.Sensors;
using BetterVitals.Localization;

namespace BetterVitals.Tests.Ui;

[Collection(UiCollection.Name)]
public sealed partial class LocTests
{
    [Fact]
    public void Every_entry_has_both_languages_and_the_same_placeholders()
    {
        Assert.NotEmpty(Loc.AllEntries);
        foreach (var (key, (pl, en)) in Loc.AllEntries)
        {
            Assert.False(string.IsNullOrWhiteSpace(pl), key + " (pl)");
            Assert.False(string.IsNullOrWhiteSpace(en), key + " (en)");
            Assert.Equal(Holes(en), Holes(pl));
        }
    }

    [Fact]
    public void Plural_keys_come_in_complete_sets()
    {
        foreach (var key in Loc.AllEntries.Keys.Where(k => k.EndsWith(".one", StringComparison.Ordinal)))
        {
            var stem = key[..^4];
            Assert.True(Loc.Has(stem + ".few"), stem + ".few");
            Assert.True(Loc.Has(stem + ".many"), stem + ".many");
        }
    }

    [Theory]
    [InlineData(1, "one", "one")]
    [InlineData(2, "few", "many")]
    [InlineData(4, "few", "many")]
    [InlineData(5, "many", "many")]
    [InlineData(12, "many", "many")]
    [InlineData(14, "many", "many")]
    [InlineData(22, "few", "many")]
    [InlineData(112, "many", "many")]
    [InlineData(0, "many", "many")]
    public void Plural_forms(long n, string polish, string english)
    {
        Assert.Equal(polish, Loc.PluralSuffix(n, polish: true));
        Assert.Equal(english, Loc.PluralSuffix(n, polish: false));
    }

    [Fact]
    public void System_language_is_polish_only_on_polish_windows()
    {
        Assert.Equal("pl", Loc.ResolveLanguage("system", CultureInfo.GetCultureInfo("pl-PL")));
        Assert.Equal("en", Loc.ResolveLanguage("system", CultureInfo.GetCultureInfo("de-DE")));
        Assert.Equal("en", Loc.ResolveLanguage(null, CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("en", Loc.ResolveLanguage("en", CultureInfo.GetCultureInfo("pl-PL")));
        Assert.Equal("pl", Loc.ResolveLanguage("pl", CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void Live_switch_changes_texts_and_number_culture()
    {
        var before = Loc.I.Language;
        var raised = 0;
        void OnChanged() => raised++;
        Loc.LanguageChanged += OnChanged;
        try
        {
            Loc.I.SetLanguage("pl");
            Assert.Equal("Ustawienia", Loc.T("settings.title"));
            Assert.Equal(",", Loc.I.Culture.NumberFormat.NumberDecimalSeparator);
            Assert.Equal("Nagrywanie: 5 czujników", Loc.Plural("flyout.recording", 5));
            Loc.I.SetLanguage("en");
            Assert.Equal("Settings", Loc.T("settings.title"));
            Assert.Equal("Recording: 1 sensor", Loc.Plural("flyout.recording", 1));
            Assert.True(raised >= 2);
        }
        finally
        {
            Loc.LanguageChanged -= OnChanged;
            Loc.I.SetLanguage(before);
        }
    }

    [Fact]
    public void A_missing_key_is_visible_and_reported()
    {
        Assert.Equal("⟦no.such.key⟧", Loc.T("no.such.key"));
        Assert.Contains("no.such.key", Loc.MissingKeys);
    }

    [Fact]
    public void Every_component_family_and_sensor_kind_has_a_label()
    {
        foreach (var k in Enum.GetValues<HardwareKind>()) Assert.True(Loc.Has("hw." + k), "hw." + k);
        foreach (var k in Enum.GetValues<SensorKind>()) Assert.True(Loc.Has("kind." + k), "kind." + k);
    }

    /// <summary>Every key the app's XAML ({l:Tr key}) and code (Loc.T / Loc.Plural / Tr("…")) asks for exists.</summary>
    [Fact]
    public void Every_key_used_in_the_source_exists()
    {
        var src = SourceDir();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(src, "*.xaml", SearchOption.AllDirectories))
            foreach (Match m in XamlKey().Matches(File.ReadAllText(file))) used.Add(m.Groups[1].Value);
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.Combine("obj", ""), StringComparison.OrdinalIgnoreCase)) continue;
            var code = string.Join(Environment.NewLine, File.ReadLines(file).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            foreach (Match m in CodeKey().Matches(code))
            {
                var key = m.Groups[2].Value;
                if (!key.EndsWith('.')) used.Add(key); // "hw." + kind: covered by Every_component_family_and_sensor_kind_has_a_label
            }
        }
        Assert.NotEmpty(used);
        var missing = used.Where(k => !Loc.Has(k) && !Loc.Has(k + ".one")).Order().ToList();
        Assert.True(missing.Count == 0, "missing: " + string.Join(", ", missing));
    }

    private static string SourceDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "BetterVitals");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("src/BetterVitals not found above " + AppContext.BaseDirectory);
    }

    private static string[] Holes(string s) => Hole().Matches(s).Select(m => m.Groups[1].Value).Order().ToArray();

    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex Hole();

    [GeneratedRegex(@"\{l:Tr\s+([\w.]+)\}")]
    private static partial Regex XamlKey();

    [GeneratedRegex(@"\b(Loc\.T|Loc\.Plural|Tr)\(""([\w.]+)""")]
    private static partial Regex CodeKey();
}
