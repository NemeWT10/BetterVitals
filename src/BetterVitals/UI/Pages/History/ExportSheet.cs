using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BetterVitals.Core;
using BetterVitals.Core.History;
using BetterVitals.Core.Sensors;
using BetterVitals.Localization;
using BetterVitals.UI.Charts;
using BetterVitals.UI.Controls;

namespace BetterVitals.UI.Pages.History;

/// <summary>A sensor the export can include.</summary>
public sealed record ExportCandidate(string Id, string Name, string Unit, SensorDescriptor? Descriptor, bool Selected);

/// <summary>
/// "Export to a file" as an in-page sheet (PROJEKT §6: as light as possible): pick sensors (the chart's by default),
/// range (visible / selection / everything), resolution (every sample / 1 min / 1 h with min–avg–max), CSV with
/// "Compress (.zip)" on by default, the estimated size before saving, then a progress bar, a toast with "Open folder".
/// </summary>
public sealed class ExportSheet : Grid
{
    private readonly IHistoryQuery _history;
    private readonly Func<long> _now;
    private readonly TimeZoneInfo _zone;
    private readonly TimeView _visible;
    private readonly (long From, long To)? _selection;
    private readonly List<(ExportCandidate Item, ToggleButton Chip)> _chips = [];
    private readonly RadioButton _rangeVisible = new(), _rangeSelection = new(), _rangeAll = new();
    private readonly RadioButton _resRaw = new(), _resMinute = new(), _resHour = new();
    private readonly CheckBox _zip = new() { IsChecked = true };
    private readonly TextBlock _rangeText = new() { FontSize = 12, Margin = new Thickness(2, 6, 0, 0) };
    private readonly TextBlock _estimate = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button _save = new() { Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _cancel = new();
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Grid _progress = new() { Visibility = Visibility.Collapsed };
    private readonly Border _bar = new() { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    private readonly TextBlock _progressText = new() { FontSize = 12.5, Margin = new Thickness(0, 0, 0, 6) };
    private readonly Border _card = new();
    private DispatcherTimer? _estimateTimer;
    private CancellationTokenSource? _work;
    private int _estimateVersion;

    public ExportSheet(IHistoryQuery history, IReadOnlyList<ExportCandidate> candidates, TimeView visible, (long From, long To)? selection,
        Func<long> now, TimeZoneInfo zone)
    {
        _history = history;
        _visible = visible;
        _selection = selection;
        _now = now;
        _zone = zone;
        Resources.MergedDictionaries.Add(HistoryStyles.Shared);

        // dimmed backdrop; a click on it closes the sheet
        var dim = new Border();
        dim.SetResourceReference(Border.BackgroundProperty, "Brush.Dim");
        dim.MouseLeftButtonDown += (_, _) => Close();
        Children.Add(dim);

        _card.SetResourceReference(StyleProperty, "Ov.Panel");
        _card.Padding = new Thickness(24, 20, 24, 20);
        _card.Width = 560;
        _card.HorizontalAlignment = HorizontalAlignment.Center;
        _card.VerticalAlignment = VerticalAlignment.Center;
        _card.Margin = new Thickness(24);
        Children.Add(_card);

        var root = new StackPanel();
        var titleRow = new DockPanel();
        var close = new Button { Width = 30, Height = 30, ToolTip = Loc.T("history.export.cancel") };
        close.SetResourceReference(StyleProperty, "IconButton");
        var x = new Icon { Size = 15 };
        x.SetResourceReference(Icon.DataProperty, "Icon.Close");
        close.Content = x;
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        titleRow.Children.Add(close);
        var badge = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 12, 0) };
        badge.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSoft");
        var bi = new Icon { Size = 18 };
        bi.SetResourceReference(Icon.DataProperty, "Icon.Export");
        bi.SetResourceReference(Icon.ForegroundProperty, "Brush.AccentText");
        badge.Child = bi;
        DockPanel.SetDock(badge, Dock.Left);
        titleRow.Children.Add(badge);
        var title = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Display");
        title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelText");
        title.SetBinding(TextBlock.TextProperty, Tr("history.export.title"));
        titleRow.Children.Add(title);
        root.Children.Add(titleRow);
        var subtitle = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(46, 2, 0, 14) };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelTextDim");
        subtitle.SetBinding(TextBlock.TextProperty, Tr("history.export.subtitle"));
        root.Children.Add(subtitle);

        // sensors
        root.Children.Add(Label("history.export.sensors"));
        var wrap = new WrapPanel();
        foreach (var c in candidates)
        {
            var chip = new ToggleButton { IsChecked = c.Selected, Margin = new Thickness(0, 0, 6, 6) };
            chip.SetResourceReference(StyleProperty, "ChipToggle");
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var name = new TextBlock { Text = c.Name, FontSize = 12.5, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
            if (c.Descriptor is not null) Explain.Explain.SetSensor(name, c.Descriptor);
            content.Children.Add(name);
            if (c.Unit.Length > 0)
            {
                var u = new TextBlock { Text = c.Unit, FontSize = 11.5, Margin = new Thickness(5, 0, 0, 0), Opacity = 0.75 };
                content.Children.Add(u);
            }
            chip.Content = content;
            chip.Checked += (_, _) => Changed();
            chip.Unchecked += (_, _) => Changed();
            wrap.Children.Add(chip);
            _chips.Add((c, chip));
        }
        root.Children.Add(new ScrollViewer { Content = wrap, MaxHeight = 128, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 10), Focusable = false });

        // range
        root.Children.Add(Label("history.export.range"));
        root.Children.Add(Segments("export-range", (_rangeVisible, "history.export.range.visible"), (_rangeSelection, "history.export.range.selection"),
            (_rangeAll, "history.export.range.all")));
        _rangeSelection.IsEnabled = selection is not null;
        (selection is not null ? _rangeSelection : _rangeVisible).IsChecked = true;
        _rangeText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelTextDim");
        root.Children.Add(_rangeText);

        // resolution
        var resLabel = Label("history.export.resolution");
        resLabel.Margin = new Thickness(2, 14, 0, 6);
        root.Children.Add(resLabel);
        root.Children.Add(Segments("export-res", (_resRaw, "history.export.res.raw"), (_resMinute, "history.export.res.minute"), (_resHour, "history.export.res.hour")));
        // the lightest sensible default: every sample for short ranges, minutes beyond a day
        (visible.Span > TimeSpan.TicksPerDay * 2 ? _resMinute : _resRaw).IsChecked = true;
        var resHint = new TextBlock { FontSize = 11.5, Margin = new Thickness(2, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        resHint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelTextDim");
        resHint.SetBinding(TextBlock.TextProperty, Tr("history.export.res.hint"));
        root.Children.Add(resHint);

        // format
        var zipRow = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        _zip.SetResourceReference(StyleProperty, "Switch");
        _zip.Checked += (_, _) => Changed();
        _zip.Unchecked += (_, _) => Changed();
        DockPanel.SetDock(_zip, Dock.Right);
        zipRow.Children.Add(_zip);
        var zipText = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        var zipTitle = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold };
        zipTitle.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelText");
        zipTitle.SetBinding(TextBlock.TextProperty, Tr("history.export.zip"));
        zipText.Children.Add(zipTitle);
        var zipDesc = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        zipDesc.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelTextDim");
        zipDesc.SetBinding(TextBlock.TextProperty, Tr("history.export.zip.desc"));
        zipText.Children.Add(zipDesc);
        zipRow.Children.Add(zipText);
        root.Children.Add(zipRow);

        var sep = new Rectangle { Margin = new Thickness(-24, 18, -24, 14) };
        sep.SetResourceReference(StyleProperty, "Divider");
        sep.SetResourceReference(Shape.FillProperty, "Brush.PanelSeparator");
        root.Children.Add(sep);

        // footer: estimate + buttons, or the progress
        var footer = new Grid();
        var normal = new DockPanel();
        _cancel.SetResourceReference(StyleProperty, "GhostButton");
        _cancel.SetBinding(ContentControl.ContentProperty, Tr("history.export.cancel"));
        _cancel.Click += (_, _) => Close();
        _save.SetResourceReference(StyleProperty, "PrimaryButton");
        _save.SetBinding(ContentControl.ContentProperty, Tr("history.export.save"));
        _save.Click += async (_, _) => await SaveAsync();
        _buttons.Children.Add(_cancel);
        _buttons.Children.Add(_save);
        DockPanel.SetDock(_buttons, Dock.Right);
        normal.Children.Add(_buttons);
        _estimate.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelTextDim");
        normal.Children.Add(_estimate);
        footer.Children.Add(normal);

        var stop = new Button { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -6, 0, 0) };
        stop.SetResourceReference(StyleProperty, "GhostButton");
        stop.SetBinding(ContentControl.ContentProperty, Tr("history.export.stop"));
        stop.Click += (_, _) => _work?.Cancel();
        var prog = new StackPanel();
        _progressText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelText");
        prog.Children.Add(_progressText);
        var track = new Border { Height = 6, CornerRadius = new CornerRadius(3) };
        track.SetResourceReference(Border.BackgroundProperty, "Brush.ToggleOff");
        _bar.SetResourceReference(Border.BackgroundProperty, "Brush.AccentGradientH");
        track.Child = _bar;
        prog.Children.Add(track);
        _progress.Children.Add(prog);
        _progress.Children.Add(stop);
        footer.Children.Add(_progress);
        normal.SetBinding(VisibilityProperty, new Binding(nameof(Visibility)) { Source = _progress, Converter = new Invert() });
        root.Children.Add(footer);

        _card.Child = root;
        Focusable = true;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
        Changed();
    }

    /// <summary>Asks where to save (null = cancelled). The page passes the SaveFileDialog; tests a fake.</summary>
    public Func<string, bool, string?> PickPath { get; set; } = static (_, _) => null;

    /// <summary>The file was written (full path).</summary>
    public event Action<string>? Saved;

    /// <summary>Writing failed (message for the toast).</summary>
    public event Action<string>? Failed;

    /// <summary>The sheet wants to go (Cancel, Esc, backdrop, after saving).</summary>
    public event Action? Closed;

    public bool IsBusy => _work is not null;

    /// <summary>The estimate line as shown (tests).</summary>
    public string EstimateText => _estimate.Text;

    public string RangeText => _rangeText.Text;

    public Border Card => _card;

    public void SelectRange(HistoryModel.ExportRange r) => (r switch
    {
        HistoryModel.ExportRange.Selection => _rangeSelection,
        HistoryModel.ExportRange.All => _rangeAll,
        _ => _rangeVisible,
    }).IsChecked = true;

    public void SelectResolution(ExportResolution r) => (r switch
    {
        ExportResolution.Minute => _resMinute,
        ExportResolution.Hour => _resHour,
        _ => _resRaw,
    }).IsChecked = true;

    public bool Zip
    {
        get => _zip.IsChecked == true;
        set => _zip.IsChecked = value;
    }

    public void SetSelected(string id, bool selected)
    {
        foreach (var (item, chip) in _chips)
            if (item.Id == id) chip.IsChecked = selected;
    }

    public HistoryModel.ExportRange Range => _rangeAll.IsChecked == true ? HistoryModel.ExportRange.All
        : _rangeSelection.IsChecked == true ? HistoryModel.ExportRange.Selection : HistoryModel.ExportRange.Visible;

    public ExportResolution Resolution => _resHour.IsChecked == true ? ExportResolution.Hour
        : _resMinute.IsChecked == true ? ExportResolution.Minute : ExportResolution.Raw;

    /// <summary>The request the current choices make.</summary>
    public ExportRequest BuildRequest()
    {
        var ids = _chips.Where(c => c.Chip.IsChecked == true).Select(c => c.Item.Id).ToList();
        (DateTime, DateTime)? coverage = null;
        if (Range == HistoryModel.ExportRange.All)
        {
            foreach (var id in ids)
            {
                if (_history.GetCoverage(id) is not { } c) continue;
                coverage = coverage is { } cur ? (c.FromUtc < cur.Item1 ? c.FromUtc : cur.Item1, c.ToUtc > cur.Item2 ? c.ToUtc : cur.Item2) : c;
            }
        }
        return HistoryModel.BuildExport(ids, Range, _visible, _selection, coverage, _now(), Resolution, Zip);
    }

    private void Changed()
    {
        var r = BuildRequest();
        _rangeText.Text = HistoryModel.RangeText(r.FromUtc.Ticks, r.ToUtc.Ticks, _now(), _zone, Loc.I.Culture) + " · " +
                          HistoryModel.Duration(r.ToUtc - r.FromUtc);
        _save.IsEnabled = r.SensorIds.Count > 0 && !IsBusy;
        if (r.SensorIds.Count == 0)
        {
            _estimate.Text = Loc.T("history.export.noSensors");
            return;
        }
        _estimate.Text = Loc.T("history.export.estimating");
        var version = ++_estimateVersion;
        _estimateTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(140), DispatcherPriority.Background, (_, _) => RunEstimate(), Dispatcher) { IsEnabled = false };
        _estimateTimer.Stop();
        _estimateTimer.Tag = version;
        _estimateTimer.Start();
    }

    /// <summary>Estimates the size now (the debounce timer calls it; tests too).</summary>
    internal Task RunEstimate()
    {
        _estimateTimer?.Stop();
        var version = _estimateVersion;
        var request = BuildRequest();
        if (request.SensorIds.Count == 0) return Task.CompletedTask;
        return Task.Run(() => _history.EstimateExportBytes(request)).ContinueWith(t =>
        {
            var bytes = t.IsCompletedSuccessfully ? t.Result : -1;
            Dispatcher.BeginInvoke(() =>
            {
                if (version != _estimateVersion) return;
                _estimate.Text = bytes <= 0 ? Loc.T("history.export.noData") : Loc.T("history.export.estimate", "~" + HistoryModel.Bytes(bytes, Loc.I.Culture));
            });
        }, TaskScheduler.Default);
    }

    /// <summary>Save: file dialog, export with progress, then <see cref="Saved"/> (or <see cref="Failed"/>).</summary>
    public async Task SaveAsync()
    {
        if (IsBusy) return;
        var request = BuildRequest();
        if (request.SensorIds.Count == 0) return;
        var path = PickPath(HistoryModel.ExportFileName(request, _zone), request.Zip);
        if (string.IsNullOrEmpty(path)) return;

        var cts = new CancellationTokenSource();
        _work = cts;
        _progress.Visibility = Visibility.Visible;
        _save.IsEnabled = false;
        SetProgress(0);
        var progress = new Progress<double>(p => Dispatcher.BeginInvoke(() => SetProgress(p)));
        try
        {
            await using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: false))
                await _history.ExportAsync(request, fs, progress, cts.Token);
            SetProgress(1);
            _work = null;
            Saved?.Invoke(path);
            Close();
        }
        catch (OperationCanceledException)
        {
            TryDelete(path);
            _work = null;
            _progress.Visibility = Visibility.Collapsed;
            _save.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Log.Warn("history: export failed: " + ex.Message);
            TryDelete(path);
            _work = null;
            _progress.Visibility = Visibility.Collapsed;
            _save.IsEnabled = true;
            Failed?.Invoke(Loc.T("history.export.failed"));
        }
        finally
        {
            cts.Dispose();
        }
    }

    private void SetProgress(double p)
    {
        p = Math.Clamp(p, 0, 1);
        _progressText.Text = Loc.T("history.export.progress", p.ToString("P0", Loc.I.Culture));
        var width = (_progress.ActualWidth > 0 ? _progress.ActualWidth : _card.Width - 48);
        _bar.Width = Math.Max(6, width * p);
    }

    public void Close()
    {
        _work?.Cancel();
        _estimateTimer?.Stop();
        Closed?.Invoke();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static TextBlock Label(string key)
    {
        var t = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 6) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelTextDim");
        t.SetBinding(TextBlock.TextProperty, Tr(key));
        return t;
    }

    private Border Segments(string group, params (RadioButton Button, string Key)[] items)
    {
        var host = new Border();
        host.SetResourceReference(StyleProperty, "SegmentHost");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (b, key) in items)
        {
            b.GroupName = group + GetHashCode();
            b.SetResourceReference(StyleProperty, "Segment");
            b.SetBinding(ContentControl.ContentProperty, Tr(key));
            b.Checked += (_, _) => Changed();
            row.Children.Add(b);
        }
        host.Child = row;
        return host;
    }

    private static Binding Tr(string key) => new($"[{key}]") { Source = Loc.I, Mode = BindingMode.OneWay };

    private sealed class Invert : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => value is Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }
}
