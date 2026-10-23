using System.Diagnostics;

namespace BetterVitals.Sensors.Power;

/// <summary>Result of one power-state query.</summary>
public enum PowerQuery : byte
{
    /// <summary>Could not be determined (property missing, query failed); callers treat it as awake.</summary>
    Unknown,
    Awake,
    Asleep,
    /// <summary>The device node is gone (surprise removal).</summary>
    Gone,
}

/// <summary>
/// Decides whether a device may be touched right now without waking it. Queries the OS view of the power state
/// (never the device itself) and, while the device sleeps, re-checks at most every <see cref="PowerGateState.RecheckInterval"/>.
/// </summary>
internal abstract class PowerGate
{
    private PowerGateState _state;

    /// <summary>What the gate is bound to (for diagnostics), e.g. "PnP PCI\VEN_10DE…" or "\\.\PhysicalDrive2".</summary>
    public abstract string Target { get; }

    /// <summary>Last query result.</summary>
    public PowerQuery Last => _state.Last;

    /// <summary>Number of OS power-state queries so far.</summary>
    public long Queries { get; private set; }

    /// <summary>True when the device may be touched now. Engine thread; allocation-free.</summary>
    public bool IsAwake(long nowTimestamp)
    {
        if (_state.NeedsQuery(nowTimestamp, Stopwatch.Frequency))
        {
            Queries++;
            _state.Record(nowTimestamp, Query());
        }
        return _state.AllowsAccess;
    }

    /// <summary>Asks the OS for the current power state. Must not wake the device.</summary>
    protected abstract PowerQuery Query();
}

/// <summary>Pure re-check policy of a <see cref="PowerGate"/> (unit-tested without hardware).</summary>
internal struct PowerGateState
{
    /// <summary>A sleeping device is re-checked at most this often.</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(10);

    private long _lastQuery;
    private bool _queried;

    public PowerQuery Last { get; private set; }

    /// <summary>Awake / unknown → access allowed; asleep / gone → not.</summary>
    public readonly bool AllowsAccess => Last is PowerQuery.Awake or PowerQuery.Unknown;

    /// <summary>Awake devices are checked before every access; sleeping ones only after <see cref="RecheckInterval"/>.</summary>
    public readonly bool NeedsQuery(long now, long frequency)
        => !_queried || AllowsAccess || now - _lastQuery >= (long)(RecheckInterval.TotalSeconds * frequency);

    public void Record(long now, PowerQuery result)
    {
        _queried = true;
        _lastQuery = now;
        Last = result;
    }

    /// <summary>DEVICE_POWER_STATE: 0 unspecified, 1 = D0, 2 = D1, 3 = D2, 4 = D3. D1–D3 = asleep.</summary>
    public static PowerQuery FromDeviceState(uint deviceState) => deviceState switch
    {
        1 => PowerQuery.Awake,
        2 or 3 or 4 => PowerQuery.Asleep,
        _ => PowerQuery.Unknown,
    };
}
