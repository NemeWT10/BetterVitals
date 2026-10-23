using System.Buffers.Binary;
using System.Text;
using BetterVitals.Sensors.Native.Memory;
using Xunit;
using static BetterVitals.Tests.Native.SmbiosSamples;

namespace BetterVitals.Tests.Native;

/// <summary>SMBIOS byte samples and table builders shared by the memory tests.</summary>
internal static class SmbiosSamples
{
    // ── captured on the reference PC (2 × Kingston Fury DDR5-6000 16 GB, SMBIOS 3.7, formatted length 0x64); serial numbers blanked ──
    public const string EmptySlotA = "116412000F001100FFFFFFFF0000020001020204000000030400050000000000000000000000000002040006000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000044494D4D2030005030204348414E4E454C204100556E6B6E6F776E00556E6B6E6F776E00556E6B6E6F776E00556E6B6E6F776E0000";
    public const string ModuleA = "116414000F00130040004000004009000102228040701703040005010000000070174C044C044C04030800060198000000000000000000000000000000000000040000000000000000000000000000000000000000000000000000008A8C1300000000FF44494D4D2031005030204348414E4E454C2041004B696E6773746F6E003030303030303030004B463536304333302D31362020202020202020202020202020202020202000556E6B6E6F776E0000";
    public const string EmptySlotB = "116417000F001600FFFFFFFF0000020001020204000000030400050000000000000000000000000002040006000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000044494D4D2030005030204348414E4E454C204200556E6B6E6F776E00556E6B6E6F776E00556E6B6E6F776E00556E6B6E6F776E0000";
    public const string ModuleB = "116419000F00180040004000004009000102228040701703040005010000000070174C044C044C04030800060198000000000000000000000000000000000000040000000000000000000000000000000000000000000000000000008A8C1300000000FF44494D4D2031005030204348414E4E454C2042004B696E6773746F6E003030303030303030004B463536304333302D31362020202020202020202020202020202020202000556E6B6E6F776E0000";

    public static readonly byte[] EndOfTable = [0x7F, 0x04, 0xFF, 0x00, 0x00, 0x00];

    /// <summary>RawSMBIOSData as GetSystemFirmwareTable returns it: 8-byte header (method, major, minor, DMI revision, length) + structures.</summary>
    public static byte[] Raw(byte major, byte minor, params byte[][] structures)
    {
        var body = structures.SelectMany(s => s).Concat(EndOfTable).ToArray();
        var raw = new byte[8 + body.Length];
        raw[1] = major;
        raw[2] = minor;
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(4), body.Length);
        body.CopyTo(raw, 8);
        return raw;
    }

    public static byte[] Hex(string hex) => Convert.FromHexString(hex);

    /// <summary>A structure: formatted area + string set ("a\0b\0" + the closing NUL).</summary>
    public static byte[] Structure(byte[] formatted, params string[] strings)
    {
        var text = strings.Length == 0 ? "\0" : string.Concat(strings.Select(s => s + "\0"));
        return [.. formatted, .. Encoding.ASCII.GetBytes(text), 0];
    }

    /// <summary>The reference PC's whole table: empty slot, module, empty slot, module (DDR5-6000 x2).</summary>
    public static byte[] Ddr5Pc() => Raw(3, 7, Hex(EmptySlotA), Hex(ModuleA), Hex(EmptySlotB), Hex(ModuleB));
}


/// <summary>SMBIOS type 17 (Memory Device) parsing, on structures captured from a real DDR5 PC and hand-assembled DDR4 / old / extended ones.</summary>
public sealed class SmbiosTests
{
    [Fact]
    public void Real_ddr5_pc_gives_two_kingston_modules_and_skips_the_empty_slots()
    {
        var raw = Raw(3, 7, Hex(EmptySlotA), Hex(ModuleA), Hex(EmptySlotB), Hex(ModuleB));

        var modules = Smbios.ParseMemoryModules(raw);

        Assert.Equal(2, modules.Count);
        var a = modules[0];
        Assert.Equal("DIMM 1", a.DeviceLocator);
        Assert.Equal("P0 CHANNEL A", a.BankLocator);
        Assert.Equal("DDR5", a.Type);
        Assert.Equal("DIMM", a.FormFactor);
        Assert.Equal(16384, a.CapacityMb);
        Assert.Equal(6000, a.SpeedMTs);
        Assert.Equal(6000, a.ConfiguredSpeedMTs);
        Assert.Equal("Kingston", a.Manufacturer);
        Assert.Equal("KF560C30-16", a.PartNumber); // the firmware pads it with spaces
        Assert.Equal("P0 CHANNEL B", modules[1].BankLocator);
        Assert.Equal(6000, MemoryModule.SpeedOf(modules));
        Assert.Equal("3.7", Smbios.Version(raw));
    }

    [Fact]
    public void Ddr4_module_with_the_shorter_smbios_3_0_structure()
    {
        // hand-assembled from DSP0134 (3.0): length 0x28, so there are no extended speed fields
        var formatted = Hex("11283A00" + "3800" + "FEFF" + "4000" + "4000" + "0020" + "09" + "00" + "01" + "02" + "1A" + "8000" + "800C"
                            + "03" + "04" + "05" + "06" + "01" + "00000000" + "800C" + "B004" + "B004" + "B004");
        Assert.Equal(0x28, formatted.Length);
        var raw = Raw(3, 0, Structure(formatted, "ChannelA-DIMM0", "BANK 0", "Corsair", "00000000", "9876543210", "CMK16GX4M2B3200C16 "));

        var m = Assert.Single(Smbios.ParseMemoryModules(raw));

        Assert.Equal("DDR4", m.Type);
        Assert.Equal(8192, m.CapacityMb);
        Assert.Equal(3200, m.SpeedMTs);
        Assert.Equal(3200, m.ConfiguredSpeedMTs);
        Assert.Equal("Corsair", m.Manufacturer);
        Assert.Equal("CMK16GX4M2B3200C16", m.PartNumber);
        Assert.Equal("ChannelA-DIMM0", m.DeviceLocator);
        Assert.Equal("BANK 0", m.BankLocator);
    }

    [Fact]
    public void Module_running_below_its_rated_speed_reports_the_configured_one()
    {
        var formatted = Hex("11283A00" + "3800" + "FEFF" + "4000" + "4000" + "0020" + "09" + "00" + "01" + "02" + "1A" + "8000" + "800C"
                            + "03" + "04" + "05" + "06" + "01" + "00000000" + "5508" + "B004" + "B004" + "B004"); // configured 0x0855 = 2133 MT/s
        var m = Assert.Single(Smbios.ParseMemoryModules(Raw(3, 0, Structure(formatted, "A", "B", "Vendor", "S", "T", "PN"))));

        Assert.Equal(3200, m.SpeedMTs);
        Assert.Equal(2133, m.ConfiguredSpeedMTs);
        Assert.Equal(2133, m.EffectiveSpeedMTs);
    }

    [Fact]
    public void Old_structure_without_configured_speed_or_extended_size()
    {
        // SMBIOS 2.3: the structure ends after the part number (length 0x1B)
        var formatted = Hex("111B2000" + "1000" + "FEFF" + "4000" + "4000" + "0004" + "09" + "00" + "01" + "02" + "13" + "0000" + "9001" + "03" + "04" + "00" + "05");
        Assert.Equal(0x1B, formatted.Length);
        var m = Assert.Single(Smbios.ParseMemoryModules(Raw(2, 3, Structure(formatted, "DIMM0", "BANK0", "Samsung", "123", "M378T2863"))));

        Assert.Equal("DDR2", m.Type);
        Assert.Equal(1024, m.CapacityMb);
        Assert.Equal(400, m.SpeedMTs);
        Assert.Equal(0, m.ConfiguredSpeedMTs);
        Assert.Equal(400, m.EffectiveSpeedMTs);
    }

    [Fact]
    public void Speed_of_ffff_uses_the_extended_fields_at_0x54_and_0x58()
    {
        var formatted = Hex(ModuleA)[..0x64];
        BinaryPrimitives.WriteUInt16LittleEndian(formatted.AsSpan(0x15), 0xFFFF);
        BinaryPrimitives.WriteUInt16LittleEndian(formatted.AsSpan(0x20), 0xFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(formatted.AsSpan(0x54), 12800);
        BinaryPrimitives.WriteUInt32LittleEndian(formatted.AsSpan(0x58), 0x80000000u | 8000); // bit 31 is reserved
        var m = Assert.Single(Smbios.ParseMemoryModules(Raw(3, 7, Structure(formatted, "DIMM 1", "BANK", "Kingston", "0", "PN", "PN"))));

        Assert.Equal(12800, m.SpeedMTs);
        Assert.Equal(8000, m.ConfiguredSpeedMTs);
    }

    [Fact]
    public void Size_field_7fff_means_the_extended_size_in_mb()
    {
        var formatted = Hex(ModuleA)[..0x64];
        BinaryPrimitives.WriteUInt16LittleEndian(formatted.AsSpan(0x0C), 0x7FFF);
        BinaryPrimitives.WriteUInt32LittleEndian(formatted.AsSpan(0x1C), 65536);
        var m = Assert.Single(Smbios.ParseMemoryModules(Raw(3, 7, Structure(formatted, "DIMM 1", "BANK", "Kingston", "0", "PN", "PN"))));

        Assert.Equal(65536, m.CapacityMb);
    }

    [Fact]
    public void Size_field_with_bit_15_set_is_in_kilobytes()
    {
        var formatted = Hex(ModuleA)[..0x64];
        BinaryPrimitives.WriteUInt16LittleEndian(formatted.AsSpan(0x0C), 0x8000 | 8192); // 8192 KB = 8 MB
        var m = Assert.Single(Smbios.ParseMemoryModules(Raw(3, 7, Structure(formatted, "DIMM 1", "BANK", "Kingston", "0", "PN", "PN"))));

        Assert.Equal(8, m.CapacityMb);
    }

    [Fact]
    public void Unknown_size_and_empty_slots_are_skipped()
    {
        var unknown = Hex(ModuleA)[..0x64];
        BinaryPrimitives.WriteUInt16LittleEndian(unknown.AsSpan(0x0C), 0xFFFF);
        var empty = Hex(ModuleA)[..0x64];
        BinaryPrimitives.WriteUInt16LittleEndian(empty.AsSpan(0x0C), 0);

        Assert.Empty(Smbios.ParseMemoryModules(Raw(3, 7, Structure(unknown, "A"), Structure(empty, "A"))));
    }

    [Fact]
    public void Firmware_placeholders_become_empty_strings()
    {
        var formatted = Hex(ModuleA)[..0x64];
        var m = Assert.Single(Smbios.ParseMemoryModules(Raw(3, 7, Structure(formatted, "DIMM 1", "BANK", "To Be Filled By O.E.M.", "0", "Unknown", "Not Specified"))));

        Assert.Equal("", m.Manufacturer); // string 3
        Assert.Equal("", m.PartNumber);   // string 5 is "Unknown" here (offset 0x1A = 5)
    }

    [Fact]
    public void Other_structures_are_walked_over_and_end_of_table_stops_the_walk()
    {
        byte[] bios = Structure(Hex("0006010000AA"), "Vendor", "1.0"); // type 0 stub with two strings
        byte[] noStrings = Structure(Hex("04060200BBCC"));              // type 4 stub, empty string set
        byte[] afterEnd = Hex(ModuleA);                                  // after type 127: never parsed
        var body = bios.Concat(noStrings).Concat(Hex(ModuleA)).Concat(EndOfTable).Concat(afterEnd).ToArray();
        var raw = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(4), body.Length);
        body.CopyTo(raw, 8);

        Assert.Single(Smbios.ParseMemoryModules(raw));
    }

    [Fact]
    public void Declared_length_longer_than_the_data_is_clamped()
    {
        var raw = Raw(3, 7, Hex(ModuleA));
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(4), 1_000_000);

        Assert.Single(Smbios.ParseMemoryModules(raw));
    }

    [Fact]
    public void Truncated_and_garbage_tables_never_throw()
    {
        var raw = Raw(3, 7, Hex(EmptySlotA), Hex(ModuleA), Hex(EmptySlotB), Hex(ModuleB));
        for (var cut = 0; cut <= raw.Length; cut++) _ = Smbios.ParseMemoryModules(raw.AsSpan(0, cut));

        var rng = new Random(7);
        for (var i = 0; i < 300; i++)
        {
            var junk = new byte[rng.Next(0, 400)];
            rng.NextBytes(junk);
            if (junk.Length > 8) BinaryPrimitives.WriteInt32LittleEndian(junk.AsSpan(4), rng.Next(0, 500));
            _ = Smbios.ParseMemoryModules(junk);
        }
        // garbage inside a real table too
        for (var i = 0; i < 200; i++)
        {
            var copy = (byte[])raw.Clone();
            for (var k = 0; k < 6; k++) copy[rng.Next(8, copy.Length)] = (byte)rng.Next(256);
            _ = Smbios.ParseMemoryModules(copy);
        }
    }

    [Theory]
    [InlineData(0x1A, "DDR4")]
    [InlineData(0x22, "DDR5")]
    [InlineData(0x18, "DDR3")]
    [InlineData(0x23, "LPDDR5")]
    [InlineData(0x1E, "LPDDR4")]
    [InlineData(0x13, "DDR2")]
    [InlineData(0x02, "Unknown")]
    [InlineData(0xFF, "Unknown")]
    public void Memory_type_names(int code, string expected) => Assert.Equal(expected, Smbios.TypeName((uint)code));

    [Theory]
    [InlineData(0x09, "DIMM")]
    [InlineData(0x0D, "SODIMM")]
    [InlineData(0x0B, "Row of chips")]
    [InlineData(0x42, "Unknown")]
    public void Form_factor_names(int code, string expected) => Assert.Equal(expected, Smbios.FormFactorName((uint)code));

    [Fact]
    public void String_numbers_are_one_based_and_missing_ones_are_empty()
    {
        var set = Encoding.ASCII.GetBytes("one\0two\0three");
        Assert.Equal("one", Smbios.Str(set, 1));
        Assert.Equal("three", Smbios.Str(set, 3));
        Assert.Equal("", Smbios.Str(set, 0));
        Assert.Equal("", Smbios.Str(set, 4));
        Assert.Equal("", Smbios.Str([], 1));
    }
}
