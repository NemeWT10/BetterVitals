using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using BetterVitals.Sensors.Native;
using BetterVitals.Sensors.Native.Drives;
using BetterVitals.Sensors.Native.Gpu;
using BetterVitals.Sensors.Native.Interop;
using BetterVitals.Sensors.Native.Memory;
using BetterVitals.Sensors.Native.Network;

namespace BetterVitals.Tools.NativeProbe;

/// <summary>"--raw": what the OS answers, byte for byte, next to what the parsers make of it. Read-only; never wakes a sleeping drive.</summary>
internal static unsafe class RawDump
{
    public static void Run(TextWriter w)
    {
        Smbios_(w);
        Drives(w);
        Gpus(w);
        Pdh_(w);
        Net(w);
        Cpu_(w);
    }

    private static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b);

    private static void Smbios_(TextWriter w)
    {
        w.WriteLine("## SMBIOS (RSMB)");
        var raw = Smbios.ReadRaw();
        if (raw is null) { w.WriteLine("no table"); return; }
        w.WriteLine($"version {Smbios.Version(raw)}, {raw.Length} bytes");
        // dump every type 17 structure (formatted area + string set) as hex
        var length = BitConverter.ToInt32(raw, 4);
        var table = raw.AsSpan(8, Math.Min(length, raw.Length - 8));
        var offset = 0;
        while (offset + 4 <= table.Length)
        {
            var type = table[offset];
            var formatted = table[offset + 1];
            if (type == 127 || formatted < 4) break;
            var end = offset + formatted;
            while (end + 1 < table.Length && !(table[end] == 0 && table[end + 1] == 0)) end++;
            if (type == 17) w.WriteLine($"type17 len=0x{formatted:X2} bytes={Hex(table.Slice(offset, end + 2 - offset))}");
            offset = end + 2;
        }
        foreach (var m in Smbios.ParseMemoryModules(raw)) w.WriteLine(m);
        w.WriteLine();
    }

    private static void Drives(TextWriter w)
    {
        w.WriteLine("## Drives (\\\\.\\PhysicalDriveN, desiredAccess 0)");
        var buf = stackalloc byte[1024];
        var q = stackalloc int[3];
        var tbuf = stackalloc byte[280];
        for (var i = 0; i < 32; i++)
        {
            var h = StorageQuery.OpenPhysicalDrive(i);
            if (!StorageQuery.IsValid(h)) continue;
            try
            {
                q[0] = StorageQuery.PropertyDevice;
                var okDesc = Kernel32.DeviceIoControl(h, Kernel32.IoctlStorageQueryProperty, q, 12, buf, 1024, out var n, 0);
                var desc = okDesc ? StorageQuery.ParseDescriptor(new ReadOnlySpan<byte>(buf, (int)n)) : null;
                w.WriteLine($"PhysicalDrive{i}: vendor='{desc?.Vendor}' product='{desc?.Product}' name='{desc?.Name}' serial=<{desc?.Serial.Length} chars, not printed> bus={desc?.BusType} removable={desc?.Removable}");
                var seek = StorageQuery.ReadIncursSeekPenalty(h);
                var powerOk = Kernel32.GetDevicePowerState(h, out var on);
                w.WriteLine($"  seekPenalty={seek?.ToString() ?? "n/a"} devicePowerState ok={powerOk} on={on}");
                var rotating = seek ?? true;
                if (rotating && (!powerOk || on == 0))
                {
                    w.WriteLine("  rotating and not in D0: temperature NOT queried (would wake it)");
                    continue;
                }
                q[0] = StorageQuery.PropertyTemperature;
                var t0 = Stopwatch.GetTimestamp();
                var ok = Kernel32.DeviceIoControl(h, Kernel32.IoctlStorageQueryProperty, q, 12, tbuf, 280, out n, 0);
                var us = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
                var err = ok ? 0 : Marshal.GetLastSystemError();
                var span = new ReadOnlySpan<byte>(tbuf, (int)n);
                var parsed = StorageQuery.ParseTemperature(span, out var temp);
                w.WriteLine($"  temperature ioctl(52) ok={ok} win32={err} bytes={n} {us:F0} us parsed={(parsed ? temp.ToString("F0") : "none")}");
                if (n > 0) w.WriteLine($"  raw={Hex(span[..Math.Min((int)n, 80)])}");
            }
            finally
            {
                Kernel32.CloseHandle(h);
            }
        }
        w.WriteLine();
    }

    /// <summary>Byte samples for the unit tests: device descriptors and the NVMe health log header, as the driver returns them.</summary>
    public static void Samples(TextWriter w)
    {
        w.WriteLine("## Samples");
        var q = stackalloc int[3];
        var buf = stackalloc byte[1024];
        var nq = stackalloc uint[12];
        var no = stackalloc byte[560];
        for (var i = 0; i < 32; i++)
        {
            var h = StorageQuery.OpenPhysicalDrive(i);
            if (!StorageQuery.IsValid(h)) continue;
            try
            {
                q[0] = 0; q[1] = 0; q[2] = 0;
                if (Kernel32.DeviceIoControl(h, Kernel32.IoctlStorageQueryProperty, q, 12, buf, 1024, out var n, 0))
                {
                    // blank the serial number before printing: samples end up in public test sources
                    var serialAt = *(uint*)(buf + 24);
                    for (var j = serialAt; serialAt > 0 && j < n && buf[j] != 0; j++) buf[j] = (byte)'0';
                    w.WriteLine($"PhysicalDrive{i} StorageDeviceProperty ({n} bytes): {Hex(new ReadOnlySpan<byte>(buf, (int)n))}");
                }
                var desc = StorageQuery.ReadDescriptor(h);
                if (desc is null || desc.BusType != 0x11) continue;
                new Span<uint>(nq, 12).Clear();
                nq[0] = 50; nq[2] = 3; nq[3] = 2; nq[4] = 2; nq[6] = 40; nq[7] = 512;
                if (Kernel32.DeviceIoControl(h, Kernel32.IoctlStorageQueryProperty, nq, 48, no, 560, out n, 0))
                    w.WriteLine($"PhysicalDrive{i} NVMe health log ({n} bytes): descriptor header {Hex(new ReadOnlySpan<byte>(no, 48))} log[0..16] {Hex(new ReadOnlySpan<byte>(no + 48, 16))}");
            }
            finally
            {
                Kernel32.CloseHandle(h);
            }
        }
        w.WriteLine();
    }

    /// <summary>NVMe experiment: other ways to get the composite temperature without administrator rights.</summary>
    public static void NvmeExperiments(TextWriter w)
    {
        w.WriteLine("## NVMe experiments");
        var q = stackalloc byte[256];
        var o = stackalloc byte[1024];
        for (var i = 0; i < 32; i++)
        {
            var h = StorageQuery.OpenPhysicalDrive(i);
            if (!StorageQuery.IsValid(h)) continue;
            try
            {
                var desc = StorageQuery.ReadDescriptor(h);
                if (desc is null || desc.BusType != 0x11) continue;
                w.WriteLine($"PhysicalDrive{i} {desc.Name}");
                // adapter / device temperature properties (51 / 52), standard query, various input sizes
                foreach (var prop in new[] { 51, 52 })
                foreach (var inSize in new[] { 8u, 9u, 12u })
                {
                    new Span<byte>(q, 256).Clear();
                    *(int*)q = prop;
                    new Span<byte>(o, 1024).Clear();
                    var ok = Kernel32.DeviceIoControl(h, Kernel32.IoctlStorageQueryProperty, q, inSize, o, 280, out var n, 0);
                    w.WriteLine($"  prop {prop} in={inSize}: ok={ok} win32={(ok ? 0 : Marshal.GetLastSystemError())} bytes={n} {(ok ? Hex(new ReadOnlySpan<byte>(o, (int)Math.Min(n, 64))) : "")}");
                }
                // protocol specific: NVMe SMART / health log page 2
                new Span<byte>(q, 256).Clear();
                new Span<byte>(o, 1024).Clear();
                *(int*)q = 50;            // StorageDeviceProtocolSpecificProperty
                *(int*)(q + 4) = 0;       // PropertyStandardQuery
                var psd = (uint*)(q + 8); // STORAGE_PROTOCOL_SPECIFIC_DATA
                psd[0] = 3;               // ProtocolTypeNvme
                psd[1] = 2;               // NVMeDataTypeLogPage
                psd[2] = 2;               // NVME_LOG_PAGE_HEALTH_INFO
                psd[3] = 0;               // sub value (offset)
                psd[4] = 40;              // ProtocolDataOffset
                psd[5] = 512;             // ProtocolDataLength
                var okp = Kernel32.DeviceIoControl(h, Kernel32.IoctlStorageQueryProperty, q, 8 + 40, o, 48 + 512, out var np, 0);
                w.WriteLine($"  protocol log page 2: ok={okp} win32={(okp ? 0 : Marshal.GetLastSystemError())} bytes={np}");
                if (okp && np >= 48 + 4)
                {
                    var kelvin = *(ushort*)(o + 48 + 1);
                    w.WriteLine($"    composite = {kelvin} K = {kelvin - 273} C; warning byte=0x{o[48]:X2}");
                }
            }
            finally
            {
                Kernel32.CloseHandle(h);
            }
        }
        w.WriteLine();
    }

    private static void Gpus(TextWriter w)
    {
        w.WriteLine("## Display devnodes (PnP)");
        var listCr = CfgMgr.CM_Get_Device_ID_List_SizeW(out var idLen, CfgMgr.DisplayClassGuid, CfgMgr.GetIdListFilterClass | CfgMgr.GetIdListFilterPresent);
        w.WriteLine($"id list size cr=0x{listCr:X} length={idLen}");
        var idBuf = new char[Math.Max(idLen, 2)];
        fixed (char* p = idBuf)
        {
            listCr = CfgMgr.CM_Get_Device_ID_ListW(CfgMgr.DisplayClassGuid, p, (uint)idBuf.Length, CfgMgr.GetIdListFilterClass | CfgMgr.GetIdListFilterPresent);
        }
        w.WriteLine($"id list cr=0x{listCr:X}");
        foreach (var id in new string(idBuf).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var cr = CfgMgr.CM_Locate_DevNodeW(out var inst, id, 0);
            var okBus = CfgMgr.TryGetUInt32(inst, CfgMgr.PidBusNumber, out var bus);
            var okAddr = CfgMgr.TryGetUInt32(inst, CfgMgr.PidAddress, out var addr);
            w.WriteLine($"{id}: locate cr=0x{cr:X} bus ok={okBus} {bus} addr ok={okAddr} 0x{addr:X} (dev {addr >> 16} fn {addr & 0xFFFF})");
        }
        w.WriteLine();

        w.WriteLine("## GPU adapters (D3DKMT)");
        var gate = new CmDevicePowerGate();
        foreach (var a in GpuAdapters.Enumerate())
        {
            w.WriteLine($"{a.Name}  luid={a.Luid}  type=0x{a.TypeFlags:X}  pci={(a.HasAddress ? $"{a.Address.BusNumber}:{a.Address.DeviceNumber}.{a.Address.FunctionNumber}" : "n/a")}");
            var id = a.HasAddress ? GpuAdapters.FindInstanceId(a.Address) : null;
            w.WriteLine($"  devnode={id}  awake={(id is null ? "n/a" : gate.IsAwake(id).ToString())}  needsGate={a.NeedsPowerGate}");
            if (id is not null && !gate.IsAwake(id)) { w.WriteLine("  asleep: not queried"); D3dKmt.Close(a.Handle); continue; }

            var perf = new AdapterPerfData();
            var t0 = Stopwatch.GetTimestamp();
            var st = D3dKmt.Query(a.Handle, D3dKmt.QaiAdapterPerfData, &perf, (uint)sizeof(AdapterPerfData));
            var us = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
            w.WriteLine($"  ADAPTERPERFDATA(62) size={sizeof(AdapterPerfData)} status=0x{st:X8} {us:F0} us temp={perf.Temperature / 10.0} C fan={perf.FanRpm} rpm power={perf.Power / 10.0} % memFreq={perf.MemoryFrequency / 1e6} MHz maxMem={perf.MaxMemoryFrequency / 1e6} MHz memBw={perf.MemoryBandwidth} pcieBw={perf.PcieBandwidth} override={perf.PowerStateOverride}");
            var caps = new AdapterPerfDataCaps();
            st = D3dKmt.Query(a.Handle, D3dKmt.QaiAdapterPerfDataCaps, &caps, (uint)sizeof(AdapterPerfDataCaps));
            w.WriteLine($"  ADAPTERPERFDATA_CAPS(63) size={sizeof(AdapterPerfDataCaps)} status=0x{st:X8} maxFan={caps.MaxFanRpm} warn={caps.TemperatureWarning / 10.0} overheat={caps.TemperatureOverheat / 10.0} maxMemBw={caps.MaxMemoryBandwidth}");
            for (uint node = 0; node < 4; node++)
            {
                var np = new NodePerfData { NodeOrdinal = node };
                var st2 = D3dKmt.Query(a.Handle, D3dKmt.QaiNodePerfData, &np, (uint)sizeof(NodePerfData));
                w.WriteLine($"  NODEPERFDATA(61) node {node} size={sizeof(NodePerfData)} status=0x{st2:X8} freq={np.Frequency / 1e6} MHz max={np.MaxFrequency / 1e6} MHz volt={np.Voltage} voltMax={np.VoltageMax} latency={np.MaxTransitionLatency}");
            }
            w.WriteLine($"  probe => {GpuAdapters.Probe(a.Handle)}");
            D3dKmt.Close(a.Handle);
        }
        w.WriteLine();
    }

    private static void Pdh_(TextWriter w)
    {
        w.WriteLine("## PDH GPU counters");
        var adapters = GpuAdapters.Enumerate();
        var c = new GpuCounters(adapters.Select(a => a.Luid.Value).ToList());
        var before = Process.GetCurrentProcess().PrivateMemorySize64;
        var t0 = Stopwatch.GetTimestamp();
        var ok = c.Collect(Stopwatch.GetTimestamp()); // opens the query lazily
        var openMs = (Stopwatch.GetTimestamp() - t0) * 1e3 / Stopwatch.Frequency;
        w.WriteLine($"first collect (opens PDH): ok={ok} {openMs:F1} ms, private memory +{(Process.GetCurrentProcess().PrivateMemorySize64 - before) / 1024} KB, failed={c.Failed}");
        Thread.Sleep(1000);
        for (var round = 0; round < 3; round++)
        {
            t0 = Stopwatch.GetTimestamp();
            c.Collect(Stopwatch.GetTimestamp());
            var ms = (Stopwatch.GetTimestamp() - t0) * 1e3 / Stopwatch.Frequency;
            w.WriteLine($"collect #{round + 2}: {ms:F2} ms");
            for (var i = 0; i < adapters.Count; i++)
                w.WriteLine($"  {adapters[i].Name} luid={adapters[i].Luid} 3D={c.Load3D(i):F2} % dedicated={c.DedicatedMb(i):F0} MB shared={c.SharedMb(i):F0} MB");
            Thread.Sleep(1000);
        }
        c.Dispose();
        foreach (var a in adapters) D3dKmt.Close(a.Handle);
        w.WriteLine();
    }

    private static void Net(TextWriter w)
    {
        w.WriteLine("## Network (GetIfTable2)");
        w.WriteLine($"sizeof(MibIfRow2) = {sizeof(MibIfRow2)} (expected 1352)");
        foreach (var a in NetworkModule.Enumerate()) w.WriteLine($"{a.Name} [{a.Description}] index={a.InterfaceIndex}");
        w.WriteLine();
    }

    private static void Cpu_(TextWriter w)
    {
        w.WriteLine("## CPU");
        var logical = Environment.ProcessorCount;
        var (map, cores) = BetterVitals.Sensors.Native.Cpu.CoreMap.Read(logical);
        w.WriteLine($"logical={logical} cores={cores} map=[{string.Join(',', map)}]");
        w.WriteLine($"sizeof(ProcessorPerformance) = {sizeof(ProcessorPerformance)} (expected 48)");
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var brand = key?.GetValue("ProcessorNameString") as string ?? "";
        w.WriteLine($"brand='{brand}' => '{LhmNames.Cpu(brand)}'");
        w.WriteLine();
    }
}
