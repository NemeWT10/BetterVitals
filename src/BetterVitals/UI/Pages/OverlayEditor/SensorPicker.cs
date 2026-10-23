using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using BetterVitals.Core.Sensors;
using BetterVitals.Localization;
using BetterVitals.Services;
using BetterVitals.UI.Themes;

namespace BetterVitals.UI.Pages.OverlayEditor;

/// <summary>A row of the sensor picker: a hardware header, or a sensor with its live value and switch.</summary>
internal sealed class PickerRow : INotifyPropertyChanged
{
    private bool _checked;
    private string _value = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsHeader { get; init; }

    /// <summary>Verbatim sensor / hardware name.</summary>
    public string Title { get; init; } = "";

    public string Sub { get; init; } = "";

    public string IconKey { get; init; } = "Icon.Pulse";

    public SensorDescriptor? Sensor { get; init; }

    /// <summary>Lower-case text the search looks at.</summary>
    public string Search { get; init; } = "";

    public Action<PickerRow, bool>? Toggled { get; set; }

    public bool IsChecked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            Toggled?.Invoke(this, value);
        }
    }

    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }

    /// <summary>Sets the check without raising <see cref="Toggled"/> (sync from the layout).</summary>
    public void SetQuiet(bool value)
    {
        if (_checked == value) return;
        _checked = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
    }
}

/// <summary>
/// "All sensors…": every sensor of this PC grouped by hardware, searchable, live values, in a sheet over the page
/// (no extra window). Simple mode: a switch puts a sensor on the overlay right away. Advanced mode: pick several, then
/// add them as separate items or — several of one kind — as one multi-value item (e.g. all core clocks); or drag a
/// sensor straight onto the layout board (the sheet steps aside while the drag runs and comes back after the drop).
/// </summary>
internal sealed class SensorPicker : Grid
{
    private readonly EditorContext _ctx;
    private readonly TextBox _search;
    private readonly ListBox _list;
    private readonly TextBlock _title;
    private readonly TextBlock _hint;
    private readonly Button _add;
    private readonly TextBlock _addText;
    private readonly Button _done;
    private readonly Button _cancel;
    private readonly FrameworkElement _combineLine;
    private readonly CheckBox _combine;
    private readonly TextBlock _nothing;
    private List<PickerRow> _all = [];
    private bool _advanced;
    private bool _suspended;
    private PickerRow? _pressRow;
    private Point _pressAt;

    public SensorPicker(EditorContext ctx)
    {
        _ctx = ctx;
        Visibility = Visibility.Collapsed;
        System.Windows.Controls.Panel.SetZIndex(this, 50);

        var dim = new Border { Background = Brushes.Transparent };
        dim.SetResourceReference(Border.BackgroundProperty, "Brush.Dim");
        dim.MouseLeftButtonDown += (_, _) => Close();
        Children.Add(dim);

        var sheet = new Border
        {
            MaxWidth = 680,
            Margin = new Thickness(40, 30, 40, 30),
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 18, 20, 16),
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = 0.4, Color = ThemeManager.ShadowColor },
        };
        sheet.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        sheet.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderStrong");
        var root = new DockPanel();

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var close = Ui.IconButton("Icon.Close", "ovl.picker.close", (_, _) => Close());
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        _title = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        head.Children.Add(_title);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        _hint = new TextBlock { Margin = new Thickness(0, 0, 0, 12) };
        _hint.SetResourceReference(StyleProperty, "Hint");
        DockPanel.SetDock(_hint, Dock.Top);
        root.Children.Add(_hint);

        _search = new TextBox { Margin = new Thickness(0, 0, 0, 10) };
        _search.SetResourceReference(StyleProperty, "SearchBox");
        _search.SetBinding(TagProperty, Ui.Tr("ovl.picker.search"));
        _search.TextChanged += (_, _) => Filter();
        DockPanel.SetDock(_search, Dock.Top);
        root.Children.Add(_search);

        // footer
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        _combine = new CheckBox();
        _combine.SetResourceReference(StyleProperty, "Switch");
        var combineRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        combineRow.Children.Add(_combine);
        var combineText = Ui.Text("ovl.picker.combine", size: 12.5, wrap: true);
        combineText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        combineText.Margin = new Thickness(10, 0, 0, 0);
        combineText.MaxWidth = 280;
        combineText.VerticalAlignment = VerticalAlignment.Center;
        combineRow.Children.Add(combineText);
        _combineLine = combineRow;
        DockPanel.SetDock(combineRow, Dock.Left);
        footer.Children.Add(combineRow);

        _done = Ui.Button("PrimaryButton", "ovl.picker.done", "Icon.Check", (_, _) => Close());
        DockPanel.SetDock(_done, Dock.Right);
        footer.Children.Add(_done);
        _add = new Button();
        _add.SetResourceReference(StyleProperty, "PrimaryButton");
        _addText = new TextBlock();
        _add.Content = _addText;
        _add.Click += (_, _) => AddPicked();
        DockPanel.SetDock(_add, Dock.Right);
        footer.Children.Add(_add);
        _cancel = Ui.Button("SecondaryButton", "ovl.picker.cancel", null, (_, _) => Close());
        _cancel.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(_cancel, Dock.Right);
        footer.Children.Add(_cancel);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        _nothing = Ui.Hint("ovl.picker.nothing", new Thickness(4, 10, 0, 0));
        _nothing.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(_nothing, Dock.Top);
        root.Children.Add(_nothing);

        _list = new ListBox { MinHeight = 160 };
        _list.SetResourceReference(StyleProperty, "BareList");
        _list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "OvEd.PickerItem");
        _list.SetResourceReference(ItemsControl.ItemTemplateProperty, "OvEd.PickerRow");
        _list.PreviewMouseLeftButtonUp += OnRowClick;
        _list.PreviewMouseLeftButtonDown += OnRowPress;
        _list.PreviewMouseMove += OnRowMove;
        root.Children.Add(_list);

        sheet.Child = root;
        Children.Add(sheet);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    /// <summary>Open (also while it steps aside for a drag onto the board).</summary>
    public bool IsOpen => Visibility == Visibility.Visible || _suspended;

    /// <summary>The picker opened / closed (the page widens its sensor demand while it is open).</summary>
    public event Action<bool>? OpenChanged;

    /// <summary>Sensor ids the open picker shows values for.</summary>
    public IEnumerable<string> SensorIds => _all.Where(r => r.Sensor is not null).Select(r => r.Sensor!.Id);

    public void Open(bool advanced)
    {
        _advanced = advanced;
        _title.Text = Loc.T(advanced ? "ovl.picker.title.add" : "ovl.picker.title");
        _hint.Text = Loc.T(advanced ? "ovl.picker.hint.add" : "ovl.picker.hint");
        _done.Visibility = advanced ? Visibility.Collapsed : Visibility.Visible;
        _add.Visibility = _cancel.Visibility = _combineLine.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        _combine.IsChecked = false;
        _search.Text = "";
        Build();
        Visibility = Visibility.Visible;
        UpdateValues();
        UpdateFooter();
        OpenChanged?.Invoke(true);
        Dispatcher.BeginInvoke(() => _search.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    public void Close()
    {
        if (!IsOpen) return;
        _suspended = false;
        _pressRow = null;
        Visibility = Visibility.Collapsed;
        _all = [];
        _list.ItemsSource = null;
        OpenChanged?.Invoke(false);
    }

    /// <summary>Live values (editor tick).</summary>
    public void UpdateValues()
    {
        if (!IsOpen) return;
        foreach (var row in _all)
            if (row.Sensor is { } s) row.Value = _ctx.Format(s, _ctx.Feed.Value(s.Id));
    }

    /// <summary>Re-reads which sensors are on the overlay (simple mode).</summary>
    public void Sync()
    {
        if (!IsOpen || _advanced) return;
        foreach (var row in _all)
            if (row.Sensor is { } s) row.SetQuiet(_ctx.Model.HasSensor(s.Id));
    }

    private void Build()
    {
        var rows = new List<PickerRow>();
        var hub = _ctx.Hub;
        var byHw = hub.Sensors.GroupBy(s => s.HardwareId).ToDictionary(g => g.Key, g => g.OrderBy(s => s.Order).ToList(), StringComparer.Ordinal);
        foreach (var hw in hub.Hardware)
        {
            if (!byHw.TryGetValue(hw.Id, out var sensors) || sensors.Count == 0) continue;
            var family = SensorPresentation.FamilyLabel(hw.Kind);
            rows.Add(new PickerRow { IsHeader = true, Title = hw.Name, IconKey = SensorPresentation.IconKey(hw.Kind), Search = (hw.Name + " " + family).ToLowerInvariant() });
            foreach (var s in sensors)
            {
                var kind = SensorPresentation.KindLabel(s.Kind);
                var row = new PickerRow
                {
                    Title = s.Name,
                    Sub = kind,
                    IconKey = SensorPresentation.IconKey(s.Kind),
                    Sensor = s,
                    Search = (s.Name + " " + kind + " " + hw.Name + " " + family).ToLowerInvariant(),
                };
                if (!_advanced) row.SetQuiet(_ctx.Model.HasSensor(s.Id));
                row.Toggled = OnToggled;
                rows.Add(row);
            }
        }
        _all = rows;
        Filter();
    }

    private void Filter()
    {
        var q = _search.Text.Trim().ToLowerInvariant();
        IEnumerable<PickerRow> visible = _all;
        if (q.Length > 0)
        {
            var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var result = new List<PickerRow>();
            PickerRow? header = null;
            foreach (var r in _all)
            {
                if (r.IsHeader)
                {
                    header = r;
                    continue;
                }
                if (!words.All(w => r.Search.Contains(w, StringComparison.Ordinal))) continue;
                if (header is not null)
                {
                    result.Add(header);
                    header = null;
                }
                result.Add(r);
            }
            visible = result;
        }
        var list = visible.ToList();
        _list.ItemsSource = list;
        _nothing.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        // a click anywhere on a sensor row flips its switch (the switch itself handles its own click)
        if (e.OriginalSource is not DependencyObject d) return;
        for (var p = d; p is not null && p != _list; p = VisualTreeHelper.GetParent(p))
        {
            if (p is CheckBox) return;
            if (p is ListBoxItem { DataContext: PickerRow { IsHeader: false } row })
            {
                row.IsChecked = !row.IsChecked;
                e.Handled = true;
                return;
            }
        }
    }

    // ───────────── dragging a sensor onto the board (advanced) ─────────────

    private void OnRowPress(object sender, MouseButtonEventArgs e)
    {
        _pressRow = null;
        if (!_advanced || _ctx.SensorDragStarted is null || e.OriginalSource is not DependencyObject d) return;
        for (var p = d; p is not null && p != _list; p = VisualTreeHelper.GetParent(p))
        {
            if (p is CheckBox) return;
            if (p is ListBoxItem { DataContext: PickerRow { IsHeader: false, Sensor: not null } row })
            {
                _pressRow = row;
                _pressAt = e.GetPosition(this);
                return;
            }
        }
    }

    private void OnRowMove(object sender, MouseEventArgs e)
    {
        if (_pressRow is not { Sensor: { } sensor } row) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _pressRow = null;
            return;
        }
        if ((e.GetPosition(this) - _pressAt).Length < 6) return;
        _pressRow = null;
        if (_ctx.SensorDragStarted is not { } start) return;
        // the sheet steps aside: the board under it takes the drag
        _suspended = true;
        Visibility = Visibility.Hidden;
        start([sensor.Id], row.Title);
    }

    /// <summary>A drag out of the picker ended: the sheet is back, as it was.</summary>
    public void ResumeAfterDrag()
    {
        if (!_suspended) return;
        _suspended = false;
        Visibility = Visibility.Visible;
        UpdateValues();
        UpdateFooter();
    }

    private void OnToggled(PickerRow row, bool on)
    {
        if (row.Sensor is not { } s) return;
        if (!_advanced)
        {
            if (on != _ctx.Model.HasSensor(s.Id)) _ctx.Model.ToggleSensor(s.Id);
            return;
        }
        UpdateFooter();
    }

    private List<SensorDescriptor> Picked => _all.Where(r => r.IsChecked && r.Sensor is not null).Select(r => r.Sensor!).ToList();

    private void UpdateFooter()
    {
        if (!_advanced) return;
        var picked = Picked;
        _add.IsEnabled = picked.Count > 0;
        _addText.Text = picked.Count > 0 ? Loc.T("ovl.picker.addN", picked.Count) : Loc.T("ovl.picker.add");
        var sameKind = picked.Count > 1 && picked.All(s => s.Kind == picked[0].Kind);
        _combine.IsEnabled = sameKind;
        if (!sameKind) _combine.IsChecked = false;
    }

    private void AddPicked()
    {
        var picked = Picked;
        if (picked.Count == 0) return;
        if (_combine.IsChecked == true && picked.Count > 1) _ctx.Model.Add(picked.Select(s => s.Id).ToList());
        else
            foreach (var s in picked) _ctx.Model.Add([s.Id]);
        Added?.Invoke();
        Close();
    }

    /// <summary>Items were added (advanced mode rebuilds its list).</summary>
    public event Action? Added;
}
