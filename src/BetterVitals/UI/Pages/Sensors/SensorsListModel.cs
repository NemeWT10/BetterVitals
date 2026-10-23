using System.Collections.ObjectModel;
using BetterVitals.Core;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.Services;
using BetterVitals.UI.Pages.Shared;

namespace BetterVitals.UI.Pages.Sensors;

/// <summary>
/// A hardware section of the Sensors list: header (icon, verbatim name, count, collapse) and the group switches, whose
/// state covers the rows the filters let through (all on / none / partial).
/// </summary>
public sealed class SectionModel : ObservableObject
{
    private readonly SensorsListModel _owner;
    private bool _collapsed;
    private bool? _record = false;
    private bool? _overlay = false;
    private string _count = "";

    internal SectionModel(SensorsListModel owner, HardwareGroup group, IReadOnlyList<SensorRowModel> rows)
    {
        _owner = owner;
        Group = group;
        Rows = rows;
        Visible = rows;
    }

    public HardwareGroup Group { get; }

    /// <summary>Verbatim hardware name.</summary>
    public string Name => Group.Name;

    public string IconKey => SensorPresentation.IconKey(Group.Kind);

    public string CategoryBrushKey => "Brush.Cat." + Group.Kind;

    public string CategorySoftKey => "Brush.CatSoft." + Group.Kind;

    public IReadOnlyList<SensorRowModel> Rows { get; }

    /// <summary>The rows the filters let through (what the header switches act on).</summary>
    public IReadOnlyList<SensorRowModel> Visible { get; internal set; }

    public bool IsCollapsed { get => _collapsed; internal set { if (Set(ref _collapsed, value)) OnPropertyChanged(nameof(ExpanderTag)); } }

    /// <summary>"True" when expanded (the chevron points up), for the Bv.Expander style.</summary>
    public string ExpanderTag => _collapsed ? "False" : "True";

    public bool? RecordState { get => _record; private set { if (Set(ref _record, value)) OnPropertyChanged(nameof(RecordTip)); } }

    public bool? OverlayState { get => _overlay; private set { if (Set(ref _overlay, value)) OnPropertyChanged(nameof(OverlayTip)); } }

    public string CountText { get => _count; private set => Set(ref _count, value); }

    public string RecordTip => Loc.T(_record == true ? "sensors.section.historyAllOff" : "sensors.section.historyAllOn");

    public string OverlayTip => Loc.T(_overlay == true ? "sensors.section.overlayAllOff" : "sensors.section.overlayAllOn");

    public double HistoryOpacity => _owner.RecordingEnabled ? 1 : 0.4;

    internal void Recompute()
    {
        var rec = 0;
        var ovl = 0;
        foreach (var r in Visible)
        {
            if (r.IsRecorded) rec++;
            if (r.IsOnOverlay) ovl++;
        }
        RecordState = SensorToggles.Combine(rec, Visible.Count);
        OverlayState = SensorToggles.Combine(ovl, Visible.Count);
        CountText = Visible.Count == Rows.Count ? Rows.Count.ToString(Loc.I.Culture) : $"{Visible.Count.ToString(Loc.I.Culture)} / {Rows.Count.ToString(Loc.I.Culture)}";
        OnPropertyChanged(nameof(HistoryOpacity));
    }

    internal void Relabel()
    {
        OnPropertyChanged(nameof(RecordTip));
        OnPropertyChanged(nameof(OverlayTip));
    }

    public void ToggleCollapsed() => _owner.SetCollapsed(this, !IsCollapsed);

    public void ToggleRecord() => _owner.Actions.SetRecorded(Visible.Select(r => r.Sensor).ToList(), SensorToggles.NextGroupValue(RecordState));

    public void ToggleOverlay() => _owner.Actions.SetOnOverlay(Visible.Select(r => r.Sensor).ToList(), SensorToggles.NextGroupValue(OverlayState));
}

/// <summary>
/// The Sensors page's list: every present sensor grouped by hardware, filtered by search (sensor or hardware name),
/// type chips and "only selected", flattened into headers + rows for one virtualized list. Collapsing a section edits
/// the flat list in place (the scroll position stays); a filter change rebuilds it.
/// </summary>
public sealed class SensorsListModel
{
    private readonly ISensorHub _hub;
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private List<SectionModel> _sections = [];
    private string _search = "";
    private bool _onlySelected;

    public SensorsListModel(ISensorHub hub, SensorActions actions)
    {
        _hub = hub;
        Actions = actions;
        Rebuild();
    }

    public SensorActions Actions { get; }

    public IReadOnlyList<SectionModel> Sections => _sections;

    public IEnumerable<SensorRowModel> AllRows => _sections.SelectMany(s => s.Rows);

    /// <summary>Headers and rows in display order (the list's ItemsSource; replaced on filter changes).</summary>
    public ObservableCollection<object> Items { get; private set; } = [];

    /// <summary>Raised when <see cref="Items"/> is a new collection.</summary>
    public event Action? ItemsReplaced;

    /// <summary>Kind groups present on this PC (the type chips).</summary>
    public IReadOnlyList<KindGroup> AvailableKinds { get; private set; } = [];

    public HashSet<KindGroup> Kinds { get; } = [];

    public bool RecordingEnabled { get; private set; } = true;

    public string Search
    {
        get => _search;
        set
        {
            value ??= "";
            if (_search == value) return;
            _search = value;
            ApplyFilter();
        }
    }

    public bool OnlySelected
    {
        get => _onlySelected;
        set
        {
            if (_onlySelected == value) return;
            _onlySelected = value;
            ApplyFilter();
        }
    }

    public bool HasFilter => _search.Trim().Length > 0 || Kinds.Count > 0 || _onlySelected;

    /// <summary>Rows shown after filtering (all sections, collapsed ones included).</summary>
    public int VisibleCount => _sections.Sum(s => s.Visible.Count);

    public void ToggleKind(KindGroup kind)
    {
        if (!Kinds.Remove(kind)) Kinds.Add(kind);
        ApplyFilter();
    }

    public void ClearFilters()
    {
        _search = "";
        Kinds.Clear();
        _onlySelected = false;
        ApplyFilter();
    }

    /// <summary>Re-reads the inventory (keeps filters and collapsed sections).</summary>
    public void Rebuild()
    {
        _sections = HardwareTree.Build(_hub)
            .Select(g => new SectionModel(this, g, g.Sensors.Select(s => new SensorRowModel(s, g.Name, Actions)).ToList()))
            .ToList();
        foreach (var s in _sections) s.IsCollapsed = _collapsed.Contains(s.Group.Id);
        AvailableKinds = SensorGroups.Order.Where(k => _sections.Any(s => s.Rows.Any(r => r.Group == k))).ToList();
        Kinds.IntersectWith(AvailableKinds);
        if (Actions.Settings is { } settings) Sync(settings);
        ApplyFilter();
    }

    public bool Matches(SensorRowModel r)
    {
        if (Kinds.Count > 0 && !Kinds.Contains(r.Group)) return false;
        if (_onlySelected && !r.IsRecorded && !r.IsOnOverlay) return false;
        var q = _search.Trim();
        if (q.Length == 0) return true;
        return r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.HardwareName.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    public void ApplyFilter()
    {
        var items = new ObservableCollection<object>();
        foreach (var s in _sections)
        {
            s.Visible = s.Rows.Where(Matches).ToList();
            s.Recompute();
            if (s.Visible.Count == 0) continue;
            items.Add(s);
            if (s.IsCollapsed) continue;
            foreach (var r in s.Visible) items.Add(r);
        }
        Items = items;
        ItemsReplaced?.Invoke();
    }

    internal void SetCollapsed(SectionModel section, bool collapsed)
    {
        if (section.IsCollapsed == collapsed) return;
        section.IsCollapsed = collapsed;
        if (collapsed) _collapsed.Add(section.Group.Id);
        else _collapsed.Remove(section.Group.Id);
        var at = Items.IndexOf(section);
        if (at < 0) return;
        if (collapsed)
        {
            while (at + 1 < Items.Count && Items[at + 1] is SensorRowModel) Items.RemoveAt(at + 1);
        }
        else
        {
            var i = at + 1;
            foreach (var r in section.Visible) Items.Insert(i++, r);
        }
    }

    /// <summary>Collapses / expands every section.</summary>
    public void SetAllCollapsed(bool collapsed)
    {
        foreach (var s in _sections)
        {
            s.IsCollapsed = collapsed;
            if (collapsed) _collapsed.Add(s.Group.Id);
            else _collapsed.Remove(s.Group.Id);
        }
        ApplyFilter();
    }

    /// <summary>Switch states from settings (after any change); filters are not re-applied, so rows never vanish under the pointer.</summary>
    public void Sync(AppSettings settings)
    {
        RecordingEnabled = settings.Recording.Enabled;
        foreach (var s in _sections)
        {
            foreach (var r in s.Rows) r.Sync(settings);
            s.Recompute();
        }
    }

    public void Relabel()
    {
        foreach (var s in _sections)
        {
            s.Relabel();
            s.Recompute();
            foreach (var r in s.Rows) r.Relabel();
        }
    }

    /// <summary>The sensors views show right now (what the page demands).</summary>
    public List<string> ShownIds()
    {
        var ids = new List<string>();
        foreach (var s in _sections)
            foreach (var r in s.Rows)
                if (r.IsShown) ids.Add(r.Sensor.Id);
        return ids;
    }

    public void Refresh(SensorFrame frame, SessionStats stats, TemperatureUnit unit)
    {
        foreach (var s in _sections)
            foreach (var r in s.Rows)
                if (r.IsShown) r.Refresh(frame, stats, unit);
    }
}
