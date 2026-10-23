using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using BetterVitals.Core;
using BetterVitals.Core.Settings;
using BetterVitals.Platform;
using BetterVitals.Platform.Updates;

namespace BetterVitals.Services;

/// <summary>
/// <see cref="IPlatformService"/> on top of BetterVitals.Platform. Anything needing administrator rights runs in this
/// process when it is elevated, otherwise in a short-lived elevated copy of the exe (<see cref="PlatformCommands"/>)
/// behind one UAC prompt. Also drives the update cycle: one one-shot timer asks <see cref="UpdateScheduler"/> when it
/// may check — never in games, never on metered connections, at most daily.
/// </summary>
internal sealed class PlatformService : IPlatformService, IDisposable
{
    public const string Repository = "NemeWT10/BetterVitals";

    private readonly Dispatcher _ui;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly DispatcherTimer _updateTimer;
    private readonly Func<bool> _gameInFront;
    private UpdateService? _updates;
    private ReleaseInfo? _release;
    private string? _downloaded;
    private bool _lastSkipped;

    /// <summary>Quit the app (after a successful elevation hand-over or an update restart).</summary>
    public Action? QuitApp { get; set; }

    /// <summary>A quiet tray notification (title, text).</summary>
    public Action<string, string>? Notify { get; set; }

    public PlatformService(Dispatcher ui, Func<bool> gameInFront)
    {
        _ui = ui;
        _gameInFront = gameInFront;
        _updateTimer = new DispatcherTimer(DispatcherPriority.Background, ui);
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Stop();
            await CheckAsync(manual: false);
            ScheduleUpdateCheck();
        };
        SettingsStore.I.Changed += section => { if (section == "updates") _ui.BeginInvoke(ScheduleUpdateCheck); };
        ScheduleUpdateCheck();
    }

    public bool IsElevated => Elevation.IsElevated;

    public bool IsInstalled => Installer.IsInstalled && Installer.HasLauncher(Elevation.CurrentUserSid);

    public Version? PawnIoVersion => PawnIo.InstalledVersion;

    public bool PawnIoOutdated => PawnIo.IsOutdated;

    public Version PawnIoBundledVersion => PawnIo.BundledVersion;

    public SecurityInfo Security => new(SecurityState.MemoryIntegrity, SecurityState.SecureBoot, SecurityState.Vbs);

    public string? AntiCheatPauseReason
    {
        get => _antiCheatReason;
        set
        {
            if (_antiCheatReason == value) return;
            _antiCheatReason = value;
            Raise();
        }
    }

    private string? _antiCheatReason;

    public bool AutostartEnabled => SettingsStore.I.Current.General.Autostart;

    public UpdateUiState UpdateState { get; private set; } = UpdateUiState.Idle;

    public ReleaseInfo? AvailableRelease => _release;

    public double UpdateProgress { get; private set; }

    public event Action? StateChanged;

    private void Raise() => _ui.BeginInvoke(() => StateChanged?.Invoke());

    // ───────────────────────── install / driver / autostart ─────────────────────────

    public async Task<bool> InstallFullModeAsync(bool autostart, bool installPawnIo)
    {
        var sid = Elevation.CurrentUserSid;
        int? code;
        if (Elevation.IsElevated)
        {
            code = await Task.Run(() => PlatformCommands.Install(sid, autostart, installPawnIo));
        }
        else
        {
            var exe = Environment.ProcessPath!;
            code = await Task.Run(() => Elevation.RunElevated(exe, PlatformCommands.InstallArgs(sid, autostart, installPawnIo), TimeSpan.FromMinutes(5)));
        }
        if (code is not (PlatformCommands.Ok or PlatformCommands.OkRestartNeeded))
        {
            Log.Warn($"platform: full-mode install did not finish (code {code?.ToString() ?? "declined"})");
            return false;
        }
        SettingsStore.I.Mutate("driver", s =>
        {
            s.Driver.Mode = ReadMode.Full;
            s.Driver.PawnIoOffered = true;
            s.General.Autostart = autostart;
            s.FirstRunDone = true;
        });
        UserAutostart.Set(false, Environment.ProcessPath!); // the elevated task starts it now; never both
        SettingsStore.I.Flush();
        if (code == PlatformCommands.OkRestartNeeded) Notify?.Invoke(Localization.Loc.T("tip.restart.title"), Localization.Loc.T("tip.restart.pawnio"));
        // hand over to the elevated, installed copy
        if (!Elevation.IsElevated || !Installer.IsRunningInstalled)
        {
            AppHost.ReleaseSingleInstance();
            if (ScheduledTasks.Start(ScheduledTasks.LauncherName(sid))) _ = _ui.BeginInvoke(() => QuitApp?.Invoke());
        }
        Raise();
        return true;
    }

    public async Task<PawnIoInstallOutcome?> InstallPawnIoAsync()
    {
        int? code = Elevation.IsElevated
            ? await Task.Run(PlatformCommands.InstallPawnIo)
            : await Task.Run(() => Elevation.RunElevated(Environment.ProcessPath!, PlatformCommands.InstallPawnIoArgs, TimeSpan.FromMinutes(4)));
        PawnIoInstallOutcome? outcome = code switch
        {
            PlatformCommands.Ok => PawnIoInstallOutcome.Installed,
            PlatformCommands.OkRestartNeeded => PawnIoInstallOutcome.RestartRequired,
            PlatformCommands.Corrupted => PawnIoInstallOutcome.Corrupted,
            null => null,
            _ => PawnIoInstallOutcome.Failed,
        };
        if (outcome is PawnIoInstallOutcome.Installed or PawnIoInstallOutcome.RestartRequired)
        {
            SettingsStore.I.Mutate("driver", s => s.Driver.PawnIoOffered = true);
            AppHost.Current?.OnDriverInstalled();
        }
        Raise();
        return outcome;
    }

    public async Task<bool> SetAutostartAsync(bool enabled)
    {
        var sid = Elevation.CurrentUserSid;
        bool ok;
        if (!Installer.IsInstalled || SettingsStore.I.Current.Driver.Mode == ReadMode.Basic)
        {
            // basic mode / a copy that is not installed: an unelevated per-user start, never a UAC prompt
            ok = UserAutostart.Set(enabled, Environment.ProcessPath!);
        }
        else if (Elevation.IsElevated)
        {
            ok = await Task.Run(() => Installer.SetAutostart(sid, enabled));
        }
        else
        {
            var code = await Task.Run(() => Elevation.RunElevated(Environment.ProcessPath!, PlatformCommands.AutostartArgs(sid, enabled), TimeSpan.FromMinutes(1)));
            ok = code == PlatformCommands.Ok;
        }
        if (ok) SettingsStore.I.Mutate("general", s => s.General.Autostart = enabled);
        Raise();
        return ok ? enabled : AutostartEnabled;
    }

    // ───────────────────────── updates ─────────────────────────

    private UpdateService Updates => _updates ??= new UpdateService(Repository, Version.Parse(AppEnv.ShortVersion), UpdateKey.PublicKey);

    private void ScheduleUpdateCheck()
    {
        _updateTimer.Stop();
        var delay = UpdateScheduler.NextDelay(SettingsStore.I.Current.Updates, DateTime.UtcNow, _startedUtc, _lastSkipped);
        if (delay == Timeout.InfiniteTimeSpan || AppEnv.TestMode) return;
        _updateTimer.Interval = delay > TimeSpan.FromHours(25) ? TimeSpan.FromHours(25) : delay;
        _updateTimer.Start();
    }

    public Task CheckForUpdatesAsync() => CheckAsync(manual: true);

    private async Task CheckAsync(bool manual)
    {
        if (UpdateState is UpdateUiState.Checking or UpdateUiState.Downloading) return;
        var settings = SettingsStore.I.Current.Updates;
        var metered = MeteredNetwork.IsMetered();
        if (!UpdateScheduler.MayCheck(settings, DateTime.UtcNow, _startedUtc, _gameInFront(), metered, manual))
        {
            _lastSkipped = settings.Mode != UpdateMode.Off;
            return;
        }
        UpdateState = UpdateUiState.Checking;
        Raise();
        var (outcome, release, etag) = await Updates.CheckAsync(settings.ETag, _release);
        _lastSkipped = outcome == UpdateCheckOutcome.Skipped;
        SettingsStore.I.Mutate("updates-state", s =>
        {
            if (outcome != UpdateCheckOutcome.Skipped) s.Updates.LastCheckUtc = DateTime.UtcNow;
            s.Updates.ETag = etag;
        });
        switch (outcome)
        {
            case UpdateCheckOutcome.Available when release is not null && release.Tag != settings.SkippedVersion:
                _release = release;
                UpdateState = UpdateUiState.Available;
                Raise();
                if (settings.Mode == UpdateMode.Automatic) await DownloadUpdateAsync();
                else Notify?.Invoke(Localization.Loc.T("tip.update.title"), Localization.Loc.T("tip.update.available", release.Version.ToString(3)));
                return;
            case UpdateCheckOutcome.Skipped:
                UpdateState = manual ? UpdateUiState.Failed : UpdateUiState.Idle;
                break;
            default:
                UpdateState = UpdateUiState.UpToDate;
                break;
        }
        Raise();
    }

    public async Task DownloadUpdateAsync()
    {
        if (_release is null || UpdateState == UpdateUiState.Downloading) return;
        UpdateState = UpdateUiState.Downloading;
        UpdateProgress = 0;
        Raise();
        var target = UpdateTarget() + ".new";
        var progress = new Progress<double>(p =>
        {
            UpdateProgress = p;
            StateChanged?.Invoke();
        });
        var ok = await Updates.DownloadAndVerifyAsync(_release, target, progress);
        _downloaded = ok ? target : null;
        UpdateState = ok ? UpdateUiState.Ready : UpdateUiState.Failed;
        Raise();
        if (ok && SettingsStore.I.Current.Updates.Mode == UpdateMode.Automatic && !_gameInFront() && !AppShellHasWindows())
            RestartToUpdate(); // automatic mode: apply quietly when nobody is looking
        else if (ok)
            Notify?.Invoke(Localization.Loc.T("tip.update.title"), Localization.Loc.T("tip.update.ready", _release.Version.ToString(3)));
    }

    public void RestartToUpdate()
    {
        if (_downloaded is null || !File.Exists(_downloaded)) return;
        try
        {
            var exe = UpdateTarget();
            UpdateService.Apply(_downloaded, exe);
            _downloaded = null;
            SettingsStore.I.Flush();
            AppHost.ReleaseSingleInstance();
            var sid = Elevation.CurrentUserSid;
            var viaTask = Installer.IsRunningInstalled && ScheduledTasks.Start(ScheduledTasks.LauncherName(sid));
            if (!viaTask) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose();
            _ui.BeginInvoke(() => QuitApp?.Invoke());
        }
        catch (Exception ex)
        {
            Log.Error("update: apply failed", ex);
            UpdateState = UpdateUiState.Failed;
            Raise();
        }
    }

    /// <summary>The exe the update replaces: the installed copy, or this exe in basic / portable mode.</summary>
    private static string UpdateTarget() => Installer.IsRunningInstalled ? Installer.InstalledExe : Environment.ProcessPath!;

    private static bool AppShellHasWindows() => Application.Current?.Windows.Count > 0;

    // ───────────────────────── shell ─────────────────────────

    /// <summary>
    /// The URL opens in the user's normal browser: through Explorer, which runs unelevated even when we are elevated
    /// (a browser started directly would inherit administrator rights).
    /// </summary>
    public void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        StartExplorer(uri.AbsoluteUri);
    }

    public void OpenFolder(string path)
    {
        if (Directory.Exists(path)) StartExplorer(path);
    }

    private static void StartExplorer(string argument)
    {
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(new ProcessStartInfo(explorer) { ArgumentList = { argument }, UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("platform: explorer failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _updateTimer.Stop();
        _updates?.Dispose();
    }
}
