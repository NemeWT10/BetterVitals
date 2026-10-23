using System.Diagnostics;

namespace BetterVitals.Sensors.Native;

/// <summary>
/// One refreshable unit of the native source: a drive, a GPU adapter, the CPU, a network adapter, the memory. A node is
/// refreshed at most once per <see cref="NativeSource.Update"/> call, and only when one of its sensors is due.
/// Implementations keep their handles open between ticks, never allocate in <see cref="Refresh"/> and never throw
/// (the source still guards the call).
/// </summary>
internal abstract class NativeNode
{
    /// <summary>Update-call number of the last refresh (the source compares it to skip repeated refreshes in one call).</summary>
    internal int Stamp;

    /// <summary>Reads the hardware (<paramref name="timestamp"/> = <see cref="Stopwatch.GetTimestamp"/>).</summary>
    internal abstract void Refresh(long timestamp);

    /// <summary>The value of sensor slot <paramref name="slot"/> from the last refresh (NaN = none).</summary>
    internal abstract float Read(int slot);

    /// <summary>Called when <see cref="Refresh"/> threw: forget the values.</summary>
    internal virtual void Fault() { }

    /// <summary>Releases OS handles (suspend, re-discovery, dispose).</summary>
    internal virtual void Release() { }

    protected static long Ticks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);
}

/// <summary>Source-private handle stored in <see cref="Core.Sensors.SensorDescriptor.SourceTag"/>.</summary>
internal sealed class NativeTag(NativeNode node, int slot)
{
    public NativeNode Node { get; } = node;

    public int Slot { get; } = slot;
}

/// <summary>A node with fixed values (SMBIOS memory speed): nothing to read.</summary>
internal sealed class ConstantNode(params float[] values) : NativeNode
{
    internal override void Refresh(long timestamp) { }

    internal override float Read(int slot) => (uint)slot < (uint)values.Length ? values[slot] : float.NaN;
}
