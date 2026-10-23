using System.Linq.Expressions;
using System.Reflection;
using BetterVitals.Core;
using LibreHardwareMonitor.Hardware;

namespace BetterVitals.Sensors.Lhm;

/// <summary>
/// Skips the two expensive parts of an LHM GPU update when none of their sensors is requested (measured on RTX 5080 +
/// Radeon iGPU: NVML PCIe throughput blocks ~32 ms per counter, i.e. ~63 of the ~72 ms of an NVIDIA update; the D3DKMT
/// statistics query costs ~1.7 ms CPU on NVIDIA and ~4.7 of the ~5.5 ms on the AMD iGPU).
/// Both use reflection on LHM internals; when a member is missing (other LHM version) the tweak is simply off.
/// Engine thread only.
/// </summary>
internal static class LhmGpuTweaks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>Read by the NVML wrapper: false = answer "not supported" without calling NVML.</summary>
    public static volatile bool PcieWanted = true;

    private static Delegate? _pcieWrapper;

    /// <summary>
    /// Wraps <c>NvidiaML._windowsNvmlDeviceGetPcieThroughputDelegate</c> so it honors <see cref="PcieWanted"/>.
    /// Call after every (re)open: NVML re-initialization replaces the delegate.
    /// </summary>
    public static bool InstallPcieGate()
    {
        try
        {
            var nvml = typeof(Computer).Assembly.GetType("LibreHardwareMonitor.Interop.NvidiaML");
            var field = nvml?.GetField("_windowsNvmlDeviceGetPcieThroughputDelegate", BindingFlags.Static | BindingFlags.NonPublic);
            if (field?.GetValue(null) is not Delegate original) return false;
            if (ReferenceEquals(original, _pcieWrapper)) return true;

            var invoke = field.FieldType.GetMethod("Invoke")!;
            var ps = invoke.GetParameters();
            if (ps.Length != 3 || !ps[2].ParameterType.IsByRef || ps[2].ParameterType.GetElementType() != typeof(uint)) return false;
            var args = ps.Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
            var notSupported = Expression.Convert(Expression.Constant(3), invoke.ReturnType); // NvmlReturn.NotSupported
            var body = Expression.Condition(
                Expression.Field(null, typeof(LhmGpuTweaks).GetField(nameof(PcieWanted))!),
                Expression.Invoke(Expression.Constant(original), args.Cast<Expression>()),
                Expression.Block(Expression.Assign(args[2], Expression.Constant(0u)), notSupported));
            var wrapper = Expression.Lambda(field.FieldType, body, args).Compile();
            field.SetValue(null, wrapper);
            _pcieWrapper = wrapper;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"lhm: NVML PCIe gate not installed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The private D3D adapter id of an NVIDIA / AMD GPU node (LHM skips its D3DKMT query while it is null), or null
    /// when not applicable. Intel GPUs are left alone (they re-resolve a missing id, which is slower).
    /// </summary>
    public static FieldInfo? D3dField(IHardware hw, out string? value)
    {
        value = null;
        var t = hw.GetType();
        if (t.Name is not ("NvidiaGpu" or "AmdGpu")) return null;
        var f = t.GetField("_d3dDeviceId", Private);
        if (f is null || f.FieldType != typeof(string)) return null;
        value = f.GetValue(hw) as string;
        return value is null ? null : f;
    }
}
