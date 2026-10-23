using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Derived;
using BetterVitals.Sensors.Engine;
using BetterVitals.Sensors.Fake;

namespace ReadmeMedia;

/// <summary>
/// The PC in every picture: the desktop profile of <see cref="FakeSensorSource"/> (Ryzen 7 9800X3D, RTX 5080, a game
/// running) plus the derived sensors, in the real <see cref="SensorEngine"/> — but its clock only moves when this rig
/// says so: one step = one fake second = exactly one frame. The values depend on the fake time only, so every run gives
/// the same numbers, session min / max and sparklines. The engine stands still afterwards (no more frames).
/// </summary>
internal sealed class FakeRig : IDisposable
{
    /// <summary>Friday 23 Oct 2026, 22:10 in Warsaw: two hours into an evening game (the end of the run).</summary>
    public static readonly DateTime NowUtc = new(2026, 10, 23, 20, 10, 0, DateTimeKind.Utc);

    /// <summary>Times on charts, in the history and on the taskbar use this zone, whatever the machine's zone is.</summary>
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");

    public static DateTime LocalNow => TimeZoneInfo.ConvertTimeFromUtc(NowUtc, Zone);

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _frame = new(0);
    private readonly List<float[]> _frames = [];
    private readonly IDemand _all;
    private readonly string[] _ids;
    private long _now;

    /// <summary>Runs the PC for <paramref name="seconds"/> fake seconds, ending at <see cref="NowUtc"/>.</summary>
    public FakeRig(int seconds)
    {
        _now = NowUtc.Ticks - seconds * TimeSpan.TicksPerSecond;
        Func<long> clock = () => Interlocked.Read(ref _now);
        Engine = new SensorEngine([new DerivedSource(), new FakeSensorSource(FakeProfile.DesktopAmdNvidia, seed: 1, clock)], clock);
        Engine.FrameReady += OnFrame;
        Engine.Start();
        var deadline = Environment.TickCount64 + 10_000;
        while (Engine.Sensors.Count == 0)
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("the fake PC was not discovered");
            Thread.Sleep(10);
        }
        _ids = Engine.Sensors.Select(s => s.Id).ToArray();
        // everything every second, like a recorder that records all (fills values and the session stats)
        _all = Engine.Demand("readme-media", _ids, Interval);
        // the engine thread schedules the demand at the start time (it wakes at once; the clock does not move meanwhile)
        Thread.Sleep(250);
        for (var i = 0; i < seconds; i++) Step();
    }

    public SensorEngine Engine { get; }

    /// <summary>The fake "now" (UTC ticks); frozen after the run.</summary>
    public long Now => Interlocked.Read(ref _now);

    /// <summary>One fake second: the clock moves, the engine is woken (a demand update) and reads one frame.</summary>
    private void Step()
    {
        Interlocked.Add(ref _now, Interval.Ticks);
        _all.Update(_ids, Interval);
        if (!_frame.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("the engine did not read a frame");
    }

    // engine thread: keep a copy (frames are reused), then let the next step go
    private void OnFrame(SensorFrame frame)
    {
        var values = new float[frame.Count];
        Array.Copy(frame.Values, values, frame.Count);
        lock (_frames) _frames.Add(values);
        _frame.Release();
    }

    /// <summary>The last <paramref name="count"/> values of a sensor, oldest first.</summary>
    public float[] Recent(SensorDescriptor sensor, int count)
    {
        lock (_frames)
        {
            return _frames.Skip(Math.Max(0, _frames.Count - count))
                .Select(f => (uint)sensor.Index < (uint)f.Length ? f[sensor.Index] : float.NaN)
                .Where(v => !float.IsNaN(v))
                .ToArray();
        }
    }

    public SensorDescriptor Sensor(string hardwareName, SensorKind kind, string name)
        => Engine.Sensors.Single(s => s.Name == name && s.Kind == kind && Engine.FindHardware(s.HardwareId)?.Name == hardwareName);

    public void Dispose()
    {
        Engine.FrameReady -= OnFrame;
        _all.Dispose();
        Engine.Dispose();
        _frame.Dispose();
    }
}
