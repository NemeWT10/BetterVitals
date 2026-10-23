using System.Windows;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.Tests.Ui.Settings;
using BetterVitals.UI.Onboarding;
using Xunit;

namespace BetterVitals.Tests.Ui.FirstRunGuide;

/// <summary>The first-run guide with a fake platform (no UAC, no install, no autostart for real) and its offscreen renders.</summary>
[Collection(UiCollection.Name)]
public sealed class FirstRunTests
{
    private static T Wait<T>(Task<T> task)
    {
        Assert.True(Sta.PumpUntil(() => task.IsCompleted, 5000), "task did not finish");
        return task.GetAwaiter().GetResult();
    }

    [Fact]
    public void Defaults_are_the_recommended_choices() => Sta.Run(() =>
    {
        using var env = new TestEnv();
        var flow = new FirstRunFlow(env.Platform, env.Store);
        Assert.Equal(FirstRunStep.Welcome, flow.Step);
        Assert.Equal(ReadMode.Full, flow.Mode);
        Assert.True(flow.InstallPawnIo);
        Assert.True(flow.RecordHistory);
        Assert.True(flow.Autostart);
        Assert.False(flow.CanGoBack);

        flow.Next();
        flow.Next();
        flow.Next();
        Assert.Equal(FirstRunStep.Tray, flow.Step);
        Assert.True(flow.IsLastStep);
        flow.Next();
        Assert.Equal(FirstRunStep.Tray, flow.Step);
        flow.Back();
        Assert.Equal(FirstRunStep.History, flow.Step);

        // PawnIO already installed and current: nothing to offer
        env.Platform.PawnIoVersion = new Version(2, 2, 0);
        Assert.False(new FirstRunFlow(env.Platform, env.Store).InstallPawnIo);
        env.Platform.PawnIoVersion = new Version(2, 0, 4);
        Assert.True(new FirstRunFlow(env.Platform, env.Store).InstallPawnIo);
    });

    [Fact]
    public void Full_mode_success_saves_the_choices_before_the_install_and_asks_to_exit() => Sta.Run(() =>
    {
        using var env = new TestEnv();
        var gate = new TaskCompletionSource<bool>();
        env.Platform.FullModeGate = gate;
        var flow = new FirstRunFlow(env.Platform, env.Store) { RecordHistory = false, Autostart = true };
        GoToEnd(flow);
        var task = flow.FinishAsync();
        Assert.True(Sta.PumpUntil(() => env.Platform.Calls.Count > 0));
        Assert.Equal(FirstRunStep.Working, flow.Step);
        Assert.True(flow.IsBusy);
        // the elevated copy starts from these settings
        var s = env.Store.Current;
        Assert.True(s.FirstRunDone);
        Assert.Equal(ReadMode.Full, s.Driver.Mode);
        Assert.False(s.Recording.Enabled);
        Assert.Contains("\"FirstRunDone\": true", File.ReadAllText(SettingsPath(env)));

        gate.SetResult(true);
        var result = Wait(task);
        Assert.NotNull(result);
        Assert.Equal(FirstRunOutcome.FullModeStarting, result!.Outcome);
        Assert.Equal(["InstallFullMode(autostart=True, pawnIo=True)"], env.Platform.Calls);
        Assert.True(s.Driver.PawnIoOffered);
    });

    [Fact]
    public void Declined_uac_offers_retry_or_basic_mode() => Sta.Run(() =>
    {
        using var env = new TestEnv();
        env.Platform.FullModeResult = false;
        var flow = new FirstRunFlow(env.Platform, env.Store) { InstallPawnIo = false };
        GoToEnd(flow);
        Assert.Null(Wait(flow.FinishAsync()));
        Assert.Equal(FirstRunStep.Failed, flow.Step);
        Assert.False(env.Store.Current.FirstRunDone);
        Assert.False(flow.IsBusy);

        // retry, still declined
        Assert.Null(Wait(flow.RetryAsync()));
        Assert.Equal(2, env.Platform.Calls.Count(c => c.StartsWith("InstallFullMode(autostart=True, pawnIo=False)", StringComparison.Ordinal)));

        // continue in basic mode
        var result = Wait(flow.ContinueBasicAsync());
        Assert.Equal(FirstRunOutcome.Basic, result!.Outcome);
        var s = env.Store.Current;
        Assert.True(s.FirstRunDone);
        Assert.Equal(ReadMode.Basic, s.Driver.Mode);
        Assert.True(s.Recording.Enabled);
        Assert.True(s.General.Autostart);
        Assert.Contains("SetAutostart(True)", env.Platform.Calls);
    });

    [Fact]
    public void Basic_path_saves_mode_history_and_autostart() => Sta.Run(() =>
    {
        using var env = new TestEnv();
        var flow = new FirstRunFlow(env.Platform, env.Store) { Mode = ReadMode.Basic, RecordHistory = false, Autostart = false };
        GoToEnd(flow);
        var result = Wait(flow.FinishAsync());
        Assert.Equal(new FirstRunResult(FirstRunOutcome.Basic, ReadMode.Basic, false, false), result);
        var s = env.Store.Current;
        Assert.True(s.FirstRunDone);
        Assert.Equal(ReadMode.Basic, s.Driver.Mode);
        Assert.False(s.Recording.Enabled);
        Assert.False(s.General.Autostart);
        // autostart already off: nothing to change, no prompt, no install
        Assert.Empty(env.Platform.Calls);

        // autostart wanted but Windows refused: the setting follows what was applied
        using var env2 = new TestEnv();
        env2.Platform.AutostartResult = false;
        var flow2 = new FirstRunFlow(env2.Platform, env2.Store) { Mode = ReadMode.Basic };
        GoToEnd(flow2);
        var r2 = Wait(flow2.FinishAsync());
        Assert.False(r2!.Autostart);
        Assert.False(env2.Store.Current.General.Autostart);
    });

    [Fact]
    public void Language_choice_applies_at_once_and_is_saved() => Sta.Run(() =>
    {
        using var env = new TestEnv(language: "en");
        var flow = new FirstRunFlow(env.Platform, env.Store);
        var view = new FirstRunView(flow);
        Assert.Equal("en", view.LanguageChoice.Value);
        view.LanguageChoice.Pick("pl");
        Assert.Equal("pl", Loc.I.Language);
        Assert.Equal("pl", env.Store.Current.General.Language);
        Assert.Equal("pl", view.LanguageChoice.Value);
    });

    [Fact]
    public void View_controls_drive_the_flow() => Sta.Run(() =>
    {
        using var env = new TestEnv();
        var flow = new FirstRunFlow(env.Platform, env.Store);
        var view = new FirstRunView(flow);
        FirstRunResult? done = null;
        view.Completed += r => done = r;

        Click(view.NextButton);
        Assert.Equal(FirstRunStep.Mode, flow.Step);
        Assert.Equal(Visibility.Visible, view.Screen(FirstRunStep.Mode).Visibility);
        Assert.Equal(Visibility.Collapsed, view.Screen(FirstRunStep.Welcome).Visibility);
        view.BasicCard.IsChecked = true;
        Assert.Equal(ReadMode.Basic, flow.Mode);
        view.FullCard.IsChecked = true;
        view.PawnIoSwitch.IsChecked = false;
        Assert.False(flow.InstallPawnIo);

        Click(view.NextButton);
        view.HistoryNo.IsChecked = true;
        view.AutostartSwitch.IsChecked = false;
        Assert.False(flow.RecordHistory);
        Assert.False(flow.Autostart);

        Click(view.NextButton);
        Assert.Equal(FirstRunStep.Tray, flow.Step);
        env.Platform.FullModeResult = false;
        Click(view.NextButton);
        Assert.True(Sta.PumpUntil(() => flow.Step == FirstRunStep.Failed));
        Assert.Null(done);
        Click(view.BasicButton);
        Assert.True(Sta.PumpUntil(() => done is not null));
        Assert.Equal(FirstRunOutcome.Basic, done!.Outcome);
        Assert.False(env.Store.Current.Recording.Enabled);
    });

    public static TheoryData<string, string> Variants => SettingsRenderTests.Variants;

    [Theory]
    [MemberData(nameof(Variants))]
    public void Every_step_renders(string palette, string language) => Sta.Run(() =>
    {
        using var env = new TestEnv(language: language);
        var flow = new FirstRunFlow(env.Platform, env.Store);
        var view = new FirstRunView(flow);
        var host = Render.Host(view, palette, 800, layer: false);
        var problems = new List<string>();
        void Shot(string step)
        {
            Render.Save(host, $"firstrun_{step}_{palette}_{language}", 660);
            problems.AddRange(Render.Clipped(host).Select(c => $"{step}: {c}"));
        }
        Shot("1-welcome");
        flow.Next();
        Shot("2-mode");
        flow.Next();
        Shot("3-history");
        flow.Next();
        Shot("4-tray");

        env.Platform.FullModeGate = new TaskCompletionSource<bool>();
        var finish = flow.FinishAsync();
        Assert.True(Sta.PumpUntil(() => flow.Step == FirstRunStep.Working));
        Shot("5-working");
        env.Platform.FullModeGate.SetResult(false);
        Assert.Null(Wait(finish));
        Shot("6-failed");
        Assert.DoesNotContain(Loc.MissingKeys, k => k.StartsWith("firstrun.", StringComparison.Ordinal));
        Assert.Empty(problems);
    });

    private static void GoToEnd(FirstRunFlow flow)
    {
        while (!flow.IsLastStep) flow.Next();
    }

    private static string SettingsPath(TestEnv env)
    {
        var field = typeof(SettingsStore).GetField("_path", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (string)field.GetValue(env.Store)!;
    }

    private static void Click(System.Windows.Controls.Button b)
        => b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
}
