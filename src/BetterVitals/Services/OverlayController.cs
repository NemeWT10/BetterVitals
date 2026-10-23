using System.Windows.Threading;
using BetterVitals.Core;
using BetterVitals.Core.Catalog;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.Overlay;
using BetterVitals.UI;
using BetterVitals.UI.Themes;

namespace BetterVitals.Services;

/// <summary>
/// Keeps the native overlay in step with the settings, the theme and the language: every relevant change re-applies
/// the layout (the host deep-copies it and decides visibility from Enabled / Visibility itself). The host thread is
/// started lazily — the first time the overlay is enabled — so a user who never uses it never pays for it.
/// Fills the default items (FPS, CPU and GPU temperature) once, when the sensors are known.
/// </summary>
internal sealed class OverlayController : IOverlayService, IDisposable
{
    private readonly ISensorHub _hub;
    private readonly Dispatcher _ui;
    private OverlayHost? _host;
    private Action<IReadOnlyDictionary<string, OverlayPlacement>>? _commit;
    private Action? _cancel;
    private readonly HashSet<uint> _tippedPids = [];
    private uint? _pendingTipPid;

    /// <summary>Raised on the UI thread with a tip for the tray ("use borderless window mode").</summary>
    public event Action<string, string>? Tip;

    public OverlayController(ISensorHub hub, Dispatcher ui)
    {
        _hub = hub;
        _ui = ui;
        SettingsStore.I.Changed += OnSettingsChanged;
        ThemeManager.Changed += Reapply;
        Loc.LanguageChanged += Reapply;
        _hub.InventoryChanged += OnInventoryChanged;
        if (SettingsStore.I.Current.Overlay.Enabled) Reapply();
    }

    public IReadOnlyList<OverlayMonitor> Monitors
    {
        get
        {
            try { return OverlayHost.GetMonitors(); }
            catch (Exception ex) { Log.Warn("overlay: monitors: " + ex.Message); return []; }
        }
    }

    public bool IsEditingOnScreen { get; private set; }

    public event Action? MonitorsChanged;

    /// <summary>The app saw WM_DISPLAYCHANGE (resolution / monitor layout).</summary>
    public void OnDisplayChanged() => _ui.BeginInvoke(() => MonitorsChanged?.Invoke());

    public void BeginOnScreenEdit(Action<IReadOnlyDictionary<string, OverlayPlacement>> commit, Action cancel)
    {
        var host = EnsureHost();
        _commit = commit;
        _cancel = cancel;
        IsEditingOnScreen = true;
        host.EnterEditMode(
            placements => _ui.BeginInvoke(() => Finish(placements)),
            () => _ui.BeginInvoke(() => Finish(null)));
    }

    public void EndOnScreenEdit(bool commit) => _host?.ExitEditMode(commit);

    private void Finish(IReadOnlyDictionary<string, OverlayPlacement>? placements)
    {
        IsEditingOnScreen = false;
        var commit = _commit;
        var cancel = _cancel;
        _commit = null;
        _cancel = null;
        if (placements is not null) commit?.Invoke(placements);
        else cancel?.Invoke();
    }

    /// <summary>Foreground changed (system events thread): show a pending "use borderless" tip once the game is gone.</summary>
    public void OnForegroundChanged(bool fullscreenInFront)
    {
        if (fullscreenInFront || _pendingTipPid is null) return;
        _pendingTipPid = null;
        _ui.BeginInvoke(() => Tip?.Invoke(Loc.T("tip.exclusive.title"), Loc.T("tip.exclusive.text")));
    }

    private OverlayHost EnsureHost()
    {
        if (_host is not null) return _host;
        var host = _host = new OverlayHost();
        host.ExclusiveFullscreenDetected += pid =>
        {
            // once per game process; shown later, when the user is back on the desktop (a balloon would be swallowed in game)
            lock (_tippedPids)
            {
                if (_tippedPids.Add(pid)) _pendingTipPid = pid;
            }
        };
        host.Start();
        host.SetVisible(true);
        return host;
    }

    private void OnSettingsChanged(string section)
    {
        if (section is "overlay" or "general") _ui.BeginInvoke(Reapply);
    }

    /// <summary>Re-applies the current layout (UI thread: the theme lives here).</summary>
    private void Reapply()
    {
        if (!_ui.CheckAccess())
        {
            _ui.BeginInvoke(Reapply);
            return;
        }
        var s = SettingsStore.I.Current;
        // never start the overlay thread for a user who has not enabled it
        if (_host is null && !s.Overlay.Enabled) return;
        try
        {
            EnsureHost().Apply(s.Overlay, ThemeManager.I.ResolveOverlayTheme(), new OverlayContext
            {
                Hub = _hub,
                Culture = Loc.I.Culture,
                TemperatureUnit = s.General.TemperatureUnit,
                LabelResolver = ResolveLabel,
                Animations = Motion.I.Enabled,
            });
        }
        catch (Exception ex)
        {
            Log.Error("overlay: apply failed", ex);
        }
    }

    /// <summary>Default label of an item with no label of its own; null = the overlay's short defaults ("CPU", "GPU HS"…).</summary>
    private static string? ResolveLabel(OverlayItem item) => null;

    private void OnInventoryChanged()
    {
        if (SettingsStore.I.Current.Overlay is { DefaultsApplied: true }) return;
        _ui.BeginInvoke(ApplyDefaultItems);
    }

    /// <summary>First run: FPS, CPU and GPU temperature at the top-left (the user's own example), once, one row per component.</summary>
    private void ApplyDefaultItems()
    {
        var overlay = SettingsStore.I.Current.Overlay;
        if (overlay.DefaultsApplied) return;
        var items = FriendlyCatalog.Resolve(_hub.Hardware, _hub.Sensors);
        var picked = FriendlyCatalog.DefaultOverlayKeys
            .Select(k => items.FirstOrDefault(i => i.Key == k))
            .Where(i => i is not null)
            .ToList();
        // wait until at least the CPU or GPU temperature is known (FPS exists before any game runs)
        if (!picked.Any(i => i!.Category is HardwareKind.Cpu or HardwareKind.Gpu)) return;
        SettingsStore.I.Mutate("overlay", st =>
        {
            if (st.Overlay.DefaultsApplied) return;
            if (st.Overlay.IsEmpty)
                foreach (var i in picked) st.Overlay.AddItem(new OverlayItem { SensorIds = [.. i!.SensorIds] });
            st.Overlay.DefaultsApplied = true;
        });
    }

    public void Dispose()
    {
        SettingsStore.I.Changed -= OnSettingsChanged;
        ThemeManager.Changed -= Reapply;
        Loc.LanguageChanged -= Reapply;
        _hub.InventoryChanged -= OnInventoryChanged;
        _host?.Dispose();
        _host = null;
    }
}
