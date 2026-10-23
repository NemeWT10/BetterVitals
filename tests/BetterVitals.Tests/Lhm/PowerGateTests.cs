using BetterVitals.Sensors.Power;
using Xunit;

namespace BetterVitals.Tests.Lhm;

public class PowerGateTests
{
    private const long Freq = 1000; // 1 tick = 1 ms

    [Theory]
    [InlineData(1u, PowerQuery.Awake)]
    [InlineData(2u, PowerQuery.Asleep)]
    [InlineData(3u, PowerQuery.Asleep)]
    [InlineData(4u, PowerQuery.Asleep)]
    [InlineData(0u, PowerQuery.Unknown)]
    [InlineData(5u, PowerQuery.Unknown)]
    public void Device_states(uint state, PowerQuery expected) => Assert.Equal(expected, PowerGateState.FromDeviceState(state));

    [Fact]
    public void First_access_always_queries()
    {
        var s = new PowerGateState();
        Assert.True(s.NeedsQuery(0, Freq));
    }

    [Fact]
    public void Awake_device_is_checked_before_every_access()
    {
        var s = new PowerGateState();
        s.Record(0, PowerQuery.Awake);
        Assert.True(s.AllowsAccess);
        Assert.True(s.NeedsQuery(1, Freq));
    }

    [Fact]
    public void Sleeping_device_is_rechecked_at_most_every_10_s()
    {
        var s = new PowerGateState();
        s.Record(0, PowerQuery.Asleep);
        Assert.False(s.AllowsAccess);
        Assert.False(s.NeedsQuery(1_000, Freq));
        Assert.False(s.NeedsQuery(9_999, Freq));
        Assert.True(s.NeedsQuery(10_000, Freq));
        s.Record(10_000, PowerQuery.Asleep);
        Assert.False(s.NeedsQuery(15_000, Freq));
        s.Record(20_000, PowerQuery.Awake);
        Assert.True(s.AllowsAccess);
    }

    [Fact]
    public void Gone_blocks_access_and_unknown_allows_it()
    {
        var s = new PowerGateState();
        s.Record(0, PowerQuery.Gone);
        Assert.False(s.AllowsAccess);
        s.Record(0, PowerQuery.Unknown);
        Assert.True(s.AllowsAccess);
    }

    [Fact]
    public void Gate_never_queries_a_sleeping_device_more_often_than_the_policy()
    {
        var gate = new FakeGate(PowerQuery.Asleep);
        var t = 0L;
        for (var i = 0; i < 100; i++, t += System.Diagnostics.Stopwatch.Frequency / 10) // 10 s of 100 ms ticks
            Assert.False(gate.IsAwake(t));
        Assert.Equal(1, gate.Calls);
        Assert.False(gate.IsAwake(t)); // exactly 10 s later
        Assert.Equal(2, gate.Calls);
    }

    [Theory]
    [InlineData(@"\\?\PCI#VEN_1002&DEV_731F&SUBSYS_57051682&REV_C4#6&e539058&0&00000019#{1ca05180-a699-450a-9a0c-de4fbe3ddd89}",
        @"PCI\VEN_1002&DEV_731F&SUBSYS_57051682&REV_C4\6&e539058&0&00000019")]
    [InlineData(@"PCI\VEN_10DE&DEV_2C02&SUBSYS_41761458&REV_A1\4&1&0&0008", @"PCI\VEN_10DE&DEV_2C02&SUBSYS_41761458&REV_A1\4&1&0&0008")]
    [InlineData(@"\\?\ROOT#BasicRender#0000#{1ca05180-a699-450a-9a0c-de4fbe3ddd89}", null)]
    [InlineData("0x56A0", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Normalizes_device_paths_to_instance_ids(string? input, string? expected)
        => Assert.Equal(expected, PnpIds.NormalizeInstanceId(input));

    [Fact]
    public void Parses_vendor_and_device()
    {
        Assert.True(PnpIds.TryParseVenDev(@"PCI\VEN_10DE&DEV_2C02&SUBSYS_1\x", out var v, out var d));
        Assert.Equal("10DE", v);
        Assert.Equal("2C02", d);
        Assert.True(PnpIds.TryParseVenDev("0x56a0", out v, out d));
        Assert.Null(v);
        Assert.Equal("56A0", d);
        Assert.False(PnpIds.TryParseVenDev("Intel Arc", out _, out _));
    }

    [Fact]
    public void Finds_a_unique_devnode_only()
    {
        string[] ids =
        [
            @"PCI\VEN_8086&DEV_56A0&SUBSYS_1\4&1&0&0008",
            @"PCI\VEN_10DE&DEV_2C02&SUBSYS_1\4&1&0&0010",
            @"PCI\VEN_10DE&DEV_2C02&SUBSYS_1\4&1&0&0018",
        ];
        Assert.Equal(ids[0], PnpIds.FindUnique(ids, "8086", "56A0"));
        Assert.Equal(ids[0], PnpIds.FindUnique(ids, null, "56A0"));
        Assert.Null(PnpIds.FindUnique(ids, "10DE", "2C02")); // two identical cards: ambiguous
        Assert.Null(PnpIds.FindUnique(ids, "1002", "56A0"));
    }

    private sealed class FakeGate(PowerQuery result) : PowerGate
    {
        public int Calls { get; private set; }

        public override string Target => "fake";

        protected override PowerQuery Query()
        {
            Calls++;
            return result;
        }
    }
}
