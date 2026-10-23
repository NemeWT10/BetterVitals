using BetterVitals.Core.Catalog;
using BetterVitals.Core.Settings;
using BetterVitals.Platform;
using BetterVitals.Platform.Updates;
using BetterVitals.Sensors.Derived;
using BetterVitals.Sensors.Engine;
using BetterVitals.Sensors.Fake;
using Xunit;

namespace BetterVitals.Tests.Platform;

public sealed class PolicyTests
{
    private static readonly DateTime Start = new(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Updates_never_check_in_games_on_metered_links_or_right_after_start()
    {
        var s = new UpdateSettings { Mode = UpdateMode.Ask };
        Assert.False(UpdateScheduler.MayCheck(s, Start.AddMinutes(1), Start, false, false, manual: false)); // too soon after start
        Assert.True(UpdateScheduler.MayCheck(s, Start.AddMinutes(6), Start, false, false, manual: false));
        Assert.False(UpdateScheduler.MayCheck(s, Start.AddMinutes(6), Start, gameInForeground: true, false, manual: false));
        Assert.False(UpdateScheduler.MayCheck(s, Start.AddMinutes(6), Start, false, metered: true, manual: false));
        Assert.False(UpdateScheduler.MayCheck(s, Start.AddMinutes(6), Start, gameInForeground: true, false, manual: true)); // not even "check now" in a game
        Assert.True(UpdateScheduler.MayCheck(new UpdateSettings { Mode = UpdateMode.Off }, Start.AddMinutes(6), Start, false, true, manual: true));
        Assert.False(UpdateScheduler.MayCheck(new UpdateSettings { Mode = UpdateMode.Off }, Start.AddDays(3), Start, false, false, manual: false));

        s.LastCheckUtc = Start.AddMinutes(6);
        Assert.False(UpdateScheduler.MayCheck(s, Start.AddHours(12), Start, false, false, manual: false)); // once a day
        Assert.True(UpdateScheduler.MayCheck(s, Start.AddHours(25), Start, false, false, manual: false));
        Assert.True(UpdateScheduler.MayCheck(s, Start.AddMinutes(10).AddDays(-3), Start.AddDays(-4), false, false, manual: false)); // clock went back
    }

    [Fact]
    public void Update_timer_delays_are_sane()
    {
        var s = new UpdateSettings { Mode = UpdateMode.Ask };
        Assert.Equal(UpdateScheduler.FirstCheckDelay, UpdateScheduler.NextDelay(s, Start, Start, false));
        Assert.Equal(UpdateScheduler.RetryDelay, UpdateScheduler.NextDelay(s, Start.AddHours(1), Start, lastAttemptSkipped: true));
        s.LastCheckUtc = Start.AddHours(1);
        Assert.Equal(TimeSpan.FromHours(23), UpdateScheduler.NextDelay(s, Start.AddHours(2), Start, false));
        Assert.Equal(Timeout.InfiniteTimeSpan, UpdateScheduler.NextDelay(new UpdateSettings { Mode = UpdateMode.Off }, Start, Start, false));
    }

    [Fact]
    public void Anti_cheat_guard_pauses_only_with_a_game_in_front()
    {
        var running = "vgc.exe";
        var guard = new AntiCheatGuard(() => running);
        var flips = new List<bool>();
        guard.PausedChanged += flips.Add;

        guard.Evaluate(settingEnabled: false, gameInForeground: true);
        Assert.False(guard.Paused); // setting off (default)
        guard.Evaluate(true, gameInForeground: false);
        Assert.False(guard.Paused); // Vanguard idles in the background: no pause on the desktop
        guard.Evaluate(true, true);
        Assert.True(guard.Paused);
        Assert.Equal("vgc.exe", guard.Reason);
        running = null!;
        guard.Evaluate(true, true);
        Assert.False(guard.Paused);
        Assert.Equal([true, false], flips);
    }

    [Fact]
    public void Key_preset_fills_new_hardware_but_respects_the_users_choices()
    {
        var engine = new SensorEngine([new DerivedSource(), new FakeSensorSource(FakeProfile.DesktopAmdNvidia)]);
        engine.DiscoverNow();
        var items = FriendlyCatalog.Resolve(engine.Hardware, engine.Sensors);
        var rec = new RecordingSettings();

        var first = RecordingPolicy.MissingKeySensors(rec, items, new HashSet<string>());
        Assert.NotEmpty(first);
        rec.SensorIds.UnionWith(first);
        rec.KeyPresetApplied = true;
        Assert.Empty(RecordingPolicy.MissingKeySensors(rec, items, new HashSet<string>()));

        var removed = first.First();
        rec.SensorIds.Remove(removed);
        Assert.Empty(RecordingPolicy.MissingKeySensors(rec, items, new HashSet<string> { removed }));

        var own = new RecordingSettings { SensorIds = { "x/y/z" } };
        Assert.Empty(RecordingPolicy.MissingKeySensors(own, items, new HashSet<string>())); // the user's own set before any preset

        Assert.InRange(RecordingPolicy.EstimateBytesPerDay(30, 2), 400_000, 2_000_000);
        Assert.Equal(rec.SensorIds.Count, RecordingPolicy.CountPresent(rec, engine));
    }
}
