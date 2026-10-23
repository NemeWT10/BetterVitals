using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BetterVitals.Core;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.UI.Panel;

namespace BetterVitals.UI.Tray;

/// <summary>
/// Themed replacement for the tray context menu (BetterShot's TrayMenuWindow): Overlay switch, Edit overlay…, Panel,
/// Settings, About, Exit. Opens with its corner at the cursor, closes when it loses activation, destroyed on close.
/// </summary>
public partial class TrayMenuWindow : Window
{
    /// <summary>Snapshot / probe mode: never activated, so it must not close on deactivation.</summary>
    internal bool Offscreen { get; set; }

    public TrayMenuWindow()
    {
        InitializeComponent();
        Language = Loc.I.XmlLang;
        VersionText.Text = AppEnv.ShortVersion;
        OverlaySwitch.IsChecked = SettingsStore.I?.Current.Overlay.Enabled == true;
        Deactivated += (_, _) =>
        {
            if (!Offscreen) Close();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.AddExStyle(new WindowInteropHelper(this).Handle, Native.WS_EX_TOOLWINDOW);
    }

    /// <summary>Opens with its bottom-right corner at the cursor (physical px), inside the work area, and slides in.</summary>
    internal void ShowAt(Native.RECT cursor)
    {
        Card.Opacity = 0;
        FloatingPlacement.ShowNear(this, cursor, shadowMargin: 16, gap: 4, atCursor: true);
        Native.ForceForeground(new WindowInteropHelper(this).Handle);
        Activate();
        if (Motion.I.Enabled)
        {
            Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = Motion.EaseBack });
        }
        else
        {
            Card.BeginAnimation(OpacityProperty, null);
            Card.Opacity = 1;
        }
    }

    private void Run(Action action)
    {
        Close();
        Dispatcher.BeginInvoke(action, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void Overlay_Click(object sender, RoutedEventArgs e)
    {
        var on = OverlaySwitch.IsChecked != true;
        OverlaySwitch.IsChecked = on;
        AppShell.I.SetOverlay(on);
    }

    private void EditOverlay_Click(object sender, RoutedEventArgs e) => Run(() => AppShell.I.OpenPanel(PageIds.Overlay));

    private void Panel_Click(object sender, RoutedEventArgs e) => Run(() => AppShell.I.OpenPanel(PageIds.Overview));

    private void Settings_Click(object sender, RoutedEventArgs e) => Run(() => AppShell.I.OpenPanel(PageIds.Settings));

    private void About_Click(object sender, RoutedEventArgs e) => Run(() => AppShell.I.OpenPanel(PageIds.Settings, SettingsPageParameter.About));

    private void Exit_Click(object sender, RoutedEventArgs e) => Run(AppShell.I.Quit);
}
