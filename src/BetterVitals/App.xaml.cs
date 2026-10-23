using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using BetterVitals.Core;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.Services;
using BetterVitals.UI;
using BetterVitals.UI.Dev;
using BetterVitals.UI.Explain;
using BetterVitals.UI.Themes;
using BetterVitals.UI.Tray;

namespace BetterVitals;

/// <summary>
/// Start-up order: settings (already loaded by Program) → language → theme → motion → explanations → hub → message
/// window (+ tray icon outside test mode) → single-instance listener → tray rest (or snapshots / probe / --open).
/// </summary>
public partial class App : Application
{
    private readonly StartupOptions _options;
    private readonly SingleInstance? _instance;
    private MessageWindow? _messages;
    private TrayIcon? _tray;
    private AppShell? _shell;
    private int _uiErrors;

    public static new App Current => (App)Application.Current;

    public App() : this(new StartupOptions(), null)
    {
    }

    internal App(StartupOptions options, SingleInstance? instance)
    {
        _options = options;
        _instance = instance;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    internal StartupOptions Options => _options;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var g = SettingsStore.I.Current.General;
        Loc.I.SetLanguage(g.Language);
        ThemeManager.I.Apply(g.Theme, g.Mica);
        Motion.I.Update(g.Animations);
        ExplainService.I.Enabled = g.Explanations;
        SettingsStore.I.Changed += OnSettingsChanged;

        // until the engine is integrated: the preview hub in test / snapshot mode, nothing otherwise
        if (AppEnv.TestMode && AppServices.Hub is NullSensorHub)
            AppServices.Hub = new PreviewHub(start: _options.SnapshotDir is null);

        _messages = new MessageWindow();
        _messages.Message += OnSystemMessage;
        _shell = new AppShell();
        _shell.QuitRequested += Quit;

        if (!AppEnv.TestMode)
        {
            _tray = new TrayIcon(_messages) { TooltipProvider = _shell.TooltipText };
            _tray.FlyoutRequested += _shell.ShowFlyout;
            _tray.MenuRequested += _shell.ShowTrayMenu;
            _tray.PanelRequested += () => _shell.OpenPanel();
            _shell.Tray = _tray;
        }
        _instance?.Listen(command => Dispatcher.BeginInvoke(() => _shell.HandleCommand(command)));

        if (_options.SnapshotDir is { } dir) SnapshotMode.Start(dir, _options.SnapshotLanguage, _options.SnapshotTheme);
        else if (_options.TrayProbe) TrayProbe.Start();
        else if (_options.OpenPage is { } page) _shell.OpenPanel(page);
        else TrayRest.Settle(); // straight to the tray: give the start-up memory back
        Log.Info($"ui ready (test={AppEnv.TestMode}, tray={_tray is not null})");
    }

    /// <summary>Exit: windows close, the tray icon goes, settings are written.</summary>
    public void Quit()
    {
        try
        {
            _shell?.CloseAll();
        }
        catch (Exception ex)
        {
            Log.Error("quit: closing windows failed", ex);
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _tray = null;
        _messages?.Dispose();
        (AppServices.Hub as IDisposable)?.Dispose();
        SettingsStore.I?.Flush();
        Log.Info($"exit {e.ApplicationExitCode}");
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        SettingsStore.I?.Flush();
        base.OnSessionEnding(e);
    }

    private void OnSettingsChanged(string section)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnSettingsChanged(section));
            return;
        }
        if (section != "general") return;
        var g = SettingsStore.I.Current.General;
        Loc.I.SetLanguage(g.Language);
        ThemeManager.I.Apply(g.Theme, g.Mica);
        Motion.I.Update(g.Animations);
        ExplainService.I.Enabled = g.Explanations;
    }

    private void OnSystemMessage(int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != Native.WM_SETTINGCHANGE) return;
        var area = lParam != 0 ? Marshal.PtrToStringUni(lParam) : null;
        if (area == "ImmersiveColorSet") ThemeManager.I.OnSystemThemeChanged();
        // "Animation effects" in Windows settings
        Motion.I.Update(SettingsStore.I.Current.General.Animations);
    }

    /// <summary>A UI exception: logged, settings saved, the app keeps running (the tray must not vanish over a glitch).</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("ui: unhandled exception", e.Exception);
        try
        {
            SettingsStore.I?.Flush();
        }
        catch
        {
            // best effort
        }
        e.Handled = ++_uiErrors < 50;
        if (_options.SnapshotDir is not null) SnapshotMode.ReportCrash(e.Exception);
    }
}
