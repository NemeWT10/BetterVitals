using BetterVitals.Core.Settings;

namespace BetterVitals.Platform;

public enum StartAction
{
    /// <summary>Run in this process.</summary>
    RunHere,

    /// <summary>Start the elevated launcher task and exit (no UAC; the elevated copy takes over).</summary>
    RelaunchViaTask,

    /// <summary>Show the first-run guide (full mode with one UAC prompt, or basic mode).</summary>
    FirstRun,
}

/// <summary>What to do with a fresh process start. Pure — every input is passed in, so it is fully tested.</summary>
public static class Startup
{
    public static StartAction Decide(
        bool testMode,
        bool elevated,
        bool firstRunDone,
        ReadMode mode,
        bool installed,
        bool launcherTaskExists,
        bool startedByTask)
    {
        // the task started us (elevated, or a standard user account where nothing better is possible)
        if (testMode || startedByTask) return StartAction.RunHere;
        // a fresh download is welcomed, also when started with "Run as administrator"
        if (!firstRunDone && !(installed && launcherTaskExists)) return StartAction.FirstRun;
        if (elevated) return StartAction.RunHere;
        if (!firstRunDone) return StartAction.RelaunchViaTask;
        if (mode == ReadMode.Basic) return StartAction.RunHere;
        return installed && launcherTaskExists ? StartAction.RelaunchViaTask : StartAction.RunHere;
    }
}
