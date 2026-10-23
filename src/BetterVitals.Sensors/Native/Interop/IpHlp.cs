using System.Runtime.InteropServices;

namespace BetterVitals.Sensors.Native.Interop;

/// <summary>iphlpapi: interface counters (GetIfTable2 / GetIfEntry2).</summary>
internal static unsafe partial class IpHlp
{
    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetIfTable2(out MibIfTable2* table);

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetIfEntry2(MibIfRow2* row);

    [LibraryImport("iphlpapi.dll")]
    public static partial void FreeMibTable(void* memory);

    public const uint IfTypeEthernet = 6;
    public const uint IfTypeLoopback = 24;
    public const uint IfTypeWifi = 71;
    public const uint IfTypeWwanMobile1 = 243;
    public const uint IfTypeWwanMobile2 = 244;
    public const uint IfTypeTunnel = 131;
}

/// <summary>MIB_IF_TABLE2: count followed (8-aligned) by the rows.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MibIfTable2
{
    public uint NumEntries;
    // MibIfRow2 Table[NumEntries] starts at byte offset 8 of the table
}

/// <summary>MIB_IF_ROW2 (1352 bytes on x64). Only the fields we read are named; the rest keeps the layout right.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MibIfRow2
{
    public ulong InterfaceLuid;
    public uint InterfaceIndex;
    public Guid InterfaceGuid;
    public fixed char Alias[257];
    public fixed char Description[257];
    public uint PhysicalAddressLength;
    public fixed byte PhysicalAddress[32];
    public fixed byte PermanentPhysicalAddress[32];
    public uint Mtu;
    public uint Type;
    public int TunnelType;
    public int MediaType;
    public int PhysicalMediumType;
    public int AccessType;
    public int DirectionType;
    /// <summary>bit0 HardwareInterface, bit1 FilterInterface, bit2 ConnectorPresent, bit3 NotAuthenticated, bit4 NotMediaConnected, bit5 Paused, bit6 LowPower, bit7 EndPointInterface.</summary>
    public byte InterfaceAndOperStatusFlags;
    public int OperStatus;
    public int AdminStatus;
    public int MediaConnectState;
    public Guid NetworkGuid;
    public int ConnectionType;
    public ulong TransmitLinkSpeed;
    public ulong ReceiveLinkSpeed;
    public ulong InOctets;
    public ulong InUcastPkts;
    public ulong InNUcastPkts;
    public ulong InDiscards;
    public ulong InErrors;
    public ulong InUnknownProtos;
    public ulong InUcastOctets;
    public ulong InMulticastOctets;
    public ulong InBroadcastOctets;
    public ulong OutOctets;
    public ulong OutUcastPkts;
    public ulong OutNUcastPkts;
    public ulong OutDiscards;
    public ulong OutErrors;
    public ulong OutUcastOctets;
    public ulong OutMulticastOctets;
    public ulong OutBroadcastOctets;
    public ulong OutQLen;
}
