using System.Reflection;
using System.Reflection.Emit;
using BetterVitals.Core;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Cpu;

namespace BetterVitals.Sensors.Lhm;

/// <summary>
/// CPU update without the driver. Every CPU sensor except the loads needs PawnIO (MSRs / SMU), and without it the source
/// reports those as NaN anyway — yet LHM's AMD / Intel update still walks every core, pinning the engine thread to each
/// one in turn for MSR reads that fail (measured on a Ryzen 7 9800X3D unelevated: 3.4–7 ms and ~25 thread migrations per
/// update, each waking an idle core). <see cref="UpdateLoadsOnly"/> runs only the base <c>GenericCpu.Update</c> — LHM's own
/// code that fills "CPU Total", "CPU Core Max" and the per-thread loads — so every value the source reports stays the same.
/// </summary>
internal static class LhmCpuTweaks
{
    private static readonly Action<GenericCpu>? BaseUpdate = CreateBaseUpdate();

    /// <summary>The non-virtual call could be built (false = other LHM version: the full update runs).</summary>
    public static bool Available => BaseUpdate is not null;

    /// <summary>Updates only the load sensors of an LHM CPU node. False when not applicable (the caller does the full update).</summary>
    public static bool UpdateLoadsOnly(IHardware hardware)
    {
        if (BaseUpdate is null || hardware is not GenericCpu cpu) return false;
        BaseUpdate(cpu);
        return true;
    }

    /// <summary><c>((GenericCpu)x).Update()</c> as a non-virtual call (C# cannot call a base method on another object).</summary>
    private static Action<GenericCpu>? CreateBaseUpdate()
    {
        try
        {
            var update = typeof(GenericCpu).GetMethod(nameof(GenericCpu.Update), BindingFlags.Instance | BindingFlags.Public, Type.EmptyTypes);
            if (update is null || update.DeclaringType != typeof(GenericCpu) || update.ReturnType != typeof(void)) return null;
            var method = new DynamicMethod("GenericCpuBaseUpdate", null, [typeof(GenericCpu)], typeof(LhmCpuTweaks).Module, skipVisibility: true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, update);
            il.Emit(OpCodes.Ret);
            return method.CreateDelegate<Action<GenericCpu>>();
        }
        catch (Exception ex)
        {
            Log.Warn($"lhm: load-only CPU update unavailable: {ex.Message}");
            return null;
        }
    }
}
