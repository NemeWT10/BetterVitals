using System.Windows.Controls;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using static BetterVitals.UI.Panel.SettingsUi;

namespace BetterVitals.UI.Panel;

/// <summary>
/// Settings ▸ Overlay: when it shows (always / only in games), refresh, hidden from captures, °C / °F, and the way to the
/// overlay editor. What and where is the editor's job; the overlay itself is switched on and off from the tray.
/// </summary>
internal sealed class OverlayBehaviorSection : SectionView
{
    internal static readonly int[] RefreshSteps = [250, 500, 1000, 2000];

    public OverlayBehaviorSection(PageContext? context) : base(context)
    {
        var root = new StackPanel();
        root.Children.Add(Lead("settings.ovl.lead"));

        VisibilityChoice = new Segments<OverlayVisibility>("ovl-visibility",
        [
            (OverlayVisibility.Always, "settings.ovl.visibility.always"),
            (OverlayVisibility.GamesOnly, "settings.ovl.visibility.games"),
        ]);
        VisibilityChoice.ValueChanged += v => Store.Mutate("overlay", s => s.Overlay.Visibility = v);

        RefreshChoice = new ChoiceCombo<int>(RefreshSteps.Select(ms => (ms, (Func<string>)(() => Seconds(ms)))), 110);
        RefreshChoice.ValueChanged += ms => Store.Mutate("overlay", s => s.Overlay.RefreshMs = ms);

        CaptureSwitch = Switch("settings.ovl.capture");
        CaptureSwitch.Checked += (_, _) => SetCapture(true);
        CaptureSwitch.Unchecked += (_, _) => SetCapture(false);

        Unit = new Segments<TemperatureUnit>("ovl-unit",
        [
            (TemperatureUnit.Celsius, "settings.ovl.unit.c"),
            (TemperatureUnit.Fahrenheit, "settings.ovl.unit.f"),
        ]);
        Unit.ValueChanged += u => Store.Mutate("general", s => s.General.TemperatureUnit = u);

        EditorButton = Button("SecondaryButton", "settings.ovl.editor.open", "Icon.Overlay");
        EditorButton.Click += (_, _) => Context?.Navigate(PageIds.Overlay);

        root.Children.Add(Rows(
            Row("settings.ovl.visibility", "settings.ovl.visibility.desc", "Icon.Game", VisibilityChoice),
            Row("settings.ovl.refresh", "settings.ovl.refresh.desc", "Icon.Refresh", RefreshChoice),
            Row("settings.ovl.capture", "settings.ovl.capture.desc", "Icon.EyeOff", CaptureSwitch, infoKey: "settings.ovl.capture.info"),
            Row("settings.ovl.unit", "settings.ovl.unit.desc", "Icon.Thermometer", Unit),
            Row("settings.ovl.editor", "settings.ovl.editor.desc", "Icon.Sliders", EditorButton)));
        Content = root;
        Initialize();
    }

    internal Segments<OverlayVisibility> VisibilityChoice { get; }

    internal ChoiceCombo<int> RefreshChoice { get; }

    internal CheckBox CaptureSwitch { get; }

    internal Segments<TemperatureUnit> Unit { get; }

    internal Button EditorButton { get; }

    protected override IReadOnlyCollection<string> Watches => ["overlay", "general"];

    protected override void Relabel() => RefreshChoice.Relabel();

    protected override void Refresh()
    {
        var o = Current.Overlay;
        VisibilityChoice.Value = o.Visibility;
        RefreshChoice.Value = RefreshSteps.MinBy(s => Math.Abs(s - o.RefreshMs));
        CaptureSwitch.IsChecked = o.HideFromCapture;
        Unit.Value = Current.General.TemperatureUnit;
    }

    private void SetCapture(bool on)
    {
        if (Syncing || Current.Overlay.HideFromCapture == on) return;
        Store.Mutate("overlay", s => s.Overlay.HideFromCapture = on);
    }

    /// <summary>"0,25 s" / "1 s" in the UI language.</summary>
    internal static string Seconds(int ms) => Loc.T("settings.common.seconds", (ms / 1000.0).ToString("0.##", Loc.I.Culture));
}
