using BetterVitals.Core.History;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.History.Recording;
using BetterVitals.History.Storage;

namespace BetterVitals.History;

/// <summary>
/// Sensor history: records the chosen sensors into an append-only store under the history folder (raw 60 s blocks,
/// 1 min / 1 h rollups), keeps a 15-minute live ring of the watched (and recorded) sensors also when recording is off,
/// and answers chart / stats / export queries from both. Recording off = nothing is written.
/// <para>Threading: the hub's <see cref="ISensorHub.FrameReady"/> handler only copies into preallocated buffers;
/// files are written once a minute on a timer thread (and right away on <see cref="Suspend"/>, <see cref="Dispose"/>,
/// <see cref="Configure"/> to off); queries run on the thread pool. All public members are thread-safe.</para>
/// </summary>
public sealed class HistoryService : IHistoryQuery, IDisposable
{
    /// <summary>Bytes on disk per stored value (all levels) assumed before real data was measured.</summary>
    internal const double DefaultBytesPerValue = 0.6;

    private const int MaxRetentionDays = 36_500; // 100 years

    private const string RecordConsumer = "history";
    private const string WatchConsumer = "history-live";

    private readonly ISensorHub _hub;
    private readonly HistoryOptions _o;
    private readonly Recorder _recorder = new();
    private readonly LiveRing _ring = new();
    private readonly HistoryStore _store;
    private readonly Lock _config = new();
    private readonly List<string[]> _watches = [];
    private readonly Dictionary<string, long> _lastRecorded = new(StringComparer.Ordinal);
    private readonly List<(string[] Ids, long[] Last)> _retired = []; // not the plans: those hold buffer pools

    private bool _enabled;
    private long _step = 2 * TimeGrid.Second;
    private int _rawDays = 7, _minuteDays = 365, _hourDays;
    private HashSet<string> _recordIds = new(StringComparer.Ordinal);
    private IDemand? _recordDemand;
    private IDemand? _watchDemand;
    private string[] _recordDemandIds = [];
    private long _recordDemandStep;
    private string[] _watchDemandIds = [];
    private ITimer? _timer;
    private long _lastRetentionDay = long.MinValue;
    private readonly long _wallStart;
    private readonly long _stampStart;
    private volatile bool _disposed;
    private int _frameErrors;

    /// <summary>Creates the service; nothing is recorded or written until <see cref="Configure"/> enables recording.</summary>
    public HistoryService(ISensorHub hub, string historyDir) : this(hub, historyDir, new HistoryOptions())
    {
    }

    internal HistoryService(ISensorHub hub, string historyDir, HistoryOptions options)
    {
        _hub = hub;
        _o = options;
        _store = new HistoryStore(Path.GetFullPath(historyDir), _recorder, _ring, options);
        _wallStart = options.Time.GetUtcNow().UtcTicks;
        _stampStart = options.Time.GetTimestamp();
        hub.FrameReady += OnFrame;
        hub.InventoryChanged += OnInventoryChanged;
    }

    /// <summary>Recording is on (samples go to disk).</summary>
    public bool IsRecording
    {
        get
        {
            lock (_config) return _enabled;
        }
    }

    /// <summary>Writing is paused because the disk is (almost) full; the live ring keeps working.</summary>
    public bool IsDiskLow => _store.DiskLow;

    /// <summary>Samples lost because the writer could not keep up (e.g. while the disk was full).</summary>
    public long DroppedSamples => _recorder.DroppedSamples;

    /// <summary>Memory held by the live ring (diagnostics).</summary>
    public long LiveBufferBytes => _ring.Bytes;

    internal Recorder Recorder => _recorder;
    internal HistoryStore Store => _store;

    /// <summary>
    /// Applies the recording settings (enabled, interval, retention, recorded sensors). Cheap when nothing changed.
    /// Turning recording off flushes what was recorded right away.
    /// </summary>
    public void Configure(RecordingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        bool turnedOn, turnedOff;
        lock (_config)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var step = Math.Clamp(settings.IntervalSeconds, 1, 60) * TimeGrid.Second;
            var ids = new HashSet<string>(settings.SensorIds, StringComparer.Ordinal);
            // bounded: days x ticks per day would wrap around for a silly value and delete everything
            _rawDays = Math.Clamp(settings.RawRetentionDays, 1, MaxRetentionDays);
            _minuteDays = Math.Clamp(settings.MinuteRetentionDays, 1, MaxRetentionDays);
            _hourDays = Math.Clamp(settings.HourRetentionDays, 0, MaxRetentionDays);
            turnedOn = settings.Enabled && !_enabled;
            turnedOff = !settings.Enabled && _enabled;
            var changed = turnedOn || turnedOff || step != _step || !ids.SetEquals(_recordIds);
            _enabled = settings.Enabled;
            _step = step;
            _recordIds = ids;
            if (changed) BuildPlans();
            if (turnedOn) StartTimer();
            if (turnedOff) StopTimer();
        }
        if (turnedOn)
        {
            if (_o.BackgroundStart) ThreadPool.UnsafeQueueUserWorkItem(static s => ((HistoryService)s!).StartStore(), this);
            else StartStore();
        }
        if (turnedOff) FlushFinal();
    }

    /// <summary>
    /// Keeps the last 15 minutes of these sensors in the live ring (and asks the hub to read them every second)
    /// until the returned handle is disposed. Several watches may overlap.
    /// </summary>
    public IDisposable Watch(IEnumerable<string> sensorIds)
    {
        var ids = sensorIds.Distinct(StringComparer.Ordinal).ToArray();
        lock (_config)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _watches.Add(ids);
            BuildPlans();
        }
        return new WatchHandle(this, ids);
    }

    private sealed class WatchHandle(HistoryService owner, string[] ids) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            lock (owner._config)
            {
                owner._watches.Remove(ids);
                if (!owner._disposed) owner.BuildPlans();
            }
        }
    }

    /// <summary>Before sleep / hibernate: writes everything recorded so far.</summary>
    public void Suspend() => FlushFinal();

    /// <summary>After resume: nothing to restore (the sleep gap is simply missing data); re-arms the flush timer.</summary>
    public void Resume()
    {
        lock (_config)
            if (_enabled && !_disposed)
            {
                StopTimer();
                StartTimer();
            }
    }

    /// <summary>Writes sealed blocks and due rollups now (the timer does this once a minute).</summary>
    public void Flush()
    {
        if (!HasSomethingToWrite()) return;
        lock (_store.Gate) _store.Flush(final: false);
    }

    /// <summary>Seals the open block and writes everything that is recorded.</summary>
    internal void FlushFinal()
    {
        if (!HasSomethingToWrite()) return;
        lock (_store.Gate) _store.Flush(final: true);
    }

    private bool HasSomethingToWrite() => _recorder.EarliestPendingStart() != long.MaxValue || _recorder.SealedCount > 0 || IsRecording;

    // ── engine thread ──

    private void OnFrame(SensorFrame frame)
    {
        if (_disposed) return;
        try
        {
            _ring.OnFrame(frame);
            _recorder.OnFrame(frame);
        }
        catch (Exception ex)
        {
            // never break the engine; report the first few
            if (Interlocked.Increment(ref _frameErrors) <= 3) _store.WarnOnce("frame" + _frameErrors, $"history: frame handler failed: {ex}");
        }
    }

    private void OnInventoryChanged()
    {
        if (_disposed) return;
        ThreadPool.UnsafeQueueUserWorkItem(static s =>
        {
            var self = (HistoryService)s!;
            lock (self._config)
                if (!self._disposed) self.BuildPlans();
        }, this);
    }

    // ── configuration ──

    /// <summary>Under <see cref="_config"/>: resolves ids → hub slots / catalog slots, publishes plans, updates demands.</summary>
    private void BuildPlans()
    {
        _store.EnsureCatalog();
        var old = _recorder.Plan;
        RecordPlan? plan = null;
        if (_enabled)
        {
            var cols = new List<(CatalogEntry, SensorDescriptor)>();
            foreach (var id in _recordIds)
            {
                var d = _hub.Find(id);
                if (d is null || d.Index < 0) continue;
                cols.Add((_store.Catalog.GetOrAdd(d), d));
            }
            cols.Sort((a, b) => a.Item1.Slot.CompareTo(b.Item1.Slot));
            plan = old is not null && SamePlan(old, cols, _step) ? old : new RecordPlan(cols, _step);
        }
        if (old is not null && old != plan) Retire(old);
        _recorder.SetPlan(plan);

        var ringIds = new List<(string, SensorDescriptor?)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in _watches)
            foreach (var id in w)
                if (seen.Add(id)) ringIds.Add((id, _hub.Find(id)));
        var watched = ringIds.Select(x => x.Item1).ToArray();
        foreach (var id in _recordIds)
            if (seen.Add(id)) ringIds.Add((id, _hub.Find(id)));
        _ring.SetColumns(ringIds);

        UpdateDemands(watched);
    }

    private static bool SamePlan(RecordPlan p, List<(CatalogEntry Entry, SensorDescriptor Sensor)> cols, long step)
    {
        if (p.Step != step || p.Columns != cols.Count) return false;
        for (var i = 0; i < cols.Count; i++)
            if (p.Slots[i] != cols[i].Entry.Slot || p.HubIndex[i] != cols[i].Sensor.Index) return false;
        return true;
    }

    /// <summary>
    /// Remembers until when the plan's sensors were recorded, so the live ring adds only newer rows. The engine may
    /// still store one more sample into the old plan, so the last few retired plans are also read live in <see cref="Resolve"/>.
    /// </summary>
    private void Retire(RecordPlan p)
    {
        lock (_lastRecorded)
        {
            for (var c = 0; c < p.Columns; c++)
            {
                var t = Volatile.Read(ref p.LastSample[c]);
                if (t == long.MinValue) continue;
                if (!_lastRecorded.TryGetValue(p.Ids[c], out var cur) || t > cur) _lastRecorded[p.Ids[c]] = t;
            }
            _retired.Add((p.Ids, p.LastSample));
            if (_retired.Count > 4) _retired.RemoveAt(0);
        }
    }

    private long LastRecorded(string id)
    {
        lock (_lastRecorded)
        {
            var t = _lastRecorded.GetValueOrDefault(id, long.MinValue);
            foreach (var (ids, last) in _retired)
                if (Array.IndexOf(ids, id) is var c and >= 0) t = Math.Max(t, Volatile.Read(ref last[c]));
            return t;
        }
    }

    private void UpdateDemands(string[] watched)
    {
        var recordIds = _enabled ? _recordIds.Order(StringComparer.Ordinal).ToArray() : [];
        if (recordIds.Length == 0)
        {
            _recordDemand?.Dispose();
            _recordDemand = null;
            _recordDemandIds = [];
        }
        else if (_recordDemand is null)
        {
            _recordDemand = _hub.Demand(RecordConsumer, recordIds, TimeSpan.FromTicks(_step));
            (_recordDemandIds, _recordDemandStep) = (recordIds, _step);
        }
        else if (_recordDemandStep != _step || !_recordDemandIds.AsSpan().SequenceEqual(recordIds))
        {
            _recordDemand.Update(recordIds, TimeSpan.FromTicks(_step));
            (_recordDemandIds, _recordDemandStep) = (recordIds, _step);
        }

        Array.Sort(watched, StringComparer.Ordinal);
        if (watched.Length == 0)
        {
            _watchDemand?.Dispose();
            _watchDemand = null;
            _watchDemandIds = [];
        }
        else if (_watchDemand is null)
        {
            _watchDemand = _hub.Demand(WatchConsumer, watched, TimeSpan.FromSeconds(1));
            _watchDemandIds = watched;
        }
        else if (!_watchDemandIds.AsSpan().SequenceEqual(watched))
        {
            _watchDemand.Update(watched, TimeSpan.FromSeconds(1));
            _watchDemandIds = watched;
        }
    }

    // ── store lifecycle ──

    private void StartStore()
    {
        try
        {
            lock (_store.Gate)
            {
                _store.EnsureLoaded();
                RetentionIfDue(force: true);
            }
        }
        catch (Exception ex)
        {
            _store.WarnOnce("start", $"history: start failed: {ex.Message}");
        }
    }

    /// <summary>Under the store gate: retention at start and then once per UTC day (only while recording).</summary>
    private void RetentionIfDue(bool force)
    {
        int raw, minute, hour;
        lock (_config)
        {
            if (!_enabled) return;
            (raw, minute, hour) = (_rawDays, _minuteDays, _hourDays);
        }
        var day = _o.Time.GetUtcNow().UtcTicks / TimeGrid.Day;
        if (!force && day == _lastRetentionDay) return;
        _lastRetentionDay = day;
        _store.RunRetention(raw, minute, hour, RetentionNow());
    }

    /// <summary>
    /// "Now" for retention: the wall clock, but never further ahead than the time that really passed since start, so a
    /// clock set years ahead cannot delete the history (a clock set back only makes retention keep more).
    /// </summary>
    private long RetentionNow() => Math.Min(_o.Time.GetUtcNow().UtcTicks, _wallStart + _o.Time.GetElapsedTime(_stampStart).Ticks);

    /// <summary>Test hook: retention now with the configured limits.</summary>
    internal int RunRetentionNow()
    {
        lock (_store.Gate)
        {
            lock (_config)
                return _store.RunRetention(_rawDays, _minuteDays, _hourDays, RetentionNow());
        }
    }

    private void StartTimer()
    {
        if (!_o.AutoFlush || _timer is not null) return;
        _timer = _o.Time.CreateTimer(static s => ((HistoryService)s!).OnTimer(), this, NextDue(), Timeout.InfiniteTimeSpan);
    }

    private void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private TimeSpan NextDue()
    {
        var now = _o.Time.GetUtcNow().UtcTicks;
        var next = TimeGrid.Floor(now, TimeGrid.Minute) + TimeGrid.Minute + _o.FlushOffset.Ticks;
        if (next - now < TimeGrid.Second) next += TimeGrid.Minute;
        return TimeSpan.FromTicks(next - now);
    }

    private void OnTimer()
    {
        try
        {
            lock (_store.Gate)
            {
                _store.Flush(final: false);
                RetentionIfDue(force: false);
            }
        }
        catch (Exception ex)
        {
            _store.WarnOnce("timer", $"history: flush failed: {ex.Message}");
        }
        finally
        {
            lock (_config)
                if (_timer is not null && !_disposed) _timer.Change(NextDue(), Timeout.InfiniteTimeSpan);
        }
    }

    // ── queries ──

    /// <summary>Under the store gate.</summary>
    private SensorRef? Resolve(string id)
    {
        _store.EnsureLoaded();
        var e = _store.Catalog.Find(id);
        var plan = _recorder.Plan;
        var recordedNow = plan is not null && Array.IndexOf(plan.Ids, id) >= 0;
        var ringKind = _ring.KindOf(id);
        if (e is null && ringKind is null) return null;
        var d = _hub.Find(id);
        var after = LastRecorded(id);
        return new SensorRef
        {
            Id = id,
            Kind = e?.Kind ?? ringKind!.Value,
            Name = e?.Name ?? d?.Name ?? id,
            Unit = e?.Unit ?? d?.DisplayUnit ?? "",
            Slot = e?.Slot ?? -1,
            UseRing = ringKind is not null && !recordedNow,
            RingAfter = after,
        };
    }

    private long BaseStep(SensorRef r)
    {
        long step;
        lock (_config) step = _step;
        return r.UseRing ? TimeGrid.Second : step;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HistoryPoint>> GetSeriesAsync(string sensorId, DateTime fromUtc, DateTime toUtc, int maxPoints,
        CancellationToken ct = default)
    {
        var from = TimeGrid.Utc(fromUtc);
        var to = TimeGrid.Utc(toUtc);
        return Task.Run<IReadOnlyList<HistoryPoint>>(() =>
        {
            try
            {
                lock (_store.Gate)
                {
                    var r = Resolve(sensorId);
                    return r is null ? [] : _store.Series(r, from, to, maxPoints, BaseStep(r), ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _store.WarnOnce("series" + ex.GetType().Name, $"history: series query failed: {ex}");
                return [];
            }
        }, ct);
    }

    /// <inheritdoc />
    public Task<RangeStats?> GetStatsAsync(string sensorId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var from = TimeGrid.Utc(fromUtc);
        var to = TimeGrid.Utc(toUtc);
        return Task.Run(() =>
        {
            try
            {
                lock (_store.Gate)
                {
                    var r = Resolve(sensorId);
                    return r is null ? null : _store.Stats(r, from, to, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _store.WarnOnce("stats" + ex.GetType().Name, $"history: stats query failed: {ex}");
                return null;
            }
        }, ct);
    }

    /// <inheritdoc />
    public (DateTime FromUtc, DateTime ToUtc)? GetCoverage(string sensorId)
    {
        try
        {
            lock (_store.Gate)
            {
                var r = Resolve(sensorId);
                if (r is null || _store.Coverage(r) is not { } c) return null;
                return (new DateTime(c.First, DateTimeKind.Utc), new DateTime(c.Last, DateTimeKind.Utc));
            }
        }
        catch (Exception ex)
        {
            _store.WarnOnce("coverage" + ex.GetType().Name, $"history: coverage query failed: {ex}");
            return null;
        }
    }

    /// <inheritdoc />
    public HistoryUsage GetUsage()
    {
        try
        {
            (long Bytes, long? Oldest) u;
            lock (_store.Gate) u = _store.Usage();
            return new HistoryUsage(u.Bytes, EstimateBytesPerDay(), u.Oldest is { } t ? new DateTime(t, DateTimeKind.Utc) : null, _store.BytesPerValue);
        }
        catch (Exception ex)
        {
            _store.WarnOnce("usage" + ex.GetType().Name, $"history: usage failed: {ex.Message}");
            return new HistoryUsage(0, 0, null);
        }
    }

    /// <summary>Bytes per day for the configured sensors and interval (measured rate once enough was written).</summary>
    private long EstimateBytesPerDay()
    {
        int columns;
        long step;
        lock (_config)
        {
            columns = _recorder.Plan?.Columns ?? _recordIds.Count(id => _hub.Find(id) is not null);
            step = _step;
        }
        var bytesPerValue = _store.BytesPerValue ?? DefaultBytesPerValue;
        return (long)(columns * (double)TimeGrid.Day / step * bytesPerValue);
    }

    /// <inheritdoc />
    public long EstimateExportBytes(ExportRequest request)
    {
        try
        {
            var from = TimeGrid.Utc(request.FromUtc);
            var to = Math.Min(TimeGrid.Utc(request.ToUtc), _o.Time.GetUtcNow().UtcTicks);
            long step;
            int sensors;
            lock (_store.Gate)
            {
                var (_, oldest) = _store.Usage();
                from = Math.Max(from, oldest ?? from);
                sensors = request.SensorIds.Count(id => Resolve(id) is not null || _hub.Find(id) is not null);
            }
            lock (_config) step = _step;
            return CsvExport.Estimate(sensors, request.Resolution, from, to, step, request.Zip);
        }
        catch (Exception ex)
        {
            _store.WarnOnce("estimate" + ex.GetType().Name, $"history: export estimate failed: {ex.Message}");
            return 0;
        }
    }

    /// <inheritdoc />
    public Task ExportAsync(ExportRequest request, Stream output, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);
        var dialect = new CsvExport.Dialect(_o.CsvCulture ?? System.Globalization.CultureInfo.CurrentCulture);
        return Task.Run(() =>
        {
            SensorRef[] sensors;
            var from = TimeGrid.Utc(request.FromUtc);
            var to = TimeGrid.Utc(request.ToUtc);
            lock (_store.Gate)
            {
                // walk only where data can exist (keeps "export everything" from scanning empty years)
                var oldest = _store.Usage().Oldest ?? long.MaxValue;
                foreach (var id in request.SensorIds)
                    if (_ring.FirstTime(id, long.MinValue) is var t and < long.MaxValue) oldest = Math.Min(oldest, t);
                from = Math.Max(from, oldest == long.MaxValue ? to : TimeGrid.Floor(oldest, TimeGrid.Hour));
                to = Math.Min(to, _o.Time.GetUtcNow().UtcTicks + TimeGrid.Minute);
                var list = new List<SensorRef>();
                foreach (var id in request.SensorIds.Distinct(StringComparer.Ordinal))
                {
                    if (Resolve(id) is { } r) list.Add(r);
                    else if (_hub.Find(id) is { } d)
                        list.Add(new SensorRef { Id = id, Kind = d.Kind, Name = d.Name, Unit = d.DisplayUnit });
                }
                sensors = [.. list];
            }
            CsvExport.Write(_store, sensors, request, from, to, output, dialect, progress, ct);
        }, ct);
    }

    /// <inheritdoc />
    public Task ClearAsync() => Task.Run(() =>
    {
        lock (_store.Gate) _store.Clear();
    });

    /// <summary>Stops recording (flushing what was recorded), releases the hub demands and the timer.</summary>
    public void Dispose()
    {
        lock (_config)
        {
            if (_disposed) return;
            _hub.FrameReady -= OnFrame;
            _hub.InventoryChanged -= OnInventoryChanged;
            StopTimer();
            _recordDemand?.Dispose();
            _watchDemand?.Dispose();
            _recordDemand = _watchDemand = null;
        }
        try
        {
            FlushFinal();
        }
        finally
        {
            lock (_config)
            {
                _disposed = true;
                _recorder.SetPlan(null);
            }
        }
    }
}
