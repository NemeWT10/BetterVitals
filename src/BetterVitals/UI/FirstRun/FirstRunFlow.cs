using BetterVitals.Core;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.Services;

namespace BetterVitals.UI.Onboarding;

public enum FirstRunStep
{
    Welcome,
    Mode,
    History,
    Tray,

    /// <summary>Full mode: waiting for UAC / the install.</summary>
    Working,

    /// <summary>Full mode was declined or failed: retry or continue in basic mode.</summary>
    Failed,
}

public enum FirstRunOutcome
{
    /// <summary>Full mode installed; the elevated copy is starting — this process should exit now.</summary>
    FullModeStarting,

    /// <summary>Finished in basic mode; the app keeps running here.</summary>
    Basic,

    /// <summary>The window was closed before finishing; nothing was saved and the guide shows again next start.</summary>
    Closed,
}

/// <summary>What the guide ended with (the settings are already saved).</summary>
public sealed record FirstRunResult(FirstRunOutcome Outcome, ReadMode Mode, bool Recording, bool Autostart);

/// <summary>
/// The first-run guide's state and its finish actions, without UI (tested with a fake platform). Choices start at the
/// recommended defaults: full mode, PawnIO when it is missing or outdated, history on, start with Windows on.
/// </summary>
public sealed class FirstRunFlow : ObservableObject
{
    public const int VisibleSteps = 4;

    private readonly IPlatformService _platform;
    private readonly SettingsStore _store;
    private FirstRunStep _step;
    private ReadMode _mode = ReadMode.Full;
    private bool _installPawnIo;
    private bool _recordHistory = true;
    private bool _autostart = true;
    private bool _busy;

    public FirstRunFlow(IPlatformService platform, SettingsStore store)
    {
        _platform = platform;
        _store = store;
        _installPawnIo = PawnIoNeeded;
    }

    public IPlatformService Platform => _platform;

    public FirstRunStep Step { get => _step; private set => Set(ref _step, value); }

    /// <summary>0–3 for the four visible steps (the working / failed screens stay on the last).</summary>
    public int StepIndex => Math.Min((int)Step, VisibleSteps - 1);

    public bool CanGoBack => Step is > FirstRunStep.Welcome and <= FirstRunStep.Tray && !IsBusy;

    public bool IsLastStep => Step == FirstRunStep.Tray;

    public ReadMode Mode { get => _mode; set => Set(ref _mode, value); }

    /// <summary>The PawnIO checkbox (only offered when it is missing or older than 2.1).</summary>
    public bool InstallPawnIo { get => _installPawnIo; set => Set(ref _installPawnIo, value && PawnIoNeeded); }

    public bool PawnIoNeeded => _platform.PawnIoVersion is null || _platform.PawnIoOutdated;

    public bool RecordHistory { get => _recordHistory; set => Set(ref _recordHistory, value); }

    public bool Autostart { get => _autostart; set => Set(ref _autostart, value); }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value)) OnPropertyChanged(nameof(CanGoBack));
        }
    }

    /// <summary>"pl" / "en" shown now.</summary>
    public string Language => Loc.I.Language;

    /// <summary>The user picked a language on the welcome screen (applies at once, saved as an explicit choice).</summary>
    public void SetLanguage(string language)
    {
        if (language is not ("pl" or "en")) return;
        if (_store.Current.General.Language != language) _store.Mutate("general", s => s.General.Language = language);
        Loc.I.SetLanguage(language);
        OnPropertyChanged(nameof(Language));
    }

    public void Next()
    {
        if (IsBusy || Step >= FirstRunStep.Tray) return;
        Go(Step + 1);
    }

    public void Back()
    {
        if (!CanGoBack) return;
        Go(Step - 1);
    }

    /// <summary>
    /// "Finish" on the last step. Full mode: saves the choices (so the elevated copy starts with them), then one UAC
    /// prompt → install; true = <see cref="FirstRunOutcome.FullModeStarting"/>. Declined / failed → the choices stay but
    /// the guide is not done, and the Failed screen offers retry or basic mode (result null). Basic: saves and finishes.
    /// </summary>
    public async Task<FirstRunResult?> FinishAsync()
    {
        if (IsBusy) return null;
        if (Mode == ReadMode.Basic) return await FinishBasicAsync();

        IsBusy = true;
        Go(FirstRunStep.Working);
        Save(ReadMode.Full, Autostart, done: true);
        bool ok;
        try
        {
            ok = await _platform.InstallFullModeAsync(Autostart, InstallPawnIo && PawnIoNeeded);
        }
        catch (Exception ex)
        {
            Log.Error("first run: full mode install threw", ex);
            ok = false;
        }
        IsBusy = false;
        if (ok) return new FirstRunResult(FirstRunOutcome.FullModeStarting, ReadMode.Full, RecordHistory, Autostart);

        Log.Warn("first run: full mode declined or failed");
        _store.Mutate("app", s => s.FirstRunDone = false);
        _store.Flush();
        Go(FirstRunStep.Failed);
        return null;
    }

    /// <summary>Failed screen: "Try again".</summary>
    public Task<FirstRunResult?> RetryAsync()
    {
        Mode = ReadMode.Full;
        return FinishAsync();
    }

    /// <summary>Failed screen: "Continue in basic mode".</summary>
    public Task<FirstRunResult?> ContinueBasicAsync()
    {
        Mode = ReadMode.Basic;
        return FinishBasicAsync();
    }

    private async Task<FirstRunResult?> FinishBasicAsync()
    {
        IsBusy = true;
        var autostart = Autostart;
        try
        {
            // basic mode needs no administrator rights; the platform registers a plain per-user start
            if (autostart != _platform.AutostartEnabled) autostart = await _platform.SetAutostartAsync(autostart);
        }
        catch (Exception ex)
        {
            Log.Error("first run: autostart failed", ex);
            autostart = _platform.AutostartEnabled;
        }
        Save(ReadMode.Basic, autostart, done: true);
        IsBusy = false;
        return new FirstRunResult(FirstRunOutcome.Basic, ReadMode.Basic, RecordHistory, autostart);
    }

    private void Save(ReadMode mode, bool autostart, bool done)
    {
        var record = RecordHistory;
        _store.Mutate("recording", s => s.Recording.Enabled = record);
        _store.Mutate("general", s => s.General.Autostart = autostart);
        _store.Mutate("driver", s =>
        {
            s.Driver.Mode = mode;
            s.Driver.PawnIoOffered = true;
        });
        _store.Mutate("app", s => s.FirstRunDone = done);
        _store.Flush();
    }

    private void Go(FirstRunStep step)
    {
        Step = step;
        OnPropertyChanged(nameof(StepIndex));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(IsLastStep));
    }
}
