using System.Buffers.Binary;
using System.Text;
using BetterVitals.Sensors.Native.Interop;

namespace BetterVitals.Sensors.Native.Memory;

/// <summary>
/// SMBIOS reader: the raw table through <c>GetSystemFirmwareTable('RSMB')</c> (no administrator rights) and a parser for
/// the Memory Device structure (type 17). Field offsets follow DMTF DSP0134 (SMBIOS 3.x), "7.18 Memory Device (Type 17)":
/// 0x0C size, 0x0E form factor, 0x10 / 0x11 locators, 0x12 type, 0x15 speed, 0x17 manufacturer, 0x1A part number,
/// 0x1C extended size, 0x20 configured speed, 0x54 extended speed, 0x58 extended configured speed. Fields beyond the
/// structure's own length (older SMBIOS) read as "not present". Pure functions over spans, tested on captured tables.
/// </summary>
internal static unsafe class Smbios
{
    private const int RawHeader = 8; // RawSMBIOSData: Used20CallingMethod, Major, Minor, DmiRevision, DWORD Length

    /// <summary>The raw table as <c>GetSystemFirmwareTable</c> returns it (header included), or null.</summary>
    public static byte[]? ReadRaw()
    {
        var size = Kernel32.GetSystemFirmwareTable(Kernel32.RawSmbiosSignature, 0, null, 0);
        if (size is 0 or > 4 * 1024 * 1024) return null;
        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            var got = Kernel32.GetSystemFirmwareTable(Kernel32.RawSmbiosSignature, 0, p, size);
            if (got == 0 || got > size) return null;
            return got == size ? buffer : buffer.AsSpan(0, (int)got).ToArray();
        }
    }

    /// <summary>SMBIOS version of the table ("3.7"), or empty when the header is missing.</summary>
    public static string Version(ReadOnlySpan<byte> raw) => raw.Length >= RawHeader ? $"{raw[1]}.{raw[2]}" : "";

    /// <summary>Populated memory devices of a raw table (empty slots — size 0 — are left out).</summary>
    public static List<MemoryModuleInfo> ParseMemoryModules(ReadOnlySpan<byte> raw)
    {
        var modules = new List<MemoryModuleInfo>();
        if (raw.Length <= RawHeader) return modules;
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]);
        var length = (int)Math.Min(declared, (uint)(raw.Length - RawHeader));
        var table = raw.Slice(RawHeader, length);

        var offset = 0;
        while (offset + 4 <= table.Length)
        {
            var type = table[offset];
            var formatted = table[offset + 1];
            if (type == 127 || formatted < 4 || offset + formatted > table.Length) break; // end-of-table / corrupt
            var structure = table.Slice(offset, formatted);
            var stringsStart = offset + formatted;
            var stringsEnd = stringsStart;
            // the string set ends with an empty string: two NULs in a row (two NULs right away = no strings)
            while (stringsEnd + 1 < table.Length && !(table[stringsEnd] == 0 && table[stringsEnd + 1] == 0)) stringsEnd++;
            if (type == 17 && ParseMemoryDevice(structure, table[stringsStart..Math.Min(stringsEnd, table.Length)]) is { } module)
                modules.Add(module);
            offset = stringsEnd + 2;
        }
        return modules;
    }

    /// <summary>One type 17 structure (formatted area) with its string set; null for an empty slot.</summary>
    internal static MemoryModuleInfo? ParseMemoryDevice(ReadOnlySpan<byte> s, ReadOnlySpan<byte> strings)
    {
        if (s.Length < 0x15) return null; // before SMBIOS 2.1 there is no type / size
        var sizeField = U16(s, 0x0C);
        if (sizeField is 0 or 0xFFFF) return null; // no module installed / unknown
        long capacityMb;
        if (sizeField == 0x7FFF) capacityMb = U32(s, 0x1C) & 0x7FFFFFFF; // extended size, MB
        else capacityMb = (sizeField & 0x8000) != 0 ? (sizeField & 0x7FFF) / 1024 : sizeField; // bit 15: KB units
        if (capacityMb <= 0) return null;

        var speed = (int)U16(s, 0x15);
        if (speed == 0xFFFF) speed = ExtendedSpeed(s, 0x54);
        var configured = (int)U16(s, 0x20);
        if (configured == 0xFFFF) configured = ExtendedSpeed(s, 0x58);

        return new MemoryModuleInfo(
            DeviceLocator: Str(strings, U8(s, 0x10)),
            BankLocator: Str(strings, U8(s, 0x11)),
            Type: TypeName(U8(s, 0x12)),
            FormFactor: FormFactorName(U8(s, 0x0E)),
            CapacityMb: (int)Math.Min(capacityMb, int.MaxValue),
            SpeedMTs: Math.Max(speed, 0),
            ConfiguredSpeedMTs: Math.Max(configured, 0),
            Manufacturer: Clean(Str(strings, U8(s, 0x17))),
            PartNumber: Clean(Str(strings, U8(s, 0x1A))));
    }

    private static int ExtendedSpeed(ReadOnlySpan<byte> s, int offset)
    {
        var v = U32(s, offset) & 0x7FFFFFFF; // bit 31 is reserved
        return v > 0 && v <= int.MaxValue ? (int)v : 0;
    }

    private static uint U8(ReadOnlySpan<byte> s, int offset) => offset < s.Length ? s[offset] : 0u;

    private static uint U16(ReadOnlySpan<byte> s, int offset) => offset + 2 <= s.Length ? BinaryPrimitives.ReadUInt16LittleEndian(s[offset..]) : 0u;

    private static uint U32(ReadOnlySpan<byte> s, int offset) => offset + 4 <= s.Length ? BinaryPrimitives.ReadUInt32LittleEndian(s[offset..]) : 0u;

    /// <summary>String number <paramref name="index"/> (1-based) of the string set; empty when absent.</summary>
    internal static string Str(ReadOnlySpan<byte> strings, uint index)
    {
        if (index == 0) return "";
        var rest = strings;
        for (uint i = 1; ; i++)
        {
            if (rest.IsEmpty) return "";
            var end = rest.IndexOf((byte)0);
            var one = end < 0 ? rest : rest[..end];
            if (i == index) return Encoding.Latin1.GetString(one).Trim();
            if (end < 0) return "";
            rest = rest[(end + 1)..];
        }
    }

    private static readonly string[] Placeholders =
    [
        "unknown", "not specified", "[empty]", "to be filled by o.e.m.", "undefined", "none", "n/a", "not available", "0000", "00000000", "default string",
    ];

    /// <summary>Firmware fills missing strings with placeholders; those mean "nothing".</summary>
    internal static string Clean(string text)
    {
        var t = text.Trim();
        foreach (var p in Placeholders)
            if (t.Equals(p, StringComparison.OrdinalIgnoreCase)) return "";
        return t;
    }

    /// <summary>Memory Device "Memory Type" (offset 0x12), DSP0134 table in 7.18.2.</summary>
    internal static string TypeName(uint code) => code switch
    {
        0x03 => "DRAM",
        0x04 => "EDRAM",
        0x05 => "VRAM",
        0x06 => "SRAM",
        0x07 => "RAM",
        0x0D => "CDRAM",
        0x0E => "3DRAM",
        0x0F => "SDRAM",
        0x10 => "SGRAM",
        0x11 => "RDRAM",
        0x12 => "DDR",
        0x13 => "DDR2",
        0x14 => "DDR2 FB-DIMM",
        0x18 => "DDR3",
        0x19 => "FBD2",
        0x1A => "DDR4",
        0x1B => "LPDDR",
        0x1C => "LPDDR2",
        0x1D => "LPDDR3",
        0x1E => "LPDDR4",
        0x1F => "Logical non-volatile device",
        0x20 => "HBM",
        0x21 => "HBM2",
        0x22 => "DDR5",
        0x23 => "LPDDR5",
        0x24 => "HBM3",
        _ => "Unknown",
    };

    /// <summary>Memory Device "Form Factor" (offset 0x0E), DSP0134 table in 7.18.1.</summary>
    internal static string FormFactorName(uint code) => code switch
    {
        0x03 => "SIP",
        0x04 => "DIP",
        0x05 => "ZIP",
        0x06 => "SOJ",
        0x07 => "Proprietary",
        0x08 => "SIMM",
        0x09 => "DIMM",
        0x0A => "TSOP",
        0x0B => "Row of chips",
        0x0C => "RIMM",
        0x0D => "SODIMM",
        0x0E => "SRIMM",
        0x0F => "FB-DIMM",
        0x10 => "Die",
        _ => "Unknown",
    };
}
