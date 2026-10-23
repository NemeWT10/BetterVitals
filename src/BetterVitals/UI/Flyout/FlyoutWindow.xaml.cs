using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using BetterVitals.Localization;
using BetterVitals.UI.Panel;

namespace BetterVitals.UI.Flyout;

/// <summary>
/// The tray flyout (Windows 11 style, anchored above the tray): four key values with sparklines, the overlay switch,
/// quick actions and the recording state. Closes when it loses activation and is destroyed on close (nothing lingers in
/// the tray); the live hookups exist only while it is loaded.
/// </summary>
public partial class FlyoutWindow : Window
{
    public FlyoutViewModel Model { get; }

    /// <summary>Snapshot / probe mode: never activated, so it must not close on deactivation.</summary>
    internal bool Offscreen { get; set; }

    public FlyoutWindow(FlyoutViewModel model)
    {
        Model = model;
        InitializeComponent();
        DataContext = model;
        Language = Loc.I.XmlLang;
        EmptyState.Visibility = model.HasTiles ? Visibility.Collapsed : Visibility.Visible;
        TileList.Visibility = model.HasTiles ? Visibility.Visible : Visibility.Collapsed;
        FooterIcon.SetResourceReference(BetterVitals.UI.Controls.Icon.ForegroundProperty, model.IsRecording ? "Brush.Danger" : "Brush.TextFaint");

        Loaded += (_, _) =>
        {
            Model.Attach(Dispatcher);
            Loc.LanguageChanged += OnLanguageChanged;
        };
        Unloaded += (_, _) =>
        {
            Model.Detach();
            Loc.LanguageChanged -= OnLanguageChanged;
        };
        Closed += (_, _) => Model.Detach();
        Deactivated += (_, _) =>
        {
            if (!Offscreen) Close();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        // the live dot breathes only while the flyout is on screen
        Motion.WhileVisible(LiveDot,
            () => LiveDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(900))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = Motion.EaseOut,
            }),
            () => LiveDot.BeginAnimation(OpacityProperty, null));
    }

    private void OnLanguageChanged() => Language = Loc.I.XmlLang;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.AddExStyle(new WindowInteropHelper(this).Handle, Native.WS_EX_TOOLWINDOW);
    }

    /// <summary>Opens above the tray icon (or the cursor), activates and plays the entrance.</summary>
    internal void ShowAt(Native.RECT anchor)
    {
        Card.Opacity = 0;
        var edge = FloatingPlacement.ShowNear(this, anchor, shadowMargin: 16);
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.ForceForeground(hwnd);
        Activate();
        if (Motion.I.Enabled) Motion.Appear(Card, rise: edge == FloatingPlacement.Edge.Top ? -14 : 14, fromScale: 0.96, ms: 300);
        else Card.Opacity = 1;
    }

    private void Run(Action action)
    {
        Close();
        Dispatcher.BeginInvoke(action, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void Tile_Click(object sender, RoutedEventArgs e) => Run(() => AppShell.I.OpenPanel(PageIds.Overview));

    private void Panel_Click(object sender, RoutedEventArgs e) => Run(() => AppShell.I.OpenPanel(PageIds.Overview));

    private void EditOverlay_Click(object sender, RoutedEventArgs e) => Run(() => AppShell.I.OpenPanel(PageIds.Overlay));

    private void Settings_Click(object sender, RoutedEventArgs e) => Run(() => AppShell.I.OpenPanel(PageIds.Settings));
}
