using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using BetterVitals.Sensors.Native.Interop;

namespace BetterVitals.Sensors.Native.Drives;

/// <summary>What <c>StorageDeviceProperty</c> says about a drive.</summary>
internal sealed record StorageDescriptor(string Vendor, string Product, string Serial, uint BusType, bool Removable)
{
    /// <summary>"KINGSTON SKC3000D2048G": the product id, with the vendor in front when it adds information.</summary>
    public string Name => StorageQuery.BuildName(Vendor, Product);
}

/// <summary>
/// <c>IOCTL_STORAGE_QUERY_PROPERTY</c> on a handle opened with desiredAccess 0 (works without administrator rights):
/// identity, seek penalty (rotating or not) and the temperature data. The parsers are pure functions over byte spans.
/// </summary>
internal static unsafe class StorageQuery
{
    public const int PropertyDevice = 0;
    public const int PropertyProtocolSpecific = 50;
    public const int PropertySeekPenalty = 7;
    /// <summary>StorageDeviceTemperatureProperty (52). Not 12: that is StorageDevicePowerProperty.</summary>
    public const int PropertyTemperature = 52;

    public const uint BusTypeNvme = 0x11;
    public const uint BusTypeUsb = 7, BusType1394 = 4, BusTypeSd = 0xC, BusTypeMmc = 0xD, BusTypeVirtual = 0xE,
        BusTypeFileBackedVirtual = 0xF, BusTypeSpaces = 0x10, BusTypeIscsi = 9;

    /// <summary>STORAGE_PROPERTY_QUERY: id, query type (0 = standard), one parameter byte (padded to 12).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public int Additional;
    }

    private const int TemperatureHeader = 24; // STORAGE_TEMPERATURE_DATA_DESCRIPTOR before the entries
    private const int TemperatureEntry = 16;  // STORAGE_TEMPERATURE_INFO
    private const int TemperatureMaxEntries = 16;

    public static nint OpenPhysicalDrive(int index)
        => Kernel32.CreateFile($@"\\.\PhysicalDrive{index}", 0, Kernel32.FileShareRead | Kernel32.FileShareWrite, 0, Kernel32.OpenExisting, 0, 0);

    public static bool IsValid(nint handle) => handle != 0 && handle != Kernel32.InvalidHandle;

    private static bool Ioctl(nint handle, int propertyId, void* buffer, int size, out int returned)
    {
        var q = new PropertyQuery { PropertyId = propertyId };
        var ok = Kernel32.DeviceIoControl(handle, Kernel32.IoctlStorageQueryProperty, &q, (uint)sizeof(PropertyQuery), buffer, (uint)size, out var n, 0);
        returned = (int)n;
        return ok;
    }

    public static StorageDescriptor? ReadDescriptor(nint handle)
    {
        var buf = stackalloc byte[1024];
        return Ioctl(handle, PropertyDevice, buf, 1024, out var n) ? ParseDescriptor(new ReadOnlySpan<byte>(buf, n)) : null;
    }

    /// <summary>True = the drive moves heads (rotating), false = no seek penalty (SSD), null = the driver does not say.</summary>
    public static bool? ReadIncursSeekPenalty(nint handle)
    {
        var buf = stackalloc byte[32];
        return Ioctl(handle, PropertySeekPenalty, buf, 32, out var n) && n >= 9 ? buf[8] != 0 : null;
    }

    /// <summary>
    /// Finds out which way this drive reports its temperature: the generic temperature property first, then (NVMe only) the
    /// SMART / health log page. <see cref="TemperatureMethod.None"/> = the firmware does not answer.
    /// </summary>
    public static TemperatureMethod ProbeTemperature(nint handle, uint busType, out float celsius)
    {
        if (TryReadTemperature(handle, TemperatureMethod.DeviceProperty, out celsius)) return TemperatureMethod.DeviceProperty;
        if (busType == BusTypeNvme && TryReadTemperature(handle, TemperatureMethod.NvmeHealthLog, out celsius)) return TemperatureMethod.NvmeHealthLog;
        celsius = float.NaN;
        return TemperatureMethod.None;
    }

    /// <summary>Hot path: one IOCTL into a stack buffer, no allocation.</summary>
    public static bool TryReadTemperature(nint handle, TemperatureMethod method, out float celsius)
        => method switch
        {
            TemperatureMethod.DeviceProperty => ReadDeviceProperty(handle, out celsius),
            TemperatureMethod.NvmeHealthLog => ReadNvmeHealthLog(handle, out celsius),
            _ => Fail(out celsius),
        };

    private static bool Fail(out float celsius)
    {
        celsius = float.NaN;
        return false;
    }

    private static bool ReadDeviceProperty(nint handle, out float celsius)
    {
        var buf = stackalloc byte[TemperatureHeader + TemperatureEntry * TemperatureMaxEntries];
        if (Ioctl(handle, PropertyTemperature, buf, TemperatureHeader + TemperatureEntry * TemperatureMaxEntries, out var n))
            return ParseTemperature(new ReadOnlySpan<byte>(buf, n), out celsius);
        celsius = float.NaN;
        return false;
    }

    private const int ProtocolQueryLength = 8 + 40;      // STORAGE_PROPERTY_QUERY header + STORAGE_PROTOCOL_SPECIFIC_DATA
    private const int NvmeLogLength = 512;
    private const int ProtocolDescriptorLength = 8 + 40; // STORAGE_PROTOCOL_DATA_DESCRIPTOR before the data

    /// <summary>NVMe SMART / health information log (page 2) through the protocol-specific property: composite temperature.</summary>
    private static bool ReadNvmeHealthLog(nint handle, out float celsius)
    {
        celsius = float.NaN;
        var input = stackalloc uint[ProtocolQueryLength / 4];
        new Span<uint>(input, ProtocolQueryLength / 4).Clear();
        input[0] = PropertyProtocolSpecific;
        input[1] = 0; // PropertyStandardQuery
        input[2] = 3; // ProtocolTypeNvme
        input[3] = 2; // NVMeDataTypeLogPage
        input[4] = 2; // NVME_LOG_PAGE_HEALTH_INFO
        input[5] = 0; // log page offset
        input[6] = 40; // ProtocolDataOffset = sizeof(STORAGE_PROTOCOL_SPECIFIC_DATA)
        input[7] = NvmeLogLength;
        var output = stackalloc byte[ProtocolDescriptorLength + NvmeLogLength];
        if (!Kernel32.DeviceIoControl(handle, Kernel32.IoctlStorageQueryProperty, input, ProtocolQueryLength, output,
                ProtocolDescriptorLength + NvmeLogLength, out var n, 0))
            return false;
        return ParseNvmeHealthLog(new ReadOnlySpan<byte>(output, (int)n), out celsius);
    }

    /// <summary>
    /// STORAGE_PROTOCOL_DATA_DESCRIPTOR holding NVMe log page 2 → the composite temperature (bytes 1–2 of the log, Kelvin).
    /// </summary>
    public static bool ParseNvmeHealthLog(ReadOnlySpan<byte> data, out float celsius)
    {
        celsius = float.NaN;
        if (data.Length < ProtocolDescriptorLength) return false;
        // ProtocolSpecificData starts at byte 8; its ProtocolDataOffset / ProtocolDataLength are its 5th / 6th DWORD
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(data[(8 + 16)..]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(data[(8 + 20)..]);
        if (length < 3 || offset > data.Length - 8 || 8 + (long)offset + 3 > data.Length) return false;
        var log = data[(8 + (int)offset)..];
        int kelvin = BinaryPrimitives.ReadUInt16LittleEndian(log[1..]);
        var c = kelvin - 273;
        if (kelvin == 0 || !Plausible.DriveTemperature(c)) return false;
        celsius = c;
        return true;
    }

    /// <summary>
    /// STORAGE_TEMPERATURE_DATA_DESCRIPTOR → the temperature of the lowest-index entry with a plausible value. The entry
    /// count is taken from the number of bytes returned, not from <c>InfoCount</c> (some firmware gets it wrong); zeroed or
    /// nonsense entries fall to <see cref="Plausible.DriveTemperature"/>.
    /// </summary>
    public static bool ParseTemperature(ReadOnlySpan<byte> data, out float celsius)
    {
        celsius = float.NaN;
        if (data.Length < TemperatureHeader + TemperatureEntry) return false;
        var count = Math.Min((data.Length - TemperatureHeader) / TemperatureEntry, TemperatureMaxEntries);
        var bestIndex = int.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var e = data.Slice(TemperatureHeader + i * TemperatureEntry, TemperatureEntry);
            int index = BinaryPrimitives.ReadUInt16LittleEndian(e);
            int temp = BinaryPrimitives.ReadInt16LittleEndian(e[2..]);
            if (!Plausible.DriveTemperature(temp) || index >= bestIndex) continue;
            bestIndex = index;
            celsius = temp;
        }
        return bestIndex != int.MaxValue;
    }

    /// <summary>STORAGE_DEVICE_DESCRIPTOR → vendor / product / serial strings (ANSI, NUL-terminated, at the given offsets).</summary>
    public static StorageDescriptor? ParseDescriptor(ReadOnlySpan<byte> data)
    {
        if (data.Length < 36) return null;
        var removable = data[10] != 0;
        var vendor = AnsiAt(data, BinaryPrimitives.ReadUInt32LittleEndian(data[12..]));
        var product = AnsiAt(data, BinaryPrimitives.ReadUInt32LittleEndian(data[16..]));
        var serial = AnsiAt(data, BinaryPrimitives.ReadUInt32LittleEndian(data[24..]));
        var bus = BinaryPrimitives.ReadUInt32LittleEndian(data[28..]);
        return new StorageDescriptor(vendor, product, serial, bus, removable);
    }

    private static string AnsiAt(ReadOnlySpan<byte> data, uint offset)
    {
        if (offset == 0 || offset >= data.Length) return "";
        var s = data[(int)offset..];
        var end = s.IndexOf((byte)0);
        if (end >= 0) s = s[..end];
        return Collapse(Encoding.Latin1.GetString(s));
    }

    /// <summary>Trims and collapses runs of spaces ("ST4000DM004-2U9104   " → "ST4000DM004-2U9104").</summary>
    internal static string Collapse(string text)
    {
        var sb = new StringBuilder(text.Length);
        var space = true;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || ch == '\0')
            {
                if (!space) sb.Append(' ');
                space = true;
            }
            else
            {
                sb.Append(ch);
                space = false;
            }
        }
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.ToString();
    }

    private static readonly string[] GenericVendors = ["ATA", "NVMe", "SCSI", "SATA", "USB", "Msft", "Generic"];

    /// <summary>The drive's display name, like LibreHardwareMonitor shows it (product id; a meaningful vendor goes in front).</summary>
    public static string BuildName(string vendor, string product)
    {
        vendor = Collapse(vendor);
        product = Collapse(product);
        if (product.Length == 0) return vendor;
        if (vendor.Length == 0 || product.StartsWith(vendor, StringComparison.OrdinalIgnoreCase)) return product;
        foreach (var g in GenericVendors)
            if (vendor.Equals(g, StringComparison.OrdinalIgnoreCase)) return product;
        return vendor + " " + product;
    }

    /// <summary>Removable and virtual buses are left alone (an open handle could get in the way of ejecting).</summary>
    public static bool IsFixedBus(uint busType, bool removable)
        => !removable && busType is not (BusTypeUsb or BusType1394 or BusTypeSd or BusTypeMmc or BusTypeVirtual or BusTypeFileBackedVirtual or BusTypeSpaces or BusTypeIscsi);
}

/// <summary>How a drive reports its temperature (decided once at discovery).</summary>
internal enum TemperatureMethod : byte
{
    None,

    /// <summary>StorageDeviceTemperatureProperty (SATA / many NVMe).</summary>
    DeviceProperty,

    /// <summary>NVMe SMART / health log page through StorageDeviceProtocolSpecificProperty.</summary>
    NvmeHealthLog,
}
