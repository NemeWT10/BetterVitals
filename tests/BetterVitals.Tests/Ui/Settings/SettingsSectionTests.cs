using System.Windows;
using System.Windows.Controls.Primitives;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Fps;
using BetterVitals.Localization;
using BetterVitals.Platform;
using BetterVitals.Sensors.Lhm;
using BetterVitals.Services;
using BetterVitals.UI.About;
using BetterVitals.UI.Panel;
using BetterVitals.UI.Toasts;
using Xunit;

namespace BetterVitals.Tests.Ui.Settings;

[Collection(UiCollection.Name)]
public sealed class SettingsSectionTests
{
    private static void Click(ButtonBase b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static void Wait(Task t) => Assert.True(Sta.PumpUntil(() => t.IsCompleted, 5000));

    [Fact]
    public void Sections_are_registered_in_order_between_general_and_about()
    {
        var ids = SettingsSections.All.Select(s => s.Id).Where(i => !i.StartsWith("test", StringComparison.Ordinal)).ToList();
        Assert.Equal(["general", "recording", "overlay", "startup", "updates", "permissions", "about"], ids);
        foreach (var s in SettingsSections.All) Assert.True(Loc.Has(s.TitleKey), s.TitleKey);
    }

    [Fact]
    public void Recording_controls_write_the_recording_settings() => Sta.Run(() =>
    {
        using var env = new TestEnv();
        var history = new FakeHistory();
        var view = new RecordingSection(env.Host.Context(history: history));
        var r = env.Store.Current.Recording;
        Assert.True(view.EnabledSwitch.IsChecked);
        Assert.Equal(2, view.Interval.Value);
        Assert.Equal(7, view.Raw.Value);
        Assert.Equal(365, view.Minute.Value);
        Assert.Equal(0, view.Hour.Value);

        view.EnabledSwitch.IsChecked = false;
        Assert.False(r.Enabled);
        view.Interval.Pick(10);
        Assert.Equal(10, r.IntervalSeconds);
        view.Raw.Pick(30);
        Assert.Equal(30, r.RawRetentionDays);
        view.Hour.Pick(365);
        Assert.Equal(365, r.HourRetentionDays);

        // summaries never end before the detail they summarize
        view.Raw.Pick(90);
        Assert.Equal(90, r.RawRetentionDays);
        Assert.True(r.MinuteRetentionDays >= 90);
        view.AttachForTests();
        view.Minute.Pick(30);
        Assert.Equal(30, r.MinuteRetentionDays);
        Assert.True(r.RawRetentionDays <= 30);
        Assert.Equal(r.RawRetentionDays, view.Raw.Value);

        // a change from elsewhere shows up
        env.Store.Mutate("recording", s => s.Recording.Enabled = true);
        Assert.True(view.EnabledSwitch.IsChecked);
        view.DetachForTests();
    });

    [Fact]
    public void Retention_offers_forever_summaries_a_space_limit_and_size_estimates() => Sta.Run(() =>
    {
        using var env = new TestEnv();
        env.Store.Mutate("recording", s => s.Recording.SensorIds.UnionWith(["a", "b", "c"]));
        var view = new RecordingSection(env.Host.Context(history: new FakeHistory()));
        view.AttachForTests();
        var r = env.Store.Current.Recording;
        Assert.Equal(1000, view.Limit.Value);
        Assert.StartsWith("about ", view.SizeTexts.Raw);
        Assert.StartsWith("about ", view.SizeTexts.Minute);
        Assert.EndsWith(" a year", view.SizeTexts.Hour); // hour summaries are kept forever by default
        Assert.StartsWith("In total with these settings: about ", view.SizeTexts.Total);
        Assert.Contains(" more each year", view.SizeTexts.Total);

        // minute summaries kept forever take the hour ones along; a shorter hour retention pulls both back
        view.Hour.Pick(730);
        view.Minute.Pick(0);
        Assert.Equal(0, r.MinuteRetentionDays);
        Assert.Equal(0, r.HourRetentionDays);
        Assert.EndsWith(" a year", view.SizeTexts.Minute);
        view.Raw.Pick(365);
        Assert.Equal(365, r.RawRetentionDays);
        view.Hour.Pick(90);
        Assert.Equal((90, 90, 90), (r.RawRetentionDays, r.MinuteRetentionDays, r.HourRetentionDays));
        Assert.DoesNotContain(" more each year", view.SizeTexts.Total);

        view.Limit.Pick(0);
        Assert.Equal(0, r.MaxHistoryMegabytes);
        Assert.Equal("No limit", RecordingSection.LimitLabel(0));
        Assert.Equal("250 MB", RecordingSection.LimitLabel(250));
        Assert.Equal("2 GB", RecordingSection.LimitLabel(2000));

        // a limit under the estimate says the oldest data goes sooner
        env.Store.Mutate("recording", s =>
        {
            s.Recording.SensorIds.UnionWith(Enumerable.Range(0, 400).Select(i => "s" + i));
            s.Recording.IntervalSeconds = 1;
            s.Recording.MaxHistoryMegabytes = 100;
        });
        Assert.Equal(100, view.Limit.Value);
        Assert.EndsWith(Loc.T("settings.rec.total.capped"), view.SizeTexts.Total);

        // live only: nothing to estimate
        env.Store.Mutate("recording", s => s.Recording.Enabled = false);
        Assert.False(view.SizesVisible);
        view.DetachForTests();
    });

    [Fact]
    public void Recording_summary_usage_and_clear() => Sta.Run(() =>
    {
        using var env = new TestEnv();
        env.Store.Mutate("recording", s => s.Recording.SensorIds.UnionWith(["a", "b", "c"]));
        var history = new FakeHistory();
        var view = new RecordingSection(env.Host.Context(history: history));
        Assert.StartsWith("3 sensors · about ", view.SummaryText);
        Assert.StartsWith("63 MB · data since ", view.UsageText); // decimal units and a no-break space, as on the Sensors page

        Click(view.ClearButton);
        Assert.True(view.ConfirmVisible);
        Assert.Equal(0, history.Cleared);
        Click(view.ConfirmButton);
        Assert.True(Sta.PumpUntil(() => history.Cleared == 1 && !view.ConfirmVisible));
        Assert.Equal(Loc.T("settings.rec.usage.empty"), view.UsageText);
        Assert.False(view.ClearButton.IsEnabled);
        Assert.Contains(env.Host.Toasts, t => t.Text == "History cleared" && t.Kind == ToastKind.Success);

        // no history attached yet
        var none = new RecordingSection(env.Host.Context());
        Assert.Equal(Loc.T("settings.rec.usage.unavailable"), none.UsageText);
        Assert.False(none.ClearButton.IsEnabled);
    });

    [Fact]
    public void Retention_labels_read_naturally()
    {
        var before = Loc.I.Language;
        try
        {
            Loc.I.SetLanguage("pl");
            Assert.Equal("1 dzień", RecordingSection.Days(1));
            Assert.Equal("3 dni", RecordingSection.Days(3));
            Assert.Equal("1 rok", RecordingSection.Days(365));
            Assert.Equal("2 lata", RecordingSection.Days(730));
            Assert.Equal("5 lat", RecordingSection.Days(1825));
            Assert.Equal("Bez limitu", RecordingSection.Days(0));
            Assert.Equal("0,25 s", OverlayBehaviorSection.Seconds(250));
            Loc.I.SetLanguage("en");
            Assert.Equal("7 days", RecordingSection.Days(7));
            Assert.Equal("1 year", RecordingSection.Days(365));
            Assert.Equal("0.5 s", OverlayBehaviorSection.Seconds(500));
        }
        finally
        {
            Loc.I.SetLanguage(before);
        }
    }

    [Fact]
    public void Overlay_controls_write_overlay_and_unit() => Sta.Run(() =>
    {
        using var env = new TestEnv();
        var view = new OverlayBehaviorSection(env.Host.Context());
        var s = env.Store.Current;
        Assert.Equal(OverlayVisibility.Always, view.VisibilityChoice.Value);
        Assert.Equal(1000, view.RefreshChoice.Value);
        Assert.True(view.CaptureSwitch.IsChecked);
        Assert.Equal(TemperatureUnit.Celsius, view.Unit.Value);

        view.VisibilityChoice.Pick(OverlayVisibility.GamesOnly);
        view.RefreshChoice.Pick(250);
        view.CaptureSwitch.IsChecked = false;
        view.Unit.Pick(TemperatureUnit.Fahrenheit);
        Assert.Equal(OverlayVisibility.GamesOnly, s.Overlay.Visibility);
        Assert.Equal(250, s.Overlay.RefreshMs);
        Assert.False(s.Overlay.HideFromCapture);
        Assert.Equal(TemperatureUnit.Fahrenheit, s.General.TemperatureUnit);

        Click(view.EditorButton);
        Assert.Equal([PageIds.Overlay], env.Host.Navigations);
    });

    [Fact]
    public void Startup_switch_uses_the_platform_and_follows_what_was_applied() => Sta.Run(() =>
    {
        using var env = new TestEnv();
        var p = env.Platform;
        var view = new StartupSection(env.Host.Context(), p);
        Assert.False(view.AutostartSwitch.IsChecked);
        Assert.Equal(Loc.T("settings.start.uac"), view.AutostartWarning);

        view.AutostartSwitch.IsChecked = true;
        Assert.True(Sta.PumpUntil(() => p.Calls.Count == 1 && env.Store.Current.General.Autostart));
        Assert.Equal(["SetAutostart(True)"], p.Calls);
        Assert.True(view.AutostartSwitch.IsChecked);

        // Windows refused: the switch and the setting go back
        p.AutostartResult = true;
        view.AutostartSwitch.IsChecked = false;
        Assert.True(Sta.PumpUntil(() => p.Calls.Count == 2));
        Sta.Pump(30);
        Assert.True(view.AutostartSwitch.IsChecked);
        Assert.True(env.Store.Current.General.Autostart);
        Assert.Contains(env.Host.Toasts, t => t.Kind == ToastKind.Warning);

        // elevated: no UAC warning
        p.IsElevated = true;
        view.AttachForTests();
        p.RaiseChanged();
        Assert.Equal("", view.AutostartWarning);
        view.DetachForTests();
        Assert.Equal(0, p.StateChangedSubscribers);
    });

    [Fact]
    public void Updates_show_each_state_with_its_action() => Sta.Run(() =>
    {
        using var env = new TestEnv();
        var p = env.Platform;
        var view = new UpdatesSection(env.Host.Context(), p);
        view.AttachForTests();
        Assert.Equal(UpdateMode.Ask, view.Mode.Value);
        view.Mode.Pick(UpdateMode.Off);
        Assert.Equal(UpdateMode.Off, env.Store.Current.Updates.Mode);

        Assert.StartsWith("You have version ", view.StateTitle);
        Assert.Equal("Not checked yet.", view.StateText);
        Assert.Equal(Visibility.Visible, view.CheckButton.Visibility);
        Click(view.CheckButton);
        Assert.True(Sta.PumpUntil(() => p.Calls.Contains("CheckForUpdates")));

        p.UpdateState = UpdateUiState.Checking;
        p.RaiseChanged();
        Assert.Equal("Looking for a new version…", view.StateTitle);
        Assert.False(view.CheckButton.IsEnabled);

        p.UpdateState = UpdateUiState.Available;
        p.AvailableRelease = FakePlatform.Release();
        p.RaiseChanged();
        Assert.Equal("New version 0.2.0 is available", view.StateTitle);
        Assert.Contains("30 MB", view.StateText);
        Assert.Equal(Visibility.Visible, view.DownloadButton.Visibility);
        Assert.True(view.NotesVisible);
        Click(view.DownloadButton);
        Assert.True(Sta.PumpUntil(() => p.Calls.Contains("DownloadUpdate")));

        p.UpdateState = UpdateUiState.Downloading;
        p.UpdateProgress = 0.5;
        p.RaiseChanged();
        Assert.Equal("Downloading version 0.2.0… 50%", view.StateTitle);
        Assert.Equal(0.5, view.ProgressHost.Value, 3);

        p.UpdateState = UpdateUiState.Ready;
        p.RaiseChanged();
        Assert.Equal("Version 0.2.0 is ready to install", view.StateTitle);
        Assert.Equal(Visibility.Visible, view.RestartButton.Visibility);
        Assert.Equal(Visibility.Collapsed, view.DownloadButton.Visibility);
        Click(view.RestartButton);
        Assert.Contains("RestartToUpdate", p.Calls);

        p.UpdateState = UpdateUiState.Failed;
        p.AvailableRelease = null;
        p.RaiseChanged();
        Assert.Equal("Could not check for updates", view.StateTitle);
        Assert.False(view.NotesVisible);

        p.UpdateState = UpdateUiState.UpToDate;
        env.Store.Mutate("updates", s => s.Updates.LastCheckUtc = DateTime.UtcNow);
        p.RaiseChanged();
        Assert.StartsWith("You're up to date", view.StateTitle);
        Assert.StartsWith("Last checked: ", view.StateText);
        view.DetachForTests();
        Assert.Equal(0, p.StateChangedSubscribers);
    });

    [Fact]
    public void Release_notes_markdown_is_reduced_to_text()
    {
        Assert.Equal("Faster charts and a link", ReleaseNotesText.Clean("**Faster** charts and a [link](https://x.invalid)"));
        Assert.Equal("code", ReleaseNotesText.Clean("`code`"));
    }

    [Fact]
    public void Permissions_basic_mode_offers_full_mode_and_pawnio() => Sta.Run(() =>
    {
        using var env = new TestEnv();
        env.Store.Mutate("driver", s => s.Driver.Mode = ReadMode.Basic);
        var p = env.Platform;
        p.Security = new SecurityInfo(true, false, null);
        var hub = new StatusHub();
        hub.Statuses.Add(("lhm", new SourceStatus(SourceState.Limited, LhmStatusKeys.NotElevated)));
        hub.Statuses.Add(("fps", new SourceStatus(SourceState.Unavailable, FpsSource.StatusNoAccess)));
        hub.Statuses.Add(("native", new SourceStatus(SourceState.Ok)));
        var view = new PermissionsSection(env.Host.Context(hub), p, hub);

        Assert.Equal("Basic", view.ModePill.Text);
        Assert.Equal(Visibility.Visible, view.FullButton.Visibility);
        Assert.Equal(Visibility.Collapsed, view.BasicButton.Visibility);
        Assert.Equal("No", view.AdminPill.Text);
        Assert.Equal("Not installed", view.PawnIoPill.Text);
        Assert.Equal(Visibility.Visible, view.PawnIoButton.Visibility);
        Assert.Equal("On", view.SecurityPills["settings.perm.hvci"].Text);
        Assert.Equal("Off", view.SecurityPills["settings.perm.secureBoot"].Text);
        Assert.Equal("Unknown", view.SecurityPills["settings.perm.vbs"].Text);
        var rows = view.SourceRows.ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(("Hardware sensors (LibreHardwareMonitor)", "Partly", Loc.T(LhmStatusKeys.NotElevated)), rows[0]);
        Assert.Equal(("FPS in games", "Unavailable", Loc.T(FpsSource.StatusNoAccess)), rows[1]);
        Assert.Equal(("Windows sensors", "Working", "All good."), rows[2]);

        // PawnIO install
        Wait(view.InstallPawnIoAsync());
        Assert.Contains("InstallPawnIo", p.Calls);
        Assert.Equal("Version 2.2.0", view.PawnIoPill.Text);
        Assert.Equal(Visibility.Collapsed, view.PawnIoButton.Visibility);
        Assert.True(env.Store.Current.Driver.PawnIoOffered);

        // full mode: declined → mode stays basic
        p.FullModeResult = false;
        Wait(view.EnableFullAsync());
        Assert.Contains("InstallFullMode(autostart=True, pawnIo=False)", p.Calls);
        Assert.Equal(ReadMode.Basic, env.Store.Current.Driver.Mode);
        Assert.Contains(env.Host.Toasts, t => t.Kind == ToastKind.Warning);

        // full mode: accepted → Full saved (the elevated copy takes over)
        p.FullModeResult = true;
        Wait(view.EnableFullAsync());
        Assert.Equal(ReadMode.Full, env.Store.Current.Driver.Mode);
    });

    [Fact]
    public void Permissions_full_mode_switches_to_basic_and_anti_cheat_pause() => Sta.Run(() =>
    {
        using var env = new TestEnv();
        var p = env.Platform;
        p.IsElevated = true;
        p.IsInstalled = true;
        p.PawnIoVersion = new Version(2, 0, 3);
        var hub = new StatusHub();
        var view = new PermissionsSection(env.Host.Context(hub), p, hub);
        view.AttachForTests();
        Assert.Equal("Full", view.ModePill.Text);
        Assert.Equal(Visibility.Visible, view.BasicButton.Visibility);
        Assert.Equal("Yes", view.AdminPill.Text);
        Assert.Equal("Version 2.0.3 — outdated", view.PawnIoPill.Text);
        Assert.Equal(Loc.T("settings.perm.sources.none"), view.SourceRows.Single().Title);

        Assert.False(view.AntiCheatSwitch.IsChecked);
        view.AntiCheatSwitch.IsChecked = true;
        Assert.True(env.Store.Current.Driver.PauseForAntiCheat);
        Assert.Equal("", view.AntiCheatReason);
        p.AntiCheatPauseReason = "vgc.exe";
        p.RaiseChanged();
        Assert.Equal("Paused right now: Riot Vanguard", view.AntiCheatReason);

        hub.Statuses.Add(("lhm", new SourceStatus(SourceState.Paused, LhmStatusKeys.Paused)));
        hub.RaiseInventory();
        Assert.Equal("Paused", view.SourceRows.Single().State);

        env.Store.Mutate("general", s => s.General.Autostart = true);
        Click(view.BasicButton);
        Assert.Equal(ReadMode.Basic, env.Store.Current.Driver.Mode);
        Assert.Equal("Basic from the next start", view.ModePill.Text);
        // the autostart moves to the per-user entry (re-applied in basic mode)
        Assert.Equal("SetAutostart(True)", p.Calls[^1]);
        // back to full without any prompt: already installed and elevated; the task takes the autostart back
        p.Calls.Clear();
        Wait(view.EnableFullAsync());
        Assert.Equal(ReadMode.Full, env.Store.Current.Driver.Mode);
        Assert.Equal(["SetAutostart(True)"], p.Calls);
        view.DetachForTests();
        Assert.Equal(0, p.StateChangedSubscribers);
    });

    [Fact]
    public void Every_source_status_key_has_a_text_in_both_languages()
    {
        string[] fromSources =
        [
            LhmStatusKeys.NotElevated, LhmStatusKeys.NoPawnIo, LhmStatusKeys.OldPawnIo, LhmStatusKeys.DriverNotRunning, LhmStatusKeys.Paused,
            LhmStatusKeys.Failed, FpsSource.StatusNoAccess, FpsSource.StatusBlocked, LhmClassifier.HotSpotNoteKey,
        ];
        foreach (var key in fromSources.Concat(StatusStrings.SourceKeys))
        {
            Assert.True(Loc.AllEntries.TryGetValue(key, out var t), key);
            Assert.False(string.IsNullOrWhiteSpace(t.Pl), key);
            Assert.False(string.IsNullOrWhiteSpace(t.En), key);
        }
        foreach (var state in Enum.GetValues<SourceState>()) Assert.True(Loc.Has("source.state." + state), state.ToString());
        foreach (var name in new[] { "lhm", "native", "fps", "derived", "fake" }) Assert.True(Loc.Has("source.name." + name), name);
        Assert.Equal("Unofficial reading (undocumented sensor), about ±1 °C", Loc.AllEntries["sensor.note.unofficialHotspot"].En);
    }

    [Fact]
    public void Settings_strings_are_complete_and_placeholders_match()
    {
        foreach (var (key, (pl, en)) in Loc.AllEntries.Where(e => e.Key.StartsWith("settings.", StringComparison.Ordinal) || e.Key.StartsWith("about.", StringComparison.Ordinal)
                     || e.Key.StartsWith("firstrun.", StringComparison.Ordinal) || e.Key.StartsWith("source.", StringComparison.Ordinal)))
        {
            Assert.False(string.IsNullOrWhiteSpace(pl), key);
            Assert.False(string.IsNullOrWhiteSpace(en), key);
            var a = System.Text.RegularExpressions.Regex.Matches(pl, @"\{(\d+)\}").Select(m => m.Value).Order();
            var b = System.Text.RegularExpressions.Regex.Matches(en, @"\{(\d+)\}").Select(m => m.Value).Order();
            Assert.True(a.SequenceEqual(b), key);
        }
    }

    [Fact]
    public void About_parses_the_version_and_lists_every_license()
    {
        Assert.Equal(("0.1.0", (string?)"abc1234", (DateOnly?)new DateOnly(2026, 10, 23)), AboutView.ParseVersion("0.1.0+abc1234.20261023"));
        Assert.Equal(("0.1.0", (string?)null, (DateOnly?)null), AboutView.ParseVersion("0.1.0"));
        Assert.Equal(("1.2.3", (string?)"local", (DateOnly?)null), AboutView.ParseVersion("1.2.3+local.x"));

        var names = Licenses.All.Select(l => l.Name).ToList();
        Assert.Contains("BetterVitals", names);
        Assert.Contains("LibreHardwareMonitor", names);
        Assert.Contains("PawnIO", names);
        foreach (var font in BetterVitals.Overlay.Fonts.BundledFonts.All) Assert.Contains(font.FamilyName, names);
        foreach (var l in Licenses.All) Assert.False(string.IsNullOrWhiteSpace(l.Text()), l.Name);
        Assert.Contains("MIT License", Licenses.Embedded(Licenses.LicenseResource));
        Assert.Contains("Third-party notices", Licenses.Embedded(Licenses.NoticesResource));
        Assert.Contains("MPL-2.0", Licenses.All.Single(l => l.Name == "LibreHardwareMonitor").Text());
        Assert.Contains("Open Font License", Licenses.All.Single(l => l.Name == "Inter").Text(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Release_notes_start_with_the_current_version_in_both_languages()
    {
        Assert.NotEmpty(ReleaseNotes.All);
        Assert.Equal(BetterVitals.Core.AppEnv.ShortVersion, ReleaseNotes.All[0].Version);
        Assert.Equal("0.1.0", ReleaseNotes.All[^1].Version);
        foreach (var n in ReleaseNotes.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(n.SummaryPl));
            Assert.False(string.IsNullOrWhiteSpace(n.SummaryEn));
            Assert.All(n.Changes, c => Assert.False(string.IsNullOrWhiteSpace(c.Pl) || string.IsNullOrWhiteSpace(c.En)));
        }
        Assert.Equal(ReleaseNotes.All.Count, ReleaseNotes.All.Select(n => n.Version).Distinct().Count());
    }

    [Fact]
    public void About_links_and_folders_go_through_the_platform() => Sta.Run(() =>
    {
        using var env = new TestEnv();
        var view = new AboutView(env.Host.Context(), env.Platform);
        var host = Render.Host(view, "aurora", 860);
        Render.Layout(host);
        var buttons = Render.Descendants<System.Windows.Controls.Button>(host).ToList();
        foreach (var b in buttons) Click(b);
        Assert.Contains("OpenUrl https://github.com/NemeWT10/BetterVitals", env.Platform.Calls);
        Assert.Contains(env.Platform.Calls, c => c.StartsWith("OpenUrl https://github.com/NemeWT10/BetterVitals/issues", StringComparison.Ordinal));
        Assert.Equal(2, env.Platform.Calls.Count(c => c.StartsWith("OpenFolder ", StringComparison.Ordinal)));
    });

    [Fact]
    public void Hidden_sections_hold_no_subscriptions() => Sta.Run(() =>
    {
        using var env = new TestEnv();
        var p = env.Platform;
        var hub = new StatusHub();
        var ctx = env.Host.Context(hub);
        SectionView[] views = [new StartupSection(ctx, p), new UpdatesSection(ctx, p), new PermissionsSection(ctx, p, hub)];
        Assert.Equal(0, p.StateChangedSubscribers);
        foreach (var v in views) v.AttachForTests();
        Assert.Equal(3, p.StateChangedSubscribers);
        foreach (var v in views) v.DetachForTests();
        Assert.Equal(0, p.StateChangedSubscribers);
    });
}
