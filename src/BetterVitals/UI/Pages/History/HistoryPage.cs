using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using BetterVitals.Core;
using BetterVitals.Core.History;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.Services;
using BetterVitals.UI.Charts;
using BetterVitals.UI.Controls;
using BetterVitals.UI.Panel;
using BetterVitals.UI.Toasts;

namespace BetterVitals.UI.Pages.History;

internal static class HistoryPageRegistration
{
    [ModuleInitializer]
    internal static void Register()
        => PageRegistry.Register(new PageInfo(PageIds.History, "nav.history", "Icon.History", 20, ctx => new HistoryPage(ctx)) { Priority = 1 });
}

/// <summary>What the page shows in its empty-state card over the chart.</summary>
public enum HistoryEmpty : byte
{
    None,
    PickSensor,
    Collecting,
    NotRecorded,
    NothingRecorded,
    NothingInRange,
}

/// <summary>
/// The History page (PROJEKT §6, §8 "Historia"): range presets, series chips with a searchable "Add sensor" popover,
/// the chart (<see cref="TimeChart"/>) with live follow, exact min / avg / max for the visible range or a drag
/// selection, empty states and the export sheet.
/// <para>Costs nothing while hidden: queries, the hub subscription, the live-buffer watch and the debounce timers
/// start in <see cref="OnNavigatedTo"/> and stop in <see cref="OnNavigatedFrom"/>. Queries run on the thread pool and
/// are cancelled by newer ones; live samples reach the UI thread at most once a second.</para>
/// </summary>
public sealed class HistoryPage : UserControl, IPage, IDisposable
{
    private sealed class Shown
    {
        public required SensorDescriptor D;
        public required ChartSeries Series;
        public HistoryModel.DisplayUnit Display;
        public RangeStats? Stats;
        public bool StatsLoading;
        public ToggleButton? Chip;
    }

    private readonly PageContext _ctx;
    private readonly ISensorHub _hub;
    private readonly IHistoryQuery? _history;
    private readonly bool _preview;
    private readonly List<Shown> _shown = [];
    private readonly TimeChart _chart = new();
    private readonly StatsPanel _stats = new();
    private readonly Grid _layers = new();
    private readonly ScrollViewer _scroll;
    private readonly Grid _body = new();
    private readonly WrapPanel _chips = new();
    private readonly Button _add = new();
    private readonly StackPanel _notes = new();
    private readonly Dictionary<RangePreset, RadioButton> _presetButtons = [];
    private readonly TextBlock _rangeText = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
    private readonly Border _livePill = new() { CornerRadius = new CornerRadius(12), Padding = new Thickness(9, 3, 10, 4), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _toLive = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _export = new();
    private readonly TextBlock _ownScale = new() { FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hint = new() { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _empty = new() { MaxWidth = 400 };
    private readonly Border _emptyCard = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(16), Padding = new Thickness(30, 22, 30, 24), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 24), Visibility = Visibility.Collapsed };
    private readonly Popup _pickerPopup = new();
    private readonly Popup _customPopup = new();
    private SensorPicker? _picker;
    private RangePicker? _rangePicker;
    private ExportSheet? _sheet;

    private RangePreset _preset = RangePreset.Hour1;
    private bool _live = true;
    private bool _active;
    private bool _started;
    private bool _userTouchedSeries;
    private bool _fadeNext;
    private long _lastStatsAt;
    private CancellationTokenSource? _queryCts;
    private CancellationTokenSource? _statsCts;
    private DispatcherTimer? _queryTimer;
    private DispatcherTimer? _statsTimer;
    private IDisposable? _watch;
    private IDemand? _demand;
    private double _queriedWidth;

    // engine thread → UI (at most once a second)
    private readonly Lock _liveGate = new();
    private int[] _liveIndex = [];
    private int[] _liveStamp = [];
    private float[] _liveValue = [];
    private long[] _liveTime = [];
    private long _lastPost;
    private bool _posted;

    public HistoryPage(PageContext ctx)
    {
        _ctx = ctx;
        _hub = ctx.Hub;
        _history = ctx.History ?? AppServices.History;
        if (_history is null && AppEnv.TestMode)
        {
            _history = new PreviewHistory(_hub, () => Clock(), id => IsRecorded(id), Zone);
            _preview = true;
        }
        Resources.MergedDictionaries.Add(HistoryStyles.Shared);
        Language = Loc.I.XmlLang;

        _chart.Clock = () => Clock();
        _chart.Culture = Loc.I.Culture;
        _chart.ViewChanged += OnChartViewChanged;
        _chart.SelectionChanged += OnChartSelectionChanged;
        _chart.SizeChanged += (_, e) =>
        {
            if (_active && Math.Abs(e.NewSize.Width - _queriedWidth) > Math.Max(40, _queriedWidth * 0.12)) ScheduleQuery(220);
        };
        _stats.GoTo += GoTo;
        _stats.ClearSelection += () => _chart.SetSelection(null);

        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Content = _body };
        _scroll.SizeChanged += (_, _) => FitBody();
        BuildBody();
        _layers.Children.Add(_scroll);
        _layers.Children.Add(_pickerPopup);
        _layers.Children.Add(_customPopup);
        Content = _layers;

        Loaded += (_, _) => Loc.LanguageChanged += OnLanguageChanged;
        Unloaded += (_, _) => Loc.LanguageChanged -= OnLanguageChanged;
    }

    // ───────────── seams (tests) ─────────────

    /// <summary>"Now" (UTC ticks).</summary>
    internal Func<long> Clock { get; set; } = static () => DateTime.UtcNow.Ticks;

    internal TimeZoneInfo Zone
    {
        get;
        set
        {
            field = value;
            _chart.Zone = value;
        }
    } = TimeZoneInfo.Local;

    /// <summary>The save-file dialog (tests replace it).</summary>
    internal Func<string, bool, string?>? PickPath { get; set; }

    internal TimeChart Chart => _chart;

    internal StatsPanel StatsPanel => _stats;

    internal IHistoryQuery? HistorySource => _history;

    /// <summary>The preview history stands in (test mode without a store).</summary>
    internal bool UsesPreview => _preview;

    internal IReadOnlyList<string> ShownIds => _shown.Select(s => s.D.Id).ToList();

    internal RangePreset Preset => _preset;

    internal bool IsLiveView => _live;

    internal HistoryEmpty EmptyState { get; private set; }

    internal int NoteCount => _notes.Children.Count;

    internal IEnumerable<string> NoteTexts => _notes.Children.OfType<Border>().Select(b => b.Tag as string ?? "");

    internal ExportSheet? Sheet => _sheet;

    internal SensorPicker Picker => _picker ??= CreatePicker();

    internal RangePicker RangePickerPanel => _rangePicker ??= CreateRangePicker();

    internal Task? PendingQuery { get; private set; }

    internal Task? PendingStats { get; private set; }

    internal int QueryCount { get; private set; }

    private SettingsStore? Store => SettingsStore.I;

    private bool RecordingOn => Store?.Current.Recording.Enabled == true;

    private bool LiveOnly => _history is null || !RecordingOn;

    private bool IsRecorded(string id) => RecordingOn && Store!.Current.Recording.SensorIds.Contains(id);

    // ───────────── lifecycle ─────────────

    public void OnNavigatedTo(object? parameter)
    {
        _active = true;
        _hub.FrameReady += OnFrame;
        _hub.InventoryChanged += OnInventoryChanged;
        if (Store is { } s) s.Changed += OnSettingsChanged;

        if (!_started)
        {
            _started = true;
            if (parameter is null) AddDefaults();
        }
        if (Preselect(parameter)) _userTouchedSeries = true;

        if (LiveOnly && _preset != RangePreset.Min15) _preset = RangePreset.Min15;
        if (_live || _preset != RangePreset.None && _preset != RangePreset.Custom)
        {
            var span = _preset is RangePreset.None or RangePreset.Custom ? _chart.View.Span : HistoryModel.Length(_preset).Ticks;
            var now = Clock();
            _chart.SetView(new TimeView(now - span, now), animate: false);
            _live = true;
        }
        UpdateWatch();
        RebuildChips();
        UpdateToolbar();
        UpdateNotes();
        Query(fade: false);
    }

    public void OnNavigatedFrom()
    {
        if (!_active) return;
        _active = false;
        _hub.FrameReady -= OnFrame;
        _hub.InventoryChanged -= OnInventoryChanged;
        if (Store is { } s) s.Changed -= OnSettingsChanged;
        _queryCts?.Cancel();
        _statsCts?.Cancel();
        _queryTimer?.Stop();
        _statsTimer?.Stop();
        _watch?.Dispose();
        _watch = null;
        _demand?.Dispose();
        _demand = null;
        _pickerPopup.IsOpen = false;
        _customPopup.IsOpen = false;
        if (_sheet is { IsBusy: false }) CloseSheet();
        lock (_liveGate) _posted = false;
    }

    public void Dispose()
    {
        OnNavigatedFrom();
        _sheet?.Close();
        _queryCts?.Dispose();
        _statsCts?.Dispose();
    }

    // ───────────── layout ─────────────

    private void BuildBody()
    {
        _body.Margin = new Thickness(PageLayout.Margin.Left, PageLayout.Margin.Top, PageLayout.Margin.Right, 22);
        _body.MaxWidth = 1440;
        _body.HorizontalAlignment = HorizontalAlignment.Stretch;
        for (var i = 0; i < 6; i++) _body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _body.RowDefinitions[4].Height = new GridLength(1, GridUnitType.Star);
        _body.RowDefinitions[4].MinHeight = 300;

        // 0 · title, subtitle, export
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        _export.SetResourceReference(StyleProperty, "SecondaryButton");
        _export.VerticalAlignment = VerticalAlignment.Top;
        _export.Margin = new Thickness(16, 4, 0, 0);
        _export.Content = IconText("Icon.Export", "history.export");
        _export.Click += (_, _) => OpenExport();
        DockPanel.SetDock(_export, Dock.Right);
        head.Children.Add(_export);
        var titles = new StackPanel();
        var title = new TextBlock();
        title.SetResourceReference(StyleProperty, "PageTitle");
        title.SetBinding(TextBlock.TextProperty, Tr("nav.history"));
        titles.Children.Add(title);
        var subtitle = new TextBlock();
        subtitle.SetResourceReference(StyleProperty, "PageSubtitle");
        subtitle.SetBinding(TextBlock.TextProperty, Tr("page.history.subtitle"));
        titles.Children.Add(subtitle);
        head.Children.Add(titles);
        Place(head, 0);

        // 1 · ranges + live state
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = true };
        var segHost = new Border();
        segHost.SetResourceReference(StyleProperty, "SegmentHost");
        var segs = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var p in HistoryModel.Presets.Append(RangePreset.Custom))
        {
            var b = new RadioButton { GroupName = "history-range-" + GetHashCode() };
            b.SetResourceReference(StyleProperty, "Segment");
            b.SetBinding(ContentProperty, Tr(HistoryModel.LabelKey(p)));
            var preset = p;
            b.Click += (_, _) => OnPresetClick(preset);
            _presetButtons[p] = b;
            segs.Children.Add(b);
        }
        segHost.Child = segs;
        DockPanel.SetDock(segHost, Dock.Left);
        bar.Children.Add(segHost);

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _rangeText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        _rangeText.Typography.NumeralAlignment = FontNumeralAlignment.Tabular;
        var live = new StackPanel { Orientation = Orientation.Horizontal };
        var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Border.BackgroundProperty, "Brush.Success");
        live.Children.Add(dot);
        var liveText = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold };
        liveText.SetBinding(TextBlock.TextProperty, Tr("history.live"));
        liveText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        live.Children.Add(liveText);
        _livePill.Child = live;
        _livePill.SetResourceReference(Border.BackgroundProperty, "Brush.SuccessSoft");
        _livePill.Margin = new Thickness(14, 0, 0, 0);
        _toLive.SetResourceReference(StyleProperty, "SecondaryButton");
        _toLive.Padding = new Thickness(10, 4, 12, 5);
        _toLive.Margin = new Thickness(14, 0, 0, 0);
        _toLive.Content = IconText("Icon.ChevronRight", "history.toLive", 13);
        _toLive.Click += (_, _) => BackToLive();
        right.Children.Add(_rangeText);
        right.Children.Add(_livePill);
        right.Children.Add(_toLive);
        bar.Children.Add(right);
        Place(bar, 1);

        // 2 · chips
        _add.SetResourceReference(StyleProperty, "History.AddChip");
        _add.Content = IconText("Icon.Plus", "history.add", 13);
        _add.Click += (_, _) => OpenPicker();
        Place(_chips, 2);

        // 3 · notes
        Place(_notes, 3);

        // 4 · chart card
        var card = new Border { Padding = new Thickness(8, 12, 8, 8), Margin = new Thickness(0, 2, 0, 14) };
        card.SetResourceReference(StyleProperty, "Card");
        card.Padding = new Thickness(8, 12, 8, 8);
        card.Margin = new Thickness(0, 2, 0, 14);
        var inner = new Grid();
        inner.RowDefinitions.Add(new RowDefinition());
        inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inner.Children.Add(_chart);
        _emptyCard.Child = _empty;
        _emptyCard.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        _emptyCard.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        inner.Children.Add(_emptyCard);
        var foot = new DockPanel { Margin = new Thickness(12, 6, 12, 0) };
        Grid.SetRow(foot, 1);
        _ownScale.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        DockPanel.SetDock(_ownScale, Dock.Right);
        foot.Children.Add(_ownScale);
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
        _hint.SetBinding(TextBlock.TextProperty, Tr("history.hint"));
        foot.Children.Add(_hint);
        inner.Children.Add(foot);
        card.Child = inner;
        Place(card, 4);

        // 5 · stats
        Place(_stats, 5);

        _pickerPopup.StaysOpen = false;
        _pickerPopup.AllowsTransparency = true;
        _pickerPopup.Placement = PlacementMode.Bottom;
        _pickerPopup.PopupAnimation = PopupAnimation.Fade;
        _pickerPopup.PlacementTarget = _add;
        _pickerPopup.VerticalOffset = -4;
        _pickerPopup.HorizontalOffset = -10;
        _customPopup.StaysOpen = false;
        _customPopup.AllowsTransparency = true;
        _customPopup.Placement = PlacementMode.Bottom;
        _customPopup.PopupAnimation = PopupAnimation.Fade;
        _customPopup.PlacementTarget = _presetButtons[RangePreset.Custom];
        _customPopup.HorizontalOffset = -150;
        _customPopup.Closed += (_, _) => UpdateToolbar();
    }

    private void Place(UIElement e, int row)
    {
        Grid.SetRow(e, row);
        _body.Children.Add(e);
    }

    /// <summary>The page fills the window; it scrolls only when the window is too short for a useful chart.</summary>
    private void FitBody()
    {
        var viewport = _scroll.ViewportHeight;
        if (viewport <= 0) return;
        var others = 0.0;
        for (var i = 0; i < _body.RowDefinitions.Count; i++)
            if (i != 4) others += _body.RowDefinitions[i].ActualHeight;
        var need = others + _body.RowDefinitions[4].MinHeight + _body.Margin.Top + _body.Margin.Bottom;
        _body.Height = Math.Max(viewport, need) - _body.Margin.Top - _body.Margin.Bottom;
    }

    private static FrameworkElement IconText(string icon, string key, double size = 14)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        var i = new Icon { Size = size + 1, Margin = new Thickness(0, 0, 7, 0) };
        i.SetResourceReference(Icon.DataProperty, icon);
        p.Children.Add(i);
        var t = new TextBlock { FontSize = size - 0.5, VerticalAlignment = VerticalAlignment.Center };
        t.SetBinding(TextBlock.TextProperty, Tr(key));
        p.Children.Add(t);
        return p;
    }

    // ───────────── series ─────────────

    private void AddDefaults()
    {
        foreach (var s in SensorPresentation.KeySensors(_hub, [], 3)) AddSeries(s, query: false);
    }

    /// <summary>A sensor id (or descriptor) from another page: show it (the chart's last series makes room when full).</summary>
    private bool Preselect(object? parameter)
    {
        var d = parameter switch
        {
            string id => _hub.Find(id),
            SensorDescriptor sd => sd,
            _ => null,
        };
        if (d is null) return false;
        if (_shown.FirstOrDefault(x => x.D.Id == d.Id) is { } existing)
        {
            existing.Series.Visible = true;
            return true;
        }
        if (_shown.Count >= HistoryModel.MaxSeries) RemoveSeries(_shown[^1].D.Id, query: false);
        AddSeries(d, query: false);
        return true;
    }

    private int FreeColor()
    {
        for (var c = 0; c < 8; c++)
            if (_shown.All(s => s.Series.ColorIndex != c)) return c;
        return _shown.Count % 8;
    }

    private void AddSeries(SensorDescriptor d, bool query = true)
    {
        if (_shown.Any(s => s.D.Id == d.Id) || _shown.Count >= HistoryModel.MaxSeries) return;
        var display = HistoryModel.DisplayOf(d.Kind, d.Unit, Store?.Current.General.TemperatureUnit ?? default);
        var series = new ChartSeries { Id = d.Id, Name = d.Name, Unit = display.Unit, Decimals = display.Decimals, ColorIndex = FreeColor() };
        _shown.Add(new Shown { D = d, Series = series, Display = display });
        if (!query) return;
        AfterSeriesChanged();
    }

    private void RemoveSeries(string id, bool query = true)
    {
        var i = _shown.FindIndex(s => s.D.Id == id);
        if (i < 0) return;
        _shown.RemoveAt(i);
        if (!query) return;
        AfterSeriesChanged();
    }

    private void AfterSeriesChanged()
    {
        _userTouchedSeries = true;
        RebuildChips();
        UpdateWatch();
        UpdateNotes();
        _chart.SetSeries(_shown.Select(s => s.Series).ToList());
        _picker?.UpdateStates();
        if (_active) Query(fade: false);
    }

    /// <summary>Toggles a sensor from the picker (tests call it too).</summary>
    internal void TogglePicked(SensorDescriptor d)
    {
        if (_shown.Any(s => s.D.Id == d.Id)) RemoveSeries(d.Id);
        else AddSeries(d);
    }

    private void RebuildChips()
    {
        _chips.Children.Clear();
        foreach (var s in _shown)
        {
            var chip = new ToggleButton { IsChecked = s.Series.Visible, ToolTip = Loc.T("history.chip.toggle") };
            chip.SetResourceReference(StyleProperty, "History.SeriesChip");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var dot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3.5), Margin = new Thickness(0, 0, 8, 0), BorderThickness = new Thickness(2) };
            var brush = SeriesBrush(s.Series.ColorIndex);
            dot.BorderBrush = brush;
            dot.Background = s.Series.Visible ? brush : Brushes.Transparent;
            row.Children.Add(dot);
            var name = new TextBlock { Text = s.D.Name, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
            Explain.Explain.SetSensor(name, s.D);
            row.Children.Add(name);
            var unit = new TextBlock { Text = s.Display.Unit, FontSize = 11.5, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            unit.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
            row.Children.Add(unit);
            if (!LiveOnly && !IsRecorded(s.D.Id))
            {
                var warn = new Icon { Size = 13, Margin = new Thickness(7, 0, 0, 0), ToolTip = Loc.T("history.chip.notRecorded"), IsHitTestVisible = true };
                warn.SetResourceReference(Icon.DataProperty, "Icon.EyeOff");
                warn.SetResourceReference(Icon.ForegroundProperty, "Brush.Warning");
                row.Children.Add(warn);
            }
            var close = new Button { Width = 22, Height = 22, Margin = new Thickness(6, 0, 0, 0), ToolTip = Loc.T("history.chip.remove") };
            close.SetResourceReference(StyleProperty, "IconButton");
            var x = new Icon { Size = 12, StrokeThickness = 1.7 };
            x.SetResourceReference(Icon.DataProperty, "Icon.Close");
            close.Content = x;
            var id = s.D.Id;
            close.Click += (_, e) =>
            {
                e.Handled = true;
                RemoveSeries(id);
            };
            row.Children.Add(close);
            chip.Content = row;
            var shown = s;
            chip.Click += (_, _) =>
            {
                shown.Series.Visible = chip.IsChecked == true;
                dot.Background = shown.Series.Visible ? brush : Brushes.Transparent;
                _chart.VisibilityChanged();
                UpdateStats();
                UpdateEmpty();
            };
            s.Chip = chip;
            _chips.Children.Add(chip);
        }
        _add.IsEnabled = _shown.Count < HistoryModel.MaxSeries;
        _add.ToolTip = _add.IsEnabled ? null : Loc.T("history.add.max", HistoryModel.MaxSeries);
        _chips.Children.Add(_add);
        _chart.SetSeries(_shown.Select(s => s.Series).ToList());
        UpdateOwnScale();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FitBody);
    }

    private Brush SeriesBrush(int index) => TryFindResource("Brush.Series." + ((index % 8) + 8) % 8) as Brush ?? Brushes.SlateBlue;

    private void UpdateOwnScale()
    {
        var own = _chart.OwnScaleUnits;
        _ownScale.Text = own.Count == 0 ? "" : Loc.T("history.ownScale", string.Join(", ", own));
        _ownScale.Visibility = own.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ───────────── live: watch + frames ─────────────

    private void UpdateWatch()
    {
        if (!_active) return;
        var ids = _shown.Select(s => s.D.Id).ToArray();
        var old = _watch;
        _watch = ids.Length > 0 ? AppServices.WatchHistory?.Invoke(ids) : null;
        old?.Dispose(); // after the new one: the live buffer of sensors still shown is kept
        if (AppServices.WatchHistory is null && ids.Length > 0)
        {
            if (_demand is null) _demand = _hub.Demand("history-page", ids, TimeSpan.FromSeconds(1));
            else _demand.Update(ids, TimeSpan.FromSeconds(1));
        }
        else
        {
            _demand?.Dispose();
            _demand = null;
        }
        var index = _shown.Select(s => s.D.Index).ToArray();
        lock (_liveGate)
        {
            _liveIndex = index;
            _liveStamp = new int[index.Length];
            _liveValue = new float[index.Length];
            _liveTime = new long[index.Length];
            Array.Fill(_liveValue, float.NaN);
        }
    }

    /// <summary>Engine thread: copies fresh values of the shown sensors; posts to the UI at most once a second.</summary>
    private void OnFrame(SensorFrame frame)
    {
        if (!_active) return;
        var post = false;
        lock (_liveGate)
        {
            var idx = _liveIndex;
            for (var i = 0; i < idx.Length; i++)
            {
                var k = idx[i];
                if ((uint)k >= (uint)frame.Count) continue;
                var stamp = frame.UpdatedAt[k];
                if (stamp == 0 || stamp == _liveStamp[i]) continue;
                _liveStamp[i] = stamp;
                _liveValue[i] = frame.Values[k];
                _liveTime[i] = frame.TimestampUtcTicks;
            }
            if (!_posted && frame.TimestampUtcTicks - _lastPost >= TimeSpan.TicksPerSecond - TimeSpan.TicksPerMillisecond * 100)
            {
                _posted = true;
                _lastPost = frame.TimestampUtcTicks;
                post = true;
            }
        }
        if (post) Dispatcher.BeginInvoke(DispatcherPriority.Background, ApplyLive);
    }

    /// <summary>UI thread: appends the pending live values (≤ 1 Hz) and slides a live window.</summary>
    internal void ApplyLive()
    {
        int[] idx;
        float[] values;
        long[] times;
        lock (_liveGate)
        {
            _posted = false;
            idx = _liveIndex;
            values = (float[])_liveValue.Clone();
            times = (long[])_liveTime.Clone();
            Array.Fill(_liveValue, float.NaN);
        }
        if (!_active) return;
        AppendSamples(idx, values, times);
    }

    /// <summary>Appends samples (base units) of the shown sensors by hub index (tests drive it directly).</summary>
    internal void AppendSamples(int[] indices, float[] values, long[] times)
    {
        var changed = false;
        for (var i = 0; i < indices.Length; i++)
        {
            if (float.IsNaN(values[i])) continue;
            var s = _shown.FirstOrDefault(x => x.D.Index == indices[i]);
            if (s is null) continue;
            changed |= s.Series.Append(times[i], s.Display.Apply(values[i]));
        }
        if (_live)
        {
            var now = Clock();
            var span = _chart.View.Span;
            _chart.SetView(new TimeView(now - span, now), animate: false);
            foreach (var s in _shown) ChartMath.TrimBefore(s.Series.Points, now - span * 2);
        }
        if (changed)
        {
            _chart.Appended();
            if (EmptyState != HistoryEmpty.None) UpdateEmpty();
        }
        UpdateRangeText();
        // the visible range moves: refresh its stats now and then (a selection stays put)
        if (_live && _chart.Selection is null && Clock() - _lastStatsAt > 5 * TimeSpan.TicksPerSecond) ScheduleStats(10);
    }

    private void OnInventoryChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_active) return;
            if (_shown.Count == 0 && !_userTouchedSeries)
            {
                AddDefaults();
                AfterSeriesChanged();
                _userTouchedSeries = false;
            }
            else UpdateWatch(); // hub indices of reappearing sensors
        });
    }

    private void OnSettingsChanged(string section)
    {
        if (section is not ("recording" or "general")) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_active) return;
            if (section == "general")
            {
                var t = Store?.Current.General.TemperatureUnit ?? default;
                foreach (var s in _shown) s.Display = HistoryModel.DisplayOf(s.D.Kind, s.D.Unit, t);
                RebuildSeriesObjects();
            }
            if (LiveOnly && _preset != RangePreset.Min15) ApplyPreset(RangePreset.Min15, animate: false);
            RebuildChips();
            UpdateNotes();
            UpdateToolbar();
            Query(fade: false);
        });
    }

    /// <summary>Units changed (°C ↔ °F): new series objects with the new unit.</summary>
    private void RebuildSeriesObjects()
    {
        foreach (var s in _shown)
            s.Series = new ChartSeries { Id = s.D.Id, Name = s.D.Name, Unit = s.Display.Unit, Decimals = s.Display.Decimals, ColorIndex = s.Series.ColorIndex, Visible = s.Series.Visible };
        _chart.SetSeries(_shown.Select(s => s.Series).ToList());
    }

    private void OnLanguageChanged()
    {
        Language = Loc.I.XmlLang;
        _chart.Culture = Loc.I.Culture;
        _chart.DataChanged(fade: false);
        RebuildChips();
        UpdateNotes();
        UpdateToolbar();
        UpdateEmpty();
        UpdateStats();
    }

    // ───────────── ranges ─────────────

    private void OnPresetClick(RangePreset p)
    {
        if (p == RangePreset.Custom)
        {
            OpenCustom();
            UpdateToolbar();
            return;
        }
        ApplyPreset(p, animate: true);
    }

    /// <summary>A preset: the window of that length ending now, live, with the range transition.</summary>
    internal void ApplyPreset(RangePreset p, bool animate)
    {
        if (!HistoryModel.PresetEnabled(p, LiveOnly)) p = RangePreset.Min15;
        _preset = p;
        _live = true;
        _chart.SetSelection(null);
        _chart.SetView(HistoryModel.ViewOf(p, Clock()), animate);
        UpdateToolbar();
        Query(fade: true);
    }

    /// <summary>A custom window (from the range popover).</summary>
    internal void ApplyCustom(long from, long to)
    {
        _customPopup.IsOpen = false;
        _preset = RangePreset.Custom;
        var now = Clock();
        _live = HistoryModel.IsLive(new TimeView(from, to), now);
        _chart.SetSelection(null);
        _chart.SetView(new TimeView(from, Math.Min(to, now)), animate: true);
        UpdateToolbar();
        Query(fade: true);
    }

    private void BackToLive()
    {
        var span = Math.Min(_chart.View.Span, LiveOnly ? HistoryModel.LiveBuffer.Ticks : long.MaxValue);
        var now = Clock();
        _live = true;
        _chart.SetView(new TimeView(now - span, now), animate: true);
        _preset = HistoryModel.Match(_chart.View, now);
        UpdateToolbar();
        Query(fade: false);
    }

    private void OnChartViewChanged(TimeView view, ViewChangeReason reason)
    {
        if (reason != ViewChangeReason.User) return;
        var now = Clock();
        _live = HistoryModel.IsLive(view, now);
        _preset = HistoryModel.Match(view, now);
        _chart.ClearMarker();
        UpdateToolbar();
        ScheduleQuery(140);
        if (_chart.Selection is null) ScheduleStats(260);
    }

    private void OnChartSelectionChanged((long From, long To)? selection)
    {
        UpdateToolbar();
        ScheduleStats(30);
    }

    private void UpdateToolbar()
    {
        var liveOnly = LiveOnly;
        foreach (var (p, b) in _presetButtons)
        {
            b.IsChecked = p == _preset && p != RangePreset.None;
            var enabled = HistoryModel.PresetEnabled(p, liveOnly) && (p != RangePreset.Custom || !liveOnly);
            b.IsEnabled = enabled;
            b.ToolTip = enabled ? null : Loc.T("history.liveOnly.preset");
        }
        _livePill.Visibility = _live ? Visibility.Visible : Visibility.Collapsed;
        _toLive.Visibility = _live ? Visibility.Collapsed : Visibility.Visible;
        _chart.IsLive = _live;
        _export.IsEnabled = _history is not null && _shown.Count > 0;
        _export.ToolTip = _history is null ? Loc.T("history.export.unavailable") : null;
        UpdateRangeText();
    }

    private void UpdateRangeText()
    {
        var v = _chart.View;
        _rangeText.Text = HistoryModel.RangeText(v.From, v.To, Clock(), Zone, Loc.I.Culture);
    }

    // ───────────── queries ─────────────

    private void ScheduleQuery(int ms)
    {
        if (!_active) return;
        _queryTimer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        _queryTimer.Stop();
        _queryTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _queryTimer.Tick -= OnQueryTimer;
        _queryTimer.Tick += OnQueryTimer;
        _queryTimer.Start();
    }

    private void OnQueryTimer(object? sender, EventArgs e)
    {
        _queryTimer?.Stop();
        Query(fade: false);
    }

    /// <summary>Asks the history for every shown series over the (padded) window; newer queries cancel older ones.</summary>
    internal void Query(bool fade)
    {
        _queryTimer?.Stop();
        if (!_active) return;
        _fadeNext |= fade;
        _queryCts?.Cancel();
        if (_history is null || _shown.Count == 0)
        {
            UpdateEmpty();
            ScheduleStats(0);
            return;
        }
        var cts = new CancellationTokenSource();
        _queryCts = cts;
        var view = _chart.View;
        var width = _chart.PlotRect.Width > 0 ? _chart.PlotRect.Width : Math.Max(300, _chart.ActualWidth - 80);
        _queriedWidth = _chart.ActualWidth;
        var now = Clock();
        var (from, to, max) = HistoryModel.QueryWindow(view, width, now);
        var targets = _shown.ToList();
        var tasks = targets.Select(s => _history.GetSeriesAsync(s.D.Id, new DateTime(from, DateTimeKind.Utc), new DateTime(to, DateTimeKind.Utc), max, cts.Token)).ToArray();
        var interval = Math.Clamp(Store?.Current.Recording.IntervalSeconds ?? 2, 1, 60) * TimeSpan.TicksPerSecond;
        QueryCount++;
        PendingQuery = Task.WhenAll(tasks).ContinueWith(t =>
        {
            if (cts.IsCancellationRequested || !t.IsCompletedSuccessfully) return;
            var results = t.Result;
            Dispatcher.BeginInvoke(() =>
            {
                if (cts != _queryCts || cts.IsCancellationRequested) return;
                for (var i = 0; i < targets.Count; i++)
                {
                    var s = targets[i];
                    if (!_shown.Contains(s)) continue;
                    var raw = results[i];
                    if (s.D.Kind == SensorKind.Throughput)
                    {
                        var peak = raw.Count == 0 ? 0 : raw.Max(p => p.Max);
                        s.Display = HistoryModel.DisplayOf(s.D.Kind, s.D.Unit, Store?.Current.General.TemperatureUnit ?? default, peak);
                        if (s.Series.Unit != s.Display.Unit)
                        {
                            s.Series = new ChartSeries { Id = s.D.Id, Name = s.D.Name, Unit = s.Display.Unit, Decimals = s.Display.Decimals, ColorIndex = s.Series.ColorIndex, Visible = s.Series.Visible };
                            _chart.SetSeries(_shown.Select(x => x.Series).ToList());
                        }
                    }
                    var list = new List<HistoryPoint>(raw.Count);
                    foreach (var p in raw) list.Add(s.Display.Apply(p));
                    s.Series.Points = list;
                    s.Series.Bucket = HistoryModel.BucketFor(from, to, max, IsRecorded(s.D.Id) ? interval : TimeSpan.TicksPerSecond);
                    s.Series.Live = default;
                }
                var f = _fadeNext;
                _fadeNext = false;
                _chart.DataChanged(fade: f);
                UpdateOwnScale();
                UpdateEmpty();
                ScheduleStats(0);
            });
        }, TaskScheduler.Default);
    }

    private void ScheduleStats(int ms)
    {
        if (!_active) return;
        if (ms <= 0)
        {
            QueryStats();
            return;
        }
        _statsTimer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        _statsTimer.Stop();
        _statsTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _statsTimer.Tick -= OnStatsTimer;
        _statsTimer.Tick += OnStatsTimer;
        _statsTimer.Start();
    }

    private void OnStatsTimer(object? sender, EventArgs e)
    {
        _statsTimer?.Stop();
        QueryStats();
    }

    /// <summary>Exact min / avg / max (+ when) of the selection or the visible window for every visible series.</summary>
    internal void QueryStats()
    {
        _statsTimer?.Stop();
        if (!_active) return;
        _statsCts?.Cancel();
        _lastStatsAt = Clock();
        var (from, to) = StatsRange();
        if (_history is null)
        {
            // no store attached: from the live points the chart has (display units already)
            foreach (var s in _shown) s.Stats = LocalStats(s.Series.Points, from, to);
            UpdateStats();
            return;
        }
        var cts = new CancellationTokenSource();
        _statsCts = cts;
        var targets = _shown.Where(s => s.Series.Visible).ToList();
        foreach (var s in targets) s.StatsLoading = true;
        var tasks = targets.Select(s => _history.GetStatsAsync(s.D.Id, new DateTime(from, DateTimeKind.Utc), new DateTime(to, DateTimeKind.Utc), cts.Token)).ToArray();
        UpdateStats();
        PendingStats = Task.WhenAll(tasks).ContinueWith(t =>
        {
            if (cts.IsCancellationRequested || !t.IsCompletedSuccessfully) return;
            var results = t.Result;
            Dispatcher.BeginInvoke(() =>
            {
                if (cts != _statsCts) return;
                for (var i = 0; i < targets.Count; i++)
                {
                    targets[i].Stats = results[i];
                    targets[i].StatsLoading = false;
                }
                UpdateStats();
            });
        }, TaskScheduler.Default);
    }

    private (long From, long To) StatsRange()
    {
        if (_chart.Selection is { } s) return s;
        var v = _chart.View;
        return (v.From, Math.Min(v.To, Clock()));
    }

    /// <summary>Stats from chart points (bucket min / max, mean of averages) when no store is attached.</summary>
    internal static RangeStats? LocalStats(List<HistoryPoint> pts, long from, long to)
    {
        var span = CollectionsMarshal.AsSpan(pts);
        float mn = float.MaxValue, mx = float.MinValue;
        long tMin = 0, tMax = 0, n = 0;
        double sum = 0;
        for (var i = ChartMath.LowerBound(span, from); i < span.Length && span[i].TimeUtcTicks <= to; i++)
        {
            var p = span[i];
            if (p.Min < mn)
            {
                mn = p.Min;
                tMin = p.TimeUtcTicks;
            }
            if (p.Max > mx)
            {
                mx = p.Max;
                tMax = p.TimeUtcTicks;
            }
            sum += p.Avg;
            n++;
        }
        return n == 0 ? null : new RangeStats(mn, (float)(sum / n), mx, tMin, tMax, n);
    }

    private void UpdateStats()
    {
        var (from, to) = StatsRange();
        var selection = _chart.Selection is not null;
        var scope = HistoryModel.RangeText(from, to, Clock(), Zone, Loc.I.Culture) + " · " + HistoryModel.Duration(TimeSpan.FromTicks(to - from));
        var rows = new List<StatsRow>();
        var culture = Loc.I.Culture;
        foreach (var s in _shown)
        {
            if (!s.Series.Visible) continue;
            var display = _history is null ? s.Display with { Scale = 1, Offset = 0 } : s.Display;
            string? session = null;
            if (LiveOnly && s.D.Index >= 0 && _hub.Stats.Count(s.D.Index) > 0)
            {
                var st = _hub.Stats;
                session = Loc.T("history.stats.session",
                    s.Display.Format(s.Display.Apply(st.Min(s.D.Index)), culture),
                    s.Display.Format(s.Display.Apply(st.Average(s.D.Index)), culture),
                    s.Display.Format(s.Display.Apply(st.Max(s.D.Index)), culture)) + " " + s.Display.Unit;
            }
            rows.Add(new StatsRow(s.D, s.D.Name, s.Display.Unit, s.Series.ColorIndex, display, s.Stats, s.StatsLoading, session));
        }
        var now = Clock();
        _stats.Update(selection, scope, rows, t => HistoryModel.MomentText(t, now, Zone, culture), culture, SeriesBrush);
        _stats.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FitBody);
    }

    /// <summary>A min / max time from the stats: the crosshair goes there (the window moves when it is outside).</summary>
    private void GoTo(long utc)
    {
        var v = _chart.View;
        if (!v.Contains(utc))
        {
            var half = v.Span / 2;
            var to = Math.Min(utc + half, Clock());
            _live = HistoryModel.IsLive(new TimeView(to - v.Span, to), Clock());
            _chart.SetView(new TimeView(to - v.Span, to), animate: true);
            _preset = HistoryModel.Match(_chart.View, Clock());
            UpdateToolbar();
            Query(fade: false);
        }
        _chart.ShowMarker(utc);
    }

    // ───────────── notes & empty states ─────────────

    private void UpdateNotes()
    {
        _notes.Children.Clear();
        if (_history is null)
        {
            _notes.Children.Add(Note("Icon.Info", Loc.T("history.starting.banner"), null, null, "Brush.InfoSoft"));
        }
        else if (!RecordingOn)
        {
            _notes.Children.Add(Note("Icon.Info", Loc.T("history.liveOnly.banner"), Loc.T("history.liveOnly.action"),
                () => _ctx.Navigate(PageIds.Sensors), "Brush.InfoSoft"));
        }
        else
        {
            var missing = _shown.Where(s => !IsRecorded(s.D.Id)).ToList();
            if (missing.Count > 0)
            {
                var text = missing.Count == 1 ? Loc.T("history.notRecorded.one", missing[0].D.Name) : Loc.T("history.notRecorded.many", missing.Count);
                _notes.Children.Add(Note("Icon.EyeOff", text, Loc.T(missing.Count == 1 ? "history.recordThis" : "history.recordThese"),
                    () => Record(missing.Select(m => m.D).ToList()), "Brush.WarningSoft"));
            }
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FitBody);
    }

    private Border Note(string icon, string text, string? action, Action? onAction, string background)
    {
        var b = new Border { Tag = text };
        b.SetResourceReference(StyleProperty, "History.Note");
        b.SetResourceReference(Border.BackgroundProperty, background);
        var row = new DockPanel();
        var i = new Icon { Size = 16, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        i.SetResourceReference(Icon.DataProperty, icon);
        i.SetResourceReference(Icon.ForegroundProperty, "Brush.TextDim");
        DockPanel.SetDock(i, Dock.Left);
        row.Children.Add(i);
        if (action is not null && onAction is not null)
        {
            var a = new Button { Content = action, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            a.SetResourceReference(StyleProperty, "FlatButton");
            a.Click += (_, _) => onAction();
            DockPanel.SetDock(a, Dock.Right);
            row.Children.Add(a);
        }
        var t = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        row.Children.Add(t);
        b.Child = row;
        return b;
    }

    /// <summary>"Record this sensor": adds the ids to Recording.SensorIds (with Undo).</summary>
    internal void Record(IReadOnlyList<SensorDescriptor> sensors)
    {
        if (Store is not { } store || sensors.Count == 0) return;
        var ids = sensors.Select(s => s.Id).Where(id => !store.Current.Recording.SensorIds.Contains(id)).ToList();
        if (ids.Count == 0) return;
        store.Mutate("recording", s => s.Recording.SensorIds.UnionWith(ids));
        var label = sensors.Count == 1 ? sensors[0].Name : string.Join(", ", sensors.Take(3).Select(s => s.Name)) + (sensors.Count > 3 ? "…" : "");
        _ctx.Toast(Loc.T("history.recording.toast", label), ToastKind.Success, Loc.T("common.undo"),
            () => store.Mutate("recording", s => s.Recording.SensorIds.ExceptWith(ids)));
    }

    /// <summary>Decides and shows the empty-state card over the chart.</summary>
    private void UpdateEmpty()
    {
        var state = DecideEmpty();
        EmptyState = state;
        _empty.Children.Clear();
        _emptyCard.Visibility = state == HistoryEmpty.None ? Visibility.Collapsed : Visibility.Visible;
        if (state == HistoryEmpty.None) return;
        var (icon, title, body, action, onAction) = state switch
        {
            HistoryEmpty.PickSensor => ("Icon.Chart", "history.empty.pick.title", "history.empty.pick.body", "history.add", (Action?)OpenPicker),
            HistoryEmpty.Collecting => ("Icon.Pulse", "history.empty.collecting.title", "history.empty.collecting.body", null, null),
            HistoryEmpty.NotRecorded => ("Icon.Record", "history.empty.notRecorded.title", "history.empty.notRecorded.body", "history.recordThis",
                () => Record(_shown.Where(s => s.Series.Visible).Select(s => s.D).ToList())),
            HistoryEmpty.NothingRecorded => ("Icon.History", "history.empty.noData.title", "history.empty.noData.body", null, null),
            _ => ("Icon.History", "history.empty.range.title", "history.empty.range.body", "history.empty.range.action", (Action?)ShowLatest),
        };
        var badge = new Grid { Width = 56, Height = 56, HorizontalAlignment = HorizontalAlignment.Center };
        var ring = new Border { CornerRadius = new CornerRadius(18) };
        ring.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSoft");
        badge.Children.Add(ring);
        var glyph = new Icon { Size = 26, StrokeThickness = 1.6 };
        glyph.SetResourceReference(Icon.DataProperty, icon);
        glyph.SetResourceReference(Icon.ForegroundProperty, "Brush.AccentText");
        badge.Children.Add(glyph);
        _empty.Children.Add(badge);
        var head = new TextBlock { Text = Loc.T(title), FontSize = 15.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        head.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        _empty.Children.Add(head);
        var text = new TextBlock { Text = Loc.T(body), FontSize = 12.5, Margin = new Thickness(0, 5, 0, 0), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        _empty.Children.Add(text);
        if (action is not null && onAction is not null)
        {
            var b = new Button { Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            b.SetResourceReference(StyleProperty, state == HistoryEmpty.PickSensor ? "PrimaryButton" : "SecondaryButton");
            b.Content = state == HistoryEmpty.PickSensor ? IconText("Icon.Plus", action, 13) : new TextBlock { Text = Loc.T(action) };
            b.Click += (_, _) => onAction();
            _empty.Children.Add(b);
        }
        if (Motion.I.Enabled && IsLoaded) Motion.Appear(_emptyCard, rise: 6, ms: 220);
    }

    private HistoryEmpty DecideEmpty()
    {
        var visible = _shown.Where(s => s.Series.Visible).ToList();
        if (visible.Count == 0) return HistoryEmpty.PickSensor;
        var v = _chart.View;
        foreach (var s in visible)
        {
            var pts = CollectionsMarshal.AsSpan(s.Series.Points);
            var i = ChartMath.LowerBound(pts, v.From - s.Series.EffectiveBucket);
            if (i < pts.Length && pts[i].TimeUtcTicks <= v.To) return HistoryEmpty.None;
        }
        if (LiveOnly) return HistoryEmpty.Collecting;
        if (visible.All(s => !IsRecorded(s.D.Id)))
            return HistoryEmpty.NotRecorded;
        var covered = visible.Select(s => _history?.GetCoverage(s.D.Id)).Where(c => c is not null).ToList();
        return covered.Count == 0 ? HistoryEmpty.NothingRecorded : HistoryEmpty.NothingInRange;
    }

    /// <summary>"Show the latest data": the same window length ending at the newest recorded point.</summary>
    private void ShowLatest()
    {
        var last = _shown.Select(s => _history?.GetCoverage(s.D.Id)).Where(c => c is not null).Select(c => c!.Value.ToUtc.Ticks).DefaultIfEmpty(Clock()).Max();
        var span = _chart.View.Span;
        var to = Math.Min(Clock(), last + span / 10);
        _live = HistoryModel.IsLive(new TimeView(to - span, to), Clock());
        _chart.SetView(new TimeView(to - span, to), animate: true);
        _preset = HistoryModel.Match(_chart.View, Clock());
        UpdateToolbar();
        Query(fade: true);
    }

    // ───────────── popovers ─────────────

    private SensorPicker CreatePicker()
    {
        var p = new SensorPicker(_hub, id => _shown.Any(s => s.D.Id == id), IsRecorded, () => _shown.Count);
        p.Picked += TogglePicked;
        return p;
    }

    private void OpenPicker()
    {
        var p = Picker;
        p.Refresh();
        _pickerPopup.Child = new Border { Padding = new Thickness(12, 4, 12, 22), Child = p };
        _pickerPopup.PlacementTarget = _add.IsVisible ? _add : _chart;
        _pickerPopup.IsOpen = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => p.Search.Focus());
    }

    private RangePicker CreateRangePicker()
    {
        var r = new RangePicker(Zone, () => Clock());
        r.Applied += ApplyCustom;
        r.Cancelled += () => _customPopup.IsOpen = false;
        return r;
    }

    private void OpenCustom()
    {
        var r = RangePickerPanel;
        r.Open(_chart.View);
        _customPopup.Child = new Border { Padding = new Thickness(12, 4, 12, 22), Child = r };
        _customPopup.IsOpen = true;
    }

    // ───────────── export ─────────────

    /// <summary>Opens the export sheet over the page (defaults: the chart's sensors, the visible range or the selection).</summary>
    internal void OpenExport()
    {
        if (_history is null || _sheet is not null) return;
        var candidates = new List<ExportCandidate>();
        foreach (var s in _shown) candidates.Add(new ExportCandidate(s.D.Id, s.D.Name, s.Display.Unit, s.D, true));
        var recorded = Store?.Current.Recording.SensorIds ?? [];
        foreach (var d in _hub.Sensors)
        {
            if (candidates.Count >= 48) break;
            if (!recorded.Contains(d.Id) || candidates.Any(c => c.Id == d.Id)) continue;
            candidates.Add(new ExportCandidate(d.Id, d.Name, HistoryModel.DisplayOf(d.Kind, d.Unit, default).Unit, d, false));
        }
        var sheet = new ExportSheet(_history, candidates, _chart.View, _chart.Selection, () => Clock(), Zone) { PickPath = PickPath ?? DefaultPickPath };
        sheet.Closed += CloseSheet;
        sheet.Saved += path =>
        {
            var folder = Path.GetDirectoryName(path) ?? "";
            _ctx.Toast(Loc.T("history.export.done", Path.GetFileName(path)), ToastKind.Success, Loc.T("history.export.openFolder"),
                () => AppServices.Platform.OpenFolder(folder));
        };
        sheet.Failed += message => _ctx.Toast(message, ToastKind.Error);
        _sheet = sheet;
        _layers.Children.Add(sheet);
        if (Motion.I.Enabled && IsLoaded) Motion.Appear(sheet.Card, rise: 10, fromScale: 0.97, ms: 240);
        sheet.Focus();
    }

    private void CloseSheet()
    {
        if (_sheet is null) return;
        _layers.Children.Remove(_sheet);
        _sheet = null;
    }

    private string? DefaultPickPath(string fileName, bool zip)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("history.export.dialogTitle"),
            FileName = fileName,
            DefaultExt = zip ? ".zip" : ".csv",
            Filter = Loc.T(zip ? "history.export.filter.zip" : "history.export.filter.csv"),
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        var owner = _ctx.Window;
        return (owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true ? dialog.FileName : null;
    }

    private static Binding Tr(string key) => new($"[{key}]") { Source = Loc.I, Mode = BindingMode.OneWay };
}
