using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.History;
using BetterVitals.History.Storage;

namespace BetterVitals.Tests.History;

/// <summary>Minimal <see cref="ISensorHub"/> double: fixed sensors, raises frames on demand, records demands.</summary>
internal sealed class FakeHub : ISensorHub
{
    private readonly List<SensorDescriptor> _sensors = [];
    private readonly SensorFrame[] _frames = [new(16), new(16), new(16)];
    private int _seq;

    public sealed class FakeDemand(FakeHub hub, string consumer, IEnumerable<string> ids, TimeSpan interval) : IDemand
    {
        public string Consumer { get; } = consumer;
        public string[] Ids { get; private set; } = ids.ToArray();
        public TimeSpan Interval { get; private set; } = interval;
        public bool Disposed { get; private set; }

        public void Update(IEnumerable<string> sensorIds, TimeSpan interval)
        {
            Ids = sensorIds.ToArray();
            Interval = interval;
        }

        public void Dispose()
        {
            Disposed = true;
            hub.Demands.Remove(this);
        }
    }

    public List<FakeDemand> Demands { get; } = [];

    public SensorDescriptor Add(string name, SensorKind kind, TimeSpan minInterval = default)
    {
        var d = new SensorDescriptor
        {
            Id = $"fake/test/0/{SensorIds.Slug(kind.ToString())}/{SensorIds.Slug(name)}",
            HardwareId = "fake/test/0",
            Name = name,
            Kind = kind,
            HardwareKind = HardwareKind.Other,
            MinInterval = minInterval,
        };
        d.Index = _sensors.Count;
        _sensors.Add(d);
        return d;
    }

    public void RaiseInventoryChanged() => InventoryChanged?.Invoke();

    /// <summary>Raises a caller-built frame as is (benchmarks).</summary>
    public void Raise(SensorFrame frame) => FrameReady?.Invoke(frame);

    /// <summary>One engine tick at <paramref name="ts"/>; values by sensor index (NaN = no value, read anyway).</summary>
    public void Tick(long ts, ReadOnlySpan<float> values, ReadOnlySpan<bool> fresh = default)
    {
        var f = _frames[_seq % _frames.Length];
        f.EnsureCapacity(_sensors.Count);
        // carry previous values / stamps (the real engine keeps them in every frame)
        var prev = _frames[(_seq + _frames.Length - 1) % _frames.Length];
        if (prev != f && prev.Count > 0) f.CopyFrom(prev);
        f.Count = _sensors.Count;
        f.Sequence = ++_seq;
        f.TimestampUtcTicks = ts;
        for (var i = 0; i < _sensors.Count; i++)
        {
            if (!fresh.IsEmpty && !fresh[i]) continue;
            f.Values[i] = i < values.Length ? values[i] : float.NaN;
            f.UpdatedAt[i] = f.Sequence;
        }
        FrameReady?.Invoke(f);
    }

    public IReadOnlyList<HardwareDescriptor> Hardware => [];
    public IReadOnlyList<SensorDescriptor> Sensors => _sensors;
    public bool IsPresent(int index) => (uint)index < (uint)_sensors.Count;
    public SensorDescriptor? Find(string sensorId) => _sensors.FirstOrDefault(s => s.Id == sensorId);
    public HardwareDescriptor? FindHardware(string hardwareId) => null;
    public SensorFrame Latest => _frames[(_seq + _frames.Length - 1) % _frames.Length];
    public SessionStats Stats { get; } = new();

    public IDemand Demand(string consumer, IEnumerable<string> sensorIds, TimeSpan interval)
    {
        var d = new FakeDemand(this, consumer, sensorIds, interval);
        Demands.Add(d);
        return d;
    }

    public event Action<SensorFrame>? FrameReady;
    public event Action? InventoryChanged;
    public IReadOnlyList<(string Source, SourceStatus Status)> SourceStatuses => [];
}

/// <summary>Settable clock; timers only fire when the test calls <see cref="ManualTimer.Fire"/>.</summary>
internal sealed class ManualTime(DateTime start) : TimeProvider
{
    public long Ticks { get; set; } = start.Ticks;

    public List<ManualTimer> Timers { get; } = [];

    public override DateTimeOffset GetUtcNow() => new(Ticks, TimeSpan.Zero);

    // elapsed time follows the settable clock (retention bounds the wall clock by it)
    public override long GetTimestamp() => Ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new ManualTimer(callback, state, dueTime);
        Timers.Add(t);
        return t;
    }

    public sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        public TimeSpan Due { get; private set; } = due;
        public bool Disposed { get; private set; }
        public int Changes { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime;
            Changes++;
            return true;
        }

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Per-test folder under tests/output/history (inside the worktree, git-ignored).</summary>
internal sealed class TestDir : IDisposable
{
    public TestDir(string name)
    {
        Path = System.IO.Path.Combine(Root, $"{name}-{Guid.NewGuid():N}"[..Math.Min(name.Length + 9, 60)]);
        Directory.CreateDirectory(Root);
    }

    public string Path { get; }

    public static string Root
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(System.IO.Path.Combine(d.FullName, "BetterVitals.sln"))) d = d.Parent;
            if (d is null) throw new InvalidOperationException("repository root not found");
            return System.IO.Path.Combine(d.FullName, "tests", "output", "history");
        }
    }

    public string[] Files() => Directory.Exists(Path) ? Directory.GetFiles(Path).Select(f => System.IO.Path.GetFileName(f)).ToArray() : [];

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }
}

/// <summary>A service over a fake hub with a manual clock, plus a brute-force copy of every stored sample.</summary>
internal sealed class Rig : IDisposable
{
    public readonly FakeHub Hub = new();
    public readonly ManualTime Time;
    public readonly TestDir Dir;
    public readonly List<string> Warnings = [];
    public HistoryService Service { get; private set; } = null!;
    public long? FreeBytes { get; set; }

    /// <summary>Expected stored samples per sensor index: (time, quantized value).</summary>
    public readonly Dictionary<int, List<(long T, long Q)>> Truth = [];

    public Rig(string name, DateTime start)
    {
        Dir = new TestDir(name);
        Time = new ManualTime(start);
    }

    public HistoryService Open(bool autoFlush = false)
    {
        Service = new HistoryService(Hub, Dir.Path, new HistoryOptions
        {
            Time = Time,
            AutoFlush = autoFlush,
            BackgroundStart = false,
            FreeSpace = _ => FreeBytes,
            Warn = m =>
            {
                lock (Warnings) Warnings.Add(m);
            },
        });
        return Service;
    }

    public static RecordingSettings Settings(bool enabled, int interval, IEnumerable<SensorDescriptor> sensors) => new()
    {
        Enabled = enabled,
        IntervalSeconds = interval,
        SensorIds = new HashSet<string>(sensors.Select(s => s.Id), StringComparer.Ordinal),
    };

    /// <summary>
    /// Ticks every <paramref name="tickSeconds"/> from <paramref name="start"/> for <paramref name="seconds"/>,
    /// flushing after each minute boundary. <paramref name="value"/>(sensor index, time) gives the value.
    /// Records into <see cref="Truth"/> the samples that land on the recording grid (when <paramref name="interval"/> &gt; 0).
    /// </summary>
    public long Feed(DateTime start, int seconds, int tickSeconds, Func<int, long, float> value, int interval = 0, bool flush = true)
    {
        var n = Hub.Sensors.Count;
        var buf = new float[n];
        var ts = start.Ticks;
        var end = ts + seconds * TimeSpan.TicksPerSecond;
        var lastMinute = TimeGrid.Floor(ts, TimeGrid.Minute);
        for (; ts < end; ts += tickSeconds * TimeSpan.TicksPerSecond)
        {
            for (var i = 0; i < n; i++) buf[i] = value(i, ts);
            Time.Ticks = ts;
            Hub.Tick(ts, buf);
            if (interval > 0 && ts % (interval * TimeSpan.TicksPerSecond) == 0)
                for (var i = 0; i < n; i++)
                    if (Quant.TryQuantize(buf[i], Quant.Of(Hub.Sensors[i].Kind), out var q))
                    {
                        if (!Truth.TryGetValue(i, out var list)) Truth[i] = list = [];
                        list.Add((ts, q));
                    }
            var minute = TimeGrid.Floor(ts, TimeGrid.Minute);
            if (flush && minute != lastMinute)
            {
                Service.Flush();
                lastMinute = minute;
            }
        }
        Time.Ticks = end;
        return end;
    }

    public void Dispose()
    {
        Service?.Dispose();
        Dir.Dispose();
    }
}

/// <summary>Deterministic, slowly varying signals per sensor kind (what a desktop at idle / light load looks like).</summary>
internal static class Signals
{
    public static float Slow(SensorKind kind, int sensor, long ts)
    {
        var s = ts / TimeSpan.TicksPerSecond;
        var h = Hash(sensor, s);
        var phase = sensor * 0.7;
        var slow = Math.Sin(s / 600.0 + phase); // ~1 h period
        return kind switch
        {
            SensorKind.Temperature => (float)(45 + 8 * slow + (h % 3) * 0.05),
            SensorKind.Load => (float)Math.Clamp(10 + 6 * slow + (h % 5) * 0.2, 0, 100),
            SensorKind.Clock => (float)((h % 50 == 0 ? 3000 : 5200) + (h % 4) * 0.1),
            SensorKind.Voltage => (float)(1.2 + (h % 3) * 0.0005),
            SensorKind.Power => (float)(60 + 15 * slow + (h % 7) * 0.05),
            SensorKind.Fan => (float)(900 + 100 * slow + h % 3),
            SensorKind.Throughput => (float)(h % 10 == 0 ? 1000 + h % 5000 : 0),
            SensorKind.SmallData => (float)(8000 + 50 * slow),
            _ => (float)(10 + slow),
        };
    }

    /// <summary>Busy machine: full-resolution noise on every sample (worst case for the delta encoding).</summary>
    public static float Noisy(SensorKind kind, int sensor, long ts)
    {
        var s = ts / TimeSpan.TicksPerSecond;
        var h = Hash(sensor, s);
        var u = (h % 10_000) / 10_000.0; // 0..1
        return kind switch
        {
            SensorKind.Temperature => (float)(60 + 15 * u),
            SensorKind.Load => (float)(100 * u),
            SensorKind.Clock => (float)(4000 + 1500 * u),
            SensorKind.Voltage => (float)(1.0 + 0.4 * u),
            SensorKind.Power => (float)(30 + 120 * u),
            SensorKind.Fan => (float)(800 + 1200 * u),
            SensorKind.Throughput => (float)(5e6 * u),
            SensorKind.SmallData => (float)(8000 + 4000 * u),
            _ => (float)(100 * u),
        };
    }

    public static ulong Hash(int sensor, long s)
    {
        var x = (ulong)s * 0x9E3779B97F4A7C15UL ^ (ulong)(sensor + 1) * 0xC2B2AE3D27D4EB4FUL;
        x ^= x >> 29;
        x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 32;
        return x;
    }

    /// <summary>A typical recorded mix of ~60 sensors.</summary>
    public static SensorKind[] TypicalMix(int count)
    {
        SensorKind[] pattern =
        [
            SensorKind.Temperature, SensorKind.Temperature, SensorKind.Load, SensorKind.Load, SensorKind.Clock,
            SensorKind.Temperature, SensorKind.Load, SensorKind.Clock, SensorKind.Voltage, SensorKind.Power,
            SensorKind.Fan, SensorKind.Throughput, SensorKind.SmallData, SensorKind.Temperature, SensorKind.Load,
        ];
        return Enumerable.Range(0, count).Select(i => pattern[i % pattern.Length]).ToArray();
    }
}
