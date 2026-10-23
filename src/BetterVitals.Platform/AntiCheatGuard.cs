namespace BetterVitals.Platform;

/// <summary>
/// The optional "pause driver-backed sensors during anti-cheat games" setting (off by default, user decision).
/// Pauses only while a game is in front AND a kernel anti-cheat process runs (Vanguard's service runs all the time
/// when installed — pausing whenever it exists would mean "always paused"). Re-evaluated on foreground changes only.
/// </summary>
public sealed class AntiCheatGuard
{
    private readonly Func<string?> _findAntiCheat;

    public AntiCheatGuard(Func<string?>? findAntiCheat = null) => _findAntiCheat = findAntiCheat ?? AntiCheat.FindRunning;

    /// <summary>Currently paused (the app releases PawnIO while true).</summary>
    public bool Paused { get; private set; }

    /// <summary>The anti-cheat that caused the pause (for the Permissions page / tooltip).</summary>
    public string? Reason { get; private set; }

    /// <summary>Raised when <see cref="Paused"/> flips.</summary>
    public event Action<bool>? PausedChanged;

    /// <summary>Call when the foreground app changes or the setting changes.</summary>
    public void Evaluate(bool settingEnabled, bool gameInForeground)
    {
        string? found = null;
        var pause = settingEnabled && gameInForeground && (found = _findAntiCheat()) is not null;
        Reason = pause ? found : null;
        if (pause == Paused) return;
        Paused = pause;
        Core.Log.Info(pause ? $"anti-cheat guard: pausing driver sensors ({found})" : "anti-cheat guard: resumed");
        PausedChanged?.Invoke(pause);
    }
}
