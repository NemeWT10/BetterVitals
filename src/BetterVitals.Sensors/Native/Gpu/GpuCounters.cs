using System.Diagnostics;
using System.Runtime.InteropServices;
using BetterVitals.Sensors.Native.Interop;

namespace BetterVitals.Sensors.Native.Gpu;

/// <summary>
/// The "GPU Engine" and "GPU Adapter Memory" performance counters through PDH, grouped per adapter LUID. One query shared by
/// all adapters; one collection per tick (calls closer than 250 ms reuse the last one). Counter arrays are parsed in place
/// in a preallocated native buffer, so a collection allocates nothing.
/// Load of an adapter = the busiest 3D engine of it (each engine's value is the sum over all processes using it).
/// <para>
/// Opening the query is expensive — the first <c>PdhAddEnglishCounter</c> loads the English counter-name tables (measured on
/// this PC: ~450 ms and ~7 MB private memory, not given back) — so it happens lazily on the first collection, i.e. only when
/// somebody actually reads a PDH sensor, never at discovery.
/// </para>
/// </summary>
internal sealed unsafe class GpuCounters : IDisposable
{
    internal const string EnginePath = @"\GPU Engine(*engtype_3D)\Utilization Percentage";
    internal const string DedicatedPath = @"\GPU Adapter Memory(*)\Dedicated Usage";
    internal const string SharedPath = @"\GPU Adapter Memory(*)\Shared Usage";

    private const int MaxAdapters = 8;
    private const int MaxEngines = 16;
    private const double Mb = 1024.0 * 1024;
    private static readonly long MinSpacing = Stopwatch.Frequency / 4;      // 250 ms
    private static readonly long Stale = Stopwatch.Frequency * 30;          // a rate over more than 30 s is not "now" (the slowest recorder asks every 10 s)

    private readonly long[] _luids = new long[MaxAdapters];
    private readonly int _adapterCount;
    private readonly float[] _load = new float[MaxAdapters];
    private readonly float[] _dedicatedMb = new float[MaxAdapters];
    private readonly float[] _sharedMb = new float[MaxAdapters];
    private readonly float[] _engineSum = new float[MaxAdapters * MaxEngines];
    private nint _query, _engine, _dedicated, _shared;
    private byte* _buffer;
    private uint _bufferSize;
    private long _lastCollect;
    private bool _opened;
    private bool _disposed;

    /// <param name="luids">Adapters to report, in slot order (more than eight are ignored).</param>
    public GpuCounters(IReadOnlyList<long> luids)
    {
        _adapterCount = Math.Min(luids.Count, MaxAdapters);
        for (var i = 0; i < _adapterCount; i++) _luids[i] = luids[i];
        Array.Fill(_load, float.NaN);
        Array.Fill(_dedicatedMb, float.NaN);
        Array.Fill(_sharedMb, float.NaN);
    }

    /// <summary>The counters could not be opened (no GPU counters on this Windows / PDH failed); values stay NaN.</summary>
    public bool Failed { get; private set; }

    public bool IsOpen => _opened;

    public float Load3D(int slot) => (uint)slot < (uint)_adapterCount ? _load[slot] : float.NaN;

    public float DedicatedMb(int slot) => (uint)slot < (uint)_adapterCount ? _dedicatedMb[slot] : float.NaN;

    public float SharedMb(int slot) => (uint)slot < (uint)_adapterCount ? _sharedMb[slot] : float.NaN;

    /// <summary>
    /// Collects (opening the query first if this is the first call). The rate counters need two samples: the first
    /// collection after opening, or after a long pause, only re-primes them and leaves the loads at NaN; memory is valid at once.
    /// </summary>
    public bool Collect(long timestamp)
    {
        if (Failed || _disposed) return false;
        if (!_opened && !Open()) return false;
        if (_lastCollect != 0 && timestamp - _lastCollect < MinSpacing) return true;
        if (Pdh.PdhCollectQueryData(_query) != 0) return false;
        var rateIsCurrent = _lastCollect != 0 && timestamp - _lastCollect <= Stale;
        _lastCollect = timestamp;

        ReadMemory(_dedicated, _dedicatedMb);
        ReadMemory(_shared, _sharedMb);
        ReadEngines(rateIsCurrent);
        return true;
    }

    private bool Open()
    {
        try
        {
            if (Pdh.PdhOpenQueryW(0, 0, out _query) != 0) return Fail();
            if (Pdh.PdhAddEnglishCounterW(_query, EnginePath, 0, out _engine) != 0) _engine = 0;
            if (Pdh.PdhAddEnglishCounterW(_query, DedicatedPath, 0, out _dedicated) != 0) _dedicated = 0;
            if (Pdh.PdhAddEnglishCounterW(_query, SharedPath, 0, out _shared) != 0) _shared = 0;
            if (_engine == 0 && _dedicated == 0 && _shared == 0) return Fail();
            _bufferSize = 64 * 1024;
            _buffer = (byte*)NativeMemory.Alloc(_bufferSize);
            _opened = true;
            return true;
        }
        catch
        {
            return Fail();
        }
    }

    private bool Fail()
    {
        Failed = true;
        CloseQuery();
        return false;
    }

    private void ReadMemory(nint counter, float[] target)
    {
        if (counter == 0 || !ReadArray(counter, out var count)) return;
        var items = (PdhCounterItem*)_buffer;
        for (uint i = 0; i < count; i++)
        {
            ref var item = ref items[i];
            if (item.Status > 1 || item.Name is null) continue; // PDH_CSTATUS_VALID_DATA / NEW_DATA only
            if (!PdhInstanceName.TryParse(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(item.Name), out var luid, out _, out _)) continue;
            var slot = IndexOf(luid);
            if (slot >= 0) target[slot] = (float)(item.Value / Mb);
        }
    }

    private void ReadEngines(bool rateIsCurrent)
    {
        if (_engine == 0) return;
        Array.Clear(_engineSum);
        if (!rateIsCurrent || !ReadArray(_engine, out var count))
        {
            Array.Fill(_load, float.NaN, 0, _adapterCount);
            return;
        }
        var items = (PdhCounterItem*)_buffer;
        for (uint i = 0; i < count; i++)
        {
            ref var item = ref items[i];
            if (item.Status > 1 || item.Name is null) continue;
            if (!PdhInstanceName.TryParse(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(item.Name), out var luid, out _, out var engine)) continue;
            var slot = IndexOf(luid);
            if (slot < 0) continue;
            Accumulate(_engineSum, slot, engine, (float)item.Value);
        }
        for (var slot = 0; slot < _adapterCount; slot++) _load[slot] = Combine(_engineSum, slot);
    }

    /// <summary>Adds one process' share of an engine (sums per adapter × engine index).</summary>
    internal static void Accumulate(Span<float> engineSums, int slot, int engine, float percent)
        => engineSums[slot * MaxEngines + Math.Clamp(engine, 0, MaxEngines - 1)] += percent;

    /// <summary>An adapter's 3D load: its busiest engine (the sums of all processes on it), at most 100 %; no process = 0 %.</summary>
    internal static float Combine(ReadOnlySpan<float> engineSums, int slot)
    {
        var max = 0f;
        for (var e = 0; e < MaxEngines; e++) max = Math.Max(max, engineSums[slot * MaxEngines + e]);
        return Math.Min(max, 100f);
    }

    private int IndexOf(long luid)
    {
        for (var i = 0; i < _adapterCount; i++)
            if (_luids[i] == luid) return i;
        return -1;
    }

    /// <summary>Formatted array into <see cref="_buffer"/>; grows the buffer once when PDH asks for more room.</summary>
    private bool ReadArray(nint counter, out uint count)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var size = _bufferSize;
            var status = Pdh.PdhGetFormattedCounterArrayW(counter, Pdh.FmtDouble | Pdh.FmtNoCap100, ref size, out count, _buffer);
            if (status == 0) return true;
            if (status != Pdh.MoreData || size <= _bufferSize || size > 64 * 1024 * 1024) return false;
            _buffer = (byte*)NativeMemory.Realloc(_buffer, size + size / 4);
            _bufferSize = size + size / 4;
        }
        count = 0;
        return false;
    }

    private void CloseQuery()
    {
        if (_query != 0) Pdh.PdhCloseQuery(_query);
        _query = _engine = _dedicated = _shared = 0;
        if (_buffer is not null) NativeMemory.Free(_buffer);
        _buffer = null;
        _opened = false;
    }

    public void Dispose()
    {
        _disposed = true; // a late Collect must not reopen the query
        CloseQuery();
    }
}
