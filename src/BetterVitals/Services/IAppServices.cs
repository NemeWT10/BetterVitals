using BetterVitals.Core.Overlay;
using BetterVitals.Core.Settings;
using BetterVitals.Overlay;
using BetterVitals.Platform;
using BetterVitals.Platform.Updates;

namespace BetterVitals.Services;

/// <summary>
/// The running overlay as the editor sees it. Implemented by the integration (wraps <see cref="OverlayHost"/>);
/// <see cref="NullOverlayService"/> in snapshots and tests. The overlay itself follows
/// <c>SettingsStore.Current.Overlay</c> — the editor only changes settings, previews with
/// <see cref="OverlayHost.RenderToBitmap"/>, and uses this for things that need the live window.
/// </summary>
public interface IOverlayService
{
    /// <summary>Monitors as the overlay sees them (stable keys for <see cref="OverlayPlacement.MonitorKey"/>).</summary>
    IReadOnlyList<OverlayMonitor> Monitors { get; }

    /// <summary>
    /// "Set on screen": the real overlay becomes draggable (snap guides, arrows 1 px / Shift 10 px, Enter / Esc).
    /// <paramref name="commit"/> / <paramref name="cancel"/> run on the UI thread.
    /// </summary>
    void BeginOnScreenEdit(Action<OverlayPlacement> commit, Action cancel);

    /// <summary>Ends the on-screen edit from the editor's Done / Cancel buttons.</summary>
    void EndOnScreenEdit(bool commit);

    bool IsEditingOnScreen { get; }

    /// <summary>Monitors were added / removed / changed (UI thread).</summary>
    event Action? MonitorsChanged;
}

public sealed class NullOverlayService : IOverlayService
{
    public static NullOverlayService Instance { get; } = new();

    public IReadOnlyList<OverlayMonitor> Monitors => [];

    public void BeginOnScreenEdit(Action<OverlayPlacement> commit, Action cancel) => cancel();

    public void EndOnScreenEdit(bool commit) { }

    public bool IsEditingOnScreen => false;

    public event Action? MonitorsChanged { add { } remove { } }
}

/// <summary>Windows security features shown on the "Permissions and driver" page (read-only).</summary>
public sealed record SecurityInfo(bool? MemoryIntegrity, bool? SecureBoot, bool? Vbs);

public enum UpdateUiState
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    /// <summary>Downloaded and verified; restart applies it.</summary>
    Ready,
    Failed,
}

/// <summary>
/// Windows-integration actions the UI triggers (first-run guide, Settings → Permissions / Startup / Updates / About).
/// Implemented by the integration on top of BetterVitals.Platform; <see cref="NullPlatformService"/> in snapshots and
/// tests. Everything that needs administrator rights shows one UAC prompt (the user is told before) or runs directly
/// when the app is already elevated. Async methods never throw for expected failures (declined UAC = false / null).
/// </summary>
public interface IPlatformService
{
    bool IsElevated { get; }

    /// <summary>Installed to Program Files with the launcher task (full mode works without prompts).</summary>
    bool IsInstalled { get; }

    Version? PawnIoVersion { get; }

    /// <summary>Installed but older than the re-signed 2.1 (FACEIT blocked the old one).</summary>
    bool PawnIoOutdated { get; }

    Version PawnIoBundledVersion { get; }

    SecurityInfo Security { get; }

    /// <summary>The driver-backed sensors are paused right now (anti-cheat setting), and which anti-cheat caused it.</summary>
    string? AntiCheatPauseReason { get; }

    /// <summary>
    /// Full mode: one UAC prompt → install to Program Files, tasks, optional PawnIO; then the elevated copy starts and
    /// this process exits. False when the user declined or it failed (the reason is logged).
    /// </summary>
    Task<bool> InstallFullModeAsync(bool autostart, bool installPawnIo);

    /// <summary>Installs / updates the bundled PawnIO (UAC unless elevated).</summary>
    Task<PawnIoInstallOutcome?> InstallPawnIoAsync();

    /// <summary>"Start with Windows" (UAC unless elevated). Returns the state actually applied.</summary>
    Task<bool> SetAutostartAsync(bool enabled);

    bool AutostartEnabled { get; }

    // ── updates ──

    UpdateUiState UpdateState { get; }

    /// <summary>The newer release when <see cref="UpdateState"/> is Available / Downloading / Ready.</summary>
    ReleaseInfo? AvailableRelease { get; }

    /// <summary>0–1 while downloading.</summary>
    double UpdateProgress { get; }

    /// <summary>Raised on the UI thread when any update / platform state above changes.</summary>
    event Action? StateChanged;

    /// <summary>"Check now" (ignores the daily limit and metered links, never runs in a game).</summary>
    Task CheckForUpdatesAsync();

    /// <summary>Downloads and verifies the available release (state → Ready).</summary>
    Task DownloadUpdateAsync();

    /// <summary>Swaps the exe and restarts (state must be Ready).</summary>
    void RestartToUpdate();

    /// <summary>Opens a URL in the user's normal (unelevated) browser.</summary>
    void OpenUrl(string url);

    /// <summary>Opens a folder in Explorer (data folder, export folder).</summary>
    void OpenFolder(string path);
}

public sealed class NullPlatformService : IPlatformService
{
    public static NullPlatformService Instance { get; } = new();

    public bool IsElevated => false;

    public bool IsInstalled => false;

    public Version? PawnIoVersion => null;

    public bool PawnIoOutdated => false;

    public Version PawnIoBundledVersion => PawnIo.BundledVersion;

    public SecurityInfo Security { get; } = new(null, null, null);

    public string? AntiCheatPauseReason => null;

    public Task<bool> InstallFullModeAsync(bool autostart, bool installPawnIo) => Task.FromResult(false);

    public Task<PawnIoInstallOutcome?> InstallPawnIoAsync() => Task.FromResult<PawnIoInstallOutcome?>(null);

    public Task<bool> SetAutostartAsync(bool enabled) => Task.FromResult(false);

    public bool AutostartEnabled => false;

    public UpdateUiState UpdateState => UpdateUiState.Idle;

    public ReleaseInfo? AvailableRelease => null;

    public double UpdateProgress => 0;

    public event Action? StateChanged { add { } remove { } }

    public Task CheckForUpdatesAsync() => Task.CompletedTask;

    public Task DownloadUpdateAsync() => Task.CompletedTask;

    public void RestartToUpdate() { }

    public void OpenUrl(string url) { }

    public void OpenFolder(string path) { }
}

/// <summary>
/// Live samples for the History page in "live only" mode and for the newest, not-yet-flushed data: the page asks
/// the history service to keep a 15-minute in-memory buffer of the sensors it shows. Null until history is attached.
/// </summary>
public delegate IDisposable WatchSensors(IEnumerable<string> sensorIds);
