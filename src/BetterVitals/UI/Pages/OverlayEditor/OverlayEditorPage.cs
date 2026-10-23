using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BetterVitals.Core;
using BetterVitals.Core.Catalog;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.Services;
using BetterVitals.UI.Panel;
using BetterVitals.UI.Themes;
using BetterVitals.UI.Toasts;

namespace BetterVitals.UI.Pages.OverlayEditor;

internal static class OverlayEditorRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        if (!PlatformCommands.IsHelperProcess) RegisterNow();
    }

    // never even compiled in a helper process (--install…): it must not load WPF
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterNow()
        => PageRegistry.Register(new PageInfo(PageIds.Overlay, "nav.overlay", "Icon.Overlay", 40, ctx => new OverlayEditorPage(ctx)) { Priority = 1 });
}

/// <summary>
/// The overlay editor (PROJEKT §7, §8 "Strona Nakładka — dwa poziomy"): on / off, Simple | Advanced (remembered),
/// "Restore defaults"; a live preview of the real renderer next to the settings. Costs nothing while not shown: the
/// 1 Hz tick, the sensor demand and every static subscription live only between OnNavigatedTo and OnNavigatedFrom
/// (and pause while the panel is hidden or minimized).
/// </summary>
public sealed class OverlayEditorPage : UserControl, IPage, IDisposable
{
    private const string Consumer = "overlay-editor";
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(350);

    private readonly PageContext? _page;
    private readonly ISensorHub _hub;
    private readonly SettingsStore _store;
    private readonly OverlayEditorModel _model;
    private readonly PreviewFeed _feed;
    private readonly EditorContext _ctx;
    private readonly OverlayProfileStore _profiles;
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _flush;

    private readonly Grid _root;
    private readonly CheckBox _enabled;
    private readonly TextBlock _enabledText;
    private readonly SegmentBar<bool> _mode;
    private readonly Grid _body;
    private readonly ScrollViewer _leftScroll;
    private readonly ScrollViewer _rightScroll;
    private readonly StackPanel _left;
    private readonly StackPanel _right;
    private readonly Border _previewCard;
    private readonly Border _orderCard;
    private readonly LivePreview _preview;
    private readonly OrderPanel _order;
    private readonly SensorPicker _picker;
    private readonly Canvas _dragLayer = new() { IsHitTestVisible = false };
    private SimpleView? _simple;
    private AdvancedView? _advanced;
    private IDemand? _demand;
    private bool _active;
    private bool _wide = true;
    private bool _prefilled;
    private bool _syncing;
    private int _inventoryVersion;

    public OverlayEditorPage(PageContext context) : this(context, context.Hub, context.Settings, null)
    {
    }

    /// <summary>For tests and snapshots: any hub and store, no panel.</summary>
    internal OverlayEditorPage(PageContext? context, ISensorHub hub, SettingsStore store, string? profileDir)
    {
        _page = context;
        _hub = hub;
        _store = store;
        _model = new OverlayEditorModel(store);
        _feed = new PreviewFeed(hub);
        _profiles = new OverlayProfileStore(profileDir ?? Path.Combine(string.IsNullOrEmpty(AppEnv.RoamingDir) ? Path.GetTempPath() : AppEnv.RoamingDir, "overlay-profiles"));
        _ctx = new EditorContext
        {
            Model = _model,
            Feed = _feed,
            Defer = Defer,
            Toast = (text, kind, action, run) =>
            {
                if (_page is not null) _page.Toast(text, kind, action, run);
                else Toasts.Add((text, run)); // tests: no panel to show it
            },
            OpenPicker = advanced => _picker!.Open(advanced),
            Colors = new ColorPopover(),
            Owner = () => _page?.Window ?? Window.GetWindow(this),
        };
        _ctx.Friendly = FriendlyCatalog.Resolve(hub.Hardware, hub.Sensors);

        Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/BetterVitals;component/UI/Pages/OverlayEditor/OverlayEditorStyles.xaml", UriKind.Relative)));

        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = Tick };
        _tick.Tick += (_, _) => OnTick();
        _flush = new DispatcherTimer(DispatcherPriority.Background) { Interval = FlushDelay };
        _flush.Tick += (_, _) =>
        {
            _flush.Stop();
            _model.Flush();
        };

        // ── header: title + on / off ──
        var header = new DockPanel { Margin = new Thickness(0, 0, 10, 16) }; // right edge in line with the cards (the scroll bars use the last 10 px)
        var onOff = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16, 6, 0, 0) };
        _enabledText = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        _enabledText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        onOff.Children.Add(_enabledText);
        _enabled = Ui.Switch(_model.Working.Enabled, OnEnabled);
        System.Windows.Automation.AutomationProperties.SetName(_enabled, Loc.T("ovl.enabled"));
        onOff.Children.Add(_enabled);
        DockPanel.SetDock(onOff, Dock.Right);
        header.Children.Add(onOff);
        var titles = new StackPanel();
        titles.Children.Add(Ui.Text("nav.overlay", "PageTitle"));
        titles.Children.Add(Ui.Text("page.overlay.subtitle", "PageSubtitle"));
        header.Children.Add(titles);

        // ── toolbar: mode + restore defaults ──
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 10, 16) };
        var reset = Ui.Button("GhostButton", "ovl.reset", "Icon.Refresh", (_, _) => ResetAll());
        DockPanel.SetDock(reset, Dock.Right);
        toolbar.Children.Add(reset);
        _mode = new SegmentBar<bool>([(false, "ovl.mode.simple"), (true, "ovl.mode.advanced")], _model.Working.Advanced, OnMode);
        var modeRow = new StackPanel { Orientation = Orientation.Horizontal };
        modeRow.Children.Add(_mode);
        var modeHint = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        modeHint.SetResourceReference(StyleProperty, "Hint");
        modeHint.SetBinding(TextBlock.TextProperty, Ui.Tr("ovl.mode.hint"));
        modeRow.Children.Add(modeHint);
        toolbar.Children.Add(modeRow);

        // ── body: settings (scrolls) | preview (scrolls on its own, stays in view) ──
        _left = new StackPanel();
        _leftScroll = new ScrollViewer { Content = _left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Padding = new Thickness(0, 0, 10, 0) };
        _preview = new LivePreview(_ctx);
        _preview.CornerPicked += a => _model.SetCorner(a);
        _preview.Dragged += id => _advanced?.OnWindowDragged(id);
        _previewCard = Ui.Card(_preview);
        _order = new OrderPanel(_ctx);
        _orderCard = Ui.Card(_order);
        _right = new StackPanel();
        _right.Children.Add(_previewCard);
        _right.Children.Add(_orderCard);
        _rightScroll = new ScrollViewer { Content = _right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Padding = new Thickness(0, 0, 10, 0) };
        _body = new Grid();
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(392) });
        Grid.SetColumn(_leftScroll, 0);
        Grid.SetColumn(_rightScroll, 2);
        _body.Children.Add(_leftScroll);
        _body.Children.Add(_rightScroll);

        var content = new Grid { Margin = new Thickness(PageLayout.Margin.Left, PageLayout.Margin.Top, PageLayout.Margin.Right - 10, 0) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(header);
        Grid.SetRow(toolbar, 1);
        content.Children.Add(toolbar);
        Grid.SetRow(_body, 2);
        content.Children.Add(_body);

        _picker = new SensorPicker(_ctx);
        _picker.OpenChanged += _ => UpdateDemand();
        _picker.Added += () => _advanced?.BuildItems();

        // drag ghosts float above everything (also above the picker a sensor is dragged out of)
        System.Windows.Controls.Panel.SetZIndex(_dragLayer, 100);
        _ctx.DragLayer = _dragLayer;
        _ctx.SensorDragEnded = () => _picker.ResumeAfterDrag();

        _root = new Grid();
        _root.Children.Add(content);
        _root.Children.Add(_picker);
        _root.Children.Add(_dragLayer);
        Content = _root;

        _model.Changed += OnModelChanged;
        _model.Reloaded += RebuildMode;
        SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
        IsVisibleChanged += (_, _) => UpdateRunning();
        Language = Loc.I.XmlLang;

        SyncHeader();
        RebuildMode();
    }

    // ───────────── IPage ─────────────

    public void OnNavigatedTo(object? parameter)
    {
        if (_active) return;
        _active = true;
        _store.Changed += OnStoreChanged;
        Loc.LanguageChanged += OnLanguageChanged;
        ThemeManager.Changed += OnThemeChanged;
        _hub.InventoryChanged += OnInventoryChanged;
        AppServices.Overlay.MonitorsChanged += OnMonitorsChanged;
        _model.Reload();
        _ctx.Friendly = FriendlyCatalog.Resolve(_hub.Hardware, _hub.Sensors);
        _preview.SetMonitors(AppServices.Overlay.Monitors);
        Prefill();
        UpdateDemand();
        UpdateRunning();
        _feed.Sample(DemandedIds());
        UpdateValues();
    }

    public void OnNavigatedFrom()
    {
        if (!_active) return;
        _active = false;
        _store.Changed -= OnStoreChanged;
        Loc.LanguageChanged -= OnLanguageChanged;
        ThemeManager.Changed -= OnThemeChanged;
        _hub.InventoryChanged -= OnInventoryChanged;
        AppServices.Overlay.MonitorsChanged -= OnMonitorsChanged;
        _picker.Close();
        _ctx.Colors.Close();
        if (AppServices.Overlay.IsEditingOnScreen) AppServices.Overlay.EndOnScreenEdit(false);
        _flush.Stop();
        _model.Flush();
        UpdateRunning();
        _demand?.Dispose();
        _demand = null;
    }

    public void Dispose()
    {
        OnNavigatedFrom();
        _model.Changed -= OnModelChanged;
        _model.Reloaded -= RebuildMode;
    }

    // ───────────── for tests / snapshots ─────────────

    internal OverlayEditorModel Model => _model;

    internal EditorContext Context => _ctx;

    internal LivePreview Preview => _preview;

    internal SensorPicker Picker => _picker;

    internal SimpleView? Simple => _simple;

    internal AdvancedView? Advanced => _advanced;

    internal OrderPanel Order => _order;

    /// <summary>Toasts shown without a panel (tests): the text and its action (Undo).</summary>
    internal List<(string Text, Action? Action)> Toasts { get; } = [];

    /// <summary>The 1 Hz work, run on demand (tests).</summary>
    internal void TickNow() => OnTick();

    /// <summary>Writes deferred edits now (tests).</summary>
    internal void FlushNow()
    {
        _flush.Stop();
        _model.Flush();
    }

    // ───────────── running only while shown ─────────────

    /// <summary>Tests render the page without a window: treat it as shown.</summary>
    internal bool AssumeVisible { get; set; }

    private bool Shown => IsVisible || AssumeVisible;

    private void UpdateRunning()
    {
        if (_active && Shown)
        {
            if (!_tick.IsEnabled) _tick.Start();
            _preview.Start();
            _advanced?.SetRunning(true);
        }
        else
        {
            _tick.Stop();
            _preview.Stop();
            _advanced?.SetRunning(false);
        }
    }

    private void OnTick()
    {
        if (!Shown) return;
        _feed.Sample(DemandedIds());
        UpdateValues();
        _preview.RequestRender();
    }

    private void UpdateValues()
    {
        _simple?.UpdateValues();
        _advanced?.UpdateValues();
        if (_orderCard.Visibility == Visibility.Visible) _order.UpdateValues();
        _picker.UpdateValues();
    }

    /// <summary>The sensors this page shows: the overlay's, the visible tiles', the open picker's.</summary>
    private HashSet<string> DemandedIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in _model.Working.AllItems()) ids.UnionWith(item.SensorIds);
        if (_simple is not null) ids.UnionWith(_simple.VisibleSensorIds);
        if (_picker.IsOpen) ids.UnionWith(_picker.SensorIds);
        return ids;
    }

    private void UpdateDemand()
    {
        if (!_active) return;
        var ids = DemandedIds();
        if (_demand is null) _demand = _hub.Demand(Consumer, ids, Tick);
        else _demand.Update(ids, Tick);
        _feed.Trim(ids);
    }

    /// <summary>Older values from the dev preview hub (snapshots), so sparklines start filled.</summary>
    private void Prefill()
    {
        if (_prefilled || _hub is not UI.Dev.PreviewHub preview) return;
        _prefilled = true;
        foreach (var id in DemandedIds())
            if (_hub.Find(id) is { Index: >= 0 } s && !_feed.HasHistory(id))
                _feed.Prefill(id, preview.Recent(s.Index, PreviewFeed.HistoryLength));
    }

    /// <summary>Test hook: older values for every sensor of the layout (oldest first).</summary>
    internal void Prefill(Func<SensorDescriptor, IReadOnlyList<float>> history)
    {
        foreach (var id in _hub.Sensors.Select(s => s.Id))
            if (_hub.Find(id) is { } s) _feed.Prefill(id, history(s));
    }

    // ───────────── events ─────────────

    private void OnStoreChanged(string section)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnStoreChanged(section));
            return;
        }
        if (section == "general")
        {
            _preview.RequestRender(); // °C / °F, language
            return;
        }
        _model.OnStoreChanged(section);
    }

    private void OnLanguageChanged()
    {
        Language = Loc.I.XmlLang;
        RebuildMode();
    }

    private void OnThemeChanged()
    {
        _preview.RequestRender();
        if (_advanced is not null) RebuildMode(); // font samples and color buttons follow the theme
    }

    private void OnInventoryChanged()
    {
        var version = Interlocked.Increment(ref _inventoryVersion);
        Dispatcher.BeginInvoke(() =>
        {
            if (version != _inventoryVersion || !_active) return;
            _ctx.Friendly = FriendlyCatalog.Resolve(_hub.Hardware, _hub.Sensors);
            RebuildMode();
            UpdateDemand();
        });
    }

    private void OnMonitorsChanged()
    {
        _preview.SetMonitors(AppServices.Overlay.Monitors);
        _simple?.Rebuild();
        if (_advanced is not null)
        {
            _advanced.BuildMonitors();
            _advanced.Sync();
        }
    }

    private void OnModelChanged()
    {
        SyncHeader();
        _simple?.Sync();
        _advanced?.Sync();
        if (_orderCard.Visibility == Visibility.Visible) _order.Rebuild();
        _picker.Sync();
        _preview.SyncCorners();
        _preview.UpdateMine();
        UpdateDemand();
        _preview.RequestRender();
    }

    private void Defer(Action<OverlayLayout> change)
    {
        _model.Edit(change, deferred: true);
        _flush.Stop();
        _flush.Start();
    }

    // ───────────── header actions ─────────────

    private void SyncHeader()
    {
        _syncing = true;
        try
        {
            var l = _model.Working;
            _enabled.IsChecked = l.Enabled;
            _enabledText.Text = Loc.T(l.Enabled ? "ovl.enabled.on" : "ovl.enabled.off");
            _mode.Set(l.Advanced);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnEnabled(bool on)
    {
        if (_syncing) return;
        _model.Edit(l => l.Enabled = on);
        _page?.Toast(Loc.T(on ? "toast.overlayOn" : "toast.overlayOff"), on ? ToastKind.Success : ToastKind.Info, Loc.T("common.undo"), () => _model.Edit(l => l.Enabled = !on));
    }

    private void OnMode(bool advanced)
    {
        if (_syncing) return;
        _model.SetAdvanced(advanced);
        RebuildMode();
    }

    private void ResetAll()
    {
        var before = LayoutJson.Clone(_model.Working);
        _model.ResetAll(_ctx.Friendly);
        _page?.Toast(Loc.T("ovl.toast.reset"), ToastKind.Success, Loc.T("common.undo"), () => _model.Replace(before));
    }

    // ───────────── layout ─────────────

    private void RebuildMode()
    {
        _picker.Close();
        _left.Children.Clear();
        _simple = null;
        _advanced = null;
        var advanced = _model.Working.Advanced;
        if (advanced)
        {
            _advanced = new AdvancedView(_ctx, _profiles);
            _advanced.ResetRequested += ResetAll;
            _left.Children.Add(_advanced);
            _advanced.SetRunning(_active && Shown);
        }
        else
        {
            _simple = new SimpleView(_ctx);
            _simple.CategoryChanged += () =>
            {
                UpdateDemand();
                _feed.Sample(_simple.VisibleSensorIds);
                _simple.UpdateValues();
            };
            _left.Children.Add(_simple);
        }
        _orderCard.Visibility = advanced ? Visibility.Collapsed : Visibility.Visible;
        _order.Rebuild();
        _preview.CornersVisible = !advanced;
        _preview.DragEnabled = advanced;
        _preview.SyncCorners();
        _preview.UpdateMine();
        SyncHeader();
        UpdateDemand();
        _preview.RequestRender();
        ApplyWidth(ActualWidth, force: true);
    }

    /// <summary>Two columns from ~860 DIP, otherwise one column with the preview on top.</summary>
    private void ApplyWidth(double width, bool force = false)
    {
        if (width <= 0) width = _wide ? 1000 : 600;
        var wide = width >= 860;
        // Advanced mode's layout board needs the width more than the preview does
        var previewWidth = width >= 1180 ? 440 : _model.Working.Advanced ? 360 : 392;
        if (!force && wide == _wide)
        {
            if (wide) _body.ColumnDefinitions[2].Width = new GridLength(previewWidth);
            return;
        }
        _wide = wide;
        _body.Children.Clear();
        _left.Children.Remove(_right);
        _rightScroll.Content = null;
        if (wide)
        {
            _body.ColumnDefinitions[1].Width = new GridLength(20);
            _body.ColumnDefinitions[2].Width = new GridLength(previewWidth);
            _rightScroll.Content = _right;
            Grid.SetColumn(_leftScroll, 0);
            _body.Children.Add(_leftScroll);
            _body.Children.Add(_rightScroll);
        }
        else
        {
            _body.ColumnDefinitions[1].Width = new GridLength(0);
            _body.ColumnDefinitions[2].Width = new GridLength(0);
            _left.Children.Insert(0, _right);
            _body.Children.Add(_leftScroll);
        }
    }
}
