using System.Windows;
using System.Windows.Controls;
using BetterVitals.Core;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.Platform.Updates;
using BetterVitals.Services;
using BetterVitals.UI.Controls;
using static BetterVitals.UI.Panel.SettingsUi;

namespace BetterVitals.UI.Panel;

/// <summary>
/// Settings ▸ Updates: mode (Automatically / Ask / Off), the live state from <see cref="IPlatformService.UpdateState"/>
/// with the one action that fits it (Check now · Download · Restart to update), download progress, the release notes of
/// the available version, and the promise that this is the only internet connection.
/// </summary>
internal sealed class UpdatesSection : SectionView
{
    private readonly IPlatformService _platform;
    private readonly SettingRow _modeRow;
    private readonly SettingRow _stateRow;
    private readonly ProgressTrack _progress;
    private readonly Border _notesBox;
    private readonly TextBlock _notesTitle;
    private readonly StackPanel _notes;
    private readonly System.Windows.Shapes.Rectangle _notesDivider;
    private string? _notesFor;

    public UpdatesSection(PageContext? context, IPlatformService? platform = null) : base(context)
    {
        _platform = platform ?? AppServices.Platform;
        var root = new StackPanel();

        Mode = new Segments<UpdateMode>("upd-mode",
        [
            (UpdateMode.Automatic, "settings.upd.mode.auto"),
            (UpdateMode.Ask, "settings.upd.mode.ask"),
            (UpdateMode.Off, "settings.upd.mode.off"),
        ]);
        Mode.ValueChanged += m => Store.Mutate("updates", s => s.Updates.Mode = m);
        _modeRow = Row("settings.upd.mode", null, "Icon.Update", Mode);

        CheckButton = Button("SecondaryButton", "settings.upd.check", "Icon.Refresh");
        CheckButton.Click += async (_, _) => await Run(_platform.CheckForUpdatesAsync, "check");
        DownloadButton = Button("PrimaryButton", "settings.upd.download", "Icon.Export");
        DownloadButton.Click += async (_, _) => await Run(_platform.DownloadUpdateAsync, "download");
        RestartButton = Button("PrimaryButton", "settings.upd.restart", "Icon.Refresh");
        RestartButton.Click += (_, _) =>
        {
            try
            {
                _platform.RestartToUpdate();
            }
            catch (Exception ex)
            {
                Log.Error("settings: restart to update failed", ex);
            }
        };
        var actions = HStack(8, DownloadButton, RestartButton, CheckButton);
        _progress = new ProgressTrack { Width = 220, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        var stateBody = new StackPanel { Children = { actions } };
        _stateRow = Row("settings.upd.state.idle", null, "Icon.Info", stateBody);

        // release notes of the available version
        _notesTitle = Text(null, 13, "Brush.Text", FontWeights.SemiBold);
        _notes = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        _notesBox = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(48, 0, 0, 16),
            Visibility = Visibility.Collapsed,
            Child = new StackPanel { Children = { _notesTitle, _notes } },
        };
        _notesBox.SetResourceReference(Border.BackgroundProperty, "Brush.BgAlt");

        var privacy = Row("settings.upd.privacy", "settings.upd.privacy.desc", "Icon.Shield");

        var card = Rows(_modeRow, _stateRow);
        var panel = (StackPanel)card.Child;
        panel.Children.Insert(panel.Children.IndexOf(_stateRow) + 1, _notesBox);
        _notesDivider = new System.Windows.Shapes.Rectangle { Visibility = Visibility.Collapsed };
        _notesDivider.SetResourceReference(StyleProperty, "Divider");
        panel.Children.Insert(panel.Children.IndexOf(_notesBox) + 1, _notesDivider);
        _stateRow.IsLast = false;
        panel.Children.Add(privacy);
        privacy.IsLast = true;
        // the progress line sits under the state text
        ProgressHost = _progress;
        root.Children.Add(card);
        Content = root;
        Initialize();
    }

    internal Segments<UpdateMode> Mode { get; }

    internal Button CheckButton { get; }

    internal Button DownloadButton { get; }

    internal Button RestartButton { get; }

    internal ProgressTrack ProgressHost { get; }

    internal string StateTitle => _stateRow.Title;

    internal string StateText => _stateRow.Description;

    internal bool NotesVisible => _notesBox.Visibility == Visibility.Visible;

    protected override IReadOnlyCollection<string> Watches => ["updates"];

    protected override void OnAttached() => _platform.StateChanged += OnPlatform;

    protected override void OnDetached() => _platform.StateChanged -= OnPlatform;

    private void OnPlatform() => OnUi(Sync);

    protected override void Relabel() => _notesFor = null;

    protected override void Refresh()
    {
        var u = Current.Updates;
        Mode.Value = u.Mode;
        _modeRow.Description = Loc.T(u.Mode switch
        {
            UpdateMode.Automatic => "settings.upd.mode.auto.desc",
            UpdateMode.Off => "settings.upd.mode.off.desc",
            _ => "settings.upd.mode.ask.desc",
        });

        var state = _platform.UpdateState;
        var release = _platform.AvailableRelease;
        var version = release is not null ? release.Version.ToString(3) : "";
        var current = AppEnv.ShortVersion;
        string title;
        string desc;
        var icon = "Icon.Info";
        switch (state)
        {
            case UpdateUiState.Checking:
                title = Loc.T("settings.upd.state.checking");
                desc = LastCheck(u);
                icon = "Icon.Refresh";
                break;
            case UpdateUiState.UpToDate:
                title = Loc.T("settings.upd.state.upToDate", current);
                desc = LastCheck(u);
                icon = "Icon.Check";
                break;
            case UpdateUiState.Available when release is not null:
                title = Loc.T("settings.upd.state.available", version);
                desc = Loc.T("settings.upd.available.desc", release.PublishedUtc.ToLocalTime().ToString("d", Loc.I.Culture), Bytes(release.Size));
                icon = "Icon.Sparkle";
                break;
            case UpdateUiState.Downloading when release is not null:
                title = Loc.T("settings.upd.state.downloading", version, Percent(_platform.UpdateProgress));
                desc = Loc.T("settings.upd.downloading.desc");
                icon = "Icon.Export";
                break;
            case UpdateUiState.Ready when release is not null:
                title = Loc.T("settings.upd.state.ready", version);
                desc = Loc.T("settings.upd.ready.desc");
                icon = "Icon.Sparkle";
                break;
            case UpdateUiState.Failed:
                title = Loc.T("settings.upd.state.failed");
                desc = Loc.T("settings.upd.failed.desc");
                icon = "Icon.Warning";
                break;
            default:
                title = Loc.T("settings.upd.state.idle", current);
                desc = LastCheck(u);
                break;
        }
        _stateRow.Title = title;
        _stateRow.Description = desc;
        _stateRow.Warning = "";
        _stateRow.SetResourceReference(SettingRow.IconProperty, icon);

        var busy = state is UpdateUiState.Checking or UpdateUiState.Downloading;
        CheckButton.IsEnabled = !busy;
        CheckButton.Visibility = state is UpdateUiState.Ready or UpdateUiState.Available or UpdateUiState.Downloading ? Visibility.Collapsed : Visibility.Visible;
        DownloadButton.Visibility = state == UpdateUiState.Available && release is not null ? Visibility.Visible : Visibility.Collapsed;
        RestartButton.Visibility = state == UpdateUiState.Ready && release is not null ? Visibility.Visible : Visibility.Collapsed;

        // progress under the description while downloading
        var stateBody = (StackPanel)_stateRow.Content;
        var showProgress = state == UpdateUiState.Downloading;
        if (showProgress && !stateBody.Children.Contains(_progress)) stateBody.Children.Add(_progress);
        if (!showProgress) stateBody.Children.Remove(_progress);
        _progress.Value = _platform.UpdateProgress;

        // notes
        if (release is not null && state is UpdateUiState.Available or UpdateUiState.Downloading or UpdateUiState.Ready)
        {
            _notesBox.Visibility = Visibility.Visible;
            _notesDivider.Visibility = Visibility.Visible;
            _stateRow.IsLast = true; // the notes belong to the state row: one separator below both
            if (_notesFor != release.Tag + Loc.I.Language)
            {
                _notesFor = release.Tag + Loc.I.Language;
                _notesTitle.Text = Loc.T("settings.upd.notes", version);
                ReleaseNotesText.Fill(_notes, release.Notes);
            }
        }
        else
        {
            _notesBox.Visibility = Visibility.Collapsed;
            _notesDivider.Visibility = Visibility.Collapsed;
            _stateRow.IsLast = false;
            _notesFor = null;
        }
    }

    private static string LastCheck(UpdateSettings u)
        => u.LastCheckUtc is { } t
            ? Loc.T("settings.upd.lastCheck", t.ToLocalTime().ToString("g", Loc.I.Culture))
            : Loc.T("settings.upd.neverChecked");

    private static string Percent(double v) => (Math.Clamp(double.IsFinite(v) ? v : 0, 0, 1)).ToString("P0", Loc.I.Culture);

    private async Task Run(Func<Task> action, string what)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log.Error($"settings: update {what} failed", ex);
        }
        Sync();
    }
}

/// <summary>
/// GitHub release notes (Markdown) as plain, readable blocks: "#" lines become headings, "-" / "*" lines bullets, the rest
/// paragraphs; inline marks (**, `, links) are reduced to their text. Nothing is ever rendered as HTML.
/// </summary>
internal static class ReleaseNotesText
{
    public static void Fill(System.Windows.Controls.Panel target, string? markdown)
    {
        target.Children.Clear();
        if (string.IsNullOrWhiteSpace(markdown)) return;
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                var h = Text(null, 12.5, "Brush.Text", FontWeights.SemiBold);
                h.Text = Clean(line.TrimStart('#').Trim());
                h.Margin = new Thickness(0, target.Children.Count == 0 ? 0 : 8, 0, 2);
                target.Children.Add(h);
            }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                var dock = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
                var dot = Text(null, 12.5, "Brush.AccentText", FontWeights.Bold);
                dot.Text = "•";
                dot.Margin = new Thickness(2, 0, 8, 0);
                dock.Children.Add(dot);
                var t = Text(null, 12.5, "Brush.TextDim");
                t.Text = Clean(line[2..].Trim());
                dock.Children.Add(t);
                target.Children.Add(dock);
            }
            else
            {
                var t = Text(null, 12.5, "Brush.TextDim");
                t.Text = Clean(line);
                t.Margin = new Thickness(0, 2, 0, 0);
                target.Children.Add(t);
            }
        }
    }

    /// <summary>"**New** [link](url) `code`" → "New link code".</summary>
    internal static string Clean(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1");
        return s.Replace("**", "").Replace("__", "").Replace("`", "");
    }
}
