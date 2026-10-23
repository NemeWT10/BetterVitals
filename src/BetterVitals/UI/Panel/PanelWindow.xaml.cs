using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using BetterVitals.Core;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.Services;
using BetterVitals.UI.Themes;
using BetterVitals.UI.Toasts;

namespace BetterVitals.UI.Panel;

/// <summary>
/// The main panel: Mica, custom title bar, nav rail built from <see cref="PageRegistry"/>, one page shown at a time.
/// Pages are created on first visit, kept while the window lives and released (disposed) when it closes — the window
/// itself is destroyed on close, so the tray keeps nothing of it.
/// </summary>
public partial class PanelWindow : Window, IPanelHost
{
    private readonly Dictionary<string, FrameworkElement> _pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RadioButton> _nav = new(StringComparer.Ordinal);
    private readonly PageContext _context;
    private string? _current;
    private bool _navigating;

    public PanelWindow()
    {
        InitializeComponent();
        Language = Loc.I.XmlLang;
        _context = new PageContext(this, AppServices.Hub, AppServices.History);
        VersionText.Text = Loc.T("about.version", AppEnv.ShortVersion);
        BuildNav();
        UpdateStatus();

        Loaded += (_, _) =>
        {
            Loc.LanguageChanged += OnLanguageChanged;
            ThemeManager.Changed += OnThemeChanged;
            if (SettingsStore.I is { } s) s.Changed += OnSettingsChanged;
        };
        Unloaded += (_, _) => Unhook();
        StateChanged += (_, _) => OnStateChanged();
        Closed += (_, _) => Release();
    }

    /// <summary>The page shown now.</summary>
    public string? CurrentPage => _current;

    /// <summary>The page element of an id, if it was created (tests, snapshots).</summary>
    internal FrameworkElement? PageElement(string id) => _pages.GetValueOrDefault(id);

    Window IPanelHost.Window => this;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.Apply(this);
        WindowEffects.RemoveSystemCaptionButtons(this);
    }

    private void BuildNav()
    {
        Nav.Children.Clear();
        _nav.Clear();
        foreach (var page in PageRegistry.All.Where(p => p.InNav))
        {
            var label = new TextBlock();
            label.SetBinding(TextBlock.TextProperty, new Binding($"[{page.TitleKey}]") { Source = Loc.I, Mode = BindingMode.OneWay });
            var item = new RadioButton
            {
                Style = (Style)FindResource("NavItem"),
                GroupName = "PanelNav",
                Content = label,
                Tag = TryFindResource(page.IconKey) as Geometry,
            };
            var id = page.Id;
            item.Checked += (_, _) =>
            {
                if (!_navigating) Navigate(id, null);
            };
            if (page.Id == PageIds.Settings) item.Margin = new Thickness(0, 10, 0, 1);
            _nav[page.Id] = item;
            Nav.Children.Add(item);
        }
    }

    /// <summary>Shows a page (created on first use); unknown ids fall back to the first page.</summary>
    public void Navigate(string pageId, object? parameter)
    {
        var info = PageRegistry.Find(pageId) ?? PageRegistry.All.FirstOrDefault();
        if (info is null) return;

        if (_current is { } old && old != info.Id && _pages.TryGetValue(old, out var oldPage) && oldPage is IPage op)
            SafeCall(() => op.OnNavigatedFrom(), "leave " + old);

        if (!_pages.TryGetValue(info.Id, out var element))
        {
            try
            {
                element = info.Create(_context);
            }
            catch (Exception ex)
            {
                Log.Error($"panel: page '{info.Id}' failed to load", ex);
                element = new PlaceholderPage(info);
            }
            _pages[info.Id] = element;
        }

        var changed = _current != info.Id;
        _current = info.Id;
        if (!ReferenceEquals(PageHost.Content, element)) PageHost.Content = element;
        _navigating = true;
        foreach (var (id, item) in _nav) item.IsChecked = id == info.Id; // a page outside the rail unchecks it
        _navigating = false;
        if (element is IPage p) SafeCall(() => p.OnNavigatedTo(parameter), "enter " + info.Id);
        if (changed && IsVisible) Motion.SlideIn(element, fromX: 18, ms: 280);
    }

    public void Toast(string text, ToastKind kind = ToastKind.Info, string? actionText = null, Action? action = null)
        => Toasts.Show(text, kind, actionText, action);

    void IPanelHost.Toast(string text, ToastKind kind, string? actionText, Action? action) => Toast(text, kind, actionText, action);

    private static void SafeCall(Action a, string what)
    {
        try
        {
            a();
        }
        catch (Exception ex)
        {
            Log.Error("panel: " + what, ex);
        }
    }

    private void UpdateStatus()
    {
        var s = SettingsStore.I?.Current;
        var recording = s?.Recording.Enabled == true;
        StatusText.Text = recording ? Loc.Plural("flyout.recording", s!.Recording.SensorIds.Count) : Loc.T("flyout.liveOnly");
        StatusIcon.SetResourceReference(Controls.Icon.ForegroundProperty, recording ? "Brush.Danger" : "Brush.TextFaint");
    }

    private void OnStateChanged()
    {
        // a maximized WindowChrome window reaches past the screen by its resize border
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxIcon.SetResourceReference(Controls.Icon.DataProperty, WindowState == WindowState.Maximized ? "Icon.Window.Restore" : "Icon.Window.Maximize");
        MaxButton.ToolTip = Loc.T(WindowState == WindowState.Maximized ? "window.restore" : "window.maximize");
        if (WindowState == WindowState.Minimized) Toasts.Clear();
    }

    private void OnLanguageChanged()
    {
        Language = Loc.I.XmlLang;
        VersionText.Text = Loc.T("about.version", AppEnv.ShortVersion);
        UpdateStatus();
    }

    private void OnThemeChanged() => WindowEffects.Apply(this);

    private void OnSettingsChanged(string section)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateStatus);
            return;
        }
        UpdateStatus();
    }

    private void Unhook()
    {
        Loc.LanguageChanged -= OnLanguageChanged;
        ThemeManager.Changed -= OnThemeChanged;
        if (SettingsStore.I is { } s) s.Changed -= OnSettingsChanged;
    }

    /// <summary>Pages leave, disposable ones are disposed, nothing stays referenced.</summary>
    private void Release()
    {
        Unhook();
        if (_current is { } cur && _pages.TryGetValue(cur, out var page) && page is IPage p) SafeCall(p.OnNavigatedFrom, "leave " + cur);
        foreach (var (id, element) in _pages)
            if (element is IDisposable d) SafeCall(d.Dispose, "dispose " + id);
        _pages.Clear();
        _current = null;
        PageHost.Content = null;
        Toasts.Clear();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
