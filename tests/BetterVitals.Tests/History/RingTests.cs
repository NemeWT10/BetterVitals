using BetterVitals.Core.Sensors;
using BetterVitals.History.Recording;
using BetterVitals.History.Storage;
using Xunit;

namespace BetterVitals.Tests.History;

/// <summary>Recorded, unwatched sensors keep only the newest ring rows; what queries see does not change.</summary>
public class RingTests
{
    private static readonly DateTime T0 = new(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc);

    private static float Value(int i, long t) => (float)(40 + i + (t / TimeSpan.TicksPerSecond % 97) * 0.1);

    [Theory]
    [InlineData(1, 37)]
    [InlineData(2, 37)]
    [InlineData(10, 9)]
    [InlineData(60, 59)]
    [InlineData(60, 1)]
    public async Task Stopping_recording_hands_over_to_the_ring_without_a_gap(int step, int intoSlot)
    {
        using var rig = new Rig("ring-handover", T0);
        var a = rig.Hub.Add("A", SensorKind.Temperature);
        var b = rig.Hub.Add("B", SensorKind.Load);
        var svc = rig.Open();
        svc.Configure(Rig.Settings(true, step, [a, b]));
        // the ring of recorded sensors holds only the newest rows while recording
        Assert.Equal(LiveRing.Rows * 8L + 2 * (LiveRing.ShortRows * 4L + 64), svc.LiveBufferBytes);

        var seconds = 20 * 60 + intoSlot;
        var stop = rig.Feed(T0, seconds, 1, Value);
        svc.Configure(Rig.Settings(false, step, [a, b])); // live only from here: the ring serves both sensors
        var end = rig.Feed(new DateTime(stop, DateTimeKind.Utc), 5 * 60, 1, Value);

        var lastRecorded = TimeGrid.Floor(stop - TimeGrid.Second, step * TimeGrid.Second);
        var expected = new List<long>();
        for (var t = T0.Ticks; t <= lastRecorded; t += step * TimeGrid.Second) expected.Add(t);
        for (var t = lastRecorded + TimeGrid.Second; t < end; t += TimeGrid.Second)
            if (t >= end - LiveRing.Rows * TimeGrid.Second) expected.Add(t);
        foreach (var (s, i) in new[] { (a, 0), (b, 1) })
        {
            var points = await svc.GetSeriesAsync(s.Id, T0, new DateTime(end, DateTimeKind.Utc), 100_000);
            Assert.Equal(expected, points.Select(p => p.TimeUtcTicks).ToList());
            foreach (var p in points)
            {
                Assert.True(Quant.TryQuantize(Value(i, p.TimeUtcTicks), Quant.Of(s.Kind), out var q));
                Assert.Equal(Quant.ToFloat(q, Quant.Of(s.Kind)), p.Min);
                Assert.Equal(p.Min, p.Max);
            }
        }
        Assert.Empty(rig.Warnings);
    }

    [Fact]
    public async Task Watching_a_recorded_sensor_keeps_its_full_ring_and_unwatching_trims_it()
    {
        using var rig = new Rig("ring-watch", T0);
        var sensors = Enumerable.Range(0, 40).Select(i => rig.Hub.Add($"S{i}", SensorKind.Temperature)).ToList();
        var svc = rig.Open();
        svc.Configure(Rig.Settings(true, 2, sensors));
        var shortBytes = svc.LiveBufferBytes;
        Assert.True(shortBytes < 40 * (LiveRing.ShortRows * 4L + 64) + LiveRing.Rows * 8L + 1, $"{shortBytes} B");

        rig.Feed(T0, 600, 1, Value);
        using (svc.Watch([sensors[3].Id]))
        {
            Assert.True(svc.LiveBufferBytes > shortBytes);
            rig.Feed(T0.AddSeconds(600), 120, 1, Value);
            // watched and recorded: the query reads the store (2 s), exactly as before
            var pts = await svc.GetSeriesAsync(sensors[3].Id, T0, T0.AddSeconds(720), 100_000);
            Assert.Equal(360, pts.Count);
        }
        Assert.Equal(shortBytes, svc.LiveBufferBytes);
    }
}
