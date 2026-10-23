using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using BetterVitals.Localization;
using BetterVitals.UI.Charts;
using BetterVitals.UI.Controls;

namespace BetterVitals.UI.Pages.History;

/// <summary>
/// "Custom…" range popover in the app's style: a month calendar where the first click picks the first day and the
/// second the last one (the days between light up), two time fields (UI culture: 24 h in Polish) and Show / Cancel.
/// Future days are disabled. Works in local time; <see cref="Applied"/> hands back UTC ticks.
/// </summary>
public sealed class RangePicker : Border
{
    private readonly TimeZoneInfo _zone;
    private readonly Func<long> _now;
    private readonly UniformGrid _days = new() { Columns = 7, Rows = 6 };
    private readonly UniformGrid _weekdays = new() { Columns = 7, Margin = new Thickness(0, 6, 0, 2) };
    private readonly TextBlock _month = new() { FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _summary = new() { FontSize = 12, Margin = new Thickness(2, 10, 2, 0), TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _fromTime = new() { Width = 92 };
    private readonly TextBox _toTime = new() { Width = 92 };
    private readonly Button _apply = new();
    private DateTime _shownMonth;
    private bool _pickingEnd;

    public RangePicker(TimeZoneInfo zone, Func<long> now)
    {
        _zone = zone;
        _now = now;
        Resources.MergedDictionaries.Add(HistoryStyles.Shared);
        SetResourceReference(StyleProperty, "Ov.Panel");
        Padding = new Thickness(14, 12, 14, 14);
        Width = 300;

        var root = new StackPanel();
        var title = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold };
        title.SetBinding(TextBlock.TextProperty, Tr("history.custom.title"));
        title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelText");
        root.Children.Add(title);
        var hint = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 2, 0, 8), TextWrapping = TextWrapping.Wrap };
        hint.SetBinding(TextBlock.TextProperty, Tr("history.custom.hint"));
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelTextDim");
        root.Children.Add(hint);

        var head = new DockPanel();
        var prev = NavButton("Icon.ChevronLeft", "history.custom.prev", -1);
        var next = NavButton("Icon.ChevronRight", "history.custom.next", 1);
        DockPanel.SetDock(prev, Dock.Left);
        DockPanel.SetDock(next, Dock.Right);
        head.Children.Add(prev);
        head.Children.Add(next);
        _month.TextAlignment = TextAlignment.Center;
        _month.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelText");
        head.Children.Add(_month);
        root.Children.Add(head);
        root.Children.Add(_weekdays);
        root.Children.Add(_days);

        var times = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        times.ColumnDefinitions.Add(new ColumnDefinition());
        times.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        times.ColumnDefinitions.Add(new ColumnDefinition());
        times.Children.Add(TimeField("history.custom.from", _fromTime, 0));
        times.Children.Add(TimeField("history.custom.to", _toTime, 2));
        root.Children.Add(times);

        _summary.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelTextDim");
        root.Children.Add(_summary);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Margin = new Thickness(0, 0, 6, 0) };
        cancel.SetResourceReference(StyleProperty, "GhostButton");
        cancel.SetBinding(ContentControl.ContentProperty, Tr("history.custom.cancel"));
        cancel.Click += (_, _) => Cancelled?.Invoke();
        buttons.Children.Add(cancel);
        _apply.SetResourceReference(StyleProperty, "PrimaryButton");
        _apply.SetBinding(ContentControl.ContentProperty, Tr("history.custom.apply"));
        _apply.Click += (_, _) => Apply();
        buttons.Children.Add(_apply);
        root.Children.Add(buttons);
        Child = root;

        _fromTime.TextChanged += (_, _) => Validate();
        _toTime.TextChanged += (_, _) => Validate();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _apply.IsEnabled)
            {
                Apply();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Cancelled?.Invoke();
                e.Handled = true;
            }
        };
    }

    /// <summary>First and last day picked (local dates).</summary>
    public DateTime FromDay { get; private set; }

    public DateTime ToDay { get; private set; }

    public CultureInfo Culture => Loc.I.Culture;

    /// <summary>The user chose a range (UTC ticks, From &lt; To).</summary>
    public event Action<long, long>? Applied;

    public event Action? Cancelled;

    /// <summary>Starts from the window shown now.</summary>
    public void Open(TimeView view)
    {
        var a = Local(view.From);
        var b = Local(view.To);
        FromDay = a.Date;
        ToDay = b.Date;
        _pickingEnd = false;
        _shownMonth = new DateTime(b.Year, b.Month, 1);
        _fromTime.Text = a.ToString(TimeTicks.ShortTime(Culture), Culture);
        _toTime.Text = b.ToString(TimeTicks.ShortTime(Culture), Culture);
        BuildWeekdays();
        BuildDays();
        Validate();
    }

    /// <summary>A day was clicked: the first click starts a new range, the second closes it (tests call it too).</summary>
    public void PickDay(DateTime day)
    {
        day = day.Date;
        if (!_pickingEnd)
        {
            FromDay = ToDay = day;
            _pickingEnd = true;
        }
        else
        {
            if (day < FromDay) (FromDay, ToDay) = (day, FromDay);
            else ToDay = day;
            _pickingEnd = false;
        }
        BuildDays();
        Validate();
    }

    /// <summary>The range as UTC ticks, or null when a time does not parse or the end is not after the start.</summary>
    public (long From, long To)? Result()
    {
        if (!TryTime(_fromTime.Text, out var ft) || !TryTime(_toTime.Text, out var tt)) return null;
        var a = FromDay + ft;
        var b = ToDay + tt;
        if (b <= a) return null;
        var from = ToUtc(a);
        var to = Math.Min(ToUtc(b), _now());
        return to > from ? (from, to) : null;
    }

    public void SetTimes(string from, string to)
    {
        _fromTime.Text = from;
        _toTime.Text = to;
    }

    private void Apply()
    {
        if (Result() is { } r) Applied?.Invoke(r.From, r.To);
    }

    private bool TryTime(string text, out TimeSpan time)
    {
        time = default;
        var s = text.Trim();
        string[] formats = [TimeTicks.ShortTime(Culture), TimeTicks.LongTime(Culture), "H:mm", "HH:mm", "H.mm", "H", "h:mm tt", "h tt", "H:mm:ss"];
        if (!DateTime.TryParseExact(s, formats, Culture, DateTimeStyles.AllowWhiteSpaces, out var dt) &&
            !DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out dt)) return false;
        time = dt.TimeOfDay;
        return true;
    }

    private void Validate()
    {
        var fromOk = TryTime(_fromTime.Text, out _);
        var toOk = TryTime(_toTime.Text, out _);
        _fromTime.Tag = fromOk ? null : "error";
        _toTime.Tag = toOk ? null : "error";
        var r = fromOk && toOk ? Result() : null;
        _apply.IsEnabled = r is not null;
        if (!fromOk || !toOk)
            _summary.Text = Loc.T("history.custom.invalid", new DateTime(2026, 1, 1, 14, 30, 0).ToString(TimeTicks.ShortTime(Culture), Culture));
        else if (r is { } range)
            _summary.Text = HistoryModel.RangeText(range.From, range.To, _now(), _zone, Culture) + " · " + HistoryModel.Duration(TimeSpan.FromTicks(range.To - range.From));
        else
            _summary.Text = "";
    }

    private void BuildWeekdays()
    {
        _weekdays.Children.Clear();
        var f = Culture.DateTimeFormat;
        for (var i = 0; i < 7; i++)
        {
            var d = (DayOfWeek)(((int)f.FirstDayOfWeek + i) % 7);
            var t = new TextBlock { Text = f.GetShortestDayName(d), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelTextDim");
            _weekdays.Children.Add(t);
        }
    }

    private void BuildDays()
    {
        _days.Children.Clear();
        var name = Culture.DateTimeFormat.GetMonthName(_shownMonth.Month);
        if (Culture.TwoLetterISOLanguageName == "pl") name = Culture.DateTimeFormat.MonthNames[_shownMonth.Month - 1];
        _month.Text = char.ToUpper(name[0], Culture) + name[1..] + " " + _shownMonth.Year.ToString(Culture);
        var first = Culture.DateTimeFormat.FirstDayOfWeek;
        var offset = ((int)_shownMonth.DayOfWeek - (int)first + 7) % 7;
        var start = _shownMonth.AddDays(-offset);
        var today = Local(_now()).Date;
        for (var i = 0; i < 42; i++)
        {
            var day = start.AddDays(i);
            var b = new Button { Content = day.Day.ToString(Culture), Tag = day };
            b.SetResourceReference(StyleProperty, "History.DayCell");
            var inMonth = day.Month == _shownMonth.Month;
            var edge = day == FromDay || day == ToDay;
            var inside = day > FromDay && day < ToDay;
            if (edge)
            {
                b.SetResourceReference(Control.BackgroundProperty, "Brush.AccentFill");
                b.SetResourceReference(Control.ForegroundProperty, "Brush.OnAccent");
                b.FontWeight = FontWeights.SemiBold;
            }
            else if (inside)
            {
                b.SetResourceReference(Control.BackgroundProperty, "Brush.AccentSoft");
            }
            else if (!inMonth)
            {
                b.SetResourceReference(Control.ForegroundProperty, "Brush.PanelTextDim");
            }
            if (day == today && !edge) b.SetResourceReference(Control.BorderBrushProperty, "Brush.AccentGlow");
            b.IsEnabled = day <= today;
            b.Click += (_, _) => PickDay((DateTime)b.Tag);
            _days.Children.Add(b);
        }
    }

    private Button NavButton(string icon, string tipKey, int months)
    {
        var b = new Button { Width = 30, Height = 30, ToolTip = Loc.T(tipKey) };
        b.SetResourceReference(StyleProperty, "IconButton");
        var i = new Icon { Size = 16 };
        i.SetResourceReference(Icon.DataProperty, icon);
        b.Content = i;
        b.Click += (_, _) =>
        {
            _shownMonth = _shownMonth.AddMonths(months);
            BuildDays();
        };
        return b;
    }

    private FrameworkElement TimeField(string labelKey, TextBox box, int column)
    {
        var panel = new StackPanel();
        Grid.SetColumn(panel, column);
        var label = new TextBlock { FontSize = 11.5, Margin = new Thickness(2, 0, 0, 4) };
        label.SetBinding(TextBlock.TextProperty, Tr(labelKey));
        label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelTextDim");
        panel.Children.Add(label);
        box.SetResourceReference(StyleProperty, "Field");
        box.HorizontalAlignment = HorizontalAlignment.Stretch;
        box.Width = double.NaN;
        panel.Children.Add(box);
        return panel;
    }

    private DateTime Local(long utc) => TimeZoneInfo.ConvertTimeFromUtc(new DateTime(utc, DateTimeKind.Utc), _zone);

    private long ToUtc(DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (_zone.IsInvalidTime(local)) local = local.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(local, _zone).Ticks;
    }

    private static Binding Tr(string key) => new($"[{key}]") { Source = Loc.I, Mode = BindingMode.OneWay };
}
