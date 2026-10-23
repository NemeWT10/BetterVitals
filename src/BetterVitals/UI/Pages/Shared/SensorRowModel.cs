using BetterVitals.Core;
using BetterVitals.Core.Formatting;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.Services;
using BetterVitals.UI.Controls;

namespace BetterVitals.UI.Pages.Shared;

/// <summary>
/// One sensor as a row shows it, wherever it appears (expanded Overview card, Sensors page): verbatim name, note,
/// Now / Min / Max / Avg since start, heat color, a short sparkline and the Historia / Nakładka switches.
/// Values are refreshed by the page (≤ 1 Hz) only while a view shows the row (<see cref="IsShown"/>).
/// </summary>
public sealed class SensorRowModel : ObservableObject
{
    private readonly SensorActions _actions;
    private string _now = "—";
    private string _unit;
    private string _min = "—";
    private string _max = "—";
    private string _avg = "—";
    private string _minMax = "—";
    private string _nowBrush = "Brush.Text";
    private string? _note;
    private string? _noteBadge;
    private bool _recorded;
    private bool _onOverlay;
    private bool _pinned;
    private bool _recordingEnabled = true;
    private int _lastSequence = -1;
    private int _views;

    public SensorRowModel(SensorDescriptor sensor, string hardwareName, SensorActions actions)
    {
        Sensor = sensor;
        HardwareName = hardwareName;
        _actions = actions;
        _unit = sensor.DisplayUnit;
        Group = SensorGroups.Of(sensor.Kind);
        Relabel();
    }

    public SensorDescriptor Sensor { get; }

    /// <summary>Verbatim, never translated.</summary>
    public string Name => Sensor.Name;

    public string HardwareName { get; }

    public KindGroup Group { get; }

    /// <summary>"Brush.Cat.Gpu" — the sparkline color.</summary>
    public string CategoryBrushKey => "Brush.Cat." + Sensor.HardwareKind;

    /// <summary>The sensor's note (e.g. the RTX 50 hot spot "unofficial, ±1 °C"), null when it has none.</summary>
    public string? Note { get => _note; private set => Set(ref _note, value); }

    /// <summary>The note as a short row badge ("unofficial"; key + ".short" when there is one), the full note is its tooltip.</summary>
    public string? NoteBadge { get => _noteBadge; private set => Set(ref _noteBadge, value); }

    public string Now { get => _now; private set => Set(ref _now, value); }

    public string Unit { get => _unit; private set => Set(ref _unit, value); }

    public string Min { get => _min; private set => Set(ref _min, value); }

    public string Max { get => _max; private set => Set(ref _max, value); }

    public string Avg { get => _avg; private set => Set(ref _avg, value); }

    /// <summary>"41,0 / 58,2" for the compact layout.</summary>
    public string MinMax { get => _minMax; private set => Set(ref _minMax, value); }

    /// <summary>Brush key of the current value (heat scale for temperatures).</summary>
    public string NowBrushKey { get => _nowBrush; private set => Set(ref _nowBrush, value); }

    public bool IsRecorded { get => _recorded; private set => Set(ref _recorded, value); }

    public bool IsOnOverlay { get => _onOverlay; private set => Set(ref _onOverlay, value); }

    public bool IsPinned { get => _pinned; private set => Set(ref _pinned, value); }

    /// <summary>The master switch: off greys the Historia column out (the choice is kept for later).</summary>
    public bool RecordingEnabled
    {
        get => _recordingEnabled;
        private set
        {
            if (!Set(ref _recordingEnabled, value)) return;
            OnPropertyChanged(nameof(HistoryOpacity));
            OnPropertyChanged(nameof(HistoryTip));
        }
    }

    public double HistoryOpacity => _recordingEnabled ? 1 : 0.4;

    public string HistoryTip => Loc.T(!_recordingEnabled ? "sensors.row.historyOffTip" : _recorded ? "sensors.row.historyOn" : "sensors.row.historyAdd");

    public string OverlayTip => Loc.T(_onOverlay ? "sensors.row.overlayOn" : "sensors.row.overlayAdd");

    public SparkSeries Series { get; } = new(40);

    /// <summary>How many views show this row right now (realized, loaded rows).</summary>
    public int Views
    {
        get => _views;
        internal set
        {
            var appeared = _views == 0 && value > 0;
            _views = value;
            if (appeared) Shown?.Invoke(this);
        }
    }

    /// <summary>A view started to show the row (scrolled in, expanded): the owner fills it at once instead of after the next tick.</summary>
    internal Action<SensorRowModel>? Shown { get; set; }

    public bool IsShown => Views > 0;

    public float Last { get; private set; } = float.NaN;

    /// <summary>Formats the latest value and the session statistics; pushes to the sparkline once per engine tick.</summary>
    public void Refresh(SensorFrame frame, SessionStats stats, TemperatureUnit unit)
    {
        var i = Sensor.Index;
        var v = frame[i];
        var culture = Loc.I.Culture;
        Last = v;
        Now = ValueFormat.ToText(Sensor.Kind, v, culture, out var u, null, unit, Sensor.Unit);
        if (!float.IsNaN(v)) Unit = u;
        NowBrushKey = SensorPresentation.HeatBrushKey(SensorPresentation.Heat(Sensor, v));
        var min = stats.Min(i);
        var max = stats.Max(i);
        Min = Format(min, unit);
        Max = Format(max, unit);
        Avg = Format(stats.Average(i), unit);
        MinMax = float.IsNaN(min) ? "—" : $"{Min} / {Max}";
        // one point per engine tick while shown (a slow sensor holds its value, so the line is continuous in time)
        if (frame.Sequence != _lastSequence)
        {
            _lastSequence = frame.Sequence;
            Series.Push(v);
        }
    }

    /// <summary>Re-reads the switches from settings (after any settings change).</summary>
    public void Sync(AppSettings s)
    {
        var recorded = IsRecorded;
        var overlay = IsOnOverlay;
        IsRecorded = SensorToggles.IsRecorded(s, Sensor.Id);
        IsOnOverlay = SensorToggles.IsOnOverlay(s, Sensor.Id);
        IsPinned = SensorToggles.IsPinned(s, Sensor.Id);
        RecordingEnabled = s.Recording.Enabled;
        if (recorded != IsRecorded) OnPropertyChanged(nameof(HistoryTip));
        if (overlay != IsOnOverlay) OnPropertyChanged(nameof(OverlayTip));
    }

    /// <summary>After a language change: note and tips (values re-format on the next refresh).</summary>
    public void Relabel()
    {
        Note = Sensor.NoteKey is { Length: > 0 } k && Loc.Has(k) ? Loc.T(k) : null;
        NoteBadge = Note is null ? null : Loc.Has(Sensor.NoteKey + ".short") ? Loc.T(Sensor.NoteKey + ".short") : Note;
        OnPropertyChanged(nameof(HistoryTip));
        OnPropertyChanged(nameof(OverlayTip));
    }

    // ───────────── user actions ─────────────

    public void ToggleRecorded() => _actions.SetRecorded([Sensor], !IsRecorded);

    public void ToggleOverlay() => _actions.SetOnOverlay([Sensor], !IsOnOverlay);

    public void TogglePinned() => _actions.SetPinned(Sensor, !IsPinned);

    public void ShowInHistory() => _actions.ShowInHistory(Sensor);

    /// <summary>Min / Max / Avg without the unit (the Now column carries it) — except throughput, which scales per value.</summary>
    private string Format(float value, TemperatureUnit unit)
    {
        var text = ValueFormat.ToText(Sensor.Kind, value, Loc.I.Culture, out var u, null, unit, Sensor.Unit);
        return Sensor.Kind == SensorKind.Throughput && !float.IsNaN(value) ? text + " " + u : text;
    }
}
