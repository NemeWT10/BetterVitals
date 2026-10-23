using BetterVitals.Core.Settings;

namespace BetterVitals.Platform.Updates;

/// <summary>
/// When an automatic update check may run — "not invasive" (user requirement): never while a game / full-screen app
/// is in front, never on a metered connection, at most once a day, and the first check only a few minutes after start
/// (so it never competes with boot). "Check now" bypasses everything except the mode-independent safety (games).
/// Pure decision; the app owns the single one-shot timer that asks it.
/// </summary>
public static class UpdateScheduler
{
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>Re-try soon after a skipped check (game in front, metered, offline).</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(30);

    public static bool MayCheck(UpdateSettings settings, DateTime nowUtc, DateTime appStartUtc, bool gameInForeground, bool metered, bool manual)
    {
        if (gameInForeground) return false;
        if (manual) return true;
        if (settings.Mode == UpdateMode.Off) return false;
        if (metered) return false;
        if (nowUtc - appStartUtc < FirstCheckDelay) return false;
        return settings.LastCheckUtc is not { } last || nowUtc - last >= Interval || last > nowUtc; // clock went back: check
    }

    /// <summary>When to ask again (for the app's one-shot timer).</summary>
    public static TimeSpan NextDelay(UpdateSettings settings, DateTime nowUtc, DateTime appStartUtc, bool lastAttemptSkipped)
    {
        if (settings.Mode == UpdateMode.Off) return Timeout.InfiniteTimeSpan;
        if (lastAttemptSkipped) return RetryDelay;
        var startGate = appStartUtc + FirstCheckDelay - nowUtc;
        var dueGate = settings.LastCheckUtc is { } last && last <= nowUtc ? last + Interval - nowUtc : TimeSpan.Zero;
        var d = startGate > dueGate ? startGate : dueGate;
        return d < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : d;
    }
}
