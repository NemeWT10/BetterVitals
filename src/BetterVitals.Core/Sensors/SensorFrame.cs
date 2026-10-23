namespace BetterVitals.Core.Sensors;

/// <summary>
/// The latest values of every registered sensor, indexed by <see cref="SensorDescriptor.Index"/>.
/// The engine rotates a small ring of frames: a frame obtained from <see cref="ISensorHub.Latest"/> or
/// <see cref="ISensorHub.FrameReady"/> stays unchanged for at least two further ticks — read it right away
/// (copy what you need) and never keep it.
/// </summary>
public sealed class SensorFrame
{
    public SensorFrame(int capacity)
    {
        Values = new float[capacity];
        UpdatedAt = new int[capacity];
        Values.AsSpan().Fill(float.NaN);
    }

    /// <summary>Value per sensor slot in base units (<see cref="SensorUnits"/>); NaN = no value.</summary>
    public float[] Values { get; internal set; }

    /// <summary><see cref="Sequence"/> of the tick that last refreshed each slot (0 = never).</summary>
    public int[] UpdatedAt { get; internal set; }

    /// <summary>Number of registered sensors (slots in use; arrays may be longer).</summary>
    public int Count { get; internal set; }

    /// <summary>Tick number, increasing by one per engine tick, starting at 1.</summary>
    public int Sequence { get; internal set; }

    /// <summary>UTC time of the tick (DateTime ticks), aligned to the tick grid.</summary>
    public long TimestampUtcTicks { get; internal set; }

    public DateTime TimestampUtc => new(TimestampUtcTicks, DateTimeKind.Utc);

    /// <summary>True when slot <paramref name="index"/> was refreshed in this very tick.</summary>
    public bool IsFresh(int index) => (uint)index < (uint)Count && UpdatedAt[index] == Sequence;

    public float this[int index] => (uint)index < (uint)Count ? Values[index] : float.NaN;

    /// <summary>Grows the arrays (engine only, between ticks).</summary>
    internal void EnsureCapacity(int capacity)
    {
        if (Values.Length >= capacity) return;
        var size = Math.Max(capacity, Values.Length * 2);
        var v = new float[size];
        v.AsSpan().Fill(float.NaN);
        Values.AsSpan().CopyTo(v);
        var u = new int[size];
        UpdatedAt.AsSpan().CopyTo(u);
        Values = v;
        UpdatedAt = u;
    }

    /// <summary>Copies values and stamps from <paramref name="other"/> (engine only).</summary>
    internal void CopyFrom(SensorFrame other)
    {
        EnsureCapacity(other.Count);
        other.Values.AsSpan(0, other.Count).CopyTo(Values);
        other.UpdatedAt.AsSpan(0, other.Count).CopyTo(UpdatedAt);
        Count = other.Count;
    }
}
