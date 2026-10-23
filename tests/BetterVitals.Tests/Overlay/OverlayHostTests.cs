using BetterVitals.Core.Overlay;
using BetterVitals.Overlay;
using BetterVitals.Overlay.Interop;
using Xunit;
using static BetterVitals.Tests.Overlay.OverlayFixtures;

namespace BetterVitals.Tests.Overlay;

/// <summary>
/// Window lifecycle with the real window, device, swap chain and composition tree — but the window is never shown
/// (<c>NeverShow</c>): nothing ever appears on the user's desktop.
/// </summary>
public class OverlayHostTests
{
    private static (OverlayHost Host, FakeHub Hub, OverlayLayout Layout) Start(bool enabled = true)
    {
        var hub = new FakeHub(FpsSensor, CpuTemp, GpuTemp, HotSpot);
        hub.Set(FpsSensor, 144);
        hub.Set(CpuTemp, 61);
        hub.Set(GpuTemp, 70);
        hub.Set(HotSpot, 92);
        var layout = new OverlayLayout
        {
            Enabled = enabled,
            Items = [Item(FpsSensor), Item(CpuTemp), Item(GpuTemp), Item(HotSpot)],
        };
        var host = new OverlayHost(new OverlayHostOptions { NeverShow = true });
        host.Start();
        host.Apply(layout, OverlayTheme.Default, Ctx(hub));
        return (host, hub, layout);
    }

    private static OverlayContext Ctx(FakeHub hub) => new() { Hub = hub, Animations = false };

    [Fact]
    public void Window_is_click_through_topmost_tool_window_excluded_from_capture_and_never_shown()
    {
        var (host, hub, _) = Start();
        using (host)
        {
            var d = host.Diagnostics();
            Assert.True(d.Shown);
            Assert.False(d.WindowVisible);
            Assert.True(d.SurfaceReady);
            const uint want = Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TRANSPARENT
                              | Win32.WS_EX_LAYERED | Win32.WS_EX_NOREDIRECTIONBITMAP;
            Assert.Equal(want, d.ExStyle & want);
            Assert.Equal(Win32.WDA_EXCLUDEFROMCAPTURE, d.DisplayAffinity);
            Assert.True(d.HasDemand);
            Assert.True(d.RefreshRunning);
            Assert.Equal(1, hub.ActiveDemands);
            Assert.Equal(4, hub.LastDemandIds.Count);
            Assert.Equal(d.SceneWidth, d.WindowRect.Width);
            Assert.Equal(d.SceneHeight, d.WindowRect.Height);
        }
    }

    [Fact]
    public void Placement_follows_anchor_on_primary_work_area()
    {
        var (host, hub, layout) = Start();
        using (host)
        {
            var monitors = OverlayHost.GetMonitors();
            var primary = monitors.First(m => m.IsPrimary);
            var d = host.Diagnostics();
            var scale = OverlayPlacementMath.Scale(layout.Style, primary.Bounds.Height);
            Assert.Equal(scale, d.Scale, 6);
            var (x, y) = OverlayPlacementMath.Position(layout.Placement, primary.WorkArea, d.SceneWidth, d.SceneHeight, scale);
            Assert.Equal(x, d.WindowRect.Left);
            Assert.Equal(y, d.WindowRect.Top);

            layout.Placement.Anchor = OverlayAnchor.BottomRight;
            host.Apply(layout, OverlayTheme.Default, Ctx(hub));
            d = host.Diagnostics();
            var right = primary.WorkArea.Right - (int)Math.Round(24 * scale);
            var bottom = primary.WorkArea.Bottom - (int)Math.Round(24 * scale);
            Assert.InRange(d.WindowRect.Right, right - 1, right + 1);
            Assert.InRange(d.WindowRect.Bottom, bottom - 1, bottom + 1);
        }
    }

    [Fact]
    public void Swap_chain_shows_the_same_picture_as_the_offscreen_render()
    {
        var (host, _, layout) = Start();
        using (host)
        {
            var back = host.ReadBack();
            Assert.NotNull(back);
            BetterVitals.Overlay.Rendering.Offscreen.WritePng(back!, Path.Combine(OutputDir, "swapchain-readback.png"));
            var primary = OverlayHost.GetMonitors().First(m => m.IsPrimary);
            var values = new PreviewSensor[] { new(FpsSensor, 144), new(CpuTemp, 61), new(GpuTemp, 70), new(HotSpot, 92) };
            var off = OverlayHost.RenderToBitmap(layout, OverlayTheme.Default, values, primary.Bounds.Height);
            Assert.Equal(off.Width, back!.Width);
            Assert.Equal(off.Height, back.Height);
            // Same coverage and colors; hardware and software antialiasing may differ by a few levels at edges.
            long diff = 0;
            for (var i = 0; i < off.Pixels.Length; i++) diff += Math.Abs(off.Pixels[i] - back.Pixels[i]);
            var mean = diff / (double)off.Pixels.Length;
            Assert.True(mean < 3, $"mean abs diff {mean:F2}");
        }
    }

    [Fact]
    public void Refresh_redraws_only_on_change()
    {
        var (host, hub, _) = Start();
        using (host)
        {
            host.RefreshNow();
            Assert.False(host.RefreshNow());
            hub.Set(CpuTemp, 62);
            Assert.True(host.RefreshNow());
            Assert.False(host.RefreshNow());
            hub.Set(CpuTemp, 62.2f); // same text at 0 decimals
            Assert.False(host.RefreshNow());
        }
    }

    [Fact]
    public void Device_loss_is_recovered()
    {
        var (host, hub, _) = Start();
        using (host)
        {
            var before = host.Diagnostics().DeviceCreations;
            Assert.True(host.SimulateDeviceLost());
            var d = host.Diagnostics();
            Assert.True(d.SurfaceReady);
            Assert.Equal(before + 1, d.DeviceCreations);
            hub.Set(FpsSensor, 120);
            Assert.True(host.RefreshNow());
        }
    }

    [Fact]
    public void Disabling_hides_and_releases_demand()
    {
        var (host, hub, layout) = Start();
        using (host)
        {
            layout.Enabled = false;
            host.Apply(layout, OverlayTheme.Default, Ctx(hub));
            var d = host.Diagnostics();
            Assert.False(d.Shown);
            Assert.False(d.HasDemand);
            Assert.False(d.RefreshRunning);
            Assert.Equal(0, hub.ActiveDemands);

            host.SetVisible(false);
            layout.Enabled = true;
            host.Apply(layout, OverlayTheme.Default, Ctx(hub));
            Assert.False(host.Diagnostics().Shown);
            host.SetVisible(true);
            Assert.True(host.Diagnostics().Shown);
        }
    }

    [Fact]
    public void Edit_mode_moves_with_keys_and_commits_placement()
    {
        var (host, hub, layout) = Start();
        using (host)
        {
            OverlayPlacement? committed = null;
            var cancelled = false;
            host.EnterEditMode(p => committed = p, () => cancelled = true);
            var d0 = host.Diagnostics();
            Assert.True(d0.Editing);
            Assert.Equal(0u, d0.ExStyle & Win32.WS_EX_TRANSPARENT);
            Assert.Equal(0u, d0.ExStyle & Win32.WS_EX_NOACTIVATE);

            host.EditKey(Win32.VK_RIGHT, shift: true);
            host.EditKey(Win32.VK_DOWN, shift: false);
            var d1 = host.Diagnostics();
            Assert.Equal(d0.WindowRect.Left + 10, d1.WindowRect.Left);
            Assert.Equal(d0.WindowRect.Top + 1, d1.WindowRect.Top);

            host.EditKey(Win32.VK_RETURN, shift: false);
            var d2 = host.Diagnostics();
            Assert.False(d2.Editing);
            Assert.NotEqual(0u, d2.ExStyle & Win32.WS_EX_TRANSPARENT);
            Assert.False(cancelled);
            Assert.NotNull(committed);
            Assert.Equal(OverlayAnchor.TopLeft, committed!.Anchor);
            Assert.Equal(10 / d0.Scale, committed.OffsetX, 1);
            Assert.Equal(1 / d0.Scale, committed.OffsetY, 1);
            Assert.Equal(d1.WindowRect.Left, d2.WindowRect.Left);
        }
    }

    [Fact]
    public void Edit_mode_cancel_restores_position()
    {
        var (host, _, _) = Start();
        using (host)
        {
            var cancelled = false;
            var d0 = host.Diagnostics();
            host.EnterEditMode(_ => { }, () => cancelled = true);
            host.EditKey(Win32.VK_LEFT, shift: true);
            host.EditKey(Win32.VK_ESCAPE, shift: false);
            var d1 = host.Diagnostics();
            Assert.True(cancelled);
            Assert.False(d1.Editing);
            Assert.Equal(d0.WindowRect, d1.WindowRect);
        }
    }

    [Fact]
    public void Edit_mode_shows_even_when_layout_is_disabled()
    {
        var (host, _, _) = Start(enabled: false);
        using (host)
        {
            Assert.False(host.Diagnostics().Shown);
            host.EnterEditMode(_ => { }, () => { });
            Assert.True(host.Diagnostics().Shown);
            host.ExitEditMode(false);
            Assert.False(host.Diagnostics().Shown);
        }
    }

    [Fact]
    public void Unknown_sensor_appears_after_inventory_change()
    {
        var (host, hub, layout) = Start();
        using (host)
        {
            var w0 = host.Diagnostics().SceneHeight;
            layout.Items.Add(Item(Low1));
            host.Apply(layout, OverlayTheme.Default, Ctx(hub));
            var w1 = host.Diagnostics().SceneHeight;
            Assert.True(w1 > w0); // shown as "—" with its default label before the sensor exists
            hub.Add(Low1);
            hub.Set(Low1, 97);
            hub.RaiseInventoryChanged();
            Assert.True(host.Diagnostics().SurfaceReady);
            Assert.Contains(Low1.Id, hub.LastDemandIds);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Both_devices_render_the_same_picture(bool warp)
    {
        var hub = new FakeHub(FpsSensor, HotSpot);
        hub.Set(FpsSensor, 60);
        hub.Set(HotSpot, 101);
        var layout = new OverlayLayout { Enabled = true, Items = [Item(FpsSensor), Item(HotSpot)] };
        using var host = new OverlayHost(new OverlayHostOptions { NeverShow = true, PreferWarp = warp });
        host.Start();
        host.Apply(layout, OverlayTheme.Default, Ctx(hub));
        var d = host.Diagnostics();
        Assert.True(d.SurfaceReady);
        if (warp) Assert.True(d.Warp); // the GPU may be missing on a CI box: then WARP is the fallback
        var back = host.ReadBack()!;
        var off = OverlayHost.RenderToBitmap(layout, OverlayTheme.Default, [new(FpsSensor, 60), new(HotSpot, 101)],
            OverlayHost.GetMonitors().First(m => m.IsPrimary).Bounds.Height);
        long diff = 0;
        for (var i = 0; i < off.Pixels.Length; i++) diff += Math.Abs(off.Pixels[i] - back.Pixels[i]);
        Assert.True(diff / (double)off.Pixels.Length < 3);
    }

    [Fact]
    public void Dispose_stops_the_thread()
    {
        var (host, _, _) = Start();
        Assert.True(host.IsRunning);
        host.Dispose();
        Assert.False(host.IsRunning);
    }
}
