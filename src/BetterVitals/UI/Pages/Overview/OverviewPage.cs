using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using BetterVitals.Core.Catalog;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.UI.Dev;
using BetterVitals.UI.Pages.Shared;
using BetterVitals.UI.Panel;
using BetterVitals.Services;

namespace BetterVitals.UI.Pages.Overview;

/// <summary>Settings page sections the Overview / Sensors pages link to (Navigate parameter; unknown ids open the page top).</summary>
public static class SettingsLinks
{
    public const string Permissions = "permissions";
    public const string Recording = "recording";
}

/// <summary>
/// The Overview (PROJEKT §8 "Przegląd"): one card per top-level hardware in a responsive grid — big key values with
/// heat colors and sparklines; a click expands a card to every sensor with Now / Min / Max / Avg since start.
/// Live work only while shown: one 1 s demand (key values of collapsed cards, all sensors of expanded ones), a coalesced
/// ≤ 1 Hz refresh, and a re-layout when the inventory changes. Expansion is remembered while the panel is open.
/// </summary>
public sealed class OverviewPage : UserControl, IPage, IDisposable
{
    private const string Consumer = "overview";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly PageContext? _ctx;
    private readonly ISensorHub _hub;
    private readonly SensorActions _actions;
    private readonly LiveTicker _ticker;
    private readonly CardGrid _grid = new();
    private readonly ScrollViewer _scroll;
    private readonly Border _banner;
    private readonly TextBlock _bannerText;
    private readonly Border _empty;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly List<HardwareCard> _cards = [];
    private IDemand? _demand;
    private object? _inventory;
    private int _sensorCount = -1;
    private int _applies;
    private bool _shown;
    private string _language = Loc.I.Language;

    public OverviewPage(PageContext ctx) : this(ctx.Hub, SensorActions.For(ctx), ctx)
    {
    }

    internal OverviewPage(ISensorHub hub, SensorActions actions, PageContext? ctx = null)
    {
        _ctx = ctx;
        _hub = hub;
        _actions = actions;
        _ticker = new LiveTicker(hub, Dispatcher, Apply, OnInventoryChanged);
        Resources.MergedDictionaries.Add(Ui.Theme);

        var root = new StackPanel();
        var title = new TextBlock();
        title.SetResourceReference(StyleProperty, "PageTitle");
        title.SetBinding(TextBlock.TextProperty, Ui.Tr("nav.overview"));
        root.Children.Add(title);
        var subtitle = new TextBlock { Margin = new Thickness(1, 4, 0, 20) };
        subtitle.SetResourceReference(StyleProperty, "PageSubtitle");
        subtitle.SetBinding(TextBlock.TextProperty, Ui.Tr("overview.subtitle"));
        root.Children.Add(subtitle);

        (_banner, _bannerText) = BuildBanner();
        root.Children.Add(_banner);
        root.Children.Add(_grid);
        _empty = BuildEmpty();
        root.Children.Add(_empty);

        _scroll = PageLayout.Scroll(root, 1080);
        _scroll.ScrollChanged += OnScrollChanged;
        Content = _scroll;
        Build();
    }

    internal IReadOnlyList<HardwareCard> Cards => _cards;

    internal bool BannerVisible => _banner.Visibility == Visibility.Visible;

    internal string BannerText => _bannerText.Text;

    /// <summary>The ids the page's demand asks for right now.</summary>
    internal IReadOnlyList<string> DemandIds => _cards.SelectMany(c => c.DemandIds).Distinct(StringComparer.Ordinal).ToList();

    public void OnNavigatedTo(object? parameter)
    {
        _shown = true;
        Loc.LanguageChanged -= OnLanguageChanged;
        Loc.LanguageChanged += OnLanguageChanged;
        if (_language != Loc.I.Language) OnLanguageChanged();
        if (!ReferenceEquals(_inventory, _hub.Hardware) || _sensorCount != _hub.Sensors.Count) Build();
        if (SettingsStore.I is { } store)
        {
            store.Changed -= OnSettingsChanged;
            store.Changed += OnSettingsChanged;
            SyncSettings();
        }
        _demand ??= _hub.Demand(Consumer, DemandIds, Interval);
        _ticker.Start();
    }

    public void OnNavigatedFrom()
    {
        _shown = false;
        Loc.LanguageChanged -= OnLanguageChanged;
        _ticker.Stop();
        _demand?.Dispose();
        _demand = null;
        if (SettingsStore.I is { } store) store.Changed -= OnSettingsChanged;
    }

    public void Dispose() => OnNavigatedFrom();

    /// <summary>Expands / collapses a card (as a click does).</summary>
    internal void Toggle(HardwareCard card)
    {
        var expand = !card.IsExpanded;
        if (expand) _expanded.Add(card.Group.Id);
        else _expanded.Remove(card.Group.Id);
        card.SetExpanded(expand);
        if (expand && SettingsStore.I is { } store) card.Sync(store.Current);
        _demand?.Update(DemandIds, Interval);
        if (_shown) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(Apply));
    }

    // ───────────── building ─────────────

    private void Build()
    {
        _inventory = _hub.Hardware;
        _sensorCount = _hub.Sensors.Count;
        _cards.Clear();
        _grid.Children.Clear();
        var groups = HardwareTree.Build(_hub);
        var items = FriendlyCatalog.Resolve(_hub.Hardware, _hub.Sensors);
        foreach (var g in groups)
        {
            var card = new HardwareCard(g, KeyValuePicker.Pick(g, items), _actions, _expanded.Contains(g.Id), Toggle) { RowShown = OnRowShown };
            _cards.Add(card);
            _grid.Children.Add(card);
        }
        _empty.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PrefillPreview();
        _demand?.Update(DemandIds, Interval);
        Apply();
    }

    private void OnInventoryChanged()
    {
        Build();
        if (SettingsStore.I is not null) SyncSettings();
    }

    /// <summary>Snapshots run on the preview hub (no live ticks): show its recent values in the sparklines.</summary>
    private void PrefillPreview()
    {
        if (_hub is not PreviewHub preview) return;
        foreach (var c in _cards)
            foreach (var t in c.Tiles)
                if (t.Series.Count == 0) t.Series.PushRange(preview.Recent(t.Key.Sensor.Index, 48));
    }

    // ───────────── live ─────────────

    internal void Apply()
    {
        var frame = _hub.Latest;
        var stats = _hub.Stats;
        var unit = SettingsStore.I?.Current.General.TemperatureUnit ?? TemperatureUnit.Celsius;
        // statuses change rarely (driver, anti-cheat pause): every 5th refresh is plenty
        var statuses = _hub.SourceStatuses;
        if (_applies++ % 5 == 0) UpdateBanner(statuses);
        var viewport = Viewport();
        foreach (var c in _cards) c.Refresh(frame, stats, unit, statuses, viewport, _scroll);
    }

    /// <summary>
    /// The visible part of the page plus half a screen above and below (rows there are refreshed each tick), or null
    /// when the page is not on screen (tests render it without a window: every row then).
    /// </summary>
    private Rect? Viewport()
    {
        if (!_scroll.IsVisible || _scroll.ActualHeight <= 0) return null;
        var margin = _scroll.ActualHeight / 2;
        return new Rect(0, -margin, Math.Max(1, _scroll.ActualWidth), _scroll.ActualHeight + 2 * margin);
    }

    private static TemperatureUnit Unit => SettingsStore.I?.Current.General.TemperatureUnit ?? TemperatureUnit.Celsius;

    /// <summary>A row appeared (a card expanded): its values now, not after the next tick.</summary>
    private void OnRowShown(SensorRowModel row) => row.Refresh(_hub.Latest, _hub.Stats, Unit);

    /// <summary>Scrolling brings rows in that were not refreshed while away: fill them before this frame renders.</summary>
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_shown || (e.VerticalChange == 0 && e.ViewportHeightChange == 0 && e.ExtentHeightChange == 0)) return;
        var viewport = Viewport();
        if (viewport is null) return;
        var frame = _hub.Latest;
        var stats = _hub.Stats;
        var unit = Unit;
        foreach (var c in _cards) c.RefreshRows(frame, stats, unit, viewport, _scroll);
    }

    private void OnSettingsChanged(string section)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnSettingsChanged(section));
            return;
        }
        if (section is "recording" or "overlay" or "flyout") SyncSettings();
        else if (section == "general") Apply();
    }

    private void SyncSettings()
    {
        if (SettingsStore.I is not { } store) return;
        foreach (var c in _cards) c.Sync(store.Current);
    }

    private void OnLanguageChanged()
    {
        _language = Loc.I.Language;
        foreach (var c in _cards) c.Relabel();
        UpdateBanner(_hub.SourceStatuses);
        if (_shown) Apply();
    }

    // ───────────── banner, empty state ─────────────

    /// <summary>The banner text for the sources' states, or null when every source is fine.</summary>
    internal static string? BannerFor(IReadOnlyList<(string Source, SourceStatus Status)> statuses)
    {
        string? message = null;
        var problem = false;
        foreach (var (_, status) in statuses)
        {
            if (status.State is not (SourceState.Limited or SourceState.Unavailable)) continue;
            problem = true;
            if (message is null && status.MessageKey is { Length: > 0 } k && Loc.Has(k)) message = Loc.T(k);
        }
        if (!problem) return null;
        return message is null ? Loc.T("overview.banner.limited") : Loc.T("overview.banner.limitedWith", message);
    }

    private void UpdateBanner(IReadOnlyList<(string Source, SourceStatus Status)> statuses)
    {
        var text = BannerFor(statuses);
        _banner.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        if (text is not null) _bannerText.Text = text;
    }

    private (Border, TextBlock) BuildBanner()
    {
        var banner = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10, 10, 10), Margin = new Thickness(0, 0, 0, 16),
            BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed,
        };
        banner.SetResourceReference(Border.BackgroundProperty, "Brush.WarningSoft");
        banner.SetResourceReference(Border.BorderBrushProperty, "Brush.Warning");
        var dock = new DockPanel();
        var go = new Button { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var goText = new StackPanel { Orientation = Orientation.Horizontal };
        var goLabel = Ui.Text(12.5, "Brush.AccentText", FontWeights.SemiBold, "overview.banner.action");
        goText.Children.Add(goLabel);
        goText.Children.Add(Ui.Glyph("Icon.ChevronRight", 13, "Brush.AccentText"));
        go.SetResourceReference(StyleProperty, "FlatButton");
        go.Content = goText;
        go.Click += (_, _) => _actions.Navigate(PageIds.Settings, SettingsLinks.Permissions);
        DockPanel.SetDock(go, Dock.Right);
        dock.Children.Add(go);
        var icon = Ui.Glyph("Icon.Shield", 18, "Brush.Warning");
        icon.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        var text = Ui.Text(12.5, "Brush.Text");
        text.TextWrapping = TextWrapping.Wrap;
        dock.Children.Add(text);
        banner.Child = dock;
        return (banner, text);
    }

    private static Border BuildEmpty()
    {
        var card = new Border { Padding = new Thickness(24, 30, 24, 30), Visibility = Visibility.Collapsed };
        card.SetResourceReference(StyleProperty, "Card");
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var icon = Ui.Glyph("Icon.Pulse", 30, "Brush.AccentText");
        stack.Children.Add(icon);
        var head = Ui.Text(15, "Brush.Text", FontWeights.SemiBold, "overview.empty.title");
        head.Margin = new Thickness(0, 12, 0, 0);
        head.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(head);
        var body = Ui.Text(12.5, "Brush.TextDim", null, "overview.empty.body");
        body.HorizontalAlignment = HorizontalAlignment.Center;
        body.TextAlignment = TextAlignment.Center;
        body.TextWrapping = TextWrapping.Wrap;
        body.MaxWidth = 420;
        body.Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(body);
        card.Child = stack;
        return card;
    }
}

internal static class OverviewPageRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        if (!PlatformCommands.IsHelperProcess) RegisterNow();
    }

    // never even compiled in a helper process (--install…): it must not load WPF
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterNow()
        => PageRegistry.Register(new PageInfo(PageIds.Overview, "nav.overview", "Icon.Dashboard", 10, ctx => new OverviewPage(ctx)) { Priority = 1 });
}
