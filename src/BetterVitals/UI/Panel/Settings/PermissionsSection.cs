using System.Windows;
using System.Windows.Controls;
using BetterVitals.Core;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.Platform;
using BetterVitals.Services;
using BetterVitals.UI.Controls;
using BetterVitals.UI.Toasts;
using static BetterVitals.UI.Panel.SettingsUi;

namespace BetterVitals.UI.Panel;

/// <summary>
/// Settings ▸ Permissions and driver: the reading mode (full / basic) with the action that changes it, administrator
/// rights, PawnIO (version, install / update), Windows security features (read-only), the anti-cheat pause switch with its
/// current reason, and the status of every sensor source in plain words.
/// </summary>
internal sealed class PermissionsSection : SectionView
{
    private readonly IPlatformService _platform;
    private readonly ISensorHub _hub;
    private readonly SettingRow _modeRow;
    private readonly StatusPill _modePill;
    private readonly SettingRow _adminRow;
    private readonly StatusPill _adminPill;
    private readonly SettingRow _pawnRow;
    private readonly StatusPill _pawnPill;
    private readonly SettingRow _antiCheatRow;
    private readonly Dictionary<string, StatusPill> _security = new();
    private readonly StackPanel _sources;
    private bool _installingFull;
    private bool _installingPawnIo;

    public PermissionsSection(PageContext? context, IPlatformService? platform = null, ISensorHub? hub = null) : base(context)
    {
        _platform = platform ?? AppServices.Platform;
        _hub = hub ?? context?.Hub ?? AppServices.Hub;
        var root = new StackPanel();
        root.Children.Add(Lead("settings.perm.lead"));

        // mode
        _modePill = new StatusPill();
        FullButton = Button("PrimaryButton", "settings.perm.enableFull", "Icon.Shield");
        FullButton.Click += async (_, _) => await EnableFullAsync();
        BasicButton = Button("SecondaryButton", "settings.perm.useBasic");
        BasicButton.Click += (_, _) => UseBasic();
        var modeActions = HStack(12, _modePill, FullButton, BasicButton);
        modeActions.HorizontalAlignment = HorizontalAlignment.Left;
        _modeRow = Row("settings.perm.mode", null, "Icon.Gauge", modeActions, stacked: true);

        _adminPill = new StatusPill();
        _adminRow = Row("settings.perm.admin", "settings.perm.admin.desc", "Icon.Shield", _adminPill);

        _pawnPill = new StatusPill();
        PawnIoButton = Button("SecondaryButton", null, "Icon.Download");
        PawnIoButton.Click += async (_, _) => await InstallPawnIoAsync();
        _pawnRow = Row("settings.perm.pawnio", "settings.perm.pawnio.desc", "Icon.Cpu", HStack(12, _pawnPill, PawnIoButton));

        var security = new WrapPanel { Margin = new Thickness(0, 0, 0, -8) };
        foreach (var (key, info) in new[] { ("settings.perm.hvci", "settings.perm.hvci.info"), ("settings.perm.secureBoot", "settings.perm.secureBoot.info"), ("settings.perm.vbs", "settings.perm.vbs.info") })
        {
            var pill = new StatusPill();
            _security[key] = pill;
            var name = Text(key, 12.5, "Brush.Text", FontWeights.SemiBold);
            name.TextWrapping = TextWrapping.NoWrap;
            name.VerticalAlignment = VerticalAlignment.Center;
            var chip = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 7, 8, 7),
                Margin = new Thickness(0, 0, 8, 8),
                BorderThickness = new Thickness(1),
                Child = HStack(0, name, new InfoDot { TextKey = info, Margin = new Thickness(6, 0, 10, 0) }, pill),
            };
            chip.SetResourceReference(Border.BackgroundProperty, "Brush.BgAlt");
            chip.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
            security.Children.Add(chip);
        }
        var securityRow = Row("settings.perm.security", "settings.perm.security.desc", "Icon.Shield", security, stacked: true);
        securityRow.SetResourceReference(SettingRow.IconProperty, "Icon.Layers");

        AntiCheatSwitch = Switch("settings.perm.anticheat");
        AntiCheatSwitch.Checked += (_, _) => SetPause(true);
        AntiCheatSwitch.Unchecked += (_, _) => SetPause(false);
        _antiCheatRow = Row("settings.perm.anticheat", "settings.perm.anticheat.desc", "Icon.Game", AntiCheatSwitch);

        root.Children.Add(Rows(_modeRow, _adminRow, _pawnRow, securityRow, _antiCheatRow));

        root.Children.Add(RecordingSection.SubTitle("settings.perm.sources"));
        _sources = new StackPanel();
        var sourcesCard = new Border { Child = _sources };
        sourcesCard.SetResourceReference(StyleProperty, "CardRows");
        root.Children.Add(sourcesCard);

        Content = root;
        Initialize();
    }

    internal Button FullButton { get; }

    internal Button BasicButton { get; }

    internal Button PawnIoButton { get; }

    internal CheckBox AntiCheatSwitch { get; }

    internal StatusPill ModePill => _modePill;

    internal StatusPill AdminPill => _adminPill;

    internal StatusPill PawnIoPill => _pawnPill;

    internal IReadOnlyDictionary<string, StatusPill> SecurityPills => _security;

    internal string AntiCheatReason => _antiCheatRow.Warning;

    /// <summary>(title, state, message) of each source row.</summary>
    internal IEnumerable<(string Title, string State, string Message)> SourceRows => _sources.Children.OfType<SettingRow>()
        .Select(r => (r.Title, (r.Content as StatusPill)?.Text ?? "", r.Description));

    protected override IReadOnlyCollection<string> Watches => ["driver", "general", "app"];

    protected override void OnAttached()
    {
        _platform.StateChanged += OnChanged;
        _hub.InventoryChanged += OnChanged;
    }

    protected override void OnDetached()
    {
        _platform.StateChanged -= OnChanged;
        _hub.InventoryChanged -= OnChanged;
    }

    private void OnChanged() => OnUi(Sync);

    protected override void Refresh()
    {
        var p = _platform;
        var mode = Current.Driver.Mode;

        // mode
        var fullActive = mode == ReadMode.Full && p.IsElevated;
        if (fullActive)
        {
            _modePill.Set(Loc.T("settings.perm.mode.full"), PillKind.Success);
            _modeRow.Description = Loc.T("settings.perm.mode.full.desc");
        }
        else if (mode == ReadMode.Full)
        {
            _modePill.Set(Loc.T("settings.perm.mode.pending"), PillKind.Warning);
            _modeRow.Description = Loc.T("settings.perm.mode.pending.desc");
        }
        else if (p.IsElevated)
        {
            _modePill.Set(Loc.T("settings.perm.mode.basicNext"), PillKind.Neutral);
            _modeRow.Description = Loc.T("settings.perm.mode.basicNext.desc");
        }
        else
        {
            _modePill.Set(Loc.T("settings.perm.mode.basic"), PillKind.Neutral);
            _modeRow.Description = Loc.T("settings.perm.mode.basic.desc");
        }
        FullButton.Visibility = fullActive ? Visibility.Collapsed : Visibility.Visible;
        BasicButton.Visibility = fullActive ? Visibility.Visible : Visibility.Collapsed;
        FullButton.IsEnabled = !_installingFull;
        var needsUac = !fullActive && !(p.IsElevated && p.IsInstalled);
        _modeRow.Warning = _installingFull ? Loc.T("settings.perm.installing") : needsUac ? Loc.T("settings.perm.enableFull.uac") : "";

        _adminPill.Set(Loc.T(p.IsElevated ? "settings.perm.yes" : "settings.perm.no"), p.IsElevated ? PillKind.Success : PillKind.Neutral);

        // PawnIO
        if (p.PawnIoVersion is not { } version)
        {
            _pawnPill.Set(Loc.T("settings.perm.pawnio.missing"), mode == ReadMode.Full ? PillKind.Warning : PillKind.Neutral);
            ButtonLabel(PawnIoButton)!.Text = Loc.T("settings.perm.pawnio.install", p.PawnIoBundledVersion.ToString(3));
            PawnIoButton.Visibility = Visibility.Visible;
            _pawnRow.Warning = "";
        }
        else if (p.PawnIoOutdated)
        {
            _pawnPill.Set(Loc.T("settings.perm.pawnio.outdated", version.ToString(3)), PillKind.Warning);
            ButtonLabel(PawnIoButton)!.Text = Loc.T("settings.perm.pawnio.update", p.PawnIoBundledVersion.ToString(3));
            PawnIoButton.Visibility = Visibility.Visible;
            _pawnRow.Warning = Loc.T("settings.perm.pawnio.outdated.desc");
        }
        else
        {
            _pawnPill.Set(Loc.T("settings.perm.pawnio.installed", version.ToString(3)), PillKind.Success);
            PawnIoButton.Visibility = Visibility.Collapsed;
            _pawnRow.Warning = "";
        }
        PawnIoButton.IsEnabled = !_installingPawnIo;
        if (_installingPawnIo) _pawnRow.Warning = Loc.T("settings.perm.pawnio.busy");

        // Windows security
        var sec = p.Security;
        SetSecurity("settings.perm.hvci", sec.MemoryIntegrity);
        SetSecurity("settings.perm.secureBoot", sec.SecureBoot);
        SetSecurity("settings.perm.vbs", sec.Vbs);

        // anti-cheat pause
        AntiCheatSwitch.IsChecked = Current.Driver.PauseForAntiCheat;
        _antiCheatRow.Warning = p.AntiCheatPauseReason is { Length: > 0 } reason ? Loc.T("settings.perm.anticheat.now", AntiCheatName(reason)) : "";

        RefreshSources();
    }

    private void SetSecurity(string key, bool? on)
        => _security[key].Set(Loc.T(on switch { true => "settings.perm.on", false => "settings.perm.off", _ => "settings.perm.unknown" }),
            on == true ? PillKind.Success : PillKind.Neutral);

    private void RefreshSources()
    {
        _sources.Children.Clear();
        IReadOnlyList<(string Source, SourceStatus Status)> statuses;
        try
        {
            statuses = _hub.SourceStatuses;
        }
        catch (Exception ex)
        {
            Log.Error("settings: source statuses failed", ex);
            statuses = [];
        }
        if (statuses.Count == 0)
        {
            var none = Row("settings.perm.sources.none", null, "Icon.Info");
            none.IsLast = true;
            _sources.Children.Add(none);
            return;
        }
        for (var i = 0; i < statuses.Count; i++)
        {
            var (source, status) = statuses[i];
            var pill = new StatusPill();
            pill.Set(SourceStatusText.State(status.State), SourceStatusText.Kind(status.State));
            var row = new SettingRow
            {
                Title = SourceStatusText.Name(source),
                Description = SourceStatusText.Message(status),
                Content = pill,
                IsLast = i == statuses.Count - 1,
            };
            row.SetResourceReference(SettingRow.IconProperty, SourceStatusText.IconKey(source));
            _sources.Children.Add(row);
        }
    }

    private void SetPause(bool on)
    {
        if (Syncing || Current.Driver.PauseForAntiCheat == on) return;
        Store.Mutate("driver", s => s.Driver.PauseForAntiCheat = on);
    }

    private void UseBasic()
    {
        Store.Mutate("driver", s => s.Driver.Mode = ReadMode.Basic);
        // basic mode starts unelevated: its autostart moves from the elevated task to the per-user Run entry
        if (Current.General.Autostart) _ = _platform.SetAutostartAsync(true);
        Toast("settings.perm.basicSoon", ToastKind.Info);
    }

    internal async Task EnableFullAsync()
    {
        if (_installingFull) return;
        var p = _platform;
        var before = Current.Driver.Mode;
        Store.Mutate("driver", s => s.Driver.Mode = ReadMode.Full);
        if (p.IsElevated && p.IsInstalled)
        {
            // back from basic: the elevated task takes the autostart over again (no prompt, we are elevated)
            if (Current.General.Autostart) _ = p.SetAutostartAsync(true);
            Toast("settings.perm.fullOn", ToastKind.Success);
            return;
        }
        // the elevated copy reads the settings: save before the prompt
        Store.Flush();
        _installingFull = true;
        Sync();
        bool ok;
        try
        {
            ok = await p.InstallFullModeAsync(Current.General.Autostart, p.PawnIoVersion is null || p.PawnIoOutdated);
        }
        catch (Exception ex)
        {
            Log.Error("settings: full mode install failed", ex);
            ok = false;
        }
        _installingFull = false;
        if (!ok)
        {
            Store.Mutate("driver", s => s.Driver.Mode = before);
            Toast("settings.perm.installFailed", ToastKind.Warning);
        }
        Sync();
    }

    internal async Task InstallPawnIoAsync()
    {
        if (_installingPawnIo) return;
        _installingPawnIo = true;
        Store.Mutate("driver", s => s.Driver.PawnIoOffered = true);
        Sync();
        PawnIoInstallOutcome? outcome;
        try
        {
            outcome = await _platform.InstallPawnIoAsync();
        }
        catch (Exception ex)
        {
            Log.Error("settings: PawnIO install failed", ex);
            outcome = PawnIoInstallOutcome.Failed;
        }
        _installingPawnIo = false;
        switch (outcome)
        {
            case PawnIoInstallOutcome.Installed:
                Toast("settings.perm.pawnio.done", ToastKind.Success);
                break;
            case PawnIoInstallOutcome.RestartRequired:
                Toast("settings.perm.pawnio.restart", ToastKind.Warning);
                break;
            case PawnIoInstallOutcome.Corrupted:
                Toast("settings.perm.pawnio.corrupted", ToastKind.Error);
                break;
            case PawnIoInstallOutcome.Failed:
                Toast("settings.perm.pawnio.failed", ToastKind.Error);
                break;
            default:
                Toast("settings.perm.pawnio.declined", ToastKind.Warning);
                break;
        }
        Sync();
    }

    /// <summary>"vgc.exe" → "Riot Vanguard"; unknown names are shown as they are.</summary>
    internal static string AntiCheatName(string reason)
    {
        var r = reason.ToLowerInvariant();
        if (r.StartsWith("vgc", StringComparison.Ordinal)) return "Riot Vanguard";
        if (r.StartsWith("faceit", StringComparison.Ordinal)) return "FACEIT";
        if (r.StartsWith("easyanticheat", StringComparison.Ordinal)) return "Easy Anti-Cheat";
        if (r.StartsWith("beservice", StringComparison.Ordinal)) return "BattlEye";
        if (r.StartsWith("eaanticheat", StringComparison.Ordinal)) return "EA Javelin";
        if (r.StartsWith("gameguard", StringComparison.Ordinal) || r.StartsWith("gamemon", StringComparison.Ordinal)) return "nProtect GameGuard";
        if (r.StartsWith("xigncode", StringComparison.Ordinal)) return "XIGNCODE3";
        if (r.StartsWith("esportal", StringComparison.Ordinal)) return "Esportal";
        if (r.StartsWith("eseaclient", StringComparison.Ordinal)) return "ESEA";
        return reason;
    }
}

/// <summary>Plain-word names, states and messages of the sensor sources (Strings.Status.cs).</summary>
internal static class SourceStatusText
{
    public static string Name(string source) => Loc.Has("source.name." + source) ? Loc.T("source.name." + source) : Loc.T("source.name.other", source);

    public static string State(SourceState state) => Loc.T("source.state." + state);

    public static PillKind Kind(SourceState state) => state switch
    {
        SourceState.Ok => PillKind.Success,
        SourceState.Limited => PillKind.Warning,
        SourceState.Paused => PillKind.Accent,
        SourceState.Unavailable => PillKind.Danger,
        _ => PillKind.Neutral,
    };

    public static string Message(SourceStatus status)
    {
        if (status.MessageKey is { Length: > 0 } key && Loc.Has(key)) return Loc.T(key);
        if (status.MessageKey is { Length: > 0 } unknown) Log.Warn($"settings: no text for source status '{unknown}'");
        return status.State switch
        {
            SourceState.Ok => Loc.T("source.ok"),
            SourceState.Idle => Loc.T("source.idle"),
            _ => "",
        };
    }

    public static string IconKey(string source) => source switch
    {
        "lhm" => "Icon.Cpu",
        "native" => "Icon.Monitor",
        "fps" => "Icon.Game",
        "derived" => "Icon.Sliders",
        _ => "Icon.Sparkle",
    };
}
