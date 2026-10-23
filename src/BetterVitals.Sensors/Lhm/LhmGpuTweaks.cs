using System.Linq.Expressions;
using System.Reflection;
using BetterVitals.Core;
using LibreHardwareMonitor.Hardware;

namespace BetterVitals.Sensors.Lhm;

/// <summary>
/// Skips the expensive parts of an LHM GPU update when none of their sensors is requested (measured on RTX 5080 +
/// Radeon iGPU: NVML PCIe throughput blocks ~32 ms per counter, i.e. ~63 of the ~72 ms of an NVIDIA update; the NVAPI
/// power topology costs ~6 ms CPU of the remaining ~7.5; the D3DKMT statistics query costs ~1.7 ms CPU on NVIDIA and
/// ~4.7 of the ~5.5 ms on the AMD iGPU), and replaces the NVIDIA group's 1 s hardware poll by device-change refreshes.
/// All use reflection on LHM internals; when a member is missing (other LHM version) the tweak is simply off.
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

    /// <summary>Read by the NVAPI power-topology wrapper: false = answer "not supported" without calling NVAPI.</summary>
    public static volatile bool PowerTopologyWanted = true;

    private static Delegate? _topologyWrapper;

    /// <summary>
    /// Wraps <c>NvApi.NvAPI_GPU_ClientPowerTopologyGetStatus</c> so it honors <see cref="PowerTopologyWanted"/>. That one
    /// call is ~6 of the ~7.5 ms CPU of an RTX 5080 update (measured) and only feeds the "GPU Power" / "GPU Board Power"
    /// loads (% of the power limit; "GPU Package" in W comes from NVML). LHM keeps those sensors when the call fails, it
    /// just does not refresh them. Call after every (re)open and NVIDIA re-enumeration: NVAPI initialization replaces it.
    /// </summary>
    public static bool InstallPowerTopologyGate()
    {
        try
        {
            var nvapi = typeof(Computer).Assembly.GetType("LibreHardwareMonitor.Interop.NvApi");
            var field = nvapi?.GetField("<NvAPI_GPU_ClientPowerTopologyGetStatus>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
            if (field is null || field.IsInitOnly || field.GetValue(null) is not Delegate original) return false;
            if (ReferenceEquals(original, _topologyWrapper)) return true;

            var invoke = field.FieldType.GetMethod("Invoke")!;
            var ps = invoke.GetParameters();
            if (ps.Length != 2 || !ps[1].ParameterType.IsByRef || !invoke.ReturnType.IsEnum) return false;
            var args = ps.Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
            var notSupported = Expression.Convert(Expression.Constant(-104), invoke.ReturnType); // NvStatus.NOT_SUPPORTED
            var body = Expression.Condition(
                Expression.Field(null, typeof(LhmGpuTweaks).GetField(nameof(PowerTopologyWanted))!),
                Expression.Invoke(Expression.Constant(original), args.Cast<Expression>()),
                notSupported);
            var wrapper = Expression.Lambda(field.FieldType, body, args).Compile();
            field.SetValue(null, wrapper);
            _topologyWrapper = wrapper;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"lhm: NVAPI power topology gate not installed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Stops the hardware poll of LHM's NVIDIA group: a thread-pool loop that re-enumerates the GPUs through NVAPI every
    /// second, forever (measured: ~20 wake-ups/s and ~0.25 % of a core in the tray, the largest idle cost of the app).
    /// <see cref="RefreshNvidiaGroups"/> does the same check on demand — on WM_DEVICECHANGE, which a GPU / driver change
    /// raises. Call after every (re)open. Returns the number of groups stopped.
    /// </summary>
    public static int StopNvidiaPolling(Computer computer)
    {
        var stopped = 0;
        try
        {
            foreach (var group in NvidiaGroups(computer))
            {
                if (group.GetType().GetField("_cancellationTokenSource", Private)?.GetValue(group) is not CancellationTokenSource cts) continue;
                cts.Cancel(); // the loop ends; NvidiaGroup.Close tolerates the cancelled task
                // a refresh may be running right now (Open takes longer than the first 1 s delay): let it finish, it
                // re-initializes NVAPI and would replace the gates installed after this
                if (group.GetType().GetField("_monitorTask", Private)?.GetValue(group) is Task task)
                {
                    try
                    {
                        task.Wait(TimeSpan.FromSeconds(3));
                    }
                    catch (AggregateException)
                    {
                        // cancelled: expected
                    }
                }
                stopped++;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"lhm: NVIDIA poll not stopped: {ex.Message}");
        }
        return stopped;
    }

    /// <summary>
    /// What the stopped poll did once a second: re-enumerates NVIDIA GPUs (added / removed / driver restarted), raising
    /// LHM's hardware events. Engine thread, on a device change.
    /// </summary>
    public static void RefreshNvidiaGroups(Computer computer)
    {
        try
        {
            foreach (var group in NvidiaGroups(computer))
                group.GetType().GetMethod("RefreshHardware", Private, [typeof(bool)])?.Invoke(group, [true]);
            InstallPowerTopologyGate(); // the refresh re-initializes NVAPI (new delegates)
            InstallPcieGate();
        }
        catch (Exception ex)
        {
            Log.Warn($"lhm: NVIDIA re-enumeration failed: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    private static IEnumerable<object> NvidiaGroups(Computer computer)
    {
        if (typeof(Computer).GetField("_groups", Private)?.GetValue(computer) is not System.Collections.IEnumerable groups) return [];
        return groups.Cast<object>().Where(g => g.GetType().FullName == "LibreHardwareMonitor.Hardware.Gpu.NvidiaGroup").ToList();
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
