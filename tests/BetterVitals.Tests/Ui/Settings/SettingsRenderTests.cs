using System.Windows;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.Platform;
using BetterVitals.Services;
using BetterVitals.UI.About;
using BetterVitals.UI.Panel;
using Xunit;

namespace BetterVitals.Tests.Ui.Settings;

/// <summary>
/// Offscreen PNGs of every section in PL / EN and two themes (dark Aurora, light Daylight) for visual review, plus the
/// automatic check that no single-line text is cut off. Output: tests\output\ui-settings.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SettingsRenderTests
{
    public static TheoryData<string, string> Variants => new()
    {
        { "aurora", "en" },
        { "aurora", "pl" },
        { "daylight", "en" },
        { "daylight", "pl" },
    };

    private const double Width = 860;

    [Theory]
    [MemberData(nameof(Variants))]
    public void Sections_render_without_clipped_text(string palette, string language) => Sta.Run(() =>
    {
        using var env = new TestEnv(language: language);
        env.Store.Mutate("recording", s => s.Recording.SensorIds.UnionWith(Enumerable.Range(0, 42).Select(i => "s" + i)));
        var p = env.Platform;
        p.Security = new SecurityInfo(true, true, null);
        p.UpdateState = UpdateUiState.Available;
        p.AvailableRelease = FakePlatform.Release();
        var hub = new StatusHub();
        hub.Statuses.Add(("lhm", new SourceStatus(SourceState.Limited, "source.lhm.noPawnIo")));
        hub.Statuses.Add(("native", new SourceStatus(SourceState.Ok)));
        hub.Statuses.Add(("fps", new SourceStatus(SourceState.Unavailable, "source.fps.noAccess")));
        hub.Statuses.Add(("derived", new SourceStatus(SourceState.Ok)));
        var ctx = env.Host.Context(hub, new FakeHistory());

        var sections = new (string Name, FrameworkElement View)[]
        {
            ("recording", new RecordingSection(ctx)),
            ("overlay", new OverlayBehaviorSection(ctx)),
            ("startup", new StartupSection(ctx, p)),
            ("updates", new UpdatesSection(ctx, p)),
            ("permissions", new PermissionsSection(ctx, p, hub)),
            ("about", new AboutView(ctx, p)),
        };
        var problems = new List<string>();
        foreach (var (name, view) in sections)
        {
            var host = Render.Host(view, palette, Width);
            Render.Save(host, $"section_{name}_{palette}_{language}");
            problems.AddRange(Render.Clipped(host).Select(c => $"{name}: {c}"));
        }
        Assert.DoesNotContain(Loc.MissingKeys, k => k.StartsWith("settings.", StringComparison.Ordinal) || k.StartsWith("about.", StringComparison.Ordinal) || k.StartsWith("source.", StringComparison.Ordinal));
        Assert.Empty(problems);
    });

    [Theory]
    [MemberData(nameof(Variants))]
    public void Section_states_render(string palette, string language) => Sta.Run(() =>
    {
        using var env = new TestEnv(language: language);
        var p = env.Platform;
        var ctx = env.Host.Context();

        // permissions: full mode active, PawnIO current, a pause in progress
        p.IsElevated = true;
        p.IsInstalled = true;
        p.PawnIoVersion = new Version(2, 2, 0);
        p.AntiCheatPauseReason = "vgc.exe";
        env.Store.Mutate("driver", s => s.Driver.PauseForAntiCheat = true);
        var hub = new StatusHub();
        hub.Statuses.Add(("lhm", new SourceStatus(SourceState.Paused, "source.lhm.paused")));
        hub.Statuses.Add(("fps", new SourceStatus(SourceState.Ok)));
        var perm = new PermissionsSection(ctx, p, hub);
        var host = Render.Host(perm, palette, Width);
        Render.Save(host, $"state_permissions-full_{palette}_{language}");
        Assert.Empty(Render.Clipped(host));

        // updates: downloading, then ready
        p.UpdateState = UpdateUiState.Downloading;
        p.AvailableRelease = FakePlatform.Release();
        p.UpdateProgress = 0.42;
        var upd = new UpdatesSection(ctx, p);
        host = Render.Host(upd, palette, Width);
        Render.Save(host, $"state_updates-downloading_{palette}_{language}");
        Assert.Empty(Render.Clipped(host));
        p.UpdateState = UpdateUiState.Ready;
        upd.AttachForTests();
        p.RaiseChanged();
        Render.Save(host, $"state_updates-ready_{palette}_{language}");
        upd.DetachForTests();

        // recording off ("live only") with the clear confirmation open; startup with the tray hint
        env.Store.Mutate("recording", s => s.Recording.Enabled = false);
        var rec = new RecordingSection(ctx, new FakeHistory());
        rec.ClearButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        host = Render.Host(rec, palette, Width);
        Render.Save(host, $"state_recording-off-confirm_{palette}_{language}");
        Assert.Empty(Render.Clipped(host));

        var start = new StartupSection(ctx, p);
        var startHost = Render.Host(start, palette, Width);
        Render.Layout(startHost);
        var show = Render.Descendants<System.Windows.Controls.Button>(startHost).First();
        show.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.True(start.HintVisible);
        Render.Save(startHost, $"state_startup-hint_{palette}_{language}");
        Assert.Empty(Render.Clipped(startHost));

        // about with a license opened
        var about = new AboutView(ctx, p);
        var aboutHost = Render.Host(about, palette, Width);
        Render.Layout(aboutHost);
        var licenseToggle = Render.Descendants<System.Windows.Controls.Primitives.ToggleButton>(aboutHost).First();
        licenseToggle.IsChecked = true;
        Render.Save(aboutHost, $"state_about-license_{palette}_{language}");
    });

    [Theory]
    [MemberData(nameof(Variants))]
    public void Whole_settings_page_renders(string palette, string language) => Sta.Run(() =>
    {
        using var env = new TestEnv(language: language);
        var ctx = env.Host.Context(new StatusHub(), new FakeHistory());
        var page = new SettingsPage(ctx);
        var host = Render.Host(page, palette, 900, layer: false);
        Render.Save(host, $"page_settings_{palette}_{language}");
        foreach (var s in SettingsSections.All) Assert.True(Loc.Has(s.TitleKey), s.TitleKey);
    });
}
