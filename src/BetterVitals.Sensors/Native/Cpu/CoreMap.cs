using System.Buffers.Binary;
using BetterVitals.Sensors.Native.Interop;

namespace BetterVitals.Sensors.Native.Cpu;

/// <summary>
/// Which logical processors (threads) belong to which physical core, from <c>GetLogicalProcessorInformationEx(RelationProcessorCore)</c>.
/// Cores are numbered 0… in the order of their first logical processor (hybrid CPUs: P and E cores alike).
/// </summary>
internal static unsafe class CoreMap
{
    /// <summary>Reads the map from the OS; <c>CoreOfLogical[i] = -1</c> for a logical processor no core record mentioned.</summary>
    public static (int[] CoreOfLogical, int CoreCount) Read(int logicalCount)
    {
        uint length = 0;
        Kernel32.GetLogicalProcessorInformationEx(0, null, ref length);
        if (length == 0 || length > 1 << 20) return (Identity(logicalCount), logicalCount);
        var buffer = new byte[length];
        fixed (byte* p = buffer)
        {
            if (!Kernel32.GetLogicalProcessorInformationEx(0, p, ref length)) return (Identity(logicalCount), logicalCount);
        }
        var groups = Kernel32.GetActiveProcessorGroupCount();
        var bases = new int[Math.Max(groups, (ushort)1)];
        for (ushort g = 1; g < bases.Length; g++) bases[g] = bases[g - 1] + (int)Kernel32.GetActiveProcessorCount((ushort)(g - 1));
        return Parse(buffer.AsSpan(0, (int)length), bases, logicalCount);
    }

    /// <summary>
    /// Pure parser. <paramref name="groupBase"/>[g] = number of logical processors in the groups before g (the index of
    /// group g's bit 0 in the performance array).
    /// </summary>
    public static (int[] CoreOfLogical, int CoreCount) Parse(ReadOnlySpan<byte> buffer, ReadOnlySpan<int> groupBase, int logicalCount)
    {
        var cores = new List<List<int>>();
        var offset = 0;
        while (offset + 8 <= buffer.Length)
        {
            var relationship = BinaryPrimitives.ReadInt32LittleEndian(buffer[offset..]);
            var size = BinaryPrimitives.ReadInt32LittleEndian(buffer[(offset + 4)..]);
            if (size < 8 || offset + size > buffer.Length) break;
            if (relationship == 0 && size >= 8 + 32) // RelationProcessorCore: PROCESSOR_RELATIONSHIP after the 8-byte header
            {
                var record = buffer.Slice(offset + 8, size - 8);
                var groupCount = BinaryPrimitives.ReadUInt16LittleEndian(record[22..]);
                var members = new List<int>();
                for (var g = 0; g < groupCount && 24 + (g + 1) * 16 <= record.Length; g++)
                {
                    var mask = BinaryPrimitives.ReadUInt64LittleEndian(record[(24 + g * 16)..]);
                    int group = BinaryPrimitives.ReadUInt16LittleEndian(record[(24 + g * 16 + 8)..]);
                    var baseIndex = group < groupBase.Length ? groupBase[group] : group * 64;
                    for (var bit = 0; bit < 64; bit++)
                        if ((mask >> bit & 1) != 0) members.Add(baseIndex + bit);
                }
                if (members.Count > 0) cores.Add(members);
            }
            offset += size;
        }
        if (cores.Count == 0) return (Identity(logicalCount), logicalCount);

        cores.Sort((a, b) => a.Min().CompareTo(b.Min()));
        var map = new int[logicalCount];
        Array.Fill(map, -1);
        for (var c = 0; c < cores.Count; c++)
            foreach (var l in cores[c])
                if ((uint)l < (uint)map.Length) map[l] = c;
        return (map, cores.Count);
    }

    /// <summary>No topology available: every logical processor is its own core.</summary>
    private static int[] Identity(int count)
    {
        var map = new int[count];
        for (var i = 0; i < count; i++) map[i] = i;
        return map;
    }
}
