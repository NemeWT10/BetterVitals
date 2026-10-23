using System.IO;
using Xunit;
using System.Windows;
using System.Windows.Media;
using BetterVitals.Core.Settings;
using BetterVitals.Core.Sensors;
using BetterVitals.Localization;
using BetterVitals.Services;
using BetterVitals.UI;
using BetterVitals.UI.Dev;
using BetterVitals.UI.Flyout;
using BetterVitals.UI.Panel;
using BetterVitals.UI.Tray;

namespace BetterVitals.Tests.Ui;

[Collection(UiCollection.Name)]
public sealed class ShellTests
{
    [Fact]
    public void Shell_registers_the_five_pages_in_order()
    {
        var ids = PageRegistry.All.Where(p => p.InNav).Select(p => p.Id).ToList();
        Assert.Equal([PageIds.Overview, PageIds.History, PageIds.Sensors, PageIds.Overlay, PageIds.Settings], ids.Take(5));
        foreach (var p in PageRegistry.All) Assert.True(Loc.Has(p.TitleKey), p.TitleKey);
    }

    [Fact]
    public void A_real_page_replaces_the_placeholder_and_a_placeholder_never_replaces_a_page()
    {
        const string id = "test-page";
        Func<PageContext, FrameworkElement> a = _ => new FrameworkElement();
        Func<PageContext, FrameworkElement> b = _ => new FrameworkElement();
        PageRegistry.Register(new PageInfo(id, "nav.overview", "Icon.Chart", 500, a) { Priority = 0, InNav = false });
        PageRegistry.Register(new PageInfo(id, "nav.overview", "Icon.Chart", 500, b) { InNav = false });
        Assert.Same(b, PageRegistry.Find(id)!.Create);
        PageRegistry.Register(new PageInfo(id, "nav.overview", "Icon.Chart", 500, a) { Priority = 0, InNav = false });
        Assert.Same(b, PageRegistry.Find(id)!.Create);
    }

    [Fact]
    public void Settings_sections_are_ordered_and_pluggable()
    {
        SettingsSections.Register(new SettingsSection("test-section", "settings.general", 500, _ => new FrameworkElement()));
        var ids = SettingsSections.All.Select(s => s.Id).ToList();
        Assert.Equal("general", ids[0]);
        Assert.Equal(SettingsPageParameter.About, ids[^1]);
        Assert.Contains("test-section", ids);
    }

    [Fact]
    public void Startup_options()
    {
        var o = StartupOptions.Parse(["--snapshots", "out", "pl", "neon", "--data-dir", "d"]);
        Assert.True(o.TestMode);
        Assert.Equal("out", o.SnapshotDir);
        Assert.Equal("pl", o.SnapshotLanguage);
        Assert.Equal("neon", o.SnapshotTheme);
        Assert.Equal("d", o.DataDir);
        var all = StartupOptions.Parse(["--snapshots", "out", "all", "all"]);
        Assert.Null(all.SnapshotLanguage);
        Assert.Null(all.SnapshotTheme);
        Assert.False(StartupOptions.Parse([]).TestMode);
        Assert.Equal("history", StartupOptions.Parse(["--open", "history"]).OpenPage);
        Assert.True(StartupOptions.Parse(["--tray-probe"]).TestMode);
    }

    [Fact]
    public void Motion_follows_the_setting() => Sta.Run(() =>
    {
        Motion.I.Update(false);
        Assert.False(Motion.I.Enabled);
        Motion.I.Update(true);
        Assert.Equal(SystemParameters.ClientAreaAnimation, Motion.I.Enabled);
    });

    [Fact]
    public void Preview_hub_ticks_fill_frames_and_stats()
    {
        using var hub = new PreviewHub(start: false);
        Assert.InRange(hub.Sensors.Count, 18, 30);
        Assert.All(hub.Sensors, s => Assert.Equal(s, hub.Sensors[s.Index]));
        Assert.Equal(hub.Sensors.Count, hub.Sensors.Select(s => s.Id).Distinct().Count());
        hub.Warm(5);
        Assert.Equal(5, hub.Latest.Sequence);
        for (var i = 0; i < hub.Sensors.Count; i++)
        {
            Assert.True(hub.Latest.IsFresh(i));
            Assert.False(float.IsNaN(hub.Latest[i]));
            Assert.Equal(5, hub.Stats.Count(i));
        }
        Assert.Equal(5, hub.Recent(0, 48).Length);
        Assert.NotNull(hub.Find(hub.Sensors[3].Id));
    }

    [Fact]
    public void Key_sensors_pick_cpu_and_gpu_temperatures_and_loads()
    {
        using var hub = new PreviewHub(start: false);
        var keys = SensorPresentation.KeySensors(hub, []);
        Assert.Equal(["Core (Tctl/Tdie)", "GPU Core", "CPU Total", "GPU Core"], keys.Select(k => k.Name));
        Assert.Equal([SensorKind.Temperature, SensorKind.Temperature, SensorKind.Load, SensorKind.Load], keys.Select(k => k.Kind));
        var fps = hub.Sensors.First(s => s.Kind == SensorKind.Fps);
        Assert.Same(fps, SensorPresentation.KeySensors(hub, [fps.Id, "missing/sensor"])[0]);
        Assert.Empty(SensorPresentation.KeySensors(NullSensorHub.Instance, []));
    }

    [Fact]
    public void Heat_thresholds_follow_the_project_defaults()
    {
        var cpu = new SensorDescriptor { Id = "c", HardwareId = "h", Name = "Core (Tctl/Tdie)", Kind = SensorKind.Temperature, HardwareKind = HardwareKind.Cpu };
        var hot = new SensorDescriptor { Id = "g", HardwareId = "h", Name = "GPU Hot Spot", Kind = SensorKind.Temperature, HardwareKind = HardwareKind.Gpu };
        var load = new SensorDescriptor { Id = "l", HardwareId = "h", Name = "CPU Total", Kind = SensorKind.Load, HardwareKind = HardwareKind.Cpu };
        Assert.Equal(HeatLevel.Normal, SensorPresentation.Heat(cpu, 79.9f));
        Assert.Equal(HeatLevel.Warm, SensorPresentation.Heat(cpu, 80));
        Assert.Equal(HeatLevel.Hot, SensorPresentation.Heat(cpu, 90));
        Assert.Equal(HeatLevel.Warm, SensorPresentation.Heat(hot, 95));
        Assert.Equal(HeatLevel.Normal, SensorPresentation.Heat(load, 100));
        Assert.Equal(HeatLevel.Normal, SensorPresentation.Heat(cpu, float.NaN));
    }

    [Fact]
    public void Flyout_model_shows_four_tiles_and_drives_the_overlay_setting() => Sta.Run(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "bv-ui-tests-" + Guid.NewGuid().ToString("N"));
        var store = SettingsStore.ForTests(Path.Combine(dir, "settings.json"), new AppSettings());
        var before = Loc.I.Language;
        try
        {
            Loc.I.SetLanguage("en");
            store.Mutate("recording", s =>
            {
                s.Recording.Enabled = true;
                s.Recording.SensorIds.UnionWith(["a", "b", "c"]);
            });
            using var hub = new PreviewHub(start: false);
            hub.Warm(10);
            var vm = new FlyoutViewModel(hub);
            Assert.Equal(4, vm.Tiles.Count);
            Assert.Equal("Recording: 3 sensors", vm.Footer);
            Assert.All(vm.Tiles, t => Assert.NotEqual("—", t.Value));
            Assert.Equal("CPU · Temperature", vm.Tiles[0].Label);
            Assert.Equal("Core (Tctl/Tdie)", vm.Tiles[0].Name);

            vm.Prefill(s => hub.Recent(s.Index, 48));
            Assert.Equal(10, vm.Tiles[0].Series.Count);

            Assert.False(vm.OverlayEnabled);
            vm.OverlayEnabled = true;
            Assert.True(store.Current.Overlay.Enabled);

            store.Mutate("recording", s => s.Recording.Enabled = false);
            vm.Attach(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            Assert.Equal(Loc.T("flyout.liveOnly"), vm.Footer);
            vm.Detach();
        }
        finally
        {
            Loc.I.SetLanguage(before);
            store.Flush();
            try { Directory.Delete(dir, true); } catch { /* temp */ }
        }
    });

    [Fact]
    public async Task Single_instance_hands_the_command_to_the_first_one()
    {
        var key = "BetterVitals-test-" + Guid.NewGuid().ToString("N");
        using var first = new SingleInstance(key);
        Assert.True(first.IsPrimary);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Listen(c => received.TrySetResult(c));
        var sent = await Task.Run(() =>
        {
            using var other = new SingleInstance(key);
            Assert.False(other.IsPrimary);
            return other.SendToPrimary("open:history");
        }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(sent);
        Assert.Equal("open:history", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void The_instance_pipe_is_open_to_this_user_elevated_or_not_and_nobody_else()
    {
        var sddl = SingleInstance.PipeAccess().GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.All);
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        Assert.Contains($";;;{user})", sddl, StringComparison.Ordinal); // by SID, not the token owner (Administrators when elevated)
        Assert.StartsWith("D:P", sddl[sddl.IndexOf("D:", StringComparison.Ordinal)..], StringComparison.Ordinal); // nothing inherited
        Assert.DoesNotContain(";;;WD)", sddl, StringComparison.Ordinal); // Everyone
        Assert.DoesNotContain(";;;AU)", sddl, StringComparison.Ordinal); // Authenticated Users
        Assert.DoesNotContain(";;;BU)", sddl, StringComparison.Ordinal); // Users
    }

    [Fact]
    public void Icons_sit_on_the_24_grid_and_the_tray_icon_draws() => Sta.Run(() =>
    {
        var icons = (ResourceDictionary)Application.LoadComponent(new Uri("/BetterVitals;component/UI/Themes/Icons.xaml", UriKind.Relative));
        string[] required =
        [
            "Cpu", "Gpu", "Ram", "Motherboard", "Disk", "Network", "Fan", "Battery", "Psu", "Cooler", "Game", "Thermometer", "Bolt",
            "Gauge", "Clock", "Wave", "Chart", "History", "Layers", "Eye", "EyeOff", "Star", "Record", "Search", "Filter", "Settings",
            "Info", "Power", "Pin", "Monitor", "Overlay", "Anchor.TopLeft", "Anchor.TopRight", "Anchor.BottomLeft", "Anchor.BottomRight",
            "Export", "Trash", "Check", "ChevronUp", "ChevronDown", "ChevronLeft", "ChevronRight", "Plus", "Minus", "Refresh", "Shield", "Update",
        ];
        foreach (var name in required) Assert.True(icons.Contains("Icon." + name), "Icon." + name);
        foreach (var key in icons.Keys.OfType<string>().Where(k => k.StartsWith("Icon.", StringComparison.Ordinal)))
        {
            var b = ((Geometry)icons[key]).Bounds;
            Assert.True(b.Left >= 0.5 && b.Top >= 0.5 && b.Right <= 23.5 && b.Bottom <= 23.5, $"{key} leaves the grid: {b}");
        }
        foreach (var size in new[] { 16, 20, 24, 32 })
        {
            var icon = TrayIconArt.Create(size);
            Assert.NotEqual(0, icon);
            global::BetterVitals.UI.Native.DestroyIcon(icon);
        }
    });
}
