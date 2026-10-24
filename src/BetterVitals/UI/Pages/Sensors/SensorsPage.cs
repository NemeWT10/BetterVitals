using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BetterVitals.Core.Catalog;
using BetterVitals.Core.History;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.Services;
using BetterVitals.UI.Dev;
using BetterVitals.UI.Pages.Overview;
using BetterVitals.UI.Pages.Shared;
using BetterVitals.UI.Panel;

namespace BetterVitals.UI.Pages.Sensors;

/// <summary>
/// The control center (PROJEKT §8 "Konfiguracja: co jest zbierane i co wyświetlane"): the "Record history" master switch
/// with a summary of what it costs, search / type chips / one-click presets / "only selected", and every sensor grouped
/// by hardware in a virtualized list with live values and the Historia / Nakładka switches. Every change applies at
/// once with an Undo toast. Live work only while shown: a 1 s demand for the rows on screen, ≤ 1 Hz refresh.
/// </summary>
public sealed class SensorsPage : UserControl, IPage, IDisposable
{
    private const string Consumer = "sensors";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly ISensorHub _hub;
    private readonly IHistoryQuery? _history;
    private readonly SensorActions _actions;
    private readonly LiveTicker _ticker;
    private readonly ListBox _list;
    private readonly CheckBox _master;
    private readonly TextBlock _summary;
    private readonly Border _liveOnly;
    private readonly Border _recordTile;
    private readonly Border _recordDot;
    private readonly TextBox _search;
    private readonly WrapPanel _chips;
    private readonly ToggleButton _onlySelected;
    private readonly Border _empty;
    private readonly DispatcherTimer _searchDelay;
    private IDemand? _demand;
    private List<string> _demanded = [];
    private object? _inventory;
    private int _sensorCount;
    private long? _bytesOnDisk;
    private double? _bytesPerSample;
    private bool _shown;
    private string _language = Loc.I.Language;
    private bool _syncingMaster;

    public SensorsPage(PageContext ctx) : this(ctx.Hub, SensorActions.For(ctx), ctx.History ?? AppServices.History)
    {
    }

    internal SensorsPage(ISensorHub hub, SensorActions actions, IHistoryQuery? history)
    {
        _hub = hub;
        _history = history;
        _actions = actions;
        Model = new SensorsListModel(hub, actions);
        _inventory = hub.Hardware;
        _sensorCount = hub.Sensors.Count;
        _ticker = new LiveTicker(hub, Dispatcher, Apply, OnInventoryChanged);
        _searchDelay = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(160) };
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            Model.Search = _search!.Text;
        };
        Resources.MergedDictionaries.Add(Ui.Theme);

        var root = new Grid { Margin = PageLayout.Margin };
        for (var i = 0; i < 5; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions[4].Height = new GridLength(1, GridUnitType.Star);

        // ── title ──
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        var title = new TextBlock();
        title.SetResourceReference(StyleProperty, "PageTitle");
        title.SetBinding(TextBlock.TextProperty, Ui.Tr("nav.sensors"));
        head.Children.Add(title);
        var subtitle = new TextBlock();
        subtitle.SetResourceReference(StyleProperty, "PageSubtitle");
        subtitle.SetBinding(TextBlock.TextProperty, Ui.Tr("sensors.subtitle"));
        head.Children.Add(subtitle);
        root.Children.Add(head);

        // ── status card ──
        (var status, _master, _summary, _liveOnly, _recordTile, _recordDot) = BuildStatus();
        Grid.SetRow(status, 1);
        root.Children.Add(status);

        // ── toolbar ──
        (var toolbar, _search, _onlySelected) = BuildToolbar();
        Grid.SetRow(toolbar, 2);
        root.Children.Add(toolbar);
        _chips = new WrapPanel { Margin = new Thickness(0, 10, 0, 10) };
        var chipsRow = new Grid();
        chipsRow.Children.Add(_chips);
        Grid.SetRow(chipsRow, 3);
        root.Children.Add(chipsRow);

        // ── list ──
        var listCard = new Border { Padding = new Thickness(8, 6, 8, 4), Margin = new Thickness(0) };
        listCard.SetResourceReference(StyleProperty, "Card");
        var listGrid = new Grid();
        listGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var columns = new SensorColumnsHeader(SensorRowLayout.Live, "sensors.col.sensor") { Margin = new Thickness(0, 0, 10, 0) };
        listGrid.Children.Add(columns);
        _list = new ListBox
        {
            ItemContainerStyle = Ui.Style("Bv.PlainItem"),
            ItemTemplateSelector = new RowTemplates(),
            Margin = new Thickness(0, 2, 0, 0),
        };
        _list.SetResourceReference(StyleProperty, "BareList");
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(_list, ScrollUnit.Pixel);
        ScrollViewer.SetCanContentScroll(_list, true);
        Grid.SetRow(_list, 1);
        listGrid.Children.Add(_list);
        _empty = BuildEmpty();
        Grid.SetRow(_empty, 1);
        listGrid.Children.Add(_empty);
        listCard.Child = listGrid;
        Grid.SetRow(listCard, 4);
        root.Children.Add(listCard);

        // the content keeps a readable width on wide windows (like PageLayout.Scroll)
        var page = new Grid();
        page.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 1080 + PageLayout.Margin.Left + PageLayout.Margin.Right });
        page.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) });
        page.Children.Add(root);
        Content = page;

        Model.RowShown = OnRowShown;
        Model.ItemsReplaced += OnItemsReplaced;
        OnItemsReplaced();
        BuildChips();
        Motion.WhileVisible(_recordDot, StartPulse, StopPulse);
        UpdateStatus();
        PrefillPreview();
        Apply();
    }

    public SensorsListModel Model { get; }

    internal ListBox List => _list;

    internal string SummaryText => _summary.Text;

    internal bool LiveOnlyVisible => _liveOnly.Visibility == Visibility.Visible;

    internal IReadOnlyList<string> DemandedIds => _demanded;

    public void OnNavigatedTo(object? parameter)
    {
        _shown = true;
        Loc.LanguageChanged -= OnLanguageChanged;
        Loc.LanguageChanged += OnLanguageChanged;
        if (_language != Loc.I.Language) OnLanguageChanged();
        if (!ReferenceEquals(_inventory, _hub.Hardware) || _sensorCount != _hub.Sensors.Count) OnInventoryChanged();
        if (SettingsStore.I is { } store)
        {
            store.Changed -= OnSettingsChanged;
            store.Changed += OnSettingsChanged;
            Model.Sync(store.Current);
        }
        UpdateStatus();
        RefreshDiskUse();
        _demand ??= _hub.Demand(Consumer, _demanded, Interval);
        _ticker.Start();
    }

    public void OnNavigatedFrom()
    {
        _shown = false;
        Loc.LanguageChanged -= OnLanguageChanged;
        _ticker.Stop();
        _demand?.Dispose();
        _demand = null;
        _searchDelay.Stop();
        if (SettingsStore.I is { } store) store.Changed -= OnSettingsChanged;
    }

    public void Dispose() => OnNavigatedFrom();

    // ───────────── live ─────────────

    internal void Apply()
    {
        var unit = SettingsStore.I?.Current.General.TemperatureUnit ?? TemperatureUnit.Celsius;
        Model.Refresh(_hub.Latest, _hub.Stats, unit);
        UpdateDemand();
    }

    private bool _demandPosted;

    /// <summary>A row scrolled in: its values now (from the latest frame), and the demand follows once per layout pass.</summary>
    private void OnRowShown(SensorRowModel r)
    {
        r.Refresh(_hub.Latest, _hub.Stats, SettingsStore.I?.Current.General.TemperatureUnit ?? TemperatureUnit.Celsius);
        if (!_shown || _demandPosted) return;
        _demandPosted = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _demandPosted = false;
            if (_shown) UpdateDemand();
        });
    }

    /// <summary>The demand follows the rows on screen (scrolling, filters, collapsing); updated in place.</summary>
    private void UpdateDemand()
    {
        var ids = Model.ShownIds();
        if (ids.SequenceEqual(_demanded, StringComparer.Ordinal)) return;
        _demanded = ids;
        _demand?.Update(ids, Interval);
    }

    private void OnInventoryChanged()
    {
        _inventory = _hub.Hardware;
        _sensorCount = _hub.Sensors.Count;
        Model.Rebuild();
        BuildChips();
        UpdateStatus();
        PrefillPreview();
        Apply();
    }

    private void OnItemsReplaced()
    {
        _list.ItemsSource = Model.Items;
        // chips follow the model (filters cleared, kinds gone after an inventory change)
        foreach (var chip in _chips.Children.OfType<ToggleButton>())
            if (chip.Tag is KindGroup k) chip.IsChecked = Model.Kinds.Contains(k);
        var none = Model.Items.Count == 0;
        _empty.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        _list.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
        // freshly realized rows get values without waiting for the next tick
        if (_shown) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Apply));
    }

    private void PrefillPreview()
    {
        if (_hub is not PreviewHub preview) return;
        foreach (var r in Model.AllRows)
            if (r.Series.Count == 0) r.Series.PushRange(preview.Recent(r.Sensor.Index, 40));
    }

    private void OnSettingsChanged(string section)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnSettingsChanged(section));
            return;
        }
        if (SettingsStore.I is not { } store) return;
        if (section is "recording" or "overlay" or "flyout") Model.Sync(store.Current);
        if (section == "recording")
        {
            UpdateStatus();
            RefreshDiskUse();
        }
        if (section == "general") Apply();
    }

    private void OnLanguageChanged()
    {
        _language = Loc.I.Language;
        Model.Relabel();
        BuildChips();
        UpdateStatus();
        Apply();
    }

    // ───────────── status card ─────────────

    /// <summary>"42 czujniki · ~1,7 MB/dzień · co 2 s · zajęte 61 MB" (the disk part only when history is attached).</summary>
    internal static string Summary(RecordingSettings r, ISensorHub hub, long? bytesOnDisk, double? bytesPerSample = null)
    {
        var n = RecordingPolicy.CountPresent(r, hub);
        var parts = new List<string> { Loc.Plural("sensors.count", n) };
        if (r.Enabled)
        {
            var perDay = RecordingPolicy.EstimateBytesPerDay(n, r.IntervalSeconds, bytesPerSample);
            parts.Add(Loc.T("sensors.status.perDay", Ui.Bytes(perDay, Loc.I.Culture)));
            parts.Add(Loc.T("sensors.status.every", r.IntervalSeconds));
        }
        else
        {
            parts.Add(Loc.T("sensors.status.notRecorded"));
        }
        if (bytesOnDisk is { } b) parts.Add(Loc.T("sensors.status.onDisk", Ui.Bytes(b, Loc.I.Culture)));
        return string.Join(" · ", parts);
    }

    private void UpdateStatus()
    {
        var r = SettingsStore.I?.Current.Recording ?? new RecordingSettings();
        _syncingMaster = true;
        _master.IsChecked = r.Enabled;
        _syncingMaster = false;
        _summary.Text = Summary(r, _hub, _bytesOnDisk, _bytesPerSample);
        _liveOnly.Visibility = r.Enabled ? Visibility.Collapsed : Visibility.Visible;
        _recordTile.SetResourceReference(Border.BackgroundProperty, r.Enabled ? "Brush.DangerSoft" : "Brush.SurfaceHover");
        _recordDot.SetResourceReference(Border.BackgroundProperty, r.Enabled ? "Brush.Danger" : "Brush.TextFaint");
        _recordDot.Opacity = 1;
        if (_recordDot.IsVisible && r.Enabled) StartPulse();
        else StopPulse();
    }

    /// <summary>Disk use comes from the history store off the UI thread (it may touch the disk).</summary>
    private void RefreshDiskUse()
    {
        if (_history is not { } h) return;
        var dispatcher = Dispatcher;
        Task.Run(() =>
        {
            HistoryUsage? usage;
            try
            {
                usage = h.GetUsage();
            }
            catch
            {
                usage = null;
            }
            dispatcher.BeginInvoke(() =>
            {
                _bytesOnDisk = usage?.BytesOnDisk;
                _bytesPerSample = usage?.BytesPerValue;
                UpdateStatus();
            });
        });
    }

    private void StartPulse()
    {
        if (!Motion.I.Enabled || SettingsStore.I?.Current.Recording.Enabled != true) return;
        Pulse.Start();
    }

    private void StopPulse() => Pulse.Stop();

    /// <summary>The record dot's breathing (one clock, really stopped when the dot hides).</summary>
    private Motion.Loop? _pulseLoop;

    private Motion.Loop Pulse => _pulseLoop ??= new Motion.Loop(_recordDot, OpacityProperty,
        () => Motion.Throttled(new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(1100))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = Motion.EaseOut,
        }));

    /// <summary>The record dot is breathing (tests).</summary>
    internal bool Pulsing => _pulseLoop?.IsRunning == true;

    private (Border Card, CheckBox Master, TextBlock Summary, Border LiveOnly, Border Tile, Border Dot) BuildStatus()
    {
        var card = new Border { Padding = new Thickness(16, 14, 18, 14) };
        card.SetResourceReference(StyleProperty, "Card");
        var stack = new StackPanel();
        var dock = new DockPanel();

        var master = new CheckBox { Margin = new Thickness(16, 0, 0, 0) };
        master.SetResourceReference(StyleProperty, "Switch");
        master.Click += (_, _) =>
        {
            if (!_syncingMaster) _actions.SetRecordingEnabled(master.IsChecked == true);
        };
        DockPanel.SetDock(master, Dock.Right);
        dock.Children.Add(master);

        var tile = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        var dot = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7) };
        var ring = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1.6) };
        ring.SetResourceReference(Border.BorderBrushProperty, "Brush.Danger");
        var tileContent = new Grid();
        tileContent.Children.Add(ring);
        tileContent.Children.Add(dot);
        tile.Child = tileContent;
        DockPanel.SetDock(tile, Dock.Left);
        dock.Children.Add(tile);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(Ui.Text(14.5, "Brush.Text", FontWeights.SemiBold, "sensors.status.title"));
        var link = new Button { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        link.SetResourceReference(StyleProperty, "FlatButton");
        var linkContent = new StackPanel { Orientation = Orientation.Horizontal };
        linkContent.Children.Add(Ui.Text(12, "Brush.AccentText", FontWeights.SemiBold, "sensors.status.settings"));
        linkContent.Children.Add(Ui.Glyph("Icon.ChevronRight", 12, "Brush.AccentText"));
        link.Content = linkContent;
        link.Click += (_, _) => _actions.Navigate(PageIds.Settings, SettingsLinks.Recording);
        line.Children.Add(link);
        texts.Children.Add(line);
        var summary = Ui.Number(12.5, "Brush.TextDim");
        summary.Margin = new Thickness(0, 2, 0, 0);
        summary.TextTrimming = TextTrimming.CharacterEllipsis;
        texts.Children.Add(summary);
        dock.Children.Add(texts);
        stack.Children.Add(dock);

        var liveOnly = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 12, 0, 0) };
        liveOnly.SetResourceReference(Border.BackgroundProperty, "Brush.InfoSoft");
        var note = new DockPanel();
        var info = Ui.Glyph("Icon.Info", 16, "Brush.Info");
        info.VerticalAlignment = VerticalAlignment.Top;
        info.Margin = new Thickness(0, 1, 10, 0);
        DockPanel.SetDock(info, Dock.Left);
        note.Children.Add(info);
        var noteText = new StackPanel();
        noteText.Children.Add(Ui.Text(12.5, "Brush.Text", FontWeights.SemiBold, "sensors.liveOnly.title"));
        var body = Ui.Text(12, "Brush.TextDim", null, "sensors.liveOnly.body");
        body.TextWrapping = TextWrapping.Wrap;
        body.Margin = new Thickness(0, 2, 0, 0);
        noteText.Children.Add(body);
        note.Children.Add(noteText);
        liveOnly.Child = note;
        stack.Children.Add(liveOnly);

        card.Child = stack;
        return (card, master, summary, liveOnly, tile, dot);
    }

    // ───────────── toolbar ─────────────

    private (FrameworkElement Bar, TextBox Search, ToggleButton OnlySelected) BuildToolbar()
    {
        var bar = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };

        var only = new ToggleButton { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        only.SetResourceReference(StyleProperty, "ChipToggle");
        var onlyContent = new StackPanel { Orientation = Orientation.Horizontal };
        onlyContent.Children.Add(Ui.Glyph("Icon.Check", 13));
        var onlyText = Ui.Text(12.5, "Brush.Text", null, "sensors.onlySelected");
        onlyText.Margin = new Thickness(6, 0, 0, 0);
        onlyText.ClearValue(TextBlock.ForegroundProperty);
        onlyContent.Children.Add(onlyText);
        only.Content = onlyContent;
        only.Click += (_, _) => Model.OnlySelected = only.IsChecked == true;
        DockPanel.SetDock(only, Dock.Right);
        bar.Children.Add(only);

        var presets = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        var presetLabel = Ui.Text(12, "Brush.TextDim", FontWeights.SemiBold, "sensors.presets");
        presetLabel.Margin = new Thickness(0, 0, 8, 0);
        presets.Children.Add(presetLabel);
        foreach (var p in Enum.GetValues<RecordingPreset>())
        {
            var b = new Button { Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(11, 6, 11, 6) };
            b.SetResourceReference(StyleProperty, "SecondaryButton");
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (p == RecordingPreset.Key)
            {
                var star = Ui.Glyph("Icon.Star", 13, "Brush.AccentText");
                star.Margin = new Thickness(0, 0, 6, 0);
                content.Children.Add(star);
            }
            var t = Ui.Text(12.5, "Brush.Text", null, "sensors.preset." + p);
            content.Children.Add(t);
            b.Content = content;
            b.SetBinding(ToolTipProperty, Ui.Tr("sensors.preset." + p + ".tip"));
            var preset = p;
            b.Click += (_, _) => _actions.ApplyPreset(preset, _hub);
            presets.Children.Add(b);
        }
        DockPanel.SetDock(presets, Dock.Right);
        bar.Children.Add(presets);

        var search = new TextBox { MinWidth = 200, VerticalAlignment = VerticalAlignment.Center };
        search.SetResourceReference(StyleProperty, "SearchBox");
        search.SetBinding(TagProperty, Ui.Tr("sensors.search"));
        search.TextChanged += (_, _) =>
        {
            _searchDelay.Stop();
            _searchDelay.Start();
        };
        bar.Children.Add(search);
        return (bar, search, only);
    }

    private void BuildChips()
    {
        _chips.Children.Clear();
        foreach (var kind in Model.AvailableKinds)
        {
            var chip = new ToggleButton { Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 5, 11, 5), IsChecked = Model.Kinds.Contains(kind), Tag = kind };
            chip.SetResourceReference(StyleProperty, "ChipToggle");
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(Ui.Glyph(SensorGroups.IconKey(kind), 13));
            var label = Ui.Text(12, "Brush.Text");
            label.ClearValue(TextBlock.ForegroundProperty);
            label.Margin = new Thickness(6, 0, 0, 0);
            label.SetBinding(TextBlock.TextProperty, Ui.Tr(SensorGroups.LabelKey(kind)));
            content.Children.Add(label);
            chip.Content = content;
            var k = kind;
            chip.Click += (_, _) => Model.ToggleKind(k);
            _chips.Children.Add(chip);
        }
    }

    private Border BuildEmpty()
    {
        var box = new Border { Padding = new Thickness(20, 34, 20, 34), Visibility = Visibility.Collapsed };
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var icon = Ui.Glyph("Icon.Search", 26, "Brush.TextFaint");
        stack.Children.Add(icon);
        var head = Ui.Text(14, "Brush.Text", FontWeights.SemiBold, "sensors.empty.title");
        head.HorizontalAlignment = HorizontalAlignment.Center;
        head.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(head);
        var clear = new Button { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        clear.SetResourceReference(StyleProperty, "SecondaryButton");
        clear.SetBinding(ContentProperty, Ui.Tr("sensors.empty.clear"));
        clear.Click += (_, _) => ClearFilters();
        stack.Children.Add(clear);
        box.Child = stack;
        return box;
    }

    internal void ClearFilters()
    {
        _searchDelay.Stop();
        _search.Text = "";
        _searchDelay.Stop();
        _onlySelected.IsChecked = false;
        Model.ClearFilters();
        BuildChips();
    }

    /// <summary>Header rows and sensor rows of the flat list.</summary>
    private sealed class RowTemplates : DataTemplateSelector
    {
        private readonly DataTemplate Row = new() { VisualTree = new FrameworkElementFactory(typeof(LiveSensorRowView)) };
        private readonly DataTemplate Header = new() { VisualTree = new FrameworkElementFactory(typeof(SectionHeaderView)) };

        public override DataTemplate SelectTemplate(object item, DependencyObject container) => item is SectionModel ? Header : Row;
    }
}

internal static class SensorsPageRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        if (!PlatformCommands.IsHelperProcess) RegisterNow();
    }

    // never even compiled in a helper process (--install…): it must not load WPF
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterNow()
        => PageRegistry.Register(new PageInfo(PageIds.Sensors, "nav.sensors", "Icon.List", 30, ctx => new SensorsPage(ctx)) { Priority = 1 });
}
