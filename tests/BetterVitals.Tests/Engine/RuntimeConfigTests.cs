using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace BetterVitals.Tests.Engine;

/// <summary>The lean-runtime knobs of the app (PROJEKT §4 / §11): measured choices, kept from silently going away.</summary>
public sealed class RuntimeConfigTests
{
    private static string AppProject()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "src", "BetterVitals", "BetterVitals.csproj");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("src/BetterVitals/BetterVitals.csproj not found above the test output");
    }

    private static Dictionary<string, string> Options()
        => XDocument.Load(AppProject()).Descendants("RuntimeHostConfigurationOption")
            .ToDictionary(e => (string)e.Attribute("Include")!, e => (string)e.Attribute("Value")!);

    [Fact]
    public void Gen0_budget_is_capped_and_the_gc_stays_lean()
    {
        var o = Options();
        // without the cap a 96 MB-L3 CPU gets a ~48 MB gen0 budget (measured) = ~44 MB more private working set over time
        Assert.True(o.TryGetValue("System.GC.Gen0MaxBudget", out var budget), "System.GC.Gen0MaxBudget missing");
        Assert.InRange(long.Parse(budget!), 1L << 20, 8L << 20);
        Assert.Equal("5", o["System.GC.ConserveMemory"]);
        Assert.Equal("false", o["System.GC.RetainVM"]);

        var csproj = XDocument.Load(AppProject());
        Assert.Equal("false", csproj.Descendants("ConcurrentGarbageCollection").Single().Value);
        Assert.Equal("false", csproj.Descendants("ServerGarbageCollection").Single().Value);
    }

    [Fact]
    public void The_built_runtimeconfig_carries_the_gen0_cap_when_present()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "BetterVitals.runtimeconfig.json");
        if (!File.Exists(path)) return; // only copied when the test output holds the app's runtimeconfig
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var props = doc.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties");
        Assert.True(props.TryGetProperty("System.GC.Gen0MaxBudget", out var v));
        Assert.InRange(v.ValueKind == JsonValueKind.Number ? v.GetInt64() : long.Parse(v.GetString()!), 1L << 20, 8L << 20);
    }
}
