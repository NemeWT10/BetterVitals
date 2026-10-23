using System.Runtime.InteropServices;
using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Native.Interop;
using Microsoft.Win32;

namespace BetterVitals.Sensors.Native.Cpu;

/// <summary>
/// CPU load fallback — only when upstream has no CPU hardware at all (LibreHardwareMonitor failed completely).
/// "CPU Total" and "CPU Core #n" from <c>NtQuerySystemInformation(SystemProcessorPerformanceInformation)</c>: per logical
/// processor busy time over the interval between two reads; a core's load is the busiest of its threads.
/// </summary>
internal sealed unsafe class CpuLoadModule(Func<string>? cpuName = null) : INativeModule
{
    internal const string TotalName = "CPU Total";

    private readonly Func<string> _cpuName = cpuName ?? ReadCpuName;
    private CpuNode? _node;

    public string Name => "cpu";

    public void Discover(InventoryBuilder b)
    {
        Release();
        if (b.Upstream.HasHardwareOfKind(HardwareKind.Cpu)) return;

        var logical = Environment.ProcessorCount;
        var (coreOfLogical, coreCount) = CoreMap.Read(logical);
        var node = new CpuNode(logical, coreOfLogical, coreCount);
        if (!node.Prime()) { node.Release(); return; }

        var hardware = b.AddHardware(HardwareKind.Cpu, LhmNames.Cpu(_cpuName()), 0);
        b.AddSensor(hardware, HardwareKind.Cpu, SensorKind.Load, TotalName, node, 0);
        for (var c = 0; c < coreCount; c++)
            b.AddSensor(hardware, HardwareKind.Cpu, SensorKind.Load, $"CPU Core #{c + 1}", node, 1 + c);
        _node = node;
    }

    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("ProcessorNameString") is string s && s.Trim().Length > 0) return s;
        }
        catch
        {
            // fall through to the generic name
        }
        return "CPU";
    }

    public void Release()
    {
        _node?.Release();
        _node = null;
    }

    public void Dispose() => Release();

    /// <summary>Per-logical-processor deltas of idle / total time, folded into cores and the overall load.</summary>
    internal sealed class CpuNode : NativeNode
    {
        private static readonly long Stale = Ticks(TimeSpan.FromSeconds(30));
        private readonly int _logical;
        private readonly int[] _coreOfLogical;
        private ProcessorPerformance* _buffer;
        private readonly long[] _prevIdle;
        private readonly long[] _prevTotal;
        private readonly float[] _values; // [0] = total, [1 + c] = core c
        private long _baseline;

        public CpuNode(int logical, int[] coreOfLogical, int coreCount)
        {
            _logical = logical;
            _coreOfLogical = coreOfLogical;
            _buffer = (ProcessorPerformance*)NativeMemory.AllocZeroed((nuint)(logical * sizeof(ProcessorPerformance)));
            _prevIdle = new long[logical];
            _prevTotal = new long[logical];
            _values = new float[1 + coreCount];
            Array.Fill(_values, float.NaN);
        }

        /// <summary>Takes the first sample (the baseline the first real reading is measured against).</summary>
        public bool Prime()
        {
            _baseline = 0;
            return Sample(System.Diagnostics.Stopwatch.GetTimestamp(), compute: false);
        }

        internal override void Refresh(long timestamp)
        {
            var fresh = _baseline != 0 && timestamp - _baseline <= Stale;
            if (!Sample(timestamp, compute: fresh) || !fresh)
                Array.Fill(_values, float.NaN); // a window older than Stale is no "now": this read only re-bases
        }

        private bool Sample(long timestamp, bool compute)
        {
            if (_buffer is null) return false;
            var status = Kernel32.NtQuerySystemInformation(Kernel32.SystemProcessorPerformanceInformation, _buffer,
                (uint)(_logical * sizeof(ProcessorPerformance)), out var returned);
            if (status != 0) return false;
            var count = Math.Min(_logical, (int)(returned / (uint)sizeof(ProcessorPerformance)));
            if (count == 0) return false;

            double sumTotal = 0, sumBusy = 0;
            if (compute) Array.Fill(_values, 0f, 1, _values.Length - 1);
            for (var i = 0; i < count; i++)
            {
                var p = _buffer[i];
                var total = p.KernelTime + p.UserTime; // KernelTime already includes idle, DPC and interrupt time
                var idle = p.IdleTime;
                if (compute)
                {
                    long dTotal = total - _prevTotal[i], dIdle = idle - _prevIdle[i];
                    if (dTotal > 0)
                    {
                        var busy = Math.Clamp(dTotal - dIdle, 0, dTotal);
                        sumTotal += dTotal;
                        sumBusy += busy;
                        var load = (float)(100.0 * busy / dTotal);
                        var core = _coreOfLogical[i];
                        if ((uint)(1 + core) < (uint)_values.Length && load > _values[1 + core]) _values[1 + core] = load;
                    }
                }
                _prevTotal[i] = total;
                _prevIdle[i] = idle;
            }
            _baseline = timestamp;
            if (compute) _values[0] = sumTotal > 0 ? (float)(100.0 * sumBusy / sumTotal) : float.NaN;
            return true;
        }

        internal override float Read(int slot) => (uint)slot < (uint)_values.Length ? _values[slot] : float.NaN;

        internal override void Fault() => Array.Fill(_values, float.NaN);

        internal override void Release()
        {
            if (_buffer is not null) NativeMemory.Free(_buffer);
            _buffer = null;
        }
    }
}
