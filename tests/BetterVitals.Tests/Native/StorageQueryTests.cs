using System.Buffers.Binary;
using BetterVitals.Sensors.Native.Drives;
using Xunit;

namespace BetterVitals.Tests.Native;

/// <summary>Parsers for IOCTL_STORAGE_QUERY_PROPERTY answers, on bytes captured from the reference PC's drives (serial numbers blanked).</summary>
public sealed class StorageQueryTests
{
    // ── StorageDeviceTemperatureProperty answers (STORAGE_TEMPERATURE_DATA_DESCRIPTOR: 24-byte header + one 16-byte entry) ──
    private const string Hdd40C = "2800000028000000008037000100000000000000000000000000280037000D000000000000000000";   // ST4000DM004-2CV104
    private const string Hdd36C = "280000002800000000803C00010000000000000000000000000024003C0000000000000000000000";     // ST4000DM004-2U9104
    private const string Ssd36C = "28000000280000000080460001000000000000000000000000002400460000800000000000000000";     // Samsung SSD 860 EVO

    // ── StorageDeviceProperty answers: STORAGE_DEVICE_DESCRIPTOR with the strings at their offsets ──
    private const string HddDescriptor = "280000008C010000000000010000000028000000510000005A0000000B0000000000000000000000535434303030444D3030342D3243563130340000000000000000000000000000000000000000000000303030310000000000303030303030303030303030303030303030303000";
    private const string SsdDescriptor = "280000008C010000000000010000000028000000510000005A0000000B000000000000000000000053616D73756E6720535344203836302045564F2032353047420000000000000000000000000000000052565430334236510030303030303030303030303030303000";
    private const string NvmeDescriptor = "280000008C0100000000000100000000390000007A0000008B00000011000000000000000000000000000000000000000000000000000000004B494E4753544F4E20534B4333303030443230343847000000000000000000000000000000000000000000000000000000000000000000000000000000000000004549464B35312E320000000000000000003030303030303030303030303030303030303030303030303030303030303030303030303030303000";

    // ── NVMe health log through StorageDeviceProtocolSpecificProperty: STORAGE_PROTOCOL_DATA_DESCRIPTOR header + log page 2 ──
    private const string NvmeHeader = "300000003000000003000000020000000200000000000000280000000002000000000000000000000000000000000000";
    private const string NvmeLog48C = "004101640A0000000000000000000000"; // composite 0x0141 K = 321 K = 48 C
    private const string NvmeLog38C = "003701640A0900000000000000000000"; // 0x0137 K = 311 K = 38 C

    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    private static byte[] TemperatureDescriptor(params (ushort Index, short Temp)[] entries)
    {
        var data = new byte[24 + 16 * entries.Length];
        BinaryPrimitives.WriteInt32LittleEndian(data, data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), (ushort)entries.Length);
        for (var i = 0; i < entries.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(24 + i * 16), entries[i].Index);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(24 + i * 16 + 2), entries[i].Temp);
        }
        return data;
    }

    private static byte[] NvmeAnswer(string log16)
    {
        var data = new byte[48 + 512];
        Hex(NvmeHeader).CopyTo(data, 0);
        Hex(log16).CopyTo(data, 48);
        return data;
    }

    [Theory]
    [InlineData(Hdd40C, 40)]
    [InlineData(Hdd36C, 36)]
    [InlineData(Ssd36C, 36)]
    public void Real_sata_answers_give_the_temperature(string hex, int expected)
    {
        Assert.True(StorageQuery.ParseTemperature(Hex(hex), out var t));
        Assert.Equal(expected, t);
    }

    [Fact]
    public void Entry_count_is_taken_from_the_bytes_not_from_InfoCount()
    {
        var zeroCount = Hex(Hdd40C);
        BinaryPrimitives.WriteUInt16LittleEndian(zeroCount.AsSpan(12), 0); // InfoCount says 0, one entry is there
        Assert.True(StorageQuery.ParseTemperature(zeroCount, out var a));
        Assert.Equal(40, a);

        var hugeCount = Hex(Hdd40C);
        BinaryPrimitives.WriteUInt16LittleEndian(hugeCount.AsSpan(12), 500); // claims far more than was returned
        Assert.True(StorageQuery.ParseTemperature(hugeCount, out var b));
        Assert.Equal(40, b);
    }

    [Fact]
    public void Lowest_index_with_a_plausible_value_wins()
    {
        // index 0 reports nothing (0), so index 1 is the one; entries may come in any order
        Assert.True(StorageQuery.ParseTemperature(TemperatureDescriptor((2, 50), (1, 45), (0, 0)), out var t));
        Assert.Equal(45, t);
        Assert.True(StorageQuery.ParseTemperature(TemperatureDescriptor((1, 52), (0, 41)), out t));
        Assert.Equal(41, t);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-273)]
    [InlineData(-1)]
    [InlineData(126)]
    [InlineData(300)]
    [InlineData(short.MaxValue)]
    [InlineData(short.MinValue)]
    public void Implausible_values_are_rejected(int celsius)
    {
        Assert.False(StorageQuery.ParseTemperature(TemperatureDescriptor((0, (short)celsius)), out var t));
        Assert.True(float.IsNaN(t));
    }

    [Fact]
    public void Short_answers_are_rejected()
    {
        Assert.False(StorageQuery.ParseTemperature([], out _));
        Assert.False(StorageQuery.ParseTemperature(Hex(Hdd40C).AsSpan(0, 39), out _)); // entry cut short
        Assert.False(StorageQuery.ParseTemperature(Hex(NvmeHeader)[..20], out _));
        // StorageDevicePowerProperty (the answer to id 12, which earlier code mistook for the temperature) has no entry either
        Assert.False(StorageQuery.ParseTemperature(Hex("14000000140000000000010000000000FFFFFFFF"), out _));
    }

    [Theory]
    [InlineData(NvmeLog48C, 48)]
    [InlineData(NvmeLog38C, 38)]
    public void Nvme_health_log_composite_temperature(string log, int expected)
    {
        Assert.True(StorageQuery.ParseNvmeHealthLog(NvmeAnswer(log), out var t));
        Assert.Equal(expected, t);
    }

    [Fact]
    public void Nvme_health_log_rejects_nonsense()
    {
        Assert.False(StorageQuery.ParseNvmeHealthLog(NvmeAnswer("00000001640A0000"), out _));          // composite 0 K: not implemented
        Assert.False(StorageQuery.ParseNvmeHealthLog(NvmeAnswer("00FFFF01640A0000"), out _));          // 65535 K
        Assert.False(StorageQuery.ParseNvmeHealthLog(Hex(NvmeHeader), out _));                         // header only
        Assert.False(StorageQuery.ParseNvmeHealthLog([], out _));

        var badOffset = NvmeAnswer(NvmeLog48C);
        BinaryPrimitives.WriteUInt32LittleEndian(badOffset.AsSpan(8 + 16), 100_000); // ProtocolDataOffset beyond the buffer
        Assert.False(StorageQuery.ParseNvmeHealthLog(badOffset, out _));

        var tooShort = NvmeAnswer(NvmeLog48C);
        BinaryPrimitives.WriteUInt32LittleEndian(tooShort.AsSpan(8 + 20), 2); // ProtocolDataLength < 3
        Assert.False(StorageQuery.ParseNvmeHealthLog(tooShort, out _));
    }

    [Fact]
    public void Device_descriptor_of_a_sata_hdd()
    {
        var d = StorageQuery.ParseDescriptor(Hex(HddDescriptor))!;

        Assert.Equal("", d.Vendor);
        Assert.Equal("ST4000DM004-2CV104", d.Product);
        Assert.Equal("ST4000DM004-2CV104", d.Name);
        Assert.Equal(0x0Bu, d.BusType); // SATA
        Assert.False(d.Removable);
        Assert.True(StorageQuery.IsFixedBus(d.BusType, d.Removable));
    }

    [Fact]
    public void Device_descriptor_of_a_sata_ssd_and_an_nvme_drive()
    {
        var ssd = StorageQuery.ParseDescriptor(Hex(SsdDescriptor))!;
        Assert.Equal("Samsung SSD 860 EVO 250GB", ssd.Name);

        var nvme = StorageQuery.ParseDescriptor(Hex(NvmeDescriptor))!;
        Assert.Equal("KINGSTON SKC3000D2048G", nvme.Name);
        Assert.Equal(StorageQuery.BusTypeNvme, nvme.BusType);
        Assert.True(StorageQuery.IsFixedBus(nvme.BusType, nvme.Removable));
    }

    [Fact]
    public void Device_descriptor_with_offsets_outside_the_buffer_is_survivable()
    {
        var data = Hex(HddDescriptor);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 5000); // product id beyond the end
        var d = StorageQuery.ParseDescriptor(data)!;
        Assert.Equal("", d.Product);
        Assert.Null(StorageQuery.ParseDescriptor(new byte[10]));
    }

    [Theory]
    [InlineData("ATA", "Samsung SSD 860 EVO 250GB", "Samsung SSD 860 EVO 250GB")]
    [InlineData("NVMe", "KINGSTON SKC3000D2048G", "KINGSTON SKC3000D2048G")]
    [InlineData("", "ST4000DM004-2CV104", "ST4000DM004-2CV104")]
    [InlineData("SAMSUNG", "MZVL21T0HCLR-00B00", "SAMSUNG MZVL21T0HCLR-00B00")]
    [InlineData("Samsung", "Samsung SSD 970 EVO", "Samsung SSD 970 EVO")]
    [InlineData("WDC", "", "WDC")]
    [InlineData("  ", " WDS500G3X0C-00SJG0  ", "WDS500G3X0C-00SJG0")]
    public void Drive_names_match_what_librehardwaremonitor_shows(string vendor, string product, string expected)
        => Assert.Equal(expected, StorageQuery.BuildName(vendor, product));

    [Fact]
    public void Collapse_trims_and_squeezes_spaces_and_nuls()
        => Assert.Equal("ST4000 DM004", StorageQuery.Collapse("  ST4000   DM004 \0\0 "));

    [Theory]
    [InlineData(0x0Bu, false, true)]   // SATA
    [InlineData(0x11u, false, true)]   // NVMe
    [InlineData(0x0Au, false, true)]   // SAS
    [InlineData(0x08u, false, true)]   // RAID
    [InlineData(0x07u, false, false)]  // USB
    [InlineData(0x04u, false, false)]  // 1394
    [InlineData(0x0Cu, false, false)]  // SD
    [InlineData(0x0Du, false, false)]  // MMC
    [InlineData(0x0Eu, false, false)]  // virtual
    [InlineData(0x0Fu, false, false)]  // file-backed virtual (VHD)
    [InlineData(0x10u, false, false)]  // Storage Spaces
    [InlineData(0x09u, false, false)]  // iSCSI
    [InlineData(0x0Bu, true, false)]   // removable media on any bus
    public void Only_fixed_internal_buses_are_opened_and_kept(uint bus, bool removable, bool expected)
        => Assert.Equal(expected, StorageQuery.IsFixedBus(bus, removable));
}
