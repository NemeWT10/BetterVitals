using System.Windows;
using System.Windows.Controls;
using BetterVitals.Core;
using BetterVitals.Localization;
using BetterVitals.Services;
using BetterVitals.UI.Controls;
using BetterVitals.UI.Onboarding;
using BetterVitals.UI.Toasts;
using static BetterVitals.UI.Panel.SettingsUi;

namespace BetterVitals.UI.Panel;

/// <summary>
/// Settings ▸ Startup: "Start with Windows" through <see cref="IPlatformService.SetAutostartAsync"/> (one UAC prompt unless
/// the app already runs elevated — said before the click), and how to pin the tray icon.
/// </summary>
internal sealed class StartupSection : SectionView
{
    private readonly IPlatformService _platform;
    private readonly SettingRow _autostartRow;
    private readonly FrameworkElement _hint;
    private readonly Button _hintButton;
    private bool _busy;

    public StartupSection(PageContext? context, IPlatformService? platform = null) : base(context)
    {
        _platform = platform ?? AppServices.Platform;
        var root = new StackPanel();

        AutostartSwitch = Switch("settings.start.autostart");
        AutostartSwitch.Checked += async (_, _) => await SetAutostartAsync(true);
        AutostartSwitch.Unchecked += async (_, _) => await SetAutostartAsync(false);
        _autostartRow = Row("settings.start.autostart", "settings.start.autostart.desc", "Icon.Power", AutostartSwitch);

        _hintButton = Button("SecondaryButton", "settings.start.tray.show");
        _hint = new TrayHint { Margin = new Thickness(0, 0, 0, 16), Visibility = Visibility.Collapsed };
        _hintButton.Click += (_, _) =>
        {
            var show = _hint.Visibility != Visibility.Visible;
            _hint.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            SetButtonText(_hintButton, show ? "settings.start.tray.hide" : "settings.start.tray.show");
            if (show) Motion.Appear(_hint, rise: 6, fromScale: 0.99, ms: 260);
        };
        var trayRow = Row("settings.start.tray", "settings.start.tray.desc", "Icon.Pin", _hintButton);

        var card = Rows(_autostartRow, trayRow);
        ((StackPanel)card.Child).Children.Add(_hint);
        root.Children.Add(card);
        Content = root;
        Initialize();
    }

    internal CheckBox AutostartSwitch { get; }

    internal bool HintVisible => _hint.Visibility == Visibility.Visible;

    internal string AutostartWarning => _autostartRow.Warning;

    protected override IReadOnlyCollection<string> Watches => ["general"];

    protected override void OnAttached() => _platform.StateChanged += OnPlatform;

    protected override void OnDetached() => _platform.StateChanged -= OnPlatform;

    private void OnPlatform() => OnUi(Sync);

    protected override void Refresh()
    {
        if (!_busy) AutostartSwitch.IsChecked = _platform.AutostartEnabled;
        AutostartSwitch.IsEnabled = !_busy;
        _autostartRow.Warning = _busy ? Loc.T("settings.start.busy") : _platform.IsElevated ? "" : Loc.T("settings.start.uac");
    }

    internal async Task SetAutostartAsync(bool on)
    {
        if (Syncing || _busy || _platform.AutostartEnabled == on) return;
        _busy = true;
        Sync();
        bool applied;
        try
        {
            applied = await _platform.SetAutostartAsync(on);
        }
        catch (Exception ex)
        {
            Log.Error("settings: autostart failed", ex);
            applied = _platform.AutostartEnabled;
        }
        _busy = false;
        Store.Mutate("general", s => s.General.Autostart = applied);
        if (applied == on) Toast(on ? "settings.start.on" : "settings.start.off", ToastKind.Success);
        else Toast("settings.start.failed", ToastKind.Warning);
        Sync();
    }
}
