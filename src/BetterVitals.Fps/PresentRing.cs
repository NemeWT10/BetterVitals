using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BetterVitals.Fps.Interop;

namespace BetterVitals.Fps;

/// <summary>
/// State shared with the ETW callback, in native memory (no GC, no handles on the hot path).
/// Single producer (the ETW consumer thread), any number of readers.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PresentShared
{
    /// <summary>Process whose presents are kept; 0 = none. Written by the engine, read per event.</summary>
    public uint TargetPid;

    /// <summary>Ring size, a power of two.</summary>
    public int Capacity;

    /// <summary>Number of entries ever written (next slot = WriteIndex &amp; (Capacity - 1)). Producer only.</summary>
    public long WriteIndex;

    /// <summary>A present class is dropped when a preferred class was seen within this many QPC ticks.</summary>
    public long DedupeWindow;

    /// <summary>QPC of the last accepted-or-suppressed present per <see cref="PresentClass"/>.</summary>
    public fixed long LastSeen[3];

    /// <summary>All events delivered to the callback (diagnostics).</summary>
    public long EventsSeen;

    /// <summary>Events stored in the ring (diagnostics).</summary>
    public long EventsStored;

    /// <summary>Present model of the last DxgKrnl PresentHistory_Start of the target (diagnostics).</summary>
    public uint LastKernelModel;

    /// <summary>QPC timestamps, <see cref="Capacity"/> entries.</summary>
    public long* Timestamps;

    /// <summary>Swap chain key per entry (0 for kernel presents), <see cref="Capacity"/> entries.</summary>
    public uint* Keys;
}

/// <summary>
/// Preallocated lock-free ring of present timestamps for the target process, plus the ETW event callback that fills it.
/// </summary>
internal sealed unsafe class PresentRing : IDisposable
{
    /// <summary>~8 s at 1000 FPS, ~57 s at 144 FPS; 96 KB.</summary>
    public const int DefaultCapacity = 8192;

    private PresentShared* _shared;

    public PresentRing(int capacity = DefaultCapacity, long? dedupeWindowQpc = null)
    {
        if (capacity < 2 || (capacity & (capacity - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _shared = (PresentShared*)NativeMemory.AllocZeroed((nuint)sizeof(PresentShared));
        _shared->Capacity = capacity;
        _shared->Timestamps = (long*)NativeMemory.AllocZeroed((nuint)capacity, sizeof(long));
        _shared->Keys = (uint*)NativeMemory.AllocZeroed((nuint)capacity, sizeof(uint));
        _shared->DedupeWindow = dedupeWindowQpc ?? System.Diagnostics.Stopwatch.Frequency; // 1 s
        for (var i = 0; i < 3; i++) _shared->LastSeen[i] = long.MinValue / 2;
    }

    public PresentShared* Shared => _shared;

    public int Capacity => _shared->Capacity;

    /// <summary>Native bytes held by the ring.</summary>
    public long NativeBytes => sizeof(PresentShared) + (long)Capacity * (sizeof(long) + sizeof(uint));

    public uint TargetPid
    {
        get => Volatile.Read(ref _shared->TargetPid);
        set => Volatile.Write(ref _shared->TargetPid, value);
    }

    public long WriteIndex => Volatile.Read(ref _shared->WriteIndex);

    public long EventsSeen => Volatile.Read(ref _shared->EventsSeen);

    public long EventsStored => Volatile.Read(ref _shared->EventsStored);

    /// <summary>
    /// Copies the entries in [<paramref name="fromIndex"/>, WriteIndex) that are still intact into the spans, oldest first.
    /// Returns the count; <paramref name="endIndex"/> = WriteIndex at the time of the copy.
    /// </summary>
    public int Snapshot(long fromIndex, Span<long> timestamps, Span<uint> keys, out long endIndex)
    {
        var s = _shared;
        var cap = s->Capacity;
        var mask = cap - 1;
        var end = Volatile.Read(ref s->WriteIndex);
        var start = Math.Max(fromIndex, end - Math.Min(cap, timestamps.Length));
        if (start < 0) start = 0;
        var n = (int)Math.Max(0, end - start);
        for (var i = 0; i < n; i++)
        {
            var slot = (int)((start + i) & mask);
            timestamps[i] = s->Timestamps[slot];
            keys[i] = s->Keys[slot];
        }
        Interlocked.MemoryBarrier();
        // entries the producer may have overwritten while we copied
        var firstIntact = Volatile.Read(ref s->WriteIndex) - cap;
        var skip = (int)Math.Clamp(firstIntact - start, 0, n);
        if (skip > 0)
        {
            timestamps.Slice(skip, n - skip).CopyTo(timestamps);
            keys.Slice(skip, n - skip).CopyTo(keys);
            n -= skip;
        }
        endIndex = end;
        return n;
    }

    /// <summary>
    /// Hot path (one call per present event of a watched provider): PID filter, class de-duplication, ring push.
    /// No allocation, no locks, no TDH.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void OnPresent(PresentShared* s, uint pid, long timestamp, PresentClass cls, uint key)
    {
        if (pid != Volatile.Read(ref s->TargetPid) || pid == 0) return;
        var c = (int)cls;
        s->LastSeen[c] = timestamp;
        for (var k = 0; k < c; k++)
        {
            // a preferred source is active for this process (e.g. DXGI and the DxgKrnl token of the same present)
            if (timestamp - s->LastSeen[k] < s->DedupeWindow) return;
        }
        var i = s->WriteIndex;
        var slot = (int)(i & (s->Capacity - 1));
        s->Timestamps[slot] = timestamp;
        s->Keys[slot] = key;
        s->EventsStored++;
        Volatile.Write(ref s->WriteIndex, i + 1);
    }

    /// <summary>The ETW EventRecordCallback. <c>UserContext</c> is the <see cref="PresentShared"/> of the session.</summary>
    [UnmanagedCallersOnly]
    public static void OnEvent(EventRecord* r)
    {
        var s = (PresentShared*)r->UserContext;
        if (s == null) return;
        s->EventsSeen++;
        var pid = r->ProcessId;
        if (pid != Volatile.Read(ref s->TargetPid)) return; // other processes: dropped right away

        var id = r->Id;
        var len = r->UserDataLength;
        var data = r->UserData;
        var ptrSize = (r->Flags & Etw.EventHeaderFlag32BitHeader) != 0 ? 4 : 8;

        if (r->ProviderIdLo == DxgiLo && r->ProviderIdHi == DxgiHi)
        {
            if (id != PresentProviders.DxgiPresentStart && id != PresentProviders.DxgiPresentMultiplaneOverlayStart) return;
            // payload: pIDXGISwapChain (pointer), Flags (UInt32), SyncInterval (Int32)
            if (len >= ptrSize + 4 && (*(uint*)(data + ptrSize) & PresentProviders.DxgiPresentTest) != 0) return;
            OnPresent(s, pid, r->TimeStamp, PresentClass.Dxgi, SwapChainKey(data, len, ptrSize));
        }
        else if (r->ProviderIdLo == D3D9Lo && r->ProviderIdHi == D3D9Hi)
        {
            if (id != PresentProviders.D3D9PresentStart) return;
            // payload: pSwapchain (pointer), Flags (UInt32)
            OnPresent(s, pid, r->TimeStamp, PresentClass.D3D9, SwapChainKey(data, len, ptrSize));
        }
        else if (r->ProviderIdLo == DxgKrnlLo && r->ProviderIdHi == DxgKrnlHi)
        {
            if (id != PresentProviders.DxgKrnlPresentHistoryStart && id != PresentProviders.DxgKrnlPresentHistoryDetailedStart) return;
            const int off = PresentProviders.PresentHistoryModelOffset64;
            if (len >= off + 4 && ptrSize == 8)
            {
                var model = *(uint*)(data + off);
                s->LastKernelModel = model;
                if (model is PresentProviders.ModelRedirectedGdi or PresentProviders.ModelRedirectedComposition) return;
            }
            OnPresent(s, pid, r->TimeStamp, PresentClass.Kernel, 0);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint SwapChainKey(byte* data, ushort len, int ptrSize)
    {
        if (len < ptrSize) return 1;
        if (ptrSize == 4) return *(uint*)data | 1;
        var p = *(ulong*)data;
        return (uint)(p ^ (p >> 32)) | 1; // never 0 (0 = kernel)
    }

    private static readonly ulong DxgiLo = GuidLo(PresentProviders.Dxgi), DxgiHi = GuidHi(PresentProviders.Dxgi);
    private static readonly ulong D3D9Lo = GuidLo(PresentProviders.D3D9), D3D9Hi = GuidHi(PresentProviders.D3D9);
    private static readonly ulong DxgKrnlLo = GuidLo(PresentProviders.DxgKrnl), DxgKrnlHi = GuidHi(PresentProviders.DxgKrnl);

    private static ulong GuidLo(Guid g) => Unsafe.As<Guid, ulong>(ref g);

    private static ulong GuidHi(Guid g) => Unsafe.Add(ref Unsafe.As<Guid, ulong>(ref g), 1);

    public void Dispose()
    {
        if (_shared == null) return;
        NativeMemory.Free(_shared->Timestamps);
        NativeMemory.Free(_shared->Keys);
        NativeMemory.Free(_shared);
        _shared = null;
    }
}
