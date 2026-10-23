using System.Windows.Threading;
using BetterVitals.Core;
using BetterVitals.Core.Formatting;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.Services;
using BetterVitals.UI.Controls;

namespace BetterVitals.UI.Flyout;

/// <summary>One flyout tile: family label, verbatim sensor name, value + unit, heat color, sparkline.</summary>
public sealed class MetricTile : ObservableObject
{
    private string _value = "—";
    private string _unit = "";
    private string _valueBrush = "Brush.Text";

    public MetricTile(SensorDescriptor sensor)
    {
        Sensor = sensor;
        CategoryBrushKey = "Brush.Cat." + sensor.HardwareKind;
        CategorySoftKey = "Brush.CatSoft." + sensor.HardwareKind;
        IconKey = SensorPresentation.IconKey(sensor.Kind);
        _unit = sensor.DisplayUnit;
    }

    public SensorDescriptor Sensor { get; }

    /// <summary>"CPU · Temperature" (Loc).</summary>
    public string Label => $"{SensorPresentation.FamilyLabel(Sensor.HardwareKind)} · {SensorPresentation.KindLabel(Sensor.Kind)}";

    public string Name => Sensor.Name;

    public string IconKey { get; }

    public string CategoryBrushKey { get; }

    public string CategorySoftKey { get; }

    public string Value { get => _value; private set => Set(ref _value, value); }

    public string Unit { get => _unit; private set => Set(ref _unit, value); }

    /// <summary>Brush key of the value (heat scale for temperatures).</summary>
    public string ValueBrushKey { get => _valueBrush; private set => Set(ref _valueBrush, value); }

    public SparkSeries Series { get; } = new(48);

    public float Last { get; private set; } = float.NaN;

    internal void Update(float v, TemperatureUnit unit, bool push = true)
    {
        Last = v;
        var decimals = Sensor.Kind is SensorKind.Temperature or SensorKind.Load or SensorKind.Clock or SensorKind.Power or SensorKind.Fan or SensorKind.Fps ? 0 : (int?)null;
        Value = ValueFormat.ToText(Sensor.Kind, v, Loc.I.Culture, out var u, decimals, unit, Sensor.Unit);
        Unit = u;
        ValueBrushKey = SensorPresentation.HeatBrushKey(SensorPresentation.Heat(Sensor, v));
        if (push) Series.Push(v);
    }

    internal void Relabel() => OnPropertyChanged(nameof(Label));
}

/// <summary>
/// The flyout's live state. <see cref="Attach"/> (on Loaded) subscribes to <see cref="ISensorHub.FrameReady"/> and
/// declares a 1 s demand for its sensors; the engine thread only copies four floats and posts one coalesced update to
/// the UI thread. <see cref="Detach"/> (on Unloaded) releases everything.
/// </summary>
public sealed class FlyoutViewModel : ObservableObject
{
    private readonly ISensorHub _hub;
    private readonly float[] _pending = new float[4];
    private readonly Lock _gate = new();
    private Dispatcher? _dispatcher;
    private IDemand? _demand;
    private bool _posted;
    private bool _attached;
    private string _footer = "";
    private bool _overlay;

    public FlyoutViewModel(ISensorHub hub)
    {
        _hub = hub;
        var pinned = SettingsStore.I?.Current.Flyout.PinnedSensorIds ?? [];
        Tiles = SensorPresentation.KeySensors(hub, pinned).Select(s => new MetricTile(s)).ToList();
        foreach (var t in Tiles) t.Update(hub.Latest[t.Sensor.Index], Temperature, push: false);
        Refresh();
    }

    public IReadOnlyList<MetricTile> Tiles { get; }

    public bool HasTiles => Tiles.Count > 0;

    public string Footer { get => _footer; private set => Set(ref _footer, value); }

    public bool IsRecording { get; private set; }

    /// <summary>The overlay switch (writes the setting; the overlay follows it).</summary>
    public bool OverlayEnabled
    {
        get => _overlay;
        set
        {
            if (!Set(ref _overlay, value)) return;
            OnPropertyChanged(nameof(OverlayHint));
            if (SettingsStore.I is { } store && store.Current.Overlay.Enabled != value)
                store.Mutate("overlay", s => s.Overlay.Enabled = value);
        }
    }

    public string OverlayHint => Loc.T(_overlay ? "flyout.overlay.on" : "flyout.overlay.off");

    private static TemperatureUnit Temperature => SettingsStore.I?.Current.General.TemperatureUnit ?? TemperatureUnit.Celsius;

    public void Attach(Dispatcher dispatcher)
    {
        if (_attached) return;
        _attached = true;
        _dispatcher = dispatcher;
        PrefillFromHistory();
        _hub.FrameReady += OnFrame;
        if (SettingsStore.I is { } store) store.Changed += OnSettingsChanged;
        Loc.LanguageChanged += OnLanguageChanged;
        _demand = Tiles.Count > 0 ? _hub.Demand("flyout", Tiles.Select(t => t.Sensor.Id), TimeSpan.FromSeconds(1)) : null;
        Refresh();
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _hub.FrameReady -= OnFrame;
        if (SettingsStore.I is { } store) store.Changed -= OnSettingsChanged;
        Loc.LanguageChanged -= OnLanguageChanged;
        _demand?.Dispose();
        _demand = null;
    }

    /// <summary>
    /// The last ~1.5 minutes from the history's live buffer (key sensors are recorded, the buffer is in memory), so the
    /// sparklines are full the moment the flyout opens. A few milliseconds, before live frames start (keeps the order).
    /// </summary>
    private void PrefillFromHistory()
    {
        if (AppServices.History is not { } history || Tiles.Count == 0) return;
        try
        {
            var to = DateTime.UtcNow;
            var from = to.AddSeconds(-96);
            var task = Task.WhenAll(Tiles.Select(t => history.GetSeriesAsync(t.Sensor.Id, from, to, 48)));
            if (!task.Wait(150)) return;
            var series = Tiles.Zip(task.Result).ToDictionary(p => p.First.Sensor.Id, p => p.Second.Select(h => h.Avg).ToArray(), StringComparer.Ordinal);
            Prefill(s => series.GetValueOrDefault(s.Id) ?? []);
        }
        catch (Exception ex)
        {
            Log.Warn("flyout: prefill failed: " + ex.Message);
        }
    }

    /// <summary>Fills the sparklines with earlier values at once (snapshots, or the live buffer later).</summary>
    internal void Prefill(Func<SensorDescriptor, IEnumerable<float>> history)
    {
        foreach (var t in Tiles)
        {
            var values = history(t.Sensor).ToArray();
            t.Series.PushRange(values);
            if (values.Length > 0) t.Update(values[^1], Temperature, push: false);
        }
    }

    // engine thread: copy and post, never block
    private void OnFrame(SensorFrame frame)
    {
        lock (_gate)
        {
            for (var i = 0; i < Tiles.Count; i++) _pending[i] = frame[Tiles[i].Sensor.Index];
            if (_posted) return;
            _posted = true;
        }
        _dispatcher?.BeginInvoke(DispatcherPriority.Background, Apply);
    }

    private void Apply()
    {
        Span<float> values = stackalloc float[4];
        lock (_gate)
        {
            _pending.AsSpan().CopyTo(values);
            _posted = false;
        }
        if (!_attached) return;
        var unit = Temperature;
        for (var i = 0; i < Tiles.Count; i++) Tiles[i].Update(values[i], unit);
    }

    private void OnSettingsChanged(string section)
    {
        if (_dispatcher is { } d && !d.CheckAccess()) d.BeginInvoke(Refresh);
        else Refresh();
    }

    private void OnLanguageChanged()
    {
        foreach (var t in Tiles)
        {
            t.Relabel();
            t.Update(t.Last, Temperature, push: false);
        }
        Refresh();
    }

    private void Refresh()
    {
        var s = SettingsStore.I?.Current;
        _overlay = s?.Overlay.Enabled ?? false;
        OnPropertyChanged(nameof(OverlayEnabled));
        OnPropertyChanged(nameof(OverlayHint));
        IsRecording = s?.Recording.Enabled ?? false;
        OnPropertyChanged(nameof(IsRecording));
        Footer = Tiles.Count == 0 ? Loc.T("flyout.noSensors")
            : IsRecording ? Loc.Plural("flyout.recording", s!.Recording.SensorIds.Count)
            : Loc.T("flyout.liveOnly");
    }
}
