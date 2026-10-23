using System.Reflection;
using BetterVitals.Sensors.Lhm;
using LibreHardwareMonitor.Hardware;
using Xunit;

namespace BetterVitals.Tests.Lhm;

/// <summary>
/// The reflection tweaks on LibreHardwareMonitor internals (perf): they must resolve on the pinned LHM build, and the
/// load-only CPU update must give the same values the source reports after a full update.
/// </summary>
public class LhmTweaksTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void Nvidia_poll_members_exist_on_the_pinned_lhm()
    {
        var group = typeof(Computer).Assembly.GetType("LibreHardwareMonitor.Hardware.Gpu.NvidiaGroup");
        Assert.NotNull(group);
        Assert.Equal(typeof(CancellationTokenSource), group!.GetField("_cancellationTokenSource", Private)?.FieldType);
        Assert.NotNull(group.GetMethod("RefreshHardware", Private, [typeof(bool)]));
        Assert.NotNull(typeof(Computer).GetField("_groups", Private));
    }

    [Fact]
    public void Power_topology_gate_member_exists_on_the_pinned_lhm()
    {
        var nvapi = typeof(Computer).Assembly.GetType("LibreHardwareMonitor.Interop.NvApi");
        var field = nvapi?.GetField("<NvAPI_GPU_ClientPowerTopologyGetStatus>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        Assert.False(field!.IsInitOnly);
        var invoke = field.FieldType.GetMethod("Invoke")!;
        Assert.Equal(2, invoke.GetParameters().Length);
        Assert.True(invoke.GetParameters()[1].ParameterType.IsByRef);
        Assert.True(invoke.ReturnType.IsEnum);
        // the gated call feeds only these (GPU Power / GPU Board Power)
        var powers = typeof(Computer).Assembly.GetType("LibreHardwareMonitor.Hardware.Gpu.NvidiaGpu")!.GetField("_powers", Private);
        Assert.True(powers is not null && typeof(ISensor[]).IsAssignableFrom(powers.FieldType));
    }

    [Theory]
    [InlineData(HardwareType.GpuNvidia, SensorType.Load, "GPU Power", true)]
    [InlineData(HardwareType.GpuNvidia, SensorType.Load, "GPU Board Power", true)]
    [InlineData(HardwareType.GpuNvidia, SensorType.Power, "GPU Package", false)]
    [InlineData(HardwareType.GpuNvidia, SensorType.Load, "GPU Core", false)]
    [InlineData(HardwareType.GpuAmd, SensorType.Load, "GPU Power", false)]
    public void Power_topology_sensors_are_flagged(HardwareType hw, SensorType type, string name, bool expected)
        => Assert.Equal(expected, (LhmClassifier.Classify(hw, "NVIDIA GeForce RTX 5080", type, name) & LhmSensorFlags.NeedsPowerTopology) != 0);

    [Fact]
    public void Nvidia_tweaks_are_harmless_without_nvidia_groups()
    {
        var c = new Computer { IsMemoryEnabled = true };
        c.Open();
        try
        {
            Assert.Equal(0, LhmGpuTweaks.StopNvidiaPolling(c));
            LhmGpuTweaks.RefreshNvidiaGroups(c); // no throw
        }
        finally
        {
            c.Close();
        }
    }

    [Fact]
    public void Loads_only_cpu_update_fills_every_value_the_source_reports_without_driver()
    {
        Assert.True(LhmCpuTweaks.Available);
        var c = new Computer { IsCpuEnabled = true };
        c.Open();
        try
        {
            var cpus = c.Hardware.Where(h => h.HardwareType == HardwareType.Cpu).ToArray();
            Assert.NotEmpty(cpus);
            foreach (var cpu in cpus)
            {
                cpu.Update();
                Thread.Sleep(200);
                Assert.True(LhmCpuTweaks.UpdateLoadsOnly(cpu));

                // without a working driver the source reports only Load sensors (everything else is NeedsDriver → NaN) …
                foreach (var s in cpu.Sensors.Where(s => s.SensorType != SensorType.Load))
                    Assert.True(LhmClassifier.NeedsDriver(HardwareType.Cpu, cpu.Identifier.ToString(), s.SensorType), s.Name);

                // … and the load-only update fills all of them, with LHM's own semantics
                var loads = cpu.Sensors.Where(s => s.SensorType == SensorType.Load).ToArray();
                Assert.NotEmpty(loads);
                foreach (var s in loads)
                {
                    Assert.True(s.Value is >= 0f and <= 100f, $"{s.Name} = {s.Value}");
                }
                // per-thread loads: "CPU Core #3 Thread #2", or "CPU Core #3" without SMT
                var threads = loads.Where(s => s.Name is not ("CPU Total" or "CPU Core Max")).Select(s => s.Value!.Value).ToArray();
                Assert.NotEmpty(threads);
                var max = loads.Single(s => s.Name == "CPU Core Max").Value!.Value;
                Assert.Equal(threads.Max(), max, 3);
            }
        }
        finally
        {
            c.Close();
        }
    }

    [Fact]
    public void Loads_only_update_refuses_other_hardware()
    {
        var c = new Computer { IsMemoryEnabled = true };
        c.Open();
        try
        {
            Assert.All(c.Hardware, h => Assert.False(LhmCpuTweaks.UpdateLoadsOnly(h)));
        }
        finally
        {
            c.Close();
        }
    }
}
