using System.Runtime.InteropServices;
using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Native.Drives;
using BetterVitals.Sensors.Native.Interop;

namespace BetterVitals.Sensors.Native.Network;

/// <summary>An adapter found by <see cref="NetworkModule"/>'s probe.</summary>
internal sealed record NetworkAdapter(uint InterfaceIndex, string Name, string Description);

/// <summary>
/// Network throughput fallback — only when upstream has no network hardware at all. Per physical adapter (Ethernet, Wi-Fi,
/// mobile broadband) "Download Speed" and "Upload Speed" in bytes/s from the <c>GetIfEntry2</c> octet counters between two reads.
/// The hardware is named after the connection ("Ethernet", "Wi-Fi"), as LibreHardwareMonitor does.
/// </summary>
internal sealed unsafe class NetworkModule(Func<List<NetworkAdapter>>? probe = null) : INativeModule
{
    internal const string DownloadName = "Download Speed";
    internal const string UploadName = "Upload Speed";

    private readonly Func<List<NetworkAdapter>> _probe = probe ?? Enumerate;
    private readonly List<NetNode> _nodes = [];

    public string Name => "network";

    public void Discover(InventoryBuilder b)
    {
        Release();
        if (b.Upstream.HasHardwareOfKind(HardwareKind.Network)) return;
        var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in _probe())
        {
            ordinals.TryGetValue(adapter.Name, out var ordinal);
            ordinals[adapter.Name] = ordinal + 1;
            var node = new NetNode(adapter.InterfaceIndex);
            if (!node.Prime()) { node.Release(); continue; }
            var hardware = b.AddHardware(HardwareKind.Network, adapter.Name, ordinal);
            b.AddSensor(hardware, HardwareKind.Network, SensorKind.Throughput, DownloadName, node, 0);
            b.AddSensor(hardware, HardwareKind.Network, SensorKind.Throughput, UploadName, node, 1);
            _nodes.Add(node);
        }
    }

    /// <summary>Physical network adapters: Ethernet / Wi-Fi / mobile broadband with a hardware interface, not loopback, tunnel or virtual.</summary>
    internal static bool IsPhysical(uint type, byte flags, string description)
    {
        const byte hardwareInterface = 1, filterInterface = 2;
        if (type is not (IpHlp.IfTypeEthernet or IpHlp.IfTypeWifi or IpHlp.IfTypeWwanMobile1 or IpHlp.IfTypeWwanMobile2)) return false;
        if ((flags & hardwareInterface) == 0 || (flags & filterInterface) != 0) return false;
        return !(description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
                 || description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                 || description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase));
    }

    internal static List<NetworkAdapter> Enumerate()
    {
        var result = new List<NetworkAdapter>();
        if (IpHlp.GetIfTable2(out var table) != 0 || table == null) return result;
        try
        {
            var rows = (MibIfRow2*)((byte*)table + 8);
            for (var i = 0; i < table->NumEntries; i++)
            {
                var row = rows + i;
                var description = StorageQuery.Collapse(new string(row->Description));
                if (!IsPhysical(row->Type, row->InterfaceAndOperStatusFlags, description)) continue;
                var alias = StorageQuery.Collapse(new string(row->Alias));
                result.Add(new NetworkAdapter(row->InterfaceIndex, alias.Length > 0 ? alias : description, description));
            }
        }
        finally
        {
            IpHlp.FreeMibTable(table);
        }
        return result;
    }

    public void Release()
    {
        foreach (var n in _nodes) n.Release();
        _nodes.Clear();
    }

    public void Dispose() => Release();

    /// <summary>One adapter: its MIB_IF_ROW2 lives in native memory for the lifetime of the node.</summary>
    private sealed class NetNode : NativeNode
    {
        private static readonly long Stale = Ticks(TimeSpan.FromSeconds(30));
        private readonly uint _index;
        private MibIfRow2* _row;
        private ulong _prevIn, _prevOut;
        private long _prevTimestamp;
        private float _down = float.NaN, _up = float.NaN;

        public NetNode(uint index)
        {
            _index = index;
            _row = (MibIfRow2*)NativeMemory.AllocZeroed((nuint)sizeof(MibIfRow2));
        }

        public bool Prime()
        {
            if (!Read(out var rx, out var tx)) return false;
            _prevIn = rx;
            _prevOut = tx;
            _prevTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            return true;
        }

        private bool Read(out ulong rx, out ulong tx)
        {
            rx = tx = 0;
            if (_row is null) return false;
            *_row = default;
            _row->InterfaceIndex = _index;
            if (IpHlp.GetIfEntry2(_row) != 0) return false;
            rx = _row->InOctets;
            tx = _row->OutOctets;
            return true;
        }

        internal override void Refresh(long timestamp)
        {
            if (!Read(out var rx, out var tx))
            {
                _down = _up = float.NaN;
                return;
            }
            var elapsed = timestamp - _prevTimestamp;
            if (elapsed > 0 && elapsed <= Stale && rx >= _prevIn && tx >= _prevOut)
            {
                var seconds = (double)elapsed / System.Diagnostics.Stopwatch.Frequency;
                _down = (float)((rx - _prevIn) / seconds);
                _up = (float)((tx - _prevOut) / seconds);
            }
            else
            {
                _down = _up = float.NaN; // counters reset, or the previous sample is too old to mean "now"
            }
            _prevIn = rx;
            _prevOut = tx;
            _prevTimestamp = timestamp;
        }

        internal override float Read(int slot) => slot == 0 ? _down : _up;

        internal override void Fault() => _down = _up = float.NaN;

        internal override void Release()
        {
            if (_row is not null) NativeMemory.Free(_row);
            _row = null;
        }
    }
}
