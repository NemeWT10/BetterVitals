using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using BetterVitals.Core.Formatting;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.Services;
using BetterVitals.UI.Controls;
using BetterVitals.UI.Pages.Shared;

namespace BetterVitals.UI.Pages.Overview;

/// <summary>State badge of a card.</summary>
public enum CardState
{
    Live,

    /// <summary>The device sleeps (dGPU in D3, HDD in standby): values are NaN although they were read.</summary>
    Asleep,

    /// <summary>The game card while no game runs.</summary>
    NoGame,
}

/// <summary>One big value on a card: friendly title, value in its heat color, sparkline.</summary>
internal sealed class KeyTile : Border
{
    private readonly TextBlock _label;
    private readonly TextBlock _value;
    private readonly TextBlock _unit;
    private int _lastSequence = -1;

    public KeyTile(KeyValue key)
    {
        Key = key;
        Padding = new Thickness(11, 9, 11, 8);
        CornerRadius = new CornerRadius(11);
        SetResourceReference(BackgroundProperty, "Brush.BgAlt");
        SetResourceReference(BorderBrushProperty, "Brush.Border");
        BorderThickness = new Thickness(1);

        var stack = new StackPanel();
        // short title + the verbatim sensor name (the card header already says which component)
        var titles = new StackPanel { Background = System.Windows.Media.Brushes.Transparent, Cursor = Cursors.Help };
        _label = Ui.Text(12, "Brush.TextDim", FontWeights.SemiBold);
        _label.TextTrimming = TextTrimming.CharacterEllipsis;
        titles.Children.Add(_label);
        var name = Ui.Text(11, "Brush.TextFaint");
        name.Text = key.Sensor.Name;
        // verbatim names can be long ("D3D Shared Memory Used"): up to two lines in a narrow tile, then ellipsis
        name.TextWrapping = TextWrapping.Wrap;
        name.MaxHeight = 30;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        titles.Children.Add(name);
        Explain.Explain.SetSensor(titles, key.Sensor);
        stack.Children.Add(titles);

        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        _value = Ui.Number(24, "Brush.Text", FontWeights.SemiBold);
        _value.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Display");
        line.Children.Add(_value);
        _unit = Ui.Text(12, "Brush.TextDim");
        _unit.Margin = new Thickness(4, 0, 0, 4);
        _unit.VerticalAlignment = VerticalAlignment.Bottom;
        line.Children.Add(_unit);
        stack.Children.Add(line);

        var spark = new Sparkline { Height = 26, Margin = new Thickness(0, 6, 0, 0), Series = Series, StrokeThickness = 1.6 };
        spark.SetResourceReference(Sparkline.StrokeProperty, "Brush.Cat." + key.Sensor.HardwareKind);
        stack.Children.Add(spark);
        Child = stack;
        Relabel();
    }

    public KeyValue Key { get; }

    /// <summary>One point per engine tick (a slow sensor holds its value), the last minute.</summary>
    public SparkSeries Series { get; } = new(60);

    public string ValueText => _value.Text;

    public string LabelText => _label.Text;

    /// <summary>Short title: "overview.key.&lt;friendly&gt;" when the parameter is known, else what it measures.</summary>
    public static string TitleFor(KeyValue key)
    {
        if (key.FriendlyKey is { } k)
        {
            var shortKey = "overview.key." + k["friendly.".Length..];
            return Loc.Has(shortKey) ? Loc.T(shortKey) : Ui.Friendly(k);
        }
        return SensorPresentation.KindLabel(key.Sensor.Kind);
    }

    public void Relabel() => _label.Text = TitleFor(Key);

    public void Refresh(SensorFrame frame, TemperatureUnit unit)
    {
        var i = Key.Sensor.Index;
        var v = frame[i];
        var s = Key.Sensor;
        var decimals = s.Kind is SensorKind.Temperature or SensorKind.Load or SensorKind.Clock or SensorKind.Power or SensorKind.Fan
            or SensorKind.Fps or SensorKind.Control or SensorKind.Level ? 0 : (int?)null;
        _value.Text = ValueFormat.ToText(s.Kind, v, Loc.I.Culture, out var u, decimals, unit, s.Unit);
        if (!float.IsNaN(v) || _unit.Text.Length == 0) _unit.Text = u;
        _value.SetResourceReference(TextBlock.ForegroundProperty, SensorPresentation.HeatBrushKey(SensorPresentation.Heat(s, v)));
        if (frame.Sequence != _lastSequence)
        {
            _lastSequence = frame.Sequence;
            Series.Push(v);
        }
    }
}

/// <summary>
/// The big values of a card side by side; when a value would get narrower than <see cref="MinTile"/> (four values on
/// a two-column grid) they wrap into two rows instead, so titles and numbers never get cut.
/// </summary>
internal sealed class TileGrid : System.Windows.Controls.Panel
{
    public const double MinTile = 108;
    public const double Gap = 8;

    public int ColumnsFor(double width, int count)
    {
        if (count <= 1 || double.IsInfinity(width)) return Math.Max(1, count);
        var cols = count;
        while (cols > 1 && (width - Gap * (cols - 1)) / cols < MinTile) cols = (cols + 1) / 2;
        return cols;
    }

    protected override Size MeasureOverride(Size available)
    {
        var n = InternalChildren.Count;
        if (n == 0) return default;
        var width = double.IsInfinity(available.Width) ? MinTile * n + Gap * (n - 1) : available.Width;
        var cols = ColumnsFor(width, n);
        var w = (width - Gap * (cols - 1)) / cols;
        double rowH = 0;
        foreach (UIElement c in InternalChildren)
        {
            c.Measure(new Size(w, double.PositiveInfinity));
            rowH = Math.Max(rowH, c.DesiredSize.Height);
        }
        var rows = (n + cols - 1) / cols;
        return new Size(width, rows * rowH + (rows - 1) * Gap);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var n = InternalChildren.Count;
        if (n == 0) return final;
        var cols = ColumnsFor(final.Width, n);
        var w = (final.Width - Gap * (cols - 1)) / cols;
        double rowH = 0;
        foreach (UIElement c in InternalChildren) rowH = Math.Max(rowH, c.DesiredSize.Height);
        for (var i = 0; i < n; i++)
            InternalChildren[i].Arrange(new Rect(i % cols * (w + Gap), i / cols * (rowH + Gap), w, rowH));
        return final;
    }
}

/// <summary>
/// A hardware card of the Overview: icon, verbatim name, family + sensor count, state badges, 2–4 big values with
/// sparklines; expanded, every sensor grouped by kind with Now / Min / Max / Avg since start and the Historia / Nakładka
/// switches (the shared sensor row). Row models are created on first expansion and kept while the page lives.
/// </summary>
internal sealed class HardwareCard : Border
{
    private readonly SensorActions _actions;
    private readonly Action<HardwareCard> _toggled;
    private readonly TextBlock _sub;
    private readonly Border _sleepBadge;
    private readonly TextBlock _sleepText;
    private readonly Border _limitedBadge;
    private readonly Button _expander;
    private readonly StackPanel _body;
    private readonly List<KeyTile> _tiles = [];
    private StackPanel? _details;
    private List<SensorRowModel>? _rows;
    private readonly List<(TextBlock Label, KindGroup Group, int Count)> _groupLabels = [];

    public HardwareCard(HardwareGroup group, IReadOnlyList<KeyValue> keys, SensorActions actions, bool expanded, Action<HardwareCard> toggled)
    {
        Group = group;
        _actions = actions;
        _toggled = toggled;
        SetResourceReference(StyleProperty, "Card");
        Margin = new Thickness(0);
        Padding = new Thickness(16, 14, 16, 16);
        Resources.MergedDictionaries.Add(Ui.Theme);

        _body = new StackPanel();

        // ── header ──
        var header = new DockPanel { Background = System.Windows.Media.Brushes.Transparent, Cursor = Cursors.Hand, LastChildFill = true };
        _expander = new Button { Style = Ui.Style("Bv.Expander"), Margin = new Thickness(8, 0, -4, 0) };
        _expander.Click += (_, _) => _toggled(this);
        DockPanel.SetDock(_expander, Dock.Right);
        header.Children.Add(_expander);

        var badges = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _sleepText = new TextBlock();
        _sleepBadge = new Border { Style = Ui.Style("Bv.Badge"), Tag = "info", Child = _sleepText, Visibility = Visibility.Collapsed, Margin = new Thickness(6, 0, 0, 0) };
        badges.Children.Add(_sleepBadge);
        var limitedText = new TextBlock();
        limitedText.SetBinding(TextBlock.TextProperty, Ui.Tr("overview.badge.limited"));
        _limitedBadge = new Border { Style = Ui.Style("Bv.Badge"), Tag = "warn", Child = limitedText, Visibility = Visibility.Collapsed, Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Help };
        Explain.Explain.SetKey(_limitedBadge, "overview.badge.limited.info");
        badges.Children.Add(_limitedBadge);
        DockPanel.SetDock(badges, Dock.Right);
        header.Children.Add(badges);

        var tile = Ui.IconTile(SensorPresentation.IconKey(group.Kind), "Brush.Cat." + group.Kind, "Brush.CatSoft." + group.Kind, 38, 20);
        tile.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(tile, Dock.Left);
        header.Children.Add(tile);

        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = Ui.Text(14.5, "Brush.Text", FontWeights.SemiBold);
        name.Text = group.Name; // verbatim
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.ToolTip = group.Name;
        titles.Children.Add(name);
        _sub = Ui.Text(12, "Brush.TextDim");
        _sub.Margin = new Thickness(0, 1, 0, 0);
        _sub.TextTrimming = TextTrimming.CharacterEllipsis;
        titles.Children.Add(_sub);
        header.Children.Add(titles);
        header.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInside(d, _expander)) return;
            _toggled(this);
        };
        _body.Children.Add(header);

        // ── key values ──
        if (keys.Count > 0)
        {
            var grid = new TileGrid { Margin = new Thickness(0, 14, 0, 0) };
            foreach (var k in keys)
            {
                var t = new KeyTile(k);
                _tiles.Add(t);
                grid.Children.Add(t);
            }
            _body.Children.Add(grid);
        }

        Child = _body;
        Relabel();
        SetExpanded(expanded, animate: false);
    }

    public HardwareGroup Group { get; }

    public bool IsExpanded { get; private set; }

    public CardState State { get; private set; }

    public bool IsLimited { get; private set; }

    internal IReadOnlyList<KeyTile> Tiles => _tiles;

    internal IReadOnlyList<SensorRowModel> Rows => _rows ?? (IReadOnlyList<SensorRowModel>)[];

    /// <summary>What the engine must read for this card now: the big values, or every sensor when expanded.</summary>
    public IEnumerable<string> DemandIds
        => IsExpanded ? Group.Sensors.Select(s => s.Id) : _tiles.Select(t => t.Key.Sensor.Id);

    public void SetExpanded(bool expanded, bool animate = true)
    {
        IsExpanded = expanded;
        CardGrid.SetIsWide(this, expanded);
        _expander.Tag = expanded ? "True" : "False";
        _expander.SetBinding(ToolTipProperty, Ui.Tr(expanded ? "overview.collapse" : "overview.expand"));
        if (expanded)
        {
            _details ??= BuildDetails();
            if (!_body.Children.Contains(_details)) _body.Children.Add(_details);
            if (animate) Motion.Appear(_details, rise: 6, fromScale: 1, ms: 240);
        }
        else if (_details is not null)
        {
            _body.Children.Remove(_details);
        }
    }

    private StackPanel BuildDetails()
    {
        var details = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var divider = new System.Windows.Shapes.Rectangle { Margin = new Thickness(0, 0, 0, 10) };
        divider.SetResourceReference(StyleProperty, "Divider");
        details.Children.Add(divider);
        details.Children.Add(new SensorColumnsHeader(SensorRowLayout.Stats));

        _rows = Group.Sensors.Select(s => new SensorRowModel(s, Group.Name, _actions)).ToList();
        if (_actions.Settings is { } settings)
            foreach (var r in _rows) r.Sync(settings);
        foreach (var g in SensorGroups.Order)
        {
            var rows = _rows.Where(r => r.Group == g).ToList();
            if (rows.Count == 0) continue;
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(RowColumns.SidePadding, 12, 0, 4) };
            head.Children.Add(Ui.Glyph(SensorGroups.IconKey(g), 14, "Brush.TextFaint"));
            var label = Ui.Text(12, "Brush.TextDim", FontWeights.SemiBold);
            label.Margin = new Thickness(7, 0, 0, 0);
            head.Children.Add(label);
            _groupLabels.Add((label, g, rows.Count));
            details.Children.Add(head);
            foreach (var r in rows) details.Children.Add(new SensorRowView(SensorRowLayout.Stats) { DataContext = r });
        }
        RelabelGroups();
        return details;
    }

    /// <summary>Texts that follow the language (values re-format on the next refresh).</summary>
    public void Relabel()
    {
        _sub.Text = $"{SensorPresentation.FamilyLabel(Group.Kind)} · {Loc.Plural("sensors.count", Group.Sensors.Count)}";
        foreach (var t in _tiles) t.Relabel();
        if (_rows is not null)
            foreach (var r in _rows) r.Relabel();
        RelabelGroups();
        UpdateBadges();
    }

    private void RelabelGroups()
    {
        foreach (var (label, g, count) in _groupLabels) label.Text = $"{Loc.T(SensorGroups.LabelKey(g))} · {count}";
    }

    public void Sync(AppSettings settings)
    {
        if (_rows is null) return;
        foreach (var r in _rows) r.Sync(settings);
    }

    public void Refresh(SensorFrame frame, SessionStats stats, TemperatureUnit unit, IReadOnlyList<(string Source, SourceStatus Status)> statuses)
    {
        foreach (var t in _tiles) t.Refresh(frame, unit);
        if (IsExpanded && _rows is not null)
            foreach (var r in _rows)
                if (r.IsShown) r.Refresh(frame, stats, unit);

        State = StateOf(Group, frame);
        IsLimited = Group.Members.Any(m => statuses.Any(s => s.Source == m.Source && s.Status.State == SourceState.Limited));
        UpdateBadges();
    }

    /// <summary>"uśpiona" (the card), "uśpiony" (the drive), "w uśpieniu" (anything else).</summary>
    public static string AsleepKey(HardwareKind kind) => kind switch
    {
        HardwareKind.Gpu => "overview.badge.asleep.gpu",
        HardwareKind.Storage => "overview.badge.asleep.drive",
        _ => "overview.badge.asleep",
    };

    private void UpdateBadges()
    {
        _sleepBadge.Visibility = State == CardState.Live ? Visibility.Collapsed : Visibility.Visible;
        _sleepText.Text = State == CardState.NoGame ? Loc.T("overview.badge.noGame") : Loc.T(AsleepKey(Group.Kind));
        _sleepBadge.Tag = "info";
        Explain.Explain.SetKey(_sleepBadge, State == CardState.NoGame ? "overview.badge.noGame.info" : "overview.badge.asleep.info");
        _limitedBadge.Visibility = IsLimited ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Asleep: everything that was ever read is NaN now (the device sleeps; the engine keeps NaN, never wakes it).
    /// The game card says "no game" instead. Sensors never read yet do not count.
    /// </summary>
    public static CardState StateOf(HardwareGroup group, SensorFrame frame)
    {
        var read = 0;
        foreach (var s in group.Sensors)
        {
            var i = s.Index;
            if ((uint)i >= (uint)frame.Count || frame.UpdatedAt[i] == 0) continue;
            read++;
            if (!float.IsNaN(frame.Values[i])) return CardState.Live;
        }
        if (read == 0) return CardState.Live;
        return group.Kind == HardwareKind.Game ? CardState.NoGame : CardState.Asleep;
    }

    private static bool IsInside(DependencyObject d, DependencyObject ancestor)
    {
        for (var cur = d; cur is not null;
             cur = cur is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (ReferenceEquals(cur, ancestor)) return true;
        return false;
    }
}
