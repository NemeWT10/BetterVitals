using Xunit;
using BetterVitals.Core.Catalog;
using BetterVitals.Core.Sensors;
using BetterVitals.Localization;
using BetterVitals.Sensors.Fake;
using BetterVitals.UI.Pages.Overview;
using BetterVitals.UI.Pages.Shared;

namespace BetterVitals.Tests.Ui.Pages;

[Collection(UiCollection.Name)]
public sealed class OverviewPageTests
{
    private static Dictionary<string, IReadOnlyList<KeyValue>> Keys(FakePc pc)
    {
        var items = FriendlyCatalog.Resolve(pc.Engine.Hardware, pc.Engine.Sensors);
        return HardwareTree.Build(pc.Engine).ToDictionary(g => g.Name, g => KeyValuePicker.Pick(g, items));
    }

    [Fact]
    public void Key_values_follow_the_friendly_catalog_per_family()
    {
        using var pc = new FakePc();
        var keys = Keys(pc);
        Assert.Equal(["Core (Tctl/Tdie)", "CPU Total", "CPU Clock Max", "Package"], keys["AMD Ryzen 7 9800X3D"].Select(k => k.Sensor.Name));
        Assert.Equal("friendly.cpu.temp", keys["AMD Ryzen 7 9800X3D"][0].FriendlyKey);
        Assert.Equal(["GPU Core", "GPU Hot Spot", "GPU Core", "GPU Memory Used"], keys["NVIDIA GeForce RTX 5080"].Select(k => k.Sensor.Name));
        Assert.Equal(["Memory Used", "Memory", "DIMM Max"], keys["Generic Memory"].Select(k => k.Sensor.Name));
        Assert.Equal(["VRM MOS", "Chipset", "CPU Fan"], keys["Gigabyte X870E AORUS ELITE WIFI7"].Select(k => k.Sensor.Name));
        Assert.Equal(["Composite Temperature", "Read Rate", "Write Rate"], keys["KINGSTON SKC3000D2048G"].Select(k => k.Sensor.Name));
        Assert.Null(keys["KINGSTON SKC3000D2048G"][0].FriendlyKey); // "Drive temperatures" is plural: titled by kind
        Assert.Equal(["Download Speed", "Upload Speed"], keys["Ethernet"].Select(k => k.Sensor.Name));
        Assert.Equal(["Framerate", "1% Low", "Frametime"], keys["Foreground app"].Select(k => k.Sensor.Name));
    }

    [Fact]
    public void Hardware_without_catalog_items_gets_values_by_kind()
    {
        using var pc = new FakePc();
        var keys = Keys(pc);
        Assert.Equal(["GPU Core", "D3D 3D", "D3D Shared Memory Used"], keys["AMD Radeon(TM) Graphics"].Select(k => k.Sensor.Name));
        Assert.All(keys["AMD Radeon(TM) Graphics"], k => Assert.Null(k.FriendlyKey));
        Assert.Equal(["Temperature", "Total Activity"], keys["ST4000DM004-2CV104"].Select(k => k.Sensor.Name));
        Assert.All(keys.Values, v => Assert.InRange(v.Count, 2, KeyValuePicker.Max));
    }

    [Fact]
    public void Laptop_battery_and_intel_cpu_get_sensible_values()
    {
        using var pc = new FakePc(FakeProfile.LaptopIntel);
        var keys = Keys(pc);
        Assert.Equal("Core Max", keys["Intel Core Ultra 7 155H"][0].Sensor.Name);
        Assert.Equal(["Charge Level", "Discharge Rate"], keys["Primary Battery"].Take(2).Select(k => k.Sensor.Name));
    }

    [Fact]
    public void Demand_asks_for_key_values_and_all_sensors_of_expanded_cards() => Sta.Run(() =>
    {
        using var env = new PageEnv();
        using var pc = new FakePc();
        pc.Run(TimeSpan.FromSeconds(5));
        var page = new OverviewPage(pc.Engine, env.Actions);
        var collapsed = page.DemandIds;
        var keyCount = page.Cards.Sum(c => c.Tiles.Count);
        Assert.Equal(keyCount, collapsed.Count);

        page.OnNavigatedTo(null);
        Assert.Contains(pc.Engine.Demands, d => d.Consumer == "overview" && d.Sensors == keyCount);
        var gpu = page.Cards.Single(c => c.Group.Name == "NVIDIA GeForce RTX 5080");
        page.Toggle(gpu);
        Assert.True(gpu.IsExpanded);
        Assert.Equal(keyCount - gpu.Tiles.Count + gpu.Group.Sensors.Count, page.DemandIds.Count);
        Assert.Contains(pc.Engine.Demands, d => d.Consumer == "overview" && d.Sensors == page.DemandIds.Count);
        Assert.Equal(gpu.Group.Sensors.Count, gpu.Rows.Count);

        page.OnNavigatedFrom();
        Assert.DoesNotContain(pc.Engine.Demands, d => d.Consumer == "overview");
        // expansion is remembered while the page lives (the panel is open)
        page.OnNavigatedTo(null);
        Assert.True(page.Cards.Single(c => c.Group.Name == "NVIDIA GeForce RTX 5080").IsExpanded);
        page.OnNavigatedFrom();
    });

    [Fact]
    public void A_sleeping_device_shows_asleep_and_the_idle_game_card_no_game() => Sta.Run(() =>
    {
        using var env = new PageEnv();
        using var pc = new FakePc();
        pc.Run(TimeSpan.FromSeconds(3));
        var page = new OverviewPage(pc.Engine, env.Actions);
        page.Apply();
        Assert.All(page.Cards, c => Assert.Equal(CardState.Live, c.State));

        pc.Fake.SetAsleep(pc.Hardware("NVIDIA GeForce RTX 5080").Id, true);
        pc.Fake.SetAsleep(pc.Hardware("Foreground app").Id, true);
        pc.Run(TimeSpan.FromSeconds(2));
        page.Apply();
        Assert.Equal(CardState.Asleep, page.Cards.Single(c => c.Group.Name == "NVIDIA GeForce RTX 5080").State);
        Assert.Equal(CardState.NoGame, page.Cards.Single(c => c.Group.Kind == HardwareKind.Game).State);
        Assert.Equal(CardState.Live, page.Cards.Single(c => c.Group.Kind == HardwareKind.Cpu).State);
        Assert.Equal("—", page.Cards.Single(c => c.Group.Name == "NVIDIA GeForce RTX 5080").Tiles[0].ValueText);
        Assert.Equal("overview.badge.asleep.gpu", HardwareCard.AsleepKey(HardwareKind.Gpu));
    });

    [Fact]
    public void Inventory_change_relayouts_and_keeps_expansion() => Sta.Run(() =>
    {
        using var env = new PageEnv();
        using var pc = new FakePc();
        pc.Run(TimeSpan.FromSeconds(2));
        var page = new OverviewPage(pc.Engine, env.Actions);
        page.OnNavigatedTo(null);
        page.Toggle(page.Cards.Single(c => c.Group.Kind == HardwareKind.Cpu));
        var hdd = pc.Hardware("ST4000DM004-2CV104");
        pc.Fake.SetPresent(hdd.Id, false);
        pc.Rediscover();
        Assert.True(Sta.PumpUntil(() => page.Cards.All(c => c.Group.Name != "ST4000DM004-2CV104")));
        Assert.True(page.Cards.Single(c => c.Group.Kind == HardwareKind.Cpu).IsExpanded);
        page.OnNavigatedFrom();
    });

    [Fact]
    public void Banner_appears_for_limited_or_unavailable_sources() => Sta.Run(() =>
    {
        using var env = new PageEnv();
        Assert.Null(OverviewPage.BannerFor([("lhm", new SourceStatus(SourceState.Ok)), ("fps", new SourceStatus(SourceState.Paused))]));
        Assert.Equal("Some sensors need full mode — Settings → Permissions.", OverviewPage.BannerFor([("lhm", new SourceStatus(SourceState.Limited, "missing.key"))]));
        Assert.Equal(Loc.T("overview.banner.limitedWith", Loc.T("overview.badge.limited")),
            OverviewPage.BannerFor([("fps", new SourceStatus(SourceState.Unavailable, "overview.badge.limited"))]));
    });

    [Theory]
    [InlineData("aurora", "pl")]
    [InlineData("daylight", "en")]
    [InlineData("graphite", "pl")]
    [InlineData("neon", "en")]
    [InlineData("sunset", "pl")]
    public void Overview_renders_with_the_desktop_engine(string theme, string language) => Sta.Run(() =>
    {
        using var env = new PageEnv(language: language);
        using var pc = new FakePc();
        pc.Run(TimeSpan.FromSeconds(50));
        var page = new OverviewPage(pc.Engine, env.Actions);
        using var host = new HiddenHost(page, 884, 1500, theme);
        page.OnNavigatedTo(null);
        // a minute of sparkline
        for (var i = 0; i < 40; i++)
        {
            pc.Run(TimeSpan.FromSeconds(1));
            page.Apply();
        }
        var path = host.Save($"overview_{theme}_{language}");
        Assert.True(new FileInfo(path).Length > 20_000);

        page.Toggle(page.Cards.Single(c => c.Group.Kind == HardwareKind.Gpu && c.Group.Name.Contains("RTX")));
        host.Settle(120);
        page.Apply();
        host.Root.Height = 2100;
        var expanded = host.Save($"overview_expanded_{theme}_{language}");
        Assert.True(new FileInfo(expanded).Length > 20_000);
        var gpu = page.Cards.Single(c => c.IsExpanded);
        Assert.All(gpu.Rows, r => Assert.True(r.IsShown));
        Assert.All(gpu.Rows, r => Assert.NotEqual("—", r.Max));
        page.OnNavigatedFrom();
        Assert.DoesNotContain(Loc.MissingKeys, k => k.StartsWith("overview.", StringComparison.Ordinal) || k.StartsWith("sensors.", StringComparison.Ordinal));
    });

    [Fact]
    public void Expanded_cards_build_and_refresh_the_rows_near_the_screen_and_fill_rows_as_they_scroll_in() => Sta.Run(() =>
    {
        using var env = new PageEnv();
        using var pc = new FakePc();
        pc.Run(TimeSpan.FromSeconds(30));
        var page = new OverviewPage(pc.Engine, env.Actions);
        using var host = new HiddenHost(page, 884, 640);
        page.OnNavigatedTo(null);
        foreach (var card in page.Cards.ToList()) page.Toggle(card);
        host.Settle(120);
        page.Apply();
        var scroll = (System.Windows.Controls.ScrollViewer)page.Content;
        var unit = BetterVitals.Core.Settings.SettingsStore.I?.Current.General.TemperatureUnit ?? BetterVitals.Core.Settings.TemperatureUnit.Celsius;

        // what a full refresh would show for a row now
        string[] Expected(SensorRowModel m)
        {
            var fresh = new SensorRowModel(m.Sensor, m.HardwareName, env.Actions);
            fresh.Refresh(pc.Engine.Latest, pc.Engine.Stats, unit);
            return [fresh.Now, fresh.Unit, fresh.Min, fresh.Max, fresh.Avg];
        }
        static string[] Shown(SensorRowModel m) => [m.Now, m.Unit, m.Min, m.Max, m.Avg];
        List<(SensorRowModel Model, double Top, double Bottom)> Rows()
        {
            host.Settle();
            return HiddenHost.Descendants<SensorRowView>(page)
                .Select(v => (v.Model!, v.TransformToAncestor(scroll).TransformBounds(new System.Windows.Rect(v.RenderSize))))
                .Select(x => (x.Item1, x.Item2.Top, x.Item2.Bottom)).ToList();
        }

        pc.Run(TimeSpan.FromSeconds(5));
        page.Apply();
        var rows = Rows();
        var height = scroll.ActualHeight;
        var onScreen = rows.Where(r => r.Bottom > 0 && r.Top < height).ToList();
        Assert.NotEmpty(onScreen);
        Assert.All(onScreen, r => Assert.Equal(Expected(r.Model), Shown(r.Model)));
        // rows far below the screen have no view yet (only a slot of their height)
        Assert.True(page.Cards.Sum(c => c.WaitingRows) > 20, "rows far below the screen were built");
        Assert.All(rows, r => Assert.True(r.Top < height * 2.5, "a row far below the screen was built"));
        var extent = scroll.ExtentHeight;

        // scrolling to the end fills what comes into view before it is drawn
        scroll.ScrollToEnd();
        rows = Rows();
        var nowOnScreen = rows.Where(r => r.Bottom > 0 && r.Top < height).ToList();
        Assert.NotEmpty(nowOnScreen);
        Assert.All(nowOnScreen, r => Assert.Equal(Expected(r.Model), Shown(r.Model)));
        // a slot is exactly as tall as its row: building rows never moves anything
        Assert.Equal(extent, scroll.ExtentHeight, 3);
        page.OnNavigatedFrom();
    });
}
