using System.Windows.Threading;
using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Fake;
using BetterVitals.Tests.Ui;
using BetterVitals.Tests.Ui.Pages;
using BetterVitals.UI.Flyout;
using BetterVitals.UI.Pages.Overview;
using Xunit;

namespace BetterVitals.Tests.Robustness;

/// <summary>PROJEKT §5 / §12 on the real WPF pages (headless: STA threads, never-shown windows): a laptop dGPU that sleeps, vanishes and returns.</summary>
[Collection(UiCollection.Name)]
public sealed class UiDeviceScenarios
{
    private const string Gpu = "NVIDIA GeForce RTX 4060 Laptop GPU";

    [Fact]
    public void The_overview_and_the_flyout_follow_a_dGPU_that_sleeps_vanishes_and_returns() => Sta.Run(() =>
    {
        using var env = new PageEnv();
        using var pc = new FakePc(FakeProfile.LaptopIntel);
        pc.Run(TimeSpan.FromSeconds(5));
        var page = new OverviewPage(pc.Engine, env.Actions);
        page.OnNavigatedTo(null);
        var vm = new FlyoutViewModel(pc.Engine);
        vm.Attach(Dispatcher.CurrentDispatcher);
        var gpuTile = vm.Tiles.Single(t => t.Sensor.HardwareKind == HardwareKind.Gpu && t.Sensor.Kind == SensorKind.Temperature);
        var cpuTile = vm.Tiles.First(t => t.Sensor.HardwareKind == HardwareKind.Cpu);
        var gpuHw = pc.Hardware(Gpu).Id;

        void Tick(int seconds)
        {
            pc.Run(TimeSpan.FromSeconds(seconds));
            Sta.DoEvents();
            page.Apply();
        }

        // awake
        Tick(3);
        Assert.All(page.Cards, c => Assert.Equal(CardState.Live, c.State));
        Assert.True(Sta.PumpUntil(() => gpuTile.Value != "—"));

        // asleep: dashes in the flyout, a badge on the card, the others untouched
        pc.Fake.SetAsleep(gpuHw, true);
        Tick(3);
        Assert.True(Sta.PumpUntil(() => gpuTile.Value == "—"), "the flyout tile shows a dash");
        var card = page.Cards.Single(c => c.Group.Name == Gpu);
        Assert.Equal(CardState.Asleep, card.State);
        Assert.Equal("—", card.Tiles[0].ValueText);
        Assert.Equal(CardState.Live, page.Cards.Single(c => c.Group.Kind == HardwareKind.Cpu).State);
        Assert.NotEqual("—", cpuTile.Value);

        // unplugged while asleep: its card is gone, everything else carries on
        pc.Fake.SetPresent(gpuHw, false);
        pc.Rediscover();
        Assert.True(Sta.PumpUntil(() => page.Cards.All(c => c.Group.Name != Gpu)), "the card disappears");
        Tick(3);
        Assert.NotEqual("—", cpuTile.Value);
        Assert.Equal("—", gpuTile.Value);

        // back, and awake
        pc.Fake.SetPresent(gpuHw, true);
        pc.Fake.SetAsleep(gpuHw, false);
        pc.Rediscover();
        Assert.True(Sta.PumpUntil(() => page.Cards.Any(c => c.Group.Name == Gpu)), "the card returns");
        Tick(4);
        Assert.Equal(CardState.Live, page.Cards.Single(c => c.Group.Name == Gpu).State);
        Assert.True(Sta.PumpUntil(() => gpuTile.Value != "—"), "the flyout shows the temperature again");

        // and a USB-style flap, fifty times, with the pages open
        for (var i = 0; i < 50; i++)
        {
            pc.Fake.SetPresent(gpuHw, i % 2 == 1);
            pc.Rediscover();
            Sta.DoEvents();
            pc.Run(TimeSpan.FromSeconds(1));
            Sta.DoEvents();
        }
        pc.Fake.SetPresent(gpuHw, true);
        pc.Rediscover();
        Tick(3);
        Assert.True(Sta.PumpUntil(() => page.Cards.Any(c => c.Group.Name == Gpu)));
        Assert.True(Sta.PumpUntil(() => gpuTile.Value != "—"));

        vm.Detach();
        page.OnNavigatedFrom();
    });
}
