using BetterVitals.Core.Glossary;
using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Derived;
using BetterVitals.Sensors.Engine;
using BetterVitals.Sensors.Fake;
using Xunit;

namespace BetterVitals.Tests.Engine;

public sealed class GlossaryTests
{
    [Theory]
    [InlineData(SensorKind.Voltage, HardwareKind.Cpu, "Core #3 VID")]
    [InlineData(SensorKind.Temperature, HardwareKind.Cpu, "Core (Tctl/Tdie)")]
    [InlineData(SensorKind.Temperature, HardwareKind.Gpu, "GPU Hot Spot")]
    [InlineData(SensorKind.FrameTime, HardwareKind.Game, "Frametime")]
    [InlineData(SensorKind.Fps, HardwareKind.Game, "1% Low")]
    [InlineData(SensorKind.Clock, HardwareKind.Cpu, "Core #12")]
    public void Known_sensors_have_a_specific_explanation_in_both_languages(SensorKind kind, HardwareKind hw, string name)
    {
        var en = Glossary.Lookup(kind, hw, name, polish: false);
        var pl = Glossary.Lookup(kind, hw, name, polish: true);
        Assert.NotNull(en);
        Assert.NotNull(pl);
        Assert.NotEqual(en!.What, pl!.What);
        Assert.InRange(en.What.Length, 10, 220);
    }

    [Fact]
    public void Unknown_names_fall_back_to_the_generic_text_of_their_kind()
    {
        var x = Glossary.Lookup(SensorKind.Voltage, HardwareKind.Other, "Totally Unknown Rail 7", polish: true);
        Assert.NotNull(x);
        Assert.Null(x!.Watch);
    }

    [Fact]
    public void Rtx50_hot_spot_says_it_is_unofficial()
    {
        var x = Glossary.Lookup(SensorKind.Temperature, HardwareKind.Gpu, "GPU Hot Spot", polish: false)!;
        Assert.Contains("1", (x.What + " " + x.Watch), StringComparison.Ordinal); // "±1 °C"
    }

    [Fact]
    public void Every_fake_sensor_gets_an_explanation()
    {
        foreach (var profile in Enum.GetValues<FakeProfile>())
        {
            var engine = new SensorEngine([new DerivedSource(), new FakeSensorSource(profile)]);
            engine.DiscoverNow();
            foreach (var s in engine.Sensors)
            {
                Assert.NotNull(Glossary.Lookup(s.Kind, s.HardwareKind, s.Name, polish: true));
                Assert.NotNull(Glossary.Lookup(s.Kind, s.HardwareKind, s.Name, polish: false));
            }
        }
    }

    [Fact]
    public void Patterns_escape_literals_and_match_whole_names()
    {
        var r = Glossary.ToRegex("Core #{n} (Tdie) +*");
        Assert.Matches(r, "core #12 (Tdie) +anything");
        Assert.DoesNotMatch(r, "Core #x (Tdie) +");
        Assert.DoesNotMatch(r, "XCore #1 (Tdie) +");
    }
}
