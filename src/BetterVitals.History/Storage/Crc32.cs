using System.Buffers.Binary;

namespace BetterVitals.History.Storage;

/// <summary>CRC-32 (IEEE 802.3, reflected 0xEDB88320, same as zlib / ZIP), slicing-by-8.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = Build();

    public static uint Compute(ReadOnlySpan<byte> data) => Append(0, data);

    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        var t = Table;
        crc = ~crc;
        while (data.Length >= 8)
        {
            var one = BinaryPrimitives.ReadUInt32LittleEndian(data) ^ crc;
            var two = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
            crc = t[7 * 256 + (one & 0xFF)] ^ t[6 * 256 + ((one >> 8) & 0xFF)] ^ t[5 * 256 + ((one >> 16) & 0xFF)] ^ t[4 * 256 + (one >> 24)]
                ^ t[3 * 256 + (two & 0xFF)] ^ t[2 * 256 + ((two >> 8) & 0xFF)] ^ t[256 + ((two >> 16) & 0xFF)] ^ t[two >> 24];
            data = data[8..];
        }
        foreach (var b in data) crc = t[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] Build()
    {
        var t = new uint[8 * 256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        for (var i = 0; i < 256; i++)
        for (var s = 1; s < 8; s++)
            t[s * 256 + i] = t[(s - 1) * 256 + i] >> 8 ^ t[t[(s - 1) * 256 + i] & 0xFF];
        return t;
    }
}
