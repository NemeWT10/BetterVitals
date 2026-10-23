using System.Diagnostics;
using System.Text.Json;
using BetterVitals.Core.Settings;
using BetterVitals.Core.Sensors;
using BetterVitals.History;
using BetterVitals.Sensors.Engine;
using Xunit;
using Xunit.Abstractions;

namespace BetterVitals.Tests.Robustness;

/// <summary>PROJEKT §4: the engine, the history and the settings are used from many threads at once (UI, overlay, flyout, system events).</summary>
[Collection(HeavyCollection.Name)]
public sealed class ConcurrencyScenarios(ITestOutputHelper output)
{
    private const int Workers = 8;
    private const int PerWorker = 4;

    /// <summary>Wall clock that runs <c>speed</c> times faster than real time (the engine thread sleeps real milliseconds).</summary>
    private sealed class FastClock(double speed)
    {
        private readonly long _start = SimPc.DefaultStart.Ticks;
        private readonly Stopwatch _sw = Stopwatch.StartNew();

        public long Now => _start + (long)(_sw.Elapsed.Ticks * speed);
    }

    private static string[] Ids(ScriptedSource source, IEnumerable<int> ordinals) => ordinals.Select(source.SensorId).ToArray();

    /// <summary>One worker: churns demands made of its random picks and keeps one persistent demand of its own sensors.</summary>
    private static void Churn(int worker, ISensorHub hub, ScriptedSource source, int total, IDemand keep, CancellationToken stop, List<Exception> errors)
    {
        var rnd = new Random(worker * 7919 + 1);
        Action<SensorFrame> handler = f =>
        {
            _ = f.Count;
        };
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var picks = Enumerable.Range(0, rnd.Next(0, 12)).Select(_ => source.SensorId(Workers * PerWorker + rnd.Next(total - Workers * PerWorker))).ToList();
                if (rnd.Next(5) == 0) picks.Add("no/such/0/temperature/sensor" + rnd.Next(5)); // ids that do not exist (yet)
                var d = hub.Demand("churn" + worker, picks, TimeSpan.FromMilliseconds(250 * rnd.Next(1, 9)));
                for (var i = rnd.Next(0, 4); i > 0; i--)
                    d.Update(Enumerable.Range(0, rnd.Next(0, 10)).Select(_ => source.SensorId(Workers * PerWorker + rnd.Next(total - Workers * PerWorker))), TimeSpan.FromMilliseconds(250 * rnd.Next(1, 5)));
                if (rnd.Next(4) == 0)
                {
                    hub.FrameReady += handler;
                    hub.FrameReady -= handler;
                }
                _ = hub.Latest[rnd.Next(total)];
                _ = hub.Find(source.SensorId(rnd.Next(total)));
                _ = hub.Sensors.Count;
                _ = hub.IsPresent(rnd.Next(total));
                d.Dispose();
                if (rnd.Next(3) == 0) d.Dispose(); // disposing twice is allowed
                if (rnd.Next(10) == 0) keep.Update(Ids(source, Enumerable.Range(worker * PerWorker, PerWorker)), TimeSpan.FromMilliseconds(250 * (worker + 1)));
            }
        }
        catch (Exception ex)
        {
            lock (errors) errors.Add(ex);
        }
    }

    private static void WaitAll(List<Thread> threads)
    {
        foreach (var t in threads)
            if (!t.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("a worker did not finish (deadlock?)");
    }

    [Fact]
    public void Demands_changed_from_many_threads_while_the_engine_thread_runs_lose_nothing_and_throw_nothing()
    {
        using var log = new LogCapture();
        var id = Guid.NewGuid().ToString("N")[..8];
        const int total = 64;
        var source = new ScriptedSource("bench-" + id, "Bench " + id, total);
        var clock = new FastClock(400);
        using var engine = new SensorEngine([source], () => clock.Now);
        var lastSeq = 0;
        var frames = 0;
        var sequenceErrors = 0;
        engine.FrameReady += f =>
        {
            if (f.Sequence != lastSeq + 1 && lastSeq != 0) sequenceErrors++;
            lastSeq = f.Sequence;
            frames++;
        };
        engine.Start();
        Assert.True(SpinWait.SpinUntil(() => engine.Sensors.Count == total, 5000));

        var keeps = Enumerable.Range(0, Workers)
            .Select(w => engine.Demand("keep" + w, Ids(source, Enumerable.Range(w * PerWorker, PerWorker)), TimeSpan.FromMilliseconds(250 * (w + 1))))
            .ToArray();
        var errors = new List<Exception>();
        using var stop = new CancellationTokenSource();
        var threads = Enumerable.Range(0, Workers).Select(w => new Thread(() => Churn(w, engine, source, total, keeps[w], stop.Token, errors)) { IsBackground = true }).ToList();

        // while the churn goes on, hardware comes and goes and a rediscovery is forced now and then
        var hardware = new Thread(() =>
        {
            var rnd = new Random(1);
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    source.Plugged = rnd.Next(3) != 0;
                    source.RaiseInventoryChanged();
                    if (rnd.Next(4) == 0) engine.RequestRediscover();
                    Thread.Sleep(rnd.Next(1, 25));
                }
            }
            catch (Exception ex)
            {
                lock (errors) errors.Add(ex);
            }
        }) { IsBackground = true };
        threads.Add(hardware);

        foreach (var t in threads) t.Start();
        Thread.Sleep(2500);
        stop.Cancel();
        WaitAll(threads);
        source.Plugged = true;
        engine.RequestRediscover();

        Assert.Empty(errors);
        var seenFrames = frames;
        output.WriteLine($"{seenFrames} frames while 8 threads churned demands, {source.Discovers} discoveries");
        Assert.True(seenFrames > 20, "the engine kept ticking");
        Assert.Equal(0, sequenceErrors);

        // every churn demand is gone: exactly the persistent ones are left
        Assert.Equal(Workers, engine.Demands.Count);
        Assert.All(engine.Demands, d => Assert.StartsWith("keep", d.Consumer, StringComparison.Ordinal));

        // and none of them was lost: the sensors of each are still read (in engine time ~1 s passes in 2.5 ms real)
        Assert.True(SpinWait.SpinUntil(() => engine.IsPresent(0), 5000));
        var before = Enumerable.Range(0, Workers * PerWorker).Select(source.Reads).ToArray();
        for (var o = 0; o < Workers * PerWorker; o++)
        {
            var sensor = o;
            Assert.True(SpinWait.SpinUntil(() => source.Reads(sensor) > before[sensor], 10_000), $"sensor {o} of worker {o / PerWorker} is not being read any more");
        }

        // and no demand leaks: with the last one disposed nothing is read any more
        foreach (var k in keeps) k.Dispose();
        Thread.Sleep(150);
        var after = Enumerable.Range(0, total).Select(source.Reads).ToArray();
        Thread.Sleep(300);
        for (var o = 0; o < total; o++) Assert.Equal(after[o], source.Reads(o));
        Assert.Empty(engine.Demands);

        Assert.Equal(0, log.Count("ERROR", "engine: step failed"));
        Assert.DoesNotContain(log.Lines, l => l.Level == "ERROR" && l.Message.Contains(id, StringComparison.Ordinal));
    }

    [Fact]
    public void The_same_with_a_stepping_thread_at_full_speed_every_persistent_demand_is_read_exactly_as_often_as_asked()
    {
        const int total = 64;
        var source = new ScriptedSource("bench2", "Bench2", total);
        using var pc = SimPc.With(source);
        var engine = pc.Engine;
        var keeps = Enumerable.Range(0, Workers)
            .Select(w => engine.Demand("keep" + w, Ids(source, Enumerable.Range(w * PerWorker, PerWorker)), TimeSpan.FromMilliseconds(250 * (w + 1))))
            .ToArray();
        var errors = new List<Exception>();
        using var stop = new CancellationTokenSource();
        var steps = 0L;
        var stepper = new Thread(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    pc.Clock.Advance(SensorEngine.BaseTick);
                    engine.Step();
                    steps++;
                }
            }
            catch (Exception ex)
            {
                lock (errors) errors.Add(ex);
            }
        }) { IsBackground = true };
        var workers = Enumerable.Range(0, Workers).Select(w => new Thread(() => Churn(w, engine, source, total, keeps[w], stop.Token, errors)) { IsBackground = true }).ToList();
        stepper.Start();
        foreach (var t in workers) t.Start();
        Thread.Sleep(2000);
        stop.Cancel();
        WaitAll(workers);
        WaitAll([stepper]);
        Assert.Empty(errors);
        output.WriteLine($"{steps} engine steps, {pc.Frames} frames");
        Assert.True(steps > 1000);
        Assert.Equal(Workers, engine.Demands.Count);

        // single-threaded again: 200 simulated seconds, each persistent demand read once per its own interval (+- 1 slot)
        engine.Step(); // adopt the last changes
        var before = Enumerable.Range(0, Workers * PerWorker).Select(source.Reads).ToArray();
        pc.Run(TimeSpan.FromSeconds(200));
        for (var w = 0; w < Workers; w++)
        {
            var interval = 0.25 * (w + 1);
            for (var k = 0; k < PerWorker; k++)
            {
                var o = w * PerWorker + k;
                var reads = source.Reads(o) - before[o];
                Assert.InRange(reads, 200 / interval - 2, 200 / interval + 2);
            }
        }
        foreach (var k in keeps) k.Dispose();
        pc.Run(TimeSpan.FromSeconds(5));
        var done = Enumerable.Range(0, total).Select(source.Reads).ToArray();
        pc.Run(TimeSpan.FromSeconds(60));
        for (var o = 0; o < total; o++) Assert.Equal(done[o], source.Reads(o));
    }

    [Fact]
    public async Task The_history_service_survives_configuration_watching_queries_flushes_and_sleep_from_many_threads()
    {
        using var app = new SimApp("conc-history");
        var pc = app.Pc;
        var all = pc.Engine.Sensors.Where(s => s.HardwareKind != HardwareKind.Game).Select(s => s.Id).ToArray();
        var errors = new List<Exception>();
        using var stop = new CancellationTokenSource();
        var stepper = new Thread(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    pc.Clock.Advance(TimeSpan.FromSeconds(1));
                    pc.Engine.Step();
                }
            }
            catch (Exception ex)
            {
                lock (errors) errors.Add(ex);
            }
        }) { IsBackground = true };

        Task Worker(int n, Func<Random, Task> body) => Task.Run(async () =>
        {
            var rnd = new Random(n);
            try
            {
                while (!stop.IsCancellationRequested) await body(rnd);
            }
            catch (Exception ex)
            {
                lock (errors) errors.Add(ex);
            }
        });

        var tasks = new List<Task>
        {
            Worker(1, rnd =>
            {
                app.History.Configure(new RecordingSettings { Enabled = rnd.Next(5) != 0, IntervalSeconds = new[] { 1, 2, 5, 10 }[rnd.Next(4)], SensorIds = [.. all.OrderBy(_ => rnd.Next()).Take(rnd.Next(0, 40))] });
                return Task.Delay(1);
            }),
            Worker(2, rnd =>
            {
                using var w = app.History.Watch(all.OrderBy(_ => rnd.Next()).Take(rnd.Next(1, 10)));
                return Task.Delay(rnd.Next(1, 5));
            }),
            Worker(3, async rnd =>
            {
                var id = all[rnd.Next(all.Length)];
                var from = pc.Clock.Utc.AddMinutes(-rnd.Next(1, 600));
                _ = await app.History.GetSeriesAsync(id, from, pc.Clock.Utc, rnd.Next(1, 500));
                _ = await app.History.GetStatsAsync(id, from, pc.Clock.Utc);
                _ = app.History.GetCoverage(id);
                _ = app.History.GetUsage();
            }),
            Worker(4, async rnd =>
            {
                var request = new Core.History.ExportRequest(all.Take(5).ToArray(), pc.Clock.Utc.AddHours(-2), pc.Clock.Utc, Core.History.ExportResolution.Minute, false);
                _ = app.History.EstimateExportBytes(request);
                await app.History.ExportAsync(request, new MemoryStream());
                await Task.Delay(rnd.Next(1, 10));
            }),
            Worker(5, rnd =>
            {
                app.History.Flush();
                if (rnd.Next(8) == 0)
                {
                    app.History.Suspend();
                    app.History.Resume();
                }
                return Task.Delay(rnd.Next(1, 8));
            }),
            Worker(6, rnd =>
            {
                if (rnd.Next(20) == 0) pc.Engine.RequestRediscover();
                if (rnd.Next(60) == 0)
                {
                    pc.Engine.NotifySuspend();
                    pc.Engine.NotifyResume();
                }
                return Task.Delay(rnd.Next(1, 8));
            }),
        };
        stepper.Start();
        await Task.Delay(3000);
        stop.Cancel();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30)); // a hang here would be a deadlock
        WaitAll([stepper]);
        Assert.Empty(errors);

        // afterwards it still records and answers
        app.History.Configure(new RecordingSettings { Enabled = true, IntervalSeconds = 2, SensorIds = [.. all.Take(10)] });
        var t = pc.Clock.Utc;
        pc.Engine.Step();
        app.Run(TimeSpan.FromMinutes(3));
        app.History.Suspend();
        Assert.True((await app.Series(all[0], t, pc.Clock.Utc)).Count > 60);
        output.WriteLine($"warnings: {app.Warnings.Count}");
        Assert.DoesNotContain(app.Warnings, w => w.Contains("frame handler failed", StringComparison.Ordinal));
    }

    [Fact]
    public void Settings_changed_and_saved_from_many_threads_always_leave_a_valid_file_with_every_change()
    {
        var torn = 0;
        var reads = 0;
        for (var round = 0; round < 12; round++)
        {
            using var dir = new QaDir("conc-settings");
            var path = dir.File_("settings.json");
            var store = SettingsStore.CreateDetached(path, new AppSettings());
            store.SaveRetryMs = 20;
            var errors = new List<Exception>();
            using var stop = new CancellationTokenSource();
            var reader = new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        if (!File.Exists(path)) continue;
                        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var doc = JsonDocument.Parse(fs);
                        Interlocked.Increment(ref reads);
                    }
                    catch (JsonException)
                    {
                        Interlocked.Increment(ref torn); // a torn file
                    }
                    catch (IOException)
                    {
                        // moved over at this very moment
                    }
                }
            }) { IsBackground = true };
            reader.Start();
            // the timer thread and the explicit saves (sleep, exit) can run at the same time
            var flushers = Enumerable.Range(0, 2).Select(_ => new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        store.Flush();
                    }
                    catch (Exception ex)
                    {
                        lock (errors) errors.Add(ex);
                    }
                }
            }) { IsBackground = true }).ToList();
            foreach (var f in flushers) f.Start();

            const int perThread = 60;
            var threads = Enumerable.Range(0, 6).Select(t => new Thread(() =>
            {
                try
                {
                    for (var i = 0; i < perThread; i++)
                    {
                        store.Mutate("test", s => s.Recording.SensorIds.Add($"t{t}/hardware/0/temperature/sensor-{i}"));
                        if (i % 17 == 0) store.Flush();
                    }
                }
                catch (Exception ex)
                {
                    lock (errors) errors.Add(ex);
                }
            }) { IsBackground = true }).ToList();
            foreach (var t in threads) t.Start();
            WaitAll(threads);
            Thread.Sleep(60); // a save that collided with another one retries by itself
            store.Flush();
            stop.Cancel();
            reader.Join();
            WaitAll(flushers);

            Assert.Empty(errors);
            var back = SettingsStore.Load(path);
            Assert.True(6 * perThread == back.Recording.SensorIds.Count, $"round {round}: {back.Recording.SensorIds.Count} of {6 * perThread} changes reached the file");
        }
        output.WriteLine($"{reads} complete reads of the file while it was written, {torn} torn");
        Assert.Equal(0, torn);
    }
}
