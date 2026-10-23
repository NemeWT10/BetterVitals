using BetterVitals.Core.Settings;
using BetterVitals.Platform;
using Xunit;

namespace BetterVitals.Tests.Robustness;

/// <summary>PROJEKT §10 / §12: how a fresh process start is decided (elevated or not, installed or not, task or not…) — and that it can never loop.</summary>
public sealed class StartupScenarios
{
    private static readonly bool[] Both = [false, true];

    private static IEnumerable<(bool Test, bool Elevated, bool FirstRunDone, ReadMode Mode, bool Installed, bool Task, bool ByTask)> AllInputs()
    {
        foreach (var test in Both)
        foreach (var elevated in Both)
        foreach (var done in Both)
        foreach (var mode in new[] { ReadMode.Full, ReadMode.Basic, (ReadMode)7 })
        foreach (var installed in Both)
        foreach (var task in Both)
        foreach (var byTask in Both)
            yield return (test, elevated, done, mode, installed, task, byTask);
    }

    [Fact]
    public void Every_combination_gets_an_answer_and_a_relaunch_is_only_asked_for_when_it_can_work()
    {
        var relaunches = 0;
        var firstRuns = 0;
        var total = 0;
        foreach (var (test, elevated, done, mode, installed, task, byTask) in AllInputs())
        {
            total++;
            var a = Startup.Decide(test, elevated, done, mode, installed, task, byTask);
            Assert.Equal(a, Startup.Decide(test, elevated, done, mode, installed, task, byTask)); // pure
            Assert.True(Enum.IsDefined(a));

            if (test || byTask || elevated && (done || installed && task)) Assert.Equal(StartAction.RunHere, a); // already where we want to be
            if (a == StartAction.RelaunchViaTask)
            {
                relaunches++;
                Assert.True(installed && task, "there must be an installed copy and its launcher task to hand over to");
                Assert.False(test || elevated || byTask);
                if (done) Assert.NotEqual(ReadMode.Basic, mode); // basic mode stays unelevated
            }
            if (a == StartAction.FirstRun)
            {
                firstRuns++;
                Assert.False(done);
                Assert.False(installed && task);
            }
            if (!done && !test && !elevated && !byTask) Assert.NotEqual(StartAction.RunHere, a); // a fresh download is welcomed
            if (!done && !test && !byTask && !(installed && task)) Assert.Equal(StartAction.FirstRun, a); // also "Run as administrator"
        }
        Assert.Equal(3 * 64, total);
        Assert.InRange(relaunches, 1, total);
        Assert.InRange(firstRuns, 1, total);
    }

    [Fact]
    public void The_copy_the_launcher_task_starts_never_asks_for_another_relaunch_whoever_it_runs_as()
    {
        // The task starts "BetterVitals.exe --from-task" with the highest rights the account has: elevated for an administrator,
        // unelevated for a standard user. Either way the child is told it was started by the task.
        foreach (var (test, elevated, done, mode, installed, task, byTask) in AllInputs())
        {
            var first = Startup.Decide(test, elevated, done, mode, installed, task, byTask);
            if (first != StartAction.RelaunchViaTask) continue;
            foreach (var childElevated in Both)
            {
                var child = Startup.Decide(test, childElevated, done, mode, installed, task, startedByTask: true);
                Assert.Equal(StartAction.RunHere, child);
            }
        }
    }

    [Fact]
    public void A_chain_of_processes_settles_after_at_most_one_hand_over()
    {
        foreach (var (test, elevated, done, mode, installed, task, byTask) in AllInputs())
        {
            var (e, bt) = (elevated, byTask);
            var generations = 0;
            while (true)
            {
                generations++;
                Assert.True(generations <= 3, "a relaunch loop");
                var action = Startup.Decide(test, e, done, mode, installed, task, bt);
                if (action != StartAction.RelaunchViaTask) break;
                // the hand-over may succeed (the child is elevated) or the account cannot elevate (it is not): both are "started by the task"
                (e, bt) = (generations % 2 == 0, true);
            }
        }
    }

    [Fact]
    public void Both_tasks_pass_the_marker_that_ends_the_chain()
    {
        var launcher = ScheduledTasks.BuildXml(@"C:\Program Files\BetterVitals\BetterVitals.exe", ScheduledTasks.FromTaskArg, "S-1-5-21-1", atLogon: false, enabled: true, "launcher");
        var autostart = ScheduledTasks.BuildXml(@"C:\Program Files\BetterVitals\BetterVitals.exe", $"{ScheduledTasks.FromTaskArg} {ScheduledTasks.AutostartArg}", "S-1-5-21-1", atLogon: true, enabled: true, "autostart");
        Assert.Contains("--from-task", launcher, StringComparison.Ordinal);
        Assert.Contains("--from-task", autostart, StringComparison.Ordinal);
        Assert.Equal("--from-task", ScheduledTasks.FromTaskArg);
    }

    [Fact]
    public void Odd_command_lines_never_throw()
    {
        var rnd = new Random(9);
        string[] pool = ["--test-mode", "--data-dir", "--snapshots", "--tray-probe", "--real-sensors", "--open", "--from-task", "--autostart", "", " ", "--", "-", "x", "--DATA-DIR", "C:\\no\\such\\dir", "pl", "all", "--open", "--snapshots"];
        for (var i = 0; i < 5000; i++)
        {
            var args = Enumerable.Range(0, rnd.Next(0, 8)).Select(_ => pool[rnd.Next(pool.Length)]).ToArray();
            var o = StartupOptions.Parse(args);
            Assert.NotNull(o);
        }
    }
}
