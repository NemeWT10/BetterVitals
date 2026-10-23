using System.Windows;
using BetterVitals.Core.Catalog;
using BetterVitals.Core.Formatting;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.Overlay;
using BetterVitals.UI.Themes;
using BetterVitals.UI.Toasts;

namespace BetterVitals.UI.Pages.OverlayEditor;

/// <summary>What the editor's views share: the model, live values, the friendly items of this PC and page services.</summary>
internal sealed class EditorContext
{
    public required OverlayEditorModel Model { get; init; }

    public required PreviewFeed Feed { get; init; }

    public ISensorHub Hub => Feed.Hub;

    /// <summary>Friendly items of this PC (catalog order); refreshed when the inventory changes.</summary>
    public IReadOnlyList<FriendlyItem> Friendly { get; set; } = [];

    public OverlayLayout Layout => Model.Working;

    /// <summary>Deferred edit (sliders, typing): applied to the preview now, written to the settings after a pause.</summary>
    public required Action<Action<OverlayLayout>> Defer { get; init; }

    public required Action<string, ToastKind, string?, Action?> Toast { get; init; }

    /// <summary>Opens the full sensor list (simple: toggles items; advanced: adds the picked sensors).</summary>
    public required Action<bool> OpenPicker { get; init; }

    public required ColorPopover Colors { get; init; }

    /// <summary>Owner window for file dialogs (null in tests).</summary>
    public Func<Window?> Owner { get; init; } = () => null;

    public TemperatureUnit TemperatureUnit => SettingsStore.I?.Current.General.TemperatureUnit ?? TemperatureUnit.Celsius;

    public OverlayTheme Theme => ThemeManager.I.ResolveOverlayTheme();

    public SensorDescriptor? Sensor(string id) => Hub.Find(id) ?? PreviewFeed.Describe(id);

    public IReadOnlyList<SensorDescriptor> Sensors(OverlayItem item)
        => item.SensorIds.Select(Sensor).Where(s => s is not null).Select(s => s!).ToList();

    /// <summary>The friendly item an overlay item shows, if any.</summary>
    public FriendlyItem? FriendlyOf(OverlayItem item) => Friendly.FirstOrDefault(f => OverlayEditorModel.Matches(item, f));

    /// <summary>Name in lists: the user's label, the friendly name, or the overlay's default short label.</summary>
    public string Title(OverlayItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Label)) return item.Label!;
        if (FriendlyOf(item) is { } f) return EditorText.Friendly(f.Key);
        return DefaultLabel(item);
    }

    /// <summary>The label the overlay draws without a user label.</summary>
    public string DefaultLabel(OverlayItem item)
    {
        var sensors = Sensors(item);
        if (sensors.Count == 0) return item.SensorIds.Count > 0 ? DefaultLabels.ForId(item.SensorIds[0]) : "—";
        if (sensors.Count == 1) return DefaultLabels.For(sensors[0], Hub.FindHardware(sensors[0].HardwareId));
        return DefaultLabels.ForGroup(sensors);
    }

    /// <summary>The label a row shows on the overlay: its own, else the automatic one (the item's, the component's, the first item's).</summary>
    public string RowLabel(OverlayRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.Label)) return row.Label!;
        var labels = row.Items.Select(i => string.IsNullOrWhiteSpace(i.Label) ? DefaultLabel(i) : i.Label!).ToList();
        var kinds = row.Items.Select(i => Sensors(i) is { Count: > 0 } s ? s[0].HardwareKind : OverlayLayoutEdits.ComponentKind(i)).ToList();
        return DefaultLabels.ForRow(labels, kinds);
    }

    /// <summary>"Main box", the box's own name, or "Box 2", "Box 3"… by its place in the list.</summary>
    public string BoxTitle(string boxId)
    {
        if (boxId == OverlayLayout.MainBoxId) return Loc.T("ovl.box.main");
        var i = Layout.Boxes.FindIndex(b => b.Id == boxId);
        if (i >= 0 && Layout.Boxes[i].Name is { Length: > 0 } name) return name;
        return Loc.T("ovl.box.n", (Math.Max(0, i) + 2).ToString(Loc.I.Culture));
    }

    /// <summary>"Top right · Main monitor": where a box sits, in words.</summary>
    public string PlacementSummary(OverlayPlacement p)
    {
        var monitor = p.MonitorMode switch
        {
            OverlayMonitorMode.Specific => Services.AppServices.Overlay.Monitors.FirstOrDefault(m => string.Equals(m.Key, p.MonitorKey, StringComparison.OrdinalIgnoreCase))?.FriendlyName
                                           ?? Loc.T("ovl.monitor.mode.specific"),
            OverlayMonitorMode.FollowGame => Loc.T("ovl.monitor.game"),
            _ => Loc.T("ovl.monitor.primary"),
        };
        return Loc.T("ovl.anchor." + p.Anchor) + " · " + monitor;
    }

    /// <summary>"54 °C" (UI culture, user's temperature unit); "—" without a value.</summary>
    public string Format(SensorDescriptor s, float value)
    {
        var text = ValueFormat.ToText(s.Kind, value, Loc.I.Culture, out var unit, Decimals(s.Kind), TemperatureUnit, s.Unit);
        if (float.IsNaN(value)) return text;
        return unit.Length == 0 ? text : text + " " + unit;
    }

    public (string Value, string Unit) FormatParts(SensorDescriptor s, float value)
    {
        var text = ValueFormat.ToText(s.Kind, value, Loc.I.Culture, out var unit, Decimals(s.Kind), TemperatureUnit, s.Unit);
        return (text, float.IsNaN(value) ? "" : unit);
    }

    /// <summary>Overlay-like decimals (the panel's default would show 54.3 °C on a tile).</summary>
    public static int? Decimals(SensorKind kind) => kind switch
    {
        SensorKind.Voltage => 3,
        SensorKind.FrameTime or SensorKind.Data or SensorKind.SmallData or SensorKind.Current => 1,
        SensorKind.Throughput => null,
        SensorKind.Factor or SensorKind.Energy => 1,
        _ => 0,
    };
}

/// <summary>Texts that are not plain Loc keys: friendly names (Core's table), categories.</summary>
internal static class EditorText
{
    /// <summary>"Temperatura CPU" / "CPU temperature" from FriendlyCatalog.Texts (also when the app registered them in Loc).</summary>
    public static string Friendly(string key)
    {
        var locKey = "friendly." + key;
        if (Loc.Has(locKey)) return Loc.T(locKey);
        return FriendlyCatalog.Texts.TryGetValue(locKey, out var t) ? (Loc.I.IsPolish ? t.Pl : t.En) : key;
    }

    /// <summary>The simple mode's categories, in the order of PROJEKT §8.</summary>
    public static readonly (HardwareKind Kind, string Key)[] Categories =
    [
        (HardwareKind.Cpu, "ovl.cat.cpu"),
        (HardwareKind.Gpu, "ovl.cat.gpu"),
        (HardwareKind.Memory, "ovl.cat.ram"),
        (HardwareKind.Motherboard, "ovl.cat.board"),
        (HardwareKind.Storage, "ovl.cat.drives"),
        (HardwareKind.Network, "ovl.cat.network"),
        (HardwareKind.Game, "ovl.cat.fps"),
    ];
}
