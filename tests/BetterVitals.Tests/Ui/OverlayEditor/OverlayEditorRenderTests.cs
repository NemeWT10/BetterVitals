using System.Windows;
using System.Windows.Controls;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.Overlay;
using BetterVitals.Services;
using BetterVitals.UI.Pages.OverlayEditor;
using Xunit;

namespace BetterVitals.Tests.Ui.OverlayEditor;

/// <summary>
/// The page rendered offscreen (never shown) with fake sensors, in several states, themes and languages; the PNGs in
/// tests/output/overlay-editor are reviewed by eye. Also checks the page's lifecycle costs (demand, timer, renders).
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OverlayEditorRenderTests
{
    // the panel's page area at its default size (1120 × 740 window minus the 236 px nav rail and the title bar)
    private const double PageWidth = 884;
    private const double PageHeight = 700;

    private static void Settle(OverlayEditorPage page, FrameworkElement host)
    {
        Offscreen.Layout(host);
        page.Preview.RequestRender();
        Assert.True(Sta.PumpUntil(() => !page.Preview.IsRendering && (page.Model.Working.Items.Count == 0 || page.Preview.RenderCount > 0), 15000), "preview render");
        Offscreen.Layout(host);
        Sta.PumpUntil(() => FontSamples.Pending == 0, 15000);
        Sta.Pump(60);
        Offscreen.Layout(host);
    }

    private static string Shot(EditorFixture f, OverlayEditorPage page, string name, double height = PageHeight, Action<OverlayEditorPage>? before = null)
    {
        var host = Offscreen.Host(page, PageWidth, height);
        page.OnNavigatedTo(null);
        before?.Invoke(page);
        f.Advance(1);
        page.TickNow();
        Settle(page, host);
        var path = Offscreen.Save(host, name);
        page.OnNavigatedFrom();
        host.Child = null;
        return path;
    }

    private static void SixItems(EditorFixture f, OverlayEditorModel m)
    {
        foreach (var key in new[] { "fps.fps", "cpu.temp", "gpu.temp", "gpu.hotspot", "cpu.clocks", "ram.used" }) m.Toggle(f.F(key));
    }

    [Fact]
    public void Simple_mode_with_defaults() => Sta.Run(() =>
    {
        using var f = new EditorFixture("en");
        var page = f.Page();
        var path = Shot(f, page, "simple_empty_aurora_en");
        Assert.True(new FileInfo(path).Length > 20_000);
        Assert.NotNull(page.Simple);
        Assert.Empty(page.Model.Working.Items);
        page.Dispose();
    });

    [Theory]
    [InlineData("en", "aurora")]
    [InlineData("pl", "aurora")]
    [InlineData("en", "daylight")]
    [InlineData("pl", "sunset")]
    public void Simple_mode_with_six_items(string lang, string theme) => Sta.Run(() =>
    {
        using var f = new EditorFixture(lang, theme);
        var page = f.Page();
        SixItems(f, page.Model);
        page.Model.SetCorner(OverlayAnchor.TopRight);
        Shot(f, page, $"simple_six_{theme}_{lang}", 1500);
        Assert.Equal(6, page.Model.Working.Items.Count);
        Assert.Equal(6, page.Order.ListCount);
        Assert.True(page.Preview.RenderedSize.Width > 50);
        page.Dispose();
    });

    [Fact]
    public void Simple_mode_custom_look_banner_and_gpu_tiles() => Sta.Run(() =>
    {
        using var f = new EditorFixture("pl");
        var page = f.Page();
        SixItems(f, page.Model);
        page.Model.Edit(l => l.Style.FontFamily = "Orbitron");
        Shot(f, page, "simple_custom_aurora_pl", before: p => p.Preview.SelectResolution("4K"));
        Assert.True(OverlayEditorModel.HasCustomizations(page.Model.Working));
        page.Dispose();
    });

    [Theory]
    [InlineData("en", "aurora")]
    [InlineData("pl", "daylight")]
    [InlineData("en", "graphite")]
    public void Advanced_mode_every_section(string lang, string theme) => Sta.Run(() =>
    {
        using var f = new EditorFixture(lang, theme);
        var page = f.Page();
        SixItems(f, page.Model);
        page.Model.SetAdvanced(true);
        var cores = page.Model.Working.Items.First(i => i.SensorIds.Count > 1);
        page.Model.EditItem(cores.Id, i => i.Thresholds = [new() { Value = 5000, Color = "#FBBF24" }, new() { Value = 5300, Color = "#F43F5E" }]);
        page.Model.Edit(l => l.Placement.Anchor = OverlayAnchor.TopRight);
        // open the item editor of the core clocks
        typeof(AdvancedView).GetField("_expanded", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, cores.Id);
        page.Model.Reload();
        Shot(f, page, $"advanced_all_{theme}_{lang}", 4600);
        Assert.NotNull(page.Advanced);
        Assert.Null(page.Simple);
        page.Dispose();
    });

    [Fact]
    public void Advanced_mode_top_as_the_panel_shows_it() => Sta.Run(() =>
    {
        using var f = new EditorFixture("en");
        var page = f.Page();
        SixItems(f, page.Model);
        page.Model.SetAdvanced(true);
        page.Model.Reload();
        Shot(f, page, "advanced_top_aurora_en");
        page.Dispose();
    });

    [Theory]
    [InlineData("aurora")]
    [InlineData("daylight")]
    public void Font_list_renders_each_font_in_itself(string theme) => Sta.Run(() =>
    {
        using var f = new EditorFixture("en", theme);
        var page = f.Page();
        var style = new StyleSection(page.Context);
        var card = BetterVitals.UI.Pages.OverlayEditor.Ui.Card(style);
        var host = Offscreen.Host(card, 460, 1100);
        host.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/BetterVitals;component/UI/Pages/OverlayEditor/OverlayEditorStyles.xaml", UriKind.Relative)));
        style.LoadSamples();
        Assert.True(Sta.PumpUntil(() => style.SamplesReady, 20000), "font samples");
        Offscreen.Save(host, "fontlist_" + theme);
        page.Dispose();
    });

    [Fact]
    public void Sensor_picker_sheet() => Sta.Run(() =>
    {
        using var f = new EditorFixture("pl");
        var page = f.Page();
        SixItems(f, page.Model);
        Shot(f, page, "picker_simple_aurora_pl", before: p => p.Picker.Open(false));
        page.Model.SetAdvanced(true);
        Shot(f, page, "picker_add_aurora_en", before: p =>
        {
            Loc.I.SetLanguage("en");
            p.Picker.Open(true);
        });
        page.Dispose();
    });

    [Fact]
    public void Resolution_switch_keeps_the_same_screen_share() => Sta.Run(() =>
    {
        using var f = new EditorFixture("en");
        var page = f.Page();
        SixItems(f, page.Model);
        var host = Offscreen.Host(page, PageWidth, PageHeight);
        page.OnNavigatedTo(null);
        var shares = new List<double>();
        foreach (var id in new[] { "1080p", "1440p", "4K", "5K" })
        {
            var before = page.Preview.RenderCount;
            page.Preview.SelectResolution(id);
            Assert.True(Sta.PumpUntil(() => page.Preview.RenderCount > before && !page.Preview.IsRendering, 15000), id);
            shares.Add((double)page.Preview.RenderedSize.Height / page.Preview.Resolution.Height);
        }
        // the overlay is rendered at each screen's real height and takes the same part of it (rounding aside)
        Assert.All(shares, s => Assert.InRange(s, shares[0] * 0.95, shares[0] * 1.05));
        page.OnNavigatedFrom();
        host.Child = null;
        page.Dispose();
    });

    [Fact]
    public void Page_costs_nothing_when_not_shown() => Sta.Run(() =>
    {
        using var f = new EditorFixture("en");
        var page = f.Page();
        SixItems(f, page.Model);
        var host = Offscreen.Host(page, PageWidth, PageHeight);
        Assert.DoesNotContain(f.Engine.Demands, d => d.Consumer == "overlay-editor");

        page.OnNavigatedTo(null);
        var demand = Assert.Single(f.Engine.Demands, d => d.Consumer == "overlay-editor");
        Assert.Equal(TimeSpan.FromSeconds(1), demand.Interval);
        Assert.True(demand.Sensors >= 6 + 7); // the overlay's sensors (8 cores) and the visible tiles
        Assert.True(Sta.PumpUntil(() => page.Preview.RenderCount > 0, 15000));

        page.OnNavigatedFrom();
        Assert.DoesNotContain(f.Engine.Demands, d => d.Consumer == "overlay-editor");
        var renders = page.Preview.RenderCount;
        page.Model.Toggle(f.F("gpu.load")); // a change from elsewhere while the page is hidden
        page.TickNow();
        Sta.Pump(300);
        Assert.Equal(renders, page.Preview.RenderCount);

        // shown again: the 1 Hz tick renders at most about once a second
        page.OnNavigatedTo(null);
        Assert.True(Sta.PumpUntil(() => page.Preview.RenderCount > renders, 15000));
        var start = page.Preview.RenderCount;
        Sta.Pump(2300);
        Assert.InRange(page.Preview.RenderCount - start, 1, 3);
        page.OnNavigatedFrom();
        host.Child = null;
        page.Dispose();
    });

    [Fact]
    public void Slider_edits_are_debounced_into_one_settings_change() => Sta.Run(() =>
    {
        using var f = new EditorFixture("en");
        var page = f.Page();
        var host = Offscreen.Host(page, PageWidth, PageHeight);
        page.OnNavigatedTo(null);
        var changes = 0;
        f.Store.Changed += s => changes += s == "overlay" ? 1 : 0;
        for (var v = 100; v <= 150; v += 5)
        {
            var x = v;
            page.Context.Defer(l => l.Style.ScalePercent = x);
        }
        Assert.Equal(150, page.Model.Working.Style.ScalePercent);
        Assert.Equal(100, f.Store.Current.Overlay.Style.ScalePercent);
        Assert.True(Sta.PumpUntil(() => changes > 0, 3000));
        Sta.Pump(500);
        Assert.Equal(1, changes);
        Assert.Equal(150, f.Store.Current.Overlay.Style.ScalePercent);
        page.OnNavigatedFrom();
        host.Child = null;
        page.Dispose();
    });

    [Fact]
    public void Mode_switch_from_the_page_rebuilds_and_loses_nothing() => Sta.Run(() =>
    {
        using var f = new EditorFixture("en");
        var page = f.Page();
        SixItems(f, page.Model);
        var host = Offscreen.Host(page, PageWidth, PageHeight);
        page.OnNavigatedTo(null);
        var before = LayoutJson.Text(f.Store.Current.Overlay);
        page.Model.SetAdvanced(true);
        page.Model.Reload();
        Assert.NotNull(page.Advanced);
        page.Model.SetAdvanced(false);
        page.Model.Reload();
        Assert.NotNull(page.Simple);
        Assert.Equal(before, LayoutJson.Text(f.Store.Current.Overlay));
        Assert.DoesNotContain(Loc.MissingKeys, k => k.StartsWith("ovl.", StringComparison.Ordinal));
        page.OnNavigatedFrom();
        host.Child = null;
        page.Dispose();
    });

    /// <summary>Two monitors and an on-screen edit that the test ends (the real overlay is never involved).</summary>
    private sealed class FakeOverlay : IOverlayService
    {
        public IReadOnlyList<OverlayMonitor> Monitors { get; } =
        [
            new(@"\?\DISPLAY#GSM5BBF#1", @"\.\DISPLAY1", "LG ULTRAGEAR", new PixelRect(0, 0, 2560, 1440), new PixelRect(0, 0, 2560, 1392), true),
            new(@"\?\DISPLAY#DEL4321#2", @"\.\DISPLAY2", "DELL U2723QE", new PixelRect(2560, 0, 6400, 2160), new PixelRect(2560, 0, 6400, 2112), false),
        ];

        private Action<OverlayPlacement>? _commit;
        private Action? _cancel;

        public bool IsEditingOnScreen { get; private set; }

        public void BeginOnScreenEdit(Action<OverlayPlacement> commit, Action cancel)
        {
            IsEditingOnScreen = true;
            _commit = commit;
            _cancel = cancel;
        }

        public void EndOnScreenEdit(bool commit)
        {
            IsEditingOnScreen = false;
            if (commit) _commit?.Invoke(new OverlayPlacement { Anchor = OverlayAnchor.BottomRight, OffsetX = 10, OffsetY = 20, Margin = 24 });
            else _cancel?.Invoke();
        }

        public event Action? MonitorsChanged { add { } remove { } }
    }

    [Fact]
    public void On_screen_edit_and_two_monitors() => Sta.Run(() =>
    {
        var before = AppServices.Overlay;
        var fake = new FakeOverlay();
        AppServices.Overlay = fake;
        try
        {
            using var f = new EditorFixture("en");
            var page = f.Page();
            SixItems(f, page.Model);
            // simple: a monitor choice appears and the preview offers "My monitor" (the main one, 2560 × 1440)
            Shot(f, page, "simple_monitors_aurora_en", 1500, p => Assert.Equal(1440, p.Preview.Resolution.Height));

            page.Model.SetAdvanced(true);
            page.Model.Reload();
            Shot(f, page, "advanced_onscreen_aurora_en", 4600, p =>
            {
                typeof(AdvancedView).GetMethod("BeginOnScreen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(p.Advanced, null);
                Assert.True(fake.IsEditingOnScreen);
            });
            fake.EndOnScreenEdit(true);
            Assert.Equal(OverlayAnchor.BottomRight, f.Store.Current.Overlay.Placement.Anchor);
            Assert.Equal(20, f.Store.Current.Overlay.Placement.OffsetY);
            page.Dispose();
        }
        finally
        {
            AppServices.Overlay = before;
        }
    });
}
