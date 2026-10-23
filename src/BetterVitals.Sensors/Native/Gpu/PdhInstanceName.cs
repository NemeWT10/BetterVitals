namespace BetterVitals.Sensors.Native.Gpu;

/// <summary>
/// Parser for the instance names of the "GPU Engine" and "GPU Adapter Memory" performance counters:
/// <c>pid_9836_luid_0x00000000_0x0000F3A5_phys_0_eng_0_engtype_3D</c> and <c>luid_0x00000000_0x0000F3A5_phys_0</c>.
/// Works on spans, never allocates.
/// </summary>
internal static class PdhInstanceName
{
    /// <summary>
    /// Extracts the adapter LUID (<c>High &lt;&lt; 32 | Low</c>, the same value as <c>Luid.Value</c>), the physical adapter index
    /// and the engine index (-1 for adapter-level names without "eng_").
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> name, out long luid, out int physical, out int engine)
    {
        luid = 0;
        physical = 0;
        engine = -1;
        var at = name.IndexOf("luid_0x", StringComparison.Ordinal);
        if (at < 0) return false;
        var rest = name[(at + 7)..];
        if (!TryHex(rest, out var high, out var used)) return false;
        rest = rest[used..];
        if (!rest.StartsWith("_0x", StringComparison.Ordinal)) return false;
        rest = rest[3..];
        if (!TryHex(rest, out var low, out used)) return false;
        rest = rest[used..];
        luid = ((long)(int)(uint)high << 32) | (uint)low;

        if (TryNumberAfter(rest, "_phys_", out var phys)) physical = phys;
        if (TryNumberAfter(rest, "_eng_", out var eng)) engine = eng;
        return true;
    }

    private static bool TryHex(ReadOnlySpan<char> s, out ulong value, out int used)
    {
        value = 0;
        used = 0;
        while (used < s.Length && used < 16)
        {
            var c = s[used];
            int digit = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
            if (digit < 0) break;
            value = (value << 4) | (uint)digit;
            used++;
        }
        return used > 0;
    }

    private static bool TryNumberAfter(ReadOnlySpan<char> s, ReadOnlySpan<char> marker, out int value)
    {
        value = 0;
        var at = s.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return false;
        var digits = s[(at + marker.Length)..];
        var n = 0;
        while (n < digits.Length && n < 9 && digits[n] is >= '0' and <= '9') value = value * 10 + (digits[n++] - '0');
        return n > 0;
    }
}
