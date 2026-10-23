using BetterVitals.Sensors.Native;
using BetterVitals.Sensors.Native.Gpu;
using BetterVitals.Sensors.Native.Interop;
using Xunit;

namespace BetterVitals.Tests.Native;

/// <summary>PDH instance names → adapter LUID, per-engine load aggregation, plausibility filters, power-state decoding.</summary>
public sealed class GpuParsingTests
{
    [Fact]
    public void Engine_instance_name_gives_luid_physical_index_and_engine()
    {
        Assert.True(PdhInstanceName.TryParse("pid_9836_luid_0x00000000_0x0001D851_phys_0_eng_0_engtype_3D", out var luid, out var phys, out var eng));
        Assert.Equal(0x0001D851L, luid);
        Assert.Equal(0, phys);
        Assert.Equal(0, eng);
    }

    [Fact]
    public void High_part_physical_index_and_two_digit_engine()
    {
        Assert.True(PdhInstanceName.TryParse("pid_4_luid_0x00000001_0x0000F3A5_phys_1_eng_12_engtype_3D", out var luid, out var phys, out var eng));
        Assert.Equal((1L << 32) | 0xF3A5, luid);
        Assert.Equal(1, phys);
        Assert.Equal(12, eng);
    }

    [Fact]
    public void Adapter_memory_instance_has_no_engine()
    {
        Assert.True(PdhInstanceName.TryParse("luid_0x00000000_0x0001EDD3_phys_0", out var luid, out var phys, out var eng));
        Assert.Equal(0x0001EDD3L, luid);
        Assert.Equal(0, phys);
        Assert.Equal(-1, eng);
    }

    [Fact]
    public void Hex_digits_may_be_lower_case_and_the_value_matches_the_d3dkmt_luid()
    {
        Assert.True(PdhInstanceName.TryParse("luid_0x00000000_0x0001edd3_phys_0", out var luid, out _, out _));
        Assert.Equal(new Luid { Low = 0x0001EDD3, High = 0 }.Value, luid);

        Assert.True(PdhInstanceName.TryParse("luid_0xFFFFFFFF_0x00000001_phys_0", out luid, out _, out _));
        Assert.Equal(new Luid { Low = 1, High = -1 }.Value, luid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pid_1234")]
    [InlineData("luid_0x")]
    [InlineData("luid_0x00000000")]
    [InlineData("luid_0x00000000_0x")]
    [InlineData("luid_0xZZ_0x00")]
    [InlineData("_total")]
    public void Malformed_names_are_rejected(string name)
        => Assert.False(PdhInstanceName.TryParse(name, out _, out _, out _));

    [Fact]
    public void Engine_load_is_the_busiest_engine_and_processes_on_one_engine_add_up()
    {
        var sums = new float[8 * 16];
        GpuCounters.Accumulate(sums, 0, 0, 30);  // process A on engine 0
        GpuCounters.Accumulate(sums, 0, 0, 20);  // process B on engine 0
        GpuCounters.Accumulate(sums, 0, 1, 10);  // process C on engine 1
        GpuCounters.Accumulate(sums, 1, 0, 5);   // another adapter

        Assert.Equal(50f, GpuCounters.Combine(sums, 0));
        Assert.Equal(5f, GpuCounters.Combine(sums, 1));
        Assert.Equal(0f, GpuCounters.Combine(sums, 2)); // nobody on a 3D engine = 0 %
    }

    [Fact]
    public void Engine_load_is_capped_at_100_and_an_odd_engine_index_does_not_overflow()
    {
        var sums = new float[8 * 16];
        GpuCounters.Accumulate(sums, 0, 0, 80);
        GpuCounters.Accumulate(sums, 0, 0, 70);
        GpuCounters.Accumulate(sums, 0, 500, 5);
        GpuCounters.Accumulate(sums, 0, -3, 5);

        Assert.Equal(100f, GpuCounters.Combine(sums, 0));
    }

    [Theory]
    [InlineData(0u, true)]    // unspecified
    [InlineData(1u, true)]    // D0
    [InlineData(2u, false)]   // D1
    [InlineData(3u, false)]   // D2
    [InlineData(4u, false)]   // D3
    [InlineData(5u, true)]    // out of range: do not block
    public void Power_state_decoding(uint mostRecentPowerState, bool awake)
        => Assert.Equal(awake, CmDevicePowerGate.State(mostRecentPowerState));

    [Theory]
    [InlineData(0u, false)]
    [InlineData(49u, false)]
    [InlineData(50u, true)]     // 5.0 C
    [InlineData(432u, true)]    // 43.2 C
    [InlineData(1500u, true)]   // 150 C
    [InlineData(1501u, false)]
    [InlineData(uint.MaxValue, false)]
    public void Gpu_temperature_is_deci_celsius_with_sane_bounds(uint deci, bool plausible)
        => Assert.Equal(plausible, Plausible.GpuTemperatureDeci(deci));

    [Theory]
    [InlineData(0UL, false)]
    [InlineData(3000UL, false)]                // the Radeon iGPU reports 3000 "Hz" for a 3000 MHz memory clock: not trusted
    [InlineData(600UL, false)]
    [InlineData(50_000_000UL, true)]
    [InlineData(15_801_000_000UL, true)]       // GDDR7 as the RTX 5080 reports it
    [InlineData(40_000_000_000UL, true)]
    [InlineData(40_000_000_001UL, false)]
    [InlineData(ulong.MaxValue, false)]
    public void Clocks_are_hertz_with_sane_bounds(ulong hz, bool plausible)
        => Assert.Equal(plausible, Plausible.ClockHz(hz));

    [Theory]
    [InlineData(0u, false)]
    [InlineData(1230u, true)]   // millivolts
    [InlineData(199u, false)]
    [InlineData(2500u, true)]
    [InlineData(2501u, false)]
    public void Voltage_is_milli_volts(uint mv, bool plausible)
        => Assert.Equal(plausible, Plausible.VoltageMilli(mv));

    [Theory]
    [InlineData(0u, true)]
    [InlineData(3000u, true)]
    [InlineData(20_000u, true)]
    [InlineData(20_001u, false)]
    [InlineData(uint.MaxValue, false)]
    public void Fan_rpm(uint rpm, bool plausible)
        => Assert.Equal(plausible, Plausible.FanRpm(rpm));

    [Theory]
    [InlineData(1, true)]
    [InlineData(40, true)]
    [InlineData(125, true)]
    [InlineData(0, false)]
    [InlineData(126, false)]
    [InlineData(-5, false)]
    public void Drive_temperature(int celsius, bool plausible)
        => Assert.Equal(plausible, Plausible.DriveTemperature(celsius));

    [Theory]
    [InlineData(99, false)]
    [InlineData(100, true)]
    [InlineData(6000, true)]
    [InlineData(32_000, true)]
    [InlineData(32_001, false)]
    [InlineData(0, false)]
    public void Memory_speed_in_mega_transfers(int mts, bool plausible)
        => Assert.Equal(plausible, Plausible.MemorySpeedMTs(mts));

    [Fact]
    public void Struct_sizes_match_the_windows_headers()
    {
        // the kernel rejects (STATUS_INVALID_PARAMETER) a query whose size is not exactly the structure's
        unsafe
        {
            Assert.Equal(64, sizeof(AdapterPerfData));
            Assert.Equal(40, sizeof(AdapterPerfDataCaps));
            Assert.Equal(56, sizeof(NodePerfData));
            Assert.Equal(20, sizeof(KmtAdapterInfo));
            Assert.Equal(24, sizeof(QueryAdapterInfo));
            Assert.Equal(1352, sizeof(MibIfRow2));
            Assert.Equal(48, sizeof(ProcessorPerformance));
            Assert.Equal(24, sizeof(PdhCounterItem));
            Assert.Equal(20, sizeof(DevPropKey));
            Assert.Equal(2080, sizeof(AdapterRegistryInfo));
        }
    }
}
