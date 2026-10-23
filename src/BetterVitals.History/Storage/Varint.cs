namespace BetterVitals.History.Storage;

/// <summary>LEB128 varints and zigzag mapping. Readers throw <see cref="InvalidDataException"/> on malformed input.</summary>
internal static class Varint
{
    public const int MaxBytes = 10;

    public static ulong ZigZag(long v) => (ulong)((v << 1) ^ (v >> 63));

    public static long UnZigZag(ulong u) => (long)(u >> 1) ^ -(long)(u & 1);

    /// <summary>Writes <paramref name="v"/> at <paramref name="pos"/> (caller guarantees <see cref="MaxBytes"/> of room).</summary>
    public static void Write(byte[] dst, ref int pos, ulong v)
    {
        while (v >= 0x80)
        {
            dst[pos++] = (byte)(v | 0x80);
            v >>= 7;
        }
        dst[pos++] = (byte)v;
    }

    public static ulong Read(ReadOnlySpan<byte> src, ref int pos)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            if ((uint)pos >= (uint)src.Length || shift > 63) throw new InvalidDataException("bad varint");
            var b = src[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) return result;
            shift += 7;
        }
    }

    /// <summary>Skips <paramref name="count"/> varints.</summary>
    public static void Skip(ReadOnlySpan<byte> src, ref int pos, int count)
    {
        while (count > 0)
        {
            if ((uint)pos >= (uint)src.Length) throw new InvalidDataException("bad varint");
            if (src[pos++] < 0x80) count--;
        }
    }
}
