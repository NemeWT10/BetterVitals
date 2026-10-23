using System.Text;
using System.Windows;
using System.Windows.Interop;
using BetterVitals.Core;
using BetterVitals.Core.Formatting;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.Services;
using BetterVitals.UI;
using BetterVitals.UI.Flyout;
using BetterVitals.UI.Panel;
using BetterVitals.UI.Toasts;
using BetterVitals.UI.Tray;

namespace BetterVitals;

/// <summary>
/// The app's windows and tray actions: panel, flyout, tray menu, overlay switch, quit. Windows are created on demand and
/// destroyed when they close; when the last one closes the app goes to <see cref="TrayRest"/>. In test mode every
/// window opens far off screen and is never activated (it must never reach the user's desktop or input).
/// </summary>
public sealed class AppShell
{
    public static AppShell I { get; private set; } = null!;

    private readonly List<Window> _windows = [];
    private PanelWindow? _panel;
    private FlyoutWindow? _flyout;
    private TrayMenuWindow? _menu;
    private long _flyoutClosedAt;
    private long _menuClosedAt;

    internal TrayIcon? Tray { get; set; }

    /// <summary>Asked to quit (the App shuts down).</summary>
    internal event Action? QuitRequested;

    internal AppShell()
    {
        I = this;
        TrayRest.AnyWindowVisible = () => _windows.Any(w => w.IsVisible);
    }

    public PanelWindow? Panel => _panel;

    /// <summary>Opens (or brings back) the panel at a page.</summary>
    public void OpenPanel(string? pageId = null, object? parameter = null)
    {
        CloseFloating();
        if (_panel is { } p)
        {
            if (p.WindowState == WindowState.Minimized) p.WindowState = WindowState.Normal;
            p.Navigate(pageId ?? p.CurrentPage ?? PageIds.Overview, parameter);
            if (!AppEnv.TestMode)
            {
                Native.ForceForeground(new WindowInteropHelper(p).Handle);
                p.Activate();
            }
            return;
        }
        var panel = _panel = new PanelWindow();
        Track(panel);
        panel.Closed += (_, _) => _panel = null;
        panel.Navigate(pageId ?? PageIds.Overview, parameter);
        if (AppEnv.TestMode)
        {
            FloatingPlacement.ShowOffscreen(panel);
        }
        else
        {
            panel.Show();
            panel.Activate();
        }
    }

    /// <summary>Left click on the tray icon: opens the flyout, or closes it when it was just open (toggle).</summary>
    public void ShowFlyout()
    {
        if (_flyout is not null)
        {
            _flyout.Close();
            return;
        }
        if (Environment.TickCount64 - _flyoutClosedAt < 350) return; // the click that deactivated it
        _menu?.Close();
        var flyout = _flyout = new FlyoutWindow(new FlyoutViewModel(AppServices.Hub));
        Track(flyout);
        flyout.Closed += (_, _) =>
        {
            _flyout = null;
            _flyoutClosedAt = Environment.TickCount64;
        };
        if (AppEnv.TestMode)
        {
            flyout.Offscreen = true;
            FloatingPlacement.ShowOffscreen(flyout);
            return;
        }
        flyout.ShowAt(Tray?.GetRect() ?? FloatingPlacement.CursorAnchor());
    }

    /// <summary>Right click on the tray icon.</summary>
    public void ShowTrayMenu()
    {
        if (_menu is not null)
        {
            _menu.Close();
            return;
        }
        if (Environment.TickCount64 - _menuClosedAt < 350) return;
        _flyout?.Close();
        var menu = _menu = new TrayMenuWindow();
        Track(menu);
        menu.Closed += (_, _) =>
        {
            _menu = null;
            _menuClosedAt = Environment.TickCount64;
        };
        if (AppEnv.TestMode)
        {
            menu.Offscreen = true;
            FloatingPlacement.ShowOffscreen(menu);
            return;
        }
        menu.ShowAt(FloatingPlacement.CursorAnchor());
    }

    /// <summary>The overlay on / off (the overlay follows the setting).</summary>
    public void SetOverlay(bool on)
    {
        if (SettingsStore.I is not { } store || store.Current.Overlay.Enabled == on) return;
        store.Mutate("overlay", s => s.Overlay.Enabled = on);
        _panel?.Toast(Loc.T(on ? "toast.overlayOn" : "toast.overlayOff"), ToastKind.Success, Loc.T("common.undo"), () => SetOverlay(!on));
    }

    public void Quit() => QuitRequested?.Invoke();

    /// <summary>"open:&lt;page&gt;" from a second launch (or anything else: open the panel).</summary>
    internal void HandleCommand(string command)
    {
        // the uninstaller / an update asks the running copy to exit
        if (command == "quit")
        {
            Quit();
            return;
        }
        var page = command.StartsWith("open:", StringComparison.Ordinal) ? command[5..] : null;
        OpenPanel(string.IsNullOrWhiteSpace(page) ? null : page);
    }

    /// <summary>Closes every window (exit).</summary>
    internal void CloseAll()
    {
        foreach (var w in _windows.ToList()) w.Close();
    }

    private void CloseFloating()
    {
        _flyout?.Close();
        _menu?.Close();
    }

    private void Track(Window w)
    {
        TrayRest.Leave();
        _windows.Add(w);
        w.Closed += (_, _) =>
        {
            _windows.Remove(w);
            if (_windows.Count == 0) TrayRest.Enter();
        };
    }

    /// <summary>The tray tooltip: the name and the flyout's key values ("CPU 54 °C · GPU 61 °C…"), at most 127 characters.</summary>
    public string TooltipText()
    {
        var sb = new StringBuilder(AppEnv.ProductName);
        var hub = AppServices.Hub;
        var pinned = SettingsStore.I?.Current.Flyout.PinnedSensorIds ?? [];
        var unit = SettingsStore.I?.Current.General.TemperatureUnit ?? TemperatureUnit.Celsius;
        foreach (var s in SensorPresentation.KeySensors(hub, pinned))
        {
            var text = ValueFormat.ToText(s.Kind, hub.Latest[s.Index], Loc.I.Culture, out var u, 0, unit, s.Unit);
            var line = $"\n{SensorPresentation.FamilyLabel(s.HardwareKind)} {SensorPresentation.KindLabel(s.Kind).ToLower(Loc.I.Culture)}: {text} {u}";
            if (sb.Length + line.Length > 127) break;
            sb.Append(line);
        }
        return sb.ToString();
    }
}
