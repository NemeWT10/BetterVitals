using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace BetterVitals.UI.Dev;

/// <summary>
/// Perf probe only: one snapshot of this process — CPU and cycle time, I/O, memory, GC, handles and every thread with its
/// CPU time, context switches (= wake-ups), name and start module. Two snapshots give the cost of the window between them.
/// Allocates; never used outside the probe.
/// </summary>
internal sealed partial class ProcessStats
{
    public long Timestamp { get; private init; }
    public long CpuTicks { get; private init; } // 100 ns, user + kernel
    public ulong Cycles { get; private init; }
    public ulong ReadOps { get; private init; }
    public ulong WriteOps { get; private init; }
    public ulong WriteBytes { get; private init; }
    public ulong OtherOps { get; private init; }
    public long PrivateWorkingSet { get; private init; }
    public long WorkingSet { get; private init; }
    public long PrivateBytes { get; private init; }
    public long GcHeap { get; private init; }
    public long GcCommitted { get; private init; }
    public long Allocated { get; private init; }
    public int Gen0 { get; private init; }
    public int Gen1 { get; private init; }
    public int Gen2 { get; private init; }
    public int Handles { get; private init; }
    public Dictionary<int, ThreadInfo> Threads { get; private init; } = [];

    public sealed record ThreadInfo(int Id, long CpuTicks, uint ContextSwitches, ulong Cycles, string Name);

    /// <summary>Logical processors of the whole machine (Task Manager's divisor), not the affinity of this process.</summary>
    public static int MachineProcessors { get; } = Math.Max(1, (int)GetActiveProcessorCount(0xFFFF));

    /// <summary>Rate of the cycle counters (TSC), measured once by spinning this thread for 100 ms.</summary>
    public static double TscHz { get; } = MeasureTsc();

    private static readonly Dictionary<int, string> NameCache = [];

    private static double MeasureTsc()
    {
        var self = GetCurrentThread();
        QueryThreadCycleTime(self, out var c0);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedTicks < Stopwatch.Frequency / 10)
        {
        }
        QueryThreadCycleTime(self, out var c1);
        return Math.Max(1e8, (c1 - c0) / sw.Elapsed.TotalSeconds);
    }

    public static ProcessStats Take()
    {
        var self = GetCurrentProcess();
        GetProcessTimes(self, out _, out _, out var kernel, out var user);
        QueryProcessCycleTime(self, out var cycles);
        GetProcessIoCounters(self, out var io);
        var mem = new PROCESS_MEMORY_COUNTERS_EX2 { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX2>() };
        GetProcessMemoryInfo(self, ref mem, mem.cb);
        GetProcessHandleCount(self, out var handles);
        var gc = GC.GetGCMemoryInfo();
        return new ProcessStats
        {
            Timestamp = Stopwatch.GetTimestamp(),
            CpuTicks = kernel + user,
            Cycles = cycles,
            ReadOps = io.ReadOperationCount,
            WriteOps = io.WriteOperationCount,
            WriteBytes = io.WriteTransferCount,
            OtherOps = io.OtherOperationCount,
            PrivateWorkingSet = (long)mem.PrivateWorkingSetSize,
            WorkingSet = (long)mem.WorkingSetSize,
            PrivateBytes = (long)mem.PrivateUsage,
            GcHeap = gc.HeapSizeBytes,
            GcCommitted = gc.TotalCommittedBytes,
            Allocated = GC.GetTotalAllocatedBytes(false),
            Gen0 = GC.CollectionCount(0),
            Gen1 = GC.CollectionCount(1),
            Gen2 = GC.CollectionCount(2),
            Handles = (int)handles,
            Threads = ReadThreads(),
        };
    }

    public static (long PrivateWorkingSet, long WorkingSet, long PrivateBytes) Memory()
    {
        var mem = new PROCESS_MEMORY_COUNTERS_EX2 { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX2>() };
        GetProcessMemoryInfo(GetCurrentProcess(), ref mem, mem.cb);
        return ((long)mem.PrivateWorkingSetSize, (long)mem.WorkingSetSize, (long)mem.PrivateUsage);
    }

    // ───────────────────────── threads (NtQuerySystemInformation) ─────────────────────────

    private const int SystemProcessInformation = 5;
    private const int ProcessEntrySize = 256; // SYSTEM_PROCESS_INFORMATION, x64
    private const int ThreadEntrySize = 80;   // SYSTEM_THREAD_INFORMATION, x64

    private static unsafe Dictionary<int, ThreadInfo> ReadThreads()
    {
        var result = new Dictionary<int, ThreadInfo>();
        var pid = Environment.ProcessId;
        var size = 1 << 20;
        for (var attempt = 0; attempt < 6; attempt++, size *= 2)
        {
            var buffer = NativeMemory.Alloc((nuint)size);
            try
            {
                var status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out var needed);
                if (status == unchecked((int)0xC0000004)) // STATUS_INFO_LENGTH_MISMATCH
                {
                    size = Math.Max(size, needed + 65536) / 2;
                    continue;
                }
                if (status < 0) return result;
                var p = (byte*)buffer;
                while (true)
                {
                    var next = *(uint*)p;
                    if ((long)*(nint*)(p + 80) == pid)
                    {
                        var count = *(uint*)(p + 4);
                        var t = p + ProcessEntrySize;
                        for (var i = 0; i < count; i++, t += ThreadEntrySize)
                        {
                            var tid = (int)*(nint*)(t + 48);
                            var cpu = *(long*)t + *(long*)(t + 8);
                            var switches = *(uint*)(t + 64);
                            result[tid] = new ThreadInfo(tid, cpu, switches, ThreadCycles(tid), NameOf(tid));
                        }
                        return result;
                    }
                    if (next == 0) return result;
                    p += next;
                }
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }
        return result;
    }

    private static ulong ThreadCycles(int tid)
    {
        var h = OpenThread(0x0800, false, (uint)tid);
        if (h == 0) return 0;
        try
        {
            return QueryThreadCycleTime(h, out var cycles) ? cycles : 0;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>
    /// Resident private pages (Task Manager's "Memory") by owner: each DLL / exe image (its written data pages), mapped
    /// files, and anonymous private memory (GC heap, native heaps, stacks) — the latter also as its largest regions.
    /// </summary>
    public static unsafe List<string> PrivateBreakdown()
    {
        var self = GetCurrentProcess();
        nuint size = 1 << 20;
        for (var attempt = 0; attempt < 8; attempt++, size *= 2)
        {
            var buffer = NativeMemory.Alloc(size);
            try
            {
                if (!QueryWorkingSet(self, buffer, (uint)size))
                {
                    if (Marshal.GetLastPInvokeError() == 24) continue; // ERROR_BAD_LENGTH
                    return ["  working set: query failed"];
                }
                var count = (long)*(nuint*)buffer;
                var blocks = (ulong*)((byte*)buffer + sizeof(nuint));
                var byOwner = new Dictionary<string, long>();
                var byRegion = new Dictionary<nuint, long>();
                (nuint Base, nuint End, uint Type) last = default;
                for (long i = 0; i < count; i++)
                {
                    var b = blocks[i];
                    if ((b & 0x100) != 0) continue; // shared
                    var page = (nuint)(b & ~0xFFFUL);
                    if (page < last.Base || page >= last.End)
                    {
                        MEMORY_BASIC_INFORMATION mbi;
                        if (VirtualQuery(page, &mbi, (nuint)sizeof(MEMORY_BASIC_INFORMATION)) == 0) continue;
                        last = (mbi.AllocationBase, mbi.BaseAddress + mbi.RegionSize, mbi.Type);
                    }
                    string owner;
                    switch (last.Type)
                    {
                        case 0x1000000: // MEM_IMAGE
                            owner = ModuleName(last.Base);
                            break;
                        case 0x40000: // MEM_MAPPED
                            owner = "(mapped files)";
                            break;
                        default:
                            owner = "(private: heaps, GC, stacks)";
                            byRegion[last.Base] = byRegion.GetValueOrDefault(last.Base) + 4096;
                            break;
                    }
                    byOwner[owner] = byOwner.GetValueOrDefault(owner) + 4096;
                }
                var lines = new List<string>();
                foreach (var (owner, bytes) in byOwner.OrderByDescending(kv => kv.Value).Take(16))
                    lines.Add($"  {bytes / 1048576.0,6:0.00} MB  {owner}");
                lines.Add("  largest private regions (allocation base: resident):");
                foreach (var (b, bytes) in byRegion.OrderByDescending(kv => kv.Value).Take(6))
                    lines.Add($"    0x{(ulong)b:X12}  {bytes / 1048576.0,6:0.00} MB");
                return lines;
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }
        return ["  working set: buffer too small"];
    }

    private static readonly Dictionary<nuint, string> ModuleNames = [];

    private static unsafe string ModuleName(nuint allocationBase)
    {
        if (ModuleNames.TryGetValue(allocationBase, out var n)) return n;
        var chars = stackalloc char[260];
        var len = GetModuleFileNameW((nint)allocationBase, chars, 260);
        n = len == 0 ? "(image)" : Path.GetFileName(new string(chars, 0, (int)len));
        ModuleNames[allocationBase] = n;
        return n;
    }

    /// <summary>Thread description (named .NET threads) or the module its start address lies in.</summary>
    private static unsafe string NameOf(int tid)
    {
        if (NameCache.TryGetValue(tid, out var cached)) return cached;
        var name = "?";
        var h = OpenThread(0x0040 | 0x0800, false, (uint)tid); // QUERY_INFORMATION | QUERY_LIMITED_INFORMATION
        if (h != 0)
        {
            try
            {
                if (GetThreadDescription(h, out var desc) >= 0 && desc != 0)
                {
                    var s = Marshal.PtrToStringUni(desc);
                    LocalFree(desc);
                    if (!string.IsNullOrEmpty(s)) name = s;
                }
                nint start = 0;
                if (NtQueryInformationThread(h, 9, &start, sizeof(nint), 0) >= 0 && start != 0
                    && GetModuleHandleExW(4 | 2, start, out var module) && module != 0)
                {
                    var chars = stackalloc char[260];
                    var n = GetModuleFileNameW(module, chars, 260);
                    var file = Path.GetFileName(new string(chars, 0, (int)n));
                    name = name == "?" ? file : $"{name} [{file}]";
                }
            }
            finally
            {
                CloseHandle(h);
            }
        }
        // a thread id can be reused: cache only named ones
        if (name != "?") NameCache[tid] = name;
        return name;
    }

    // ───────────────────────── interop ─────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX2
    {
        public uint cb, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryProcessCycleTime(nint process, out ulong cycles);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessIoCounters(nint process, out IO_COUNTERS counters);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessHandleCount(nint process, out uint count);

    [LibraryImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(nint process, ref PROCESS_MEMORY_COUNTERS_EX2 counters, uint size);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetActiveProcessorCount(ushort group);

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQuerySystemInformation(int infoClass, void* buffer, int length, out int needed);

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQueryInformationThread(nint thread, int infoClass, void* buffer, int length, nint returned);

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);

    [LibraryImport("kernel32.dll")]
    private static partial int GetThreadDescription(nint thread, out nint description);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint mem);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetModuleHandleExW(uint flags, nint address, out nint module);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial uint GetModuleFileNameW(nint module, char* name, uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryThreadCycleTime(nint thread, out ulong cycles);

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryWorkingSet(nint process, void* buffer, uint size);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial nuint VirtualQuery(nuint address, MEMORY_BASIC_INFORMATION* info, nuint length);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public nuint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }
}
