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

    /// <summary>
    /// Rendered to a bitmap (snapshots, README pictures): the glass is drawn directly, not from its cache, which holds it at
    /// the screen's scale and would be resampled at another capture scale.
    /// </summary>
    internal bool ForCapture
    {
        get => GlassHost.CacheMode is null;
        init
        {
            if (value) GlassHost.CacheMode = null;
        }
    }

    private bool _closing;

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
        // closing deactivates the window too: never close twice
        Closing += (_, _) => _closing = true;
        Deactivated += (_, _) =>
        {
            if (!Offscreen && !_closing) Close();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_closing) Close();
        };
        // the live dot breathes only while the flyout is on screen
        Motion.LoopWhileVisible(LiveDot, LiveDot, OpacityProperty,
            () => Motion.Throttled(new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(900))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = Motion.EaseOut,
            }));
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
        Root.Opacity = 0;
        var edge = FloatingPlacement.ShowNear(this, anchor, shadowMargin: 16);
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.ForceForeground(hwnd);
        Activate();
        if (Motion.I.Enabled) Motion.Appear(Root, rise: edge == FloatingPlacement.Edge.Top ? -14 : 14, fromScale: 0.96, ms: 300);
        else Root.Opacity = 1;
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
