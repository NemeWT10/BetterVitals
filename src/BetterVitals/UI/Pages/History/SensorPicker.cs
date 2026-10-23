using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using BetterVitals.Core.Sensors;
using BetterVitals.Localization;
using BetterVitals.Services;

namespace BetterVitals.UI.Pages.History;

/// <summary>A row of the sensor picker: a hardware header or a sensor.</summary>
public sealed class PickerEntry : INotifyPropertyChanged
{
    public bool IsHeader { get; init; }

    public string Title { get; init; } = "";

    public string Family { get; init; } = "";

    public string IconKey { get; init; } = "Icon.Layers";

    public SensorDescriptor? Descriptor { get; init; }

    public string HardwareId { get; init; } = "";

    public string Name { get; init; } = "";

    public string Sub { get; init; } = "";

    public string Unit { get; init; } = "";

    public string KindIconKey { get; init; } = "Icon.Pulse";

    /// <summary>Lowercase text the search matches against (name, hardware, kind, unit).</summary>
    public string Haystack { get; init; } = "";

    public bool IsShown
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsShown)));
        }
    }

    /// <summary>False when the chart is full and this sensor is not on it.</summary>
    public bool Enabled
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
        }
    } = true;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// "+ Add sensor" popover content: a search box over every sensor of the PC, grouped by hardware (verbatim names, icons,
/// the explanation bubble), virtualized. Clicking a row adds / removes it from the chart; the popover stays open for
/// picking several. Pure content — the page hosts it in a Popup.
/// </summary>
public sealed class SensorPicker : Border
{
    private readonly ISensorHub _hub;
    private readonly Func<string, bool> _isShown;
    private readonly Func<string, bool> _isRecorded;
    private readonly Func<int> _shownCount;
    private readonly ListBox _list;
    private readonly TextBlock _count;
    private readonly TextBlock _none;
    private List<PickerEntry> _all = [];

    public SensorPicker(ISensorHub hub, Func<string, bool> isShown, Func<string, bool> isRecorded, Func<int> shownCount)
    {
        _hub = hub;
        _isShown = isShown;
        _isRecorded = isRecorded;
        _shownCount = shownCount;
        Resources.MergedDictionaries.Add(HistoryStyles.Shared);
        SetResourceReference(StyleProperty, "Ov.Panel");
        Width = 380;
        Padding = new Thickness(8);

        var root = new DockPanel { Height = 440 };
        Search = new TextBox { Margin = new Thickness(2, 2, 2, 6) };
        Search.SetResourceReference(StyleProperty, "SearchBox");
        Search.SetBinding(TagProperty, new Binding("[history.picker.search]") { Source = Loc.I, Mode = BindingMode.OneWay });
        Search.TextChanged += (_, _) => Filter();
        Search.PreviewKeyDown += OnSearchKey;
        DockPanel.SetDock(Search, Dock.Top);
        root.Children.Add(Search);

        _count = new TextBlock { FontSize = 11.5, Margin = new Thickness(10, 0, 10, 2) };
        _count.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelTextDim");
        DockPanel.SetDock(_count, Dock.Top);
        root.Children.Add(_count);

        _none = new TextBlock { FontSize = 12.5, Margin = new Thickness(12, 18, 12, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        _none.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelTextDim");
        DockPanel.SetDock(_none, Dock.Top);
        root.Children.Add(_none);

        _list = new ListBox
        {
            ItemTemplateSelector = new Selector(this),
            ItemContainerStyleSelector = new ContainerSelector(this),
            SelectionMode = SelectionMode.Single,
        };
        _list.SetResourceReference(StyleProperty, "BareList");
        _list.SetResourceReference(Control.ForegroundProperty, "Brush.PanelText");
        _list.PreviewMouseLeftButtonUp += OnListClick;
        _list.KeyDown += OnListKey;
        root.Children.Add(_list);
        Child = root;
    }

    public TextBox Search { get; }

    /// <summary>The rows shown now (after the search filter), headers included.</summary>
    public IReadOnlyList<PickerEntry> Visible => (IReadOnlyList<PickerEntry>?)_list.ItemsSource ?? [];

    /// <summary>A sensor row was clicked (or Enter): the page adds / removes it.</summary>
    public event Action<SensorDescriptor>? Picked;

    /// <summary>Rebuilds the rows from the hub (call when opening) and clears the search.</summary>
    public void Refresh()
    {
        var hardware = _hub.Hardware;
        var order = hardware.Select((h, i) => (h.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        var temperature = BetterVitals.Core.Settings.SettingsStore.I?.Current.General.TemperatureUnit ?? default;
        var all = new List<PickerEntry>();
        foreach (var group in _hub.Sensors.Where(s => _hub.IsPresent(s.Index) || s.Index < 0)
                     .GroupBy(s => s.HardwareId)
                     .OrderBy(g => order.GetValueOrDefault(g.Key, int.MaxValue)))
        {
            var hw = _hub.FindHardware(group.Key);
            var first = group.First();
            var kind = hw?.Kind ?? first.HardwareKind;
            var title = hw?.Name ?? SensorPresentation.FamilyLabel(kind);
            all.Add(new PickerEntry
            {
                IsHeader = true,
                Title = title,
                Family = SensorPresentation.FamilyLabel(kind),
                IconKey = SensorPresentation.IconKey(kind),
                HardwareId = group.Key,
            });
            foreach (var s in group.OrderBy(s => KindRank(s.Kind)).ThenBy(s => s.Order))
            {
                var unit = s.Kind == SensorKind.Throughput ? "MB/s" : HistoryModel.DisplayOf(s.Kind, s.Unit, temperature).Unit;
                var kindLabel = SensorPresentation.KindLabel(s.Kind);
                var recorded = _isRecorded(s.Id);
                all.Add(new PickerEntry
                {
                    Descriptor = s,
                    HardwareId = group.Key,
                    Name = s.Name,
                    Sub = recorded ? kindLabel + " · " + Loc.T("history.picker.recorded") : kindLabel,
                    Unit = unit,
                    KindIconKey = SensorPresentation.IconKey(s.Kind),
                    Haystack = (s.Name + " " + title + " " + kindLabel + " " + unit + " " + SensorPresentation.FamilyLabel(kind)).ToLowerInvariant(),
                });
            }
        }
        _all = all;
        Search.Text = "";
        UpdateStates();
        Filter();
    }

    /// <summary>The order measurements are listed in under each hardware (what people chart most comes first).</summary>
    internal static int KindRank(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => 0,
        SensorKind.Load => 1,
        SensorKind.Clock => 2,
        SensorKind.Power => 3,
        SensorKind.Fps => 4,
        SensorKind.FrameTime => 5,
        SensorKind.Fan or SensorKind.Control => 6,
        SensorKind.Voltage or SensorKind.Current => 7,
        SensorKind.Data or SensorKind.SmallData => 8,
        SensorKind.Throughput => 9,
        _ => 10,
    };

    /// <summary>Re-reads which sensors are shown (check marks) and whether the chart is full.</summary>
    public void UpdateStates()
    {
        var shown = _shownCount();
        var full = shown >= HistoryModel.MaxSeries;
        foreach (var e in _all)
        {
            if (e.IsHeader || e.Descriptor is null) continue;
            e.IsShown = _isShown(e.Descriptor.Id);
            e.Enabled = e.IsShown || !full;
        }
        _count.Text = full
            ? Loc.T("history.add.max", HistoryModel.MaxSeries)
            : Loc.T("history.picker.count", shown, HistoryModel.MaxSeries);
    }

    /// <summary>Applies the search text: every term must match; headers stay with their matching rows.</summary>
    public void Filter()
    {
        var terms = Search.Text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<PickerEntry>(_all.Count);
        PickerEntry? header = null;
        foreach (var e in _all)
        {
            if (e.IsHeader)
            {
                header = e;
                continue;
            }
            if (terms.Length > 0 && !terms.All(t => e.Haystack.Contains(t, StringComparison.Ordinal))) continue;
            if (header is not null)
            {
                result.Add(header);
                header = null;
            }
            result.Add(e);
        }
        _list.ItemsSource = result;
        var empty = result.Count == 0;
        _none.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _none.Text = _all.Count == 0 ? Loc.T("history.picker.empty") : Loc.T("history.picker.none", Search.Text.Trim());
    }

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && _list.Items.Count > 0)
        {
            var first = Visible.FirstOrDefault(x => !x.IsHeader && x.Enabled);
            if (first is null) return;
            _list.SelectedItem = first;
            _list.UpdateLayout();
            (_list.ItemContainerGenerator.ContainerFromItem(first) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Visible.Count(x => !x.IsHeader) == 1)
        {
            Activate(Visible.First(x => !x.IsHeader));
            e.Handled = true;
        }
    }

    private void OnListClick(object sender, MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(_list, (DependencyObject)e.OriginalSource) as ListBoxItem;
        if (item?.DataContext is PickerEntry { IsHeader: false } entry) Activate(entry);
    }

    private void OnListKey(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && _list.SelectedItem is PickerEntry { IsHeader: false } entry)
        {
            Activate(entry);
            e.Handled = true;
        }
    }

    /// <summary>Picks a row (tests call it directly).</summary>
    internal void Activate(PickerEntry entry)
    {
        if (entry.Descriptor is null || !entry.Enabled) return;
        Picked?.Invoke(entry.Descriptor);
        UpdateStates();
    }

    private sealed class Selector(SensorPicker owner) : DataTemplateSelector
    {
        public override DataTemplate? SelectTemplate(object item, DependencyObject container)
            => owner.TryFindResource(item is PickerEntry { IsHeader: true } ? "History.PickerHeader" : "History.PickerRow") as DataTemplate;
    }

    private sealed class ContainerSelector(SensorPicker owner) : StyleSelector
    {
        public override Style? SelectStyle(object item, DependencyObject container)
            => owner.TryFindResource(item is PickerEntry { IsHeader: true } ? "History.PickerHead" : "History.PickerItem") as Style;
    }
}
