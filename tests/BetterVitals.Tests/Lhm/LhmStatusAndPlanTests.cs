using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Lhm;
using Xunit;

namespace BetterVitals.Tests.Lhm;

public class LhmStatusAndPlanTests
{
    private static readonly Version Current = new(2, 2, 0);

    [Fact]
    public void Status_ok_when_elevated_with_current_pawnio()
        => Assert.Equal(new SourceStatus(SourceState.Ok), LhmDriverStatus.Compute(false, false, Current, true, true));

    [Fact]
    public void Status_precedence()
    {
        Assert.Equal(LhmStatusKeys.Failed, LhmDriverStatus.Compute(true, true, null, false, null).MessageKey);
        Assert.Equal(SourceState.Unavailable, LhmDriverStatus.Compute(true, false, Current, true, true).State);
        Assert.Equal(new SourceStatus(SourceState.Paused, LhmStatusKeys.Paused), LhmDriverStatus.Compute(false, true, null, false, null));
        Assert.Equal(new SourceStatus(SourceState.Limited, LhmStatusKeys.NoPawnIo), LhmDriverStatus.Compute(false, false, null, false, null));
        Assert.Equal(new SourceStatus(SourceState.Limited, LhmStatusKeys.NoPawnIo), LhmDriverStatus.Compute(false, false, null, true, null));
        Assert.Equal(new SourceStatus(SourceState.Limited, LhmStatusKeys.NotElevated), LhmDriverStatus.Compute(false, false, Current, false, null));
        Assert.Equal(new SourceStatus(SourceState.Limited, LhmStatusKeys.OldPawnIo), LhmDriverStatus.Compute(false, false, new Version(2, 0, 1), true, true));
        Assert.Equal(new SourceStatus(SourceState.Limited, LhmStatusKeys.DriverNotRunning), LhmDriverStatus.Compute(false, false, Current, true, false));
    }

    [Fact]
    public void Pawnio_2_1_is_current()
        => Assert.Equal(SourceState.Ok, LhmDriverStatus.Compute(false, false, new Version(2, 1, 0), true, null).State);

    [Fact]
    public void Status_keys_use_the_loc_namespace()
    {
        foreach (var key in new[] { LhmStatusKeys.NotElevated, LhmStatusKeys.NoPawnIo, LhmStatusKeys.OldPawnIo, LhmStatusKeys.DriverNotRunning, LhmStatusKeys.Paused, LhmStatusKeys.Failed })
            Assert.StartsWith("source.lhm.", key);
        Assert.Equal("source.lhm.notElevated", LhmStatusKeys.NotElevated);
        Assert.Equal("source.lhm.noPawnIo", LhmStatusKeys.NoPawnIo);
        Assert.Equal("source.lhm.oldPawnIo", LhmStatusKeys.OldPawnIo);
    }

    [Fact]
    public void Plan_cache_matches_request_content_and_generation()
    {
        var a = Slots(1, 2, 3);
        var sameContent = Slots(1, 2, 3);
        var other = Slots(1, 3, 2);
        var cache = new LhmPlanCache<object>();
        var plan = new object();

        Assert.Null(cache.Find(a, 1));
        cache.Add(a, 1, plan);
        Assert.Same(plan, cache.Find(sameContent, 1));
        Assert.Null(cache.Find(other, 1));
        Assert.Null(cache.Find(a, 2));
        Assert.Null(cache.Find(Slots(1, 2), 1));
        Assert.Equal(1, cache.Builds);
        cache.Clear();
        Assert.Null(cache.Find(a, 1));
    }

    [Fact]
    public void Plan_cache_evicts_round_robin()
    {
        var cache = new LhmPlanCache<object>(capacity: 2);
        var p1 = new object();
        var p2 = new object();
        var p3 = new object();
        cache.Add(Slots(1), 0, p1);
        cache.Add(Slots(2), 0, p2);
        cache.Add(Slots(3), 0, p3);
        Assert.Null(cache.Find(Slots(1), 0));
        Assert.Same(p2, cache.Find(Slots(2), 0));
        Assert.Same(p3, cache.Find(Slots(3), 0));
    }

    [Fact]
    public void Plan_lookup_does_not_allocate()
    {
        var cache = new LhmPlanCache<object>();
        var req = Slots(Enumerable.Range(0, 300).ToArray());
        cache.Add(req, 7, new object());
        cache.Find(req, 7);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) Assert.NotNull(cache.Find(req, 7));
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Debouncer_coalesces_bursts()
    {
        var count = 0;
        using var fired = new ManualResetEventSlim();
        using var d = new Debouncer(TimeSpan.FromMilliseconds(150), () =>
        {
            Interlocked.Increment(ref count);
            fired.Set();
        });
        for (var i = 0; i < 10; i++) d.Signal();
        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)));
        Thread.Sleep(300);
        Assert.Equal(1, Volatile.Read(ref count));
    }

    private static SensorDescriptor[] Slots(params int[] indices) => indices.Select(i => new SensorDescriptor
    {
        Id = $"x/{i}",
        HardwareId = "x",
        Name = "x",
        Kind = SensorKind.Load,
        HardwareKind = HardwareKind.Other,
        Index = i,
    }).ToArray();
}
