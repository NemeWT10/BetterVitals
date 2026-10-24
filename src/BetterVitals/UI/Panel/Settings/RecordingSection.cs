using System.Windows;
using System.Windows.Controls;
using BetterVitals.Core;
using BetterVitals.Core.Catalog;
using BetterVitals.Core.History;
using BetterVitals.Localization;
using BetterVitals.Services;
using BetterVitals.UI.Controls;
using BetterVitals.UI.Toasts;
using static BetterVitals.UI.Panel.SettingsUi;

namespace BetterVitals.UI.Panel;

/// <summary>
/// Settings ▸ Recording: history on / off ("live only"), save interval, retention of raw data and summaries, disk use,
/// "Clear history" (with an inline confirmation) and the data folder. Changes apply at once.
/// </summary>
internal sealed class RecordingSection : SectionView
{
    internal static readonly int[] Intervals = [1, 2, 5, 10];
    internal static readonly int[] RawDays = [1, 2, 3, 7, 14, 30, 60, 90, 180, 365];
    internal static readonly int[] MinuteDays = [7, 14, 30, 60, 90, 180, 365, 730, 0];
    internal static readonly int[] HourDays = [90, 180, 365, 730, 1825, 3650, 0];
    internal static readonly int[] LimitsMb = [100, 250, 500, 1000, 2000, 5000, 10_000, 0];

    private readonly IHistoryQuery? _history;
    private readonly SettingRow _enabledRow;
    private readonly SettingRow _summaryRow;
    private readonly TextBlock _rawSize;
    private readonly TextBlock _minuteSize;
    private readonly TextBlock _hourSize;
    private readonly TextBlock _total;
    private readonly SettingRow _usageRow;
    private readonly SettingRow _clearRow;
    private readonly Border _confirm;
    private readonly TextBlock _confirmText;
    private HistoryUsage? _usage;
    private bool _clearing;

    public RecordingSection(PageContext? context, IHistoryQuery? history = null) : base(context)
    {
        _history = history ?? context?.History ?? AppServices.History;
        var root = new StackPanel();
        root.Children.Add(Lead("settings.rec.lead"));

        EnabledSwitch = Switch("settings.rec.enabled");
        EnabledSwitch.Checked += (_, _) => SetEnabled(true);
        EnabledSwitch.Unchecked += (_, _) => SetEnabled(false);
        _enabledRow = Row("settings.rec.enabled", null, "Icon.Record", EnabledSwitch);

        var choose = Button("SecondaryButton", "settings.rec.choose");
        choose.Click += (_, _) => Context?.Navigate(PageIds.Sensors);
        _summaryRow = Row("settings.rec.summary", null, "Icon.List", choose);

        Interval = new Segments<int>("rec-interval", Intervals.Select(s => (s, $"settings.rec.interval.{s}")));
        Interval.ValueChanged += s => Store.Mutate("recording", x => x.Recording.IntervalSeconds = s);
        var intervalRow = Row("settings.rec.interval", "settings.rec.interval.desc", "Icon.Clock", Interval);

        root.Children.Add(Rows(_enabledRow, _summaryRow, intervalRow));

        // how long to keep
        Raw = new ChoiceCombo<int>(RawDays.Select(d => (d, (Func<string>)(() => Days(d)))), 130);
        // summaries never end before the detail they summarize, hour summaries never before minute ones (0 = forever)
        Raw.ValueChanged += d => Store.Mutate("recording", x =>
        {
            var r = x.Recording;
            r.RawRetentionDays = d;
            if (Span(r.MinuteRetentionDays) < d) r.MinuteRetentionDays = MinuteDays.First(m => Span(m) >= d);
            if (Span(r.HourRetentionDays) < Span(r.MinuteRetentionDays)) r.HourRetentionDays = HourDays.First(h => Span(h) >= Span(r.MinuteRetentionDays));
        });
        Minute = new ChoiceCombo<int>(MinuteDays.Select(d => (d, (Func<string>)(() => Days(d)))), 130);
        Minute.ValueChanged += d => Store.Mutate("recording", x =>
        {
            var r = x.Recording;
            r.MinuteRetentionDays = d;
            if (r.RawRetentionDays > Span(d)) r.RawRetentionDays = RawDays.Last(v => v <= Span(d));
            if (Span(r.HourRetentionDays) < Span(d)) r.HourRetentionDays = HourDays.First(h => Span(h) >= Span(d));
        });
        Hour = new ChoiceCombo<int>(HourDays.Select(d => (d, (Func<string>)(() => Days(d)))), 130);
        Hour.ValueChanged += d => Store.Mutate("recording", x =>
        {
            var r = x.Recording;
            r.HourRetentionDays = d;
            if (Span(r.MinuteRetentionDays) > Span(d)) r.MinuteRetentionDays = MinuteDays.Last(m => Span(m) <= Span(d));
            if (r.RawRetentionDays > Span(r.MinuteRetentionDays)) r.RawRetentionDays = RawDays.Last(v => v <= Span(r.MinuteRetentionDays));
        });
        _rawSize = SizeNote();
        _minuteSize = SizeNote();
        _hourSize = SizeNote();
        _total = Text(null);
        _total.Margin = new Thickness(4, 8, 0, 10);
        root.Children.Add(SubTitle("settings.rec.keep"));
        root.Children.Add(Rows(
            Row("settings.rec.raw", "settings.rec.raw.desc", "Icon.Wave", HStack(12, _rawSize, Raw)),
            Row("settings.rec.minute", "settings.rec.minute.desc", "Icon.Chart", HStack(12, _minuteSize, Minute)),
            Row("settings.rec.hour", "settings.rec.hour.desc", "Icon.History", HStack(12, _hourSize, Hour))));
        root.Children.Add(_total);

        // disk
        var open = Button("SecondaryButton", "settings.rec.openFolder", "Icon.External");
        open.Click += (_, _) => AppServices.Platform.OpenFolder(System.IO.Directory.Exists(AppEnv.HistoryDir) ? AppEnv.HistoryDir : AppEnv.LocalDir);
        _usageRow = Row("settings.rec.usage", null, "Icon.Disk", open);

        Limit = new ChoiceCombo<int>(LimitsMb.Select(mb => (mb, (Func<string>)(() => LimitLabel(mb)))), 130);
        Limit.ValueChanged += mb => Store.Mutate("recording", x => x.Recording.MaxHistoryMegabytes = mb);
        var limitRow = Row("settings.rec.limit", "settings.rec.limit.desc", "Icon.Gauge", Limit);

        ClearButton = Button("DangerButton", "settings.rec.clear.button", "Icon.Trash");
        ClearButton.Click += (_, _) => ShowConfirm(true);
        _confirmText = Text(null, 12.5, "Brush.Text", FontWeights.SemiBold);
        ConfirmButton = Button("DangerButton", "settings.rec.clear.yes", "Icon.Trash");
        ConfirmButton.Click += async (_, _) => await ClearAsync();
        var cancel = Button("GhostButton", "settings.common.cancel");
        cancel.Click += (_, _) => ShowConfirm(false);
        var buttons = HStack(8, ConfirmButton, cancel);
        buttons.Margin = new Thickness(0, 10, 0, 0);
        _confirm = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 12),
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed,
            Child = new StackPanel { Children = { _confirmText, buttons } },
        };
        _confirm.SetResourceReference(Border.BackgroundProperty, "Brush.DangerSoft");
        _confirm.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        var clearBody = new StackPanel { Children = { ClearButton } };
        _clearRow = Row("settings.rec.clear", "settings.rec.clear.desc", "Icon.Trash", clearBody);
        root.Children.Add(SubTitle("settings.rec.disk"));
        var diskCard = Rows(_usageRow, limitRow, _clearRow);
        ((StackPanel)diskCard.Child).Children.Add(_confirm);
        _confirm.Margin = new Thickness(0, 0, 0, 14);
        root.Children.Add(diskCard);

        Content = root;
        Initialize();
    }

    internal CheckBox EnabledSwitch { get; }

    internal Segments<int> Interval { get; }

    internal ChoiceCombo<int> Raw { get; }

    internal ChoiceCombo<int> Minute { get; }

    internal ChoiceCombo<int> Hour { get; }

    internal ChoiceCombo<int> Limit { get; }

    /// <summary>The size notes next to the detail / minute / hour choices and the total line under them.</summary>
    internal (string Raw, string Minute, string Hour, string Total) SizeTexts => (_rawSize.Text, _minuteSize.Text, _hourSize.Text, _total.Text);

    internal bool SizesVisible => _total.Visibility == Visibility.Visible;

    internal Button ClearButton { get; }

    internal Button ConfirmButton { get; }

    internal bool ConfirmVisible => _confirm.Visibility == Visibility.Visible;

    internal string SummaryText => _summaryRow.Description;

    internal string UsageText => _usageRow.Description;

    protected override IReadOnlyCollection<string> Watches => ["recording"];

    protected override void OnAttached() => _usage = null; // re-read on every visit

    protected override void Relabel()
    {
        Raw.Relabel();
        Minute.Relabel();
        Hour.Relabel();
        Limit.Relabel();
    }

    /// <summary>Sizes formatted like the Sensors page's status card, so the same estimate reads the same on both pages.</summary>
    private static string DiskBytes(long bytes) => Pages.Shared.Ui.Bytes(bytes, Loc.I.Culture);

    protected override void Refresh()
    {
        var r = Current.Recording;
        EnabledSwitch.IsChecked = r.Enabled;
        _enabledRow.Description = Loc.T(r.Enabled ? "settings.rec.enabled.on" : "settings.rec.enabled.off");
        Interval.Value = Intervals.Contains(r.IntervalSeconds) ? r.IntervalSeconds : 2;
        Raw.Value = Choice(RawDays, r.RawRetentionDays);
        Minute.Value = Choice(MinuteDays, r.MinuteRetentionDays);
        Hour.Value = Choice(HourDays, r.HourRetentionDays);
        Limit.Value = Choice(LimitsMb, r.MaxHistoryMegabytes);

        // what is recorded: present sensors when the engine knows them, else the chosen ids
        var hub = Context?.Hub ?? AppServices.Hub;
        var count = hub.Sensors.Count > 0 ? RecordingPolicy.CountPresent(r, hub) : r.SensorIds.Count;
        var sensors = Loc.Plural("settings.rec.sensors", count);
        if (_history is not null) _usage ??= SafeUsage();
        var perDay = RecordingPolicy.EstimateBytesPerDay(count, r.IntervalSeconds, _usage?.BytesPerValue);
        _summaryRow.Description = r.Enabled ? Loc.T("settings.rec.summary.desc", sensors, DiskBytes(perDay)) : Loc.T("settings.rec.summary.off", sensors);

        // what each level takes once its retention is full (a level kept forever: per year)
        var (raw, minute, hour) = RecordingPolicy.EstimateRetainedBytes(r, count, _usage?.BytesPerValue);
        _rawSize.Text = Loc.T("settings.rec.size", DiskBytes(raw));
        _minuteSize.Text = Loc.T(r.MinuteRetentionDays <= 0 ? "settings.rec.size.year" : "settings.rec.size", DiskBytes(minute));
        _hourSize.Text = Loc.T(r.HourRetentionDays <= 0 ? "settings.rec.size.year" : "settings.rec.size", DiskBytes(hour));
        var growth = (r.MinuteRetentionDays <= 0 ? minute : 0) + (r.HourRetentionDays <= 0 ? hour : 0);
        var full = raw + minute + hour;
        var total = growth > 0 ? Loc.T("settings.rec.total.growing", DiskBytes(full), DiskBytes(growth)) : Loc.T("settings.rec.total", DiskBytes(full));
        if (r.MaxHistoryMegabytes > 0 && full > r.MaxHistoryMegabytes * 1_000_000L) total += " " + Loc.T("settings.rec.total.capped");
        _total.Text = total;
        var showSizes = r.Enabled && count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _rawSize.Visibility = _minuteSize.Visibility = _hourSize.Visibility = _total.Visibility = showSizes;

        // disk
        if (_history is null)
        {
            _usageRow.Description = Loc.T("settings.rec.usage.unavailable");
        }
        else
        {
            _usage ??= SafeUsage();
            var u = _usage.Value;
            _usageRow.Description = u.BytesOnDisk <= 0
                ? Loc.T("settings.rec.usage.empty")
                : u.OldestUtc is { } oldest
                    ? Loc.T("settings.rec.usage.desc", DiskBytes(u.BytesOnDisk), oldest.ToLocalTime().ToString("d", Loc.I.Culture))
                    : DiskBytes(u.BytesOnDisk);
        }
        ClearButton.IsEnabled = _history is not null && !_clearing && (_usage?.BytesOnDisk ?? 0) > 0;
        _confirmText.Text = Loc.T("settings.rec.clear.confirm", DiskBytes(_usage?.BytesOnDisk ?? 0));
        ConfirmButton.IsEnabled = !_clearing;
    }

    private HistoryUsage SafeUsage()
    {
        try
        {
            return _history!.GetUsage();
        }
        catch (Exception ex)
        {
            Log.Error("settings: history usage failed", ex);
            return default;
        }
    }

    private void SetEnabled(bool on)
    {
        if (Syncing || Current.Recording.Enabled == on) return;
        Store.Mutate("recording", x => x.Recording.Enabled = on);
    }

    private void ShowConfirm(bool show)
    {
        _confirm.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (show) Motion.Appear(_confirm, rise: 4, fromScale: 0.99, ms: 220);
        Sync();
    }

    internal async Task ClearAsync()
    {
        if (_history is null || _clearing) return;
        _clearing = true;
        Sync();
        try
        {
            await _history.ClearAsync();
            Toast("settings.rec.cleared", ToastKind.Success);
        }
        catch (Exception ex)
        {
            Log.Error("settings: clearing history failed", ex);
            Toast("settings.rec.clearFailed", ToastKind.Error);
        }
        finally
        {
            _clearing = false;
            _usage = null;
            ShowConfirm(false);
        }
    }

    /// <summary>"7 days", "1 year", "Forever" (0).</summary>
    internal static string Days(int days)
    {
        if (days <= 0) return Loc.T("settings.common.forever");
        return days % 365 == 0 ? Loc.Plural("settings.common.years", days / 365) : Loc.Plural("settings.common.days", days);
    }

    /// <summary>"100 MB", "1 GB", "No limit" (0). Decimal units, like the sizes shown elsewhere.</summary>
    internal static string LimitLabel(int megabytes) => megabytes <= 0
        ? Loc.T("settings.rec.limit.none")
        : megabytes >= 1000 ? $"{megabytes / 1000} GB" : $"{megabytes} MB";

    /// <summary>Days a retention spans; 0 = forever spans everything.</summary>
    private static int Span(int days) => days <= 0 ? int.MaxValue : days;

    /// <summary>The option shown for a stored value: 0 = forever / no limit when offered, else the nearest one.</summary>
    private static int Choice(int[] options, int value)
        => value <= 0 && options.Contains(0) ? 0 : options.Where(o => o > 0).MinBy(o => Math.Abs((long)o - value));

    private static TextBlock SizeNote()
    {
        var t = Text(null);
        t.TextWrapping = TextWrapping.NoWrap;
        t.VerticalAlignment = VerticalAlignment.Center;
        return t;
    }

    internal static TextBlock SubTitle(string key)
    {
        var t = new TextBlock { Margin = new Thickness(4, 6, 0, 8) };
        t.SetResourceReference(StyleProperty, "SectionTitle");
        t.SetBinding(TextBlock.TextProperty, Tr(key));
        return t;
    }
}
