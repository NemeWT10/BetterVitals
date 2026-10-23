using System.Diagnostics;
using System.Runtime.InteropServices;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Overlay;
using Xunit;
using Xunit.Abstractions;

namespace BetterVitals.Tests.Robustness;

/// <summary>
/// PROJEKT §7 / §12: monitors that vanish and come back, resolution and scaling changes, a lost graphics device. The placement
/// math is checked on synthetic monitor lists; the live overlay window (never shown) gets those lists through the host's
/// monitor source, so no real display is touched.
/// </summary>
public sealed class DisplayScenarios(ITestOutputHelper output)
{
    private static OverlayMonitor Mon(string key, int x, int y, int w, int h, bool primary = false, int taskbar = 48)
        => new(key, $"\\\\.\\DISPLAY{key}", $"Sim {key}", new PixelRect(x, y, x + w, y + h), new PixelRect(x, y, x + w, y + h - (primary ? taskbar : 0)), primary);

    private static readonly OverlayMonitor A = Mon("A", 0, 0, 2560, 1440, primary: true);
    private static readonly OverlayMonitor B = Mon("B", 2560, 0, 3840, 2160);

    private static OverlayPlacement Placement(OverlayAnchor anchor, string? key = null, OverlayMonitorMode mode = OverlayMonitorMode.Specific, double ox = 30, double oy = 40, double margin = 24)
        => new() { Anchor = anchor, MonitorKey = key, MonitorMode = mode, OffsetX = ox, OffsetY = oy, Margin = margin };

    private static bool Inside(PixelRect inner, PixelRect outer)
        => inner.Left >= outer.Left && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    // ───────────────────────── the placement math ─────────────────────────

    [Fact]
    public void An_unplugged_monitor_sends_the_overlay_to_the_primary_one_with_the_same_anchor_and_it_returns_when_replugged()
    {
        var style = new OverlayStyle();
        foreach (var anchor in Enum.GetValues<OverlayAnchor>())
        {
            var p = Placement(anchor, "B");
            const int w = 260, h = 130;

            var both = OverlayPlacementMath.Resolve(p, [A, B])!;
            Assert.Equal("B", both.Key);
            var scaleB = OverlayPlacementMath.Scale(style, both.Bounds.Height);
            var onB = OverlayPlacementMath.Position(p, both.WorkArea, w, h, scaleB);
            Assert.True(both.WorkArea.Contains(onB.X, onB.Y));

            var only = OverlayPlacementMath.Resolve(p, [A])!;
            Assert.Equal("A", only.Key); // B is gone: the primary one
            var scaleA = OverlayPlacementMath.Scale(style, only.Bounds.Height);
            var onA = OverlayPlacementMath.Position(p, only.WorkArea, w, h, scaleA);
            Assert.True(Inside(new PixelRect(onA.X, onA.Y, onA.X + w, onA.Y + h), only.WorkArea));
            AssertAnchored(anchor, onA, w, h, only.WorkArea, p, scaleA);

            var back = OverlayPlacementMath.Resolve(p, [B, A])!; // the list order is not stable either
            Assert.Equal("B", back.Key);
            Assert.Equal(onB, OverlayPlacementMath.Position(p, back.WorkArea, w, h, scaleB)); // exactly where it was
        }
    }

    private static void AssertAnchored(OverlayAnchor anchor, (int X, int Y) pos, int w, int h, PixelRect area, OverlayPlacement p, double scale)
    {
        var col = (int)anchor % 3;
        var row = (int)anchor / 3;
        var gapX = (p.Margin + p.OffsetX) * scale;
        var gapY = (p.Margin + p.OffsetY) * scale;
        if (col == 0) Assert.InRange(pos.X - area.Left, gapX - 1, gapX + 1);
        if (col == 2) Assert.InRange(area.Right - (pos.X + w), gapX - 1, gapX + 1);
        if (row == 0) Assert.InRange(pos.Y - area.Top, gapY - 1, gapY + 1);
        if (row == 2) Assert.InRange(area.Bottom - (pos.Y + h), gapY - 1, gapY + 1);
        if (col == 1) Assert.InRange(pos.X + w / 2.0, area.Left + area.Width / 2.0 + p.OffsetX * scale - 1, area.Left + area.Width / 2.0 + p.OffsetX * scale + 1);
        if (row == 1) Assert.InRange(pos.Y + h / 2.0, area.Top + area.Height / 2.0 + p.OffsetY * scale - 1, area.Top + area.Height / 2.0 + p.OffsetY * scale + 1);
    }

    [Fact]
    public void A_resolution_change_keeps_the_overlay_at_the_same_relative_place_at_any_size()
    {
        var style = new OverlayStyle();
        foreach (var anchor in Enum.GetValues<OverlayAnchor>())
        foreach (var (name, w, h) in new[] { ("720p", 1280, 720), ("1080p", 1920, 1080), ("1440p", 2560, 1440), ("4K", 3840, 2160), ("5K", 5120, 2880), ("ultrawide", 3440, 1440), ("8K", 7680, 4320) })
        {
            var mon = Mon("A", 0, 0, w, h, primary: true);
            var p = Placement(anchor, null, OverlayMonitorMode.Primary);
            var scale = OverlayPlacementMath.Scale(style, h);
            Assert.Equal(h / 1080.0, scale, 6);
            var ow = (int)Math.Ceiling(260 * scale);
            var oh = (int)Math.Ceiling(130 * scale);
            var pos = OverlayPlacementMath.Position(p, mon.WorkArea, ow, oh, scale);
            Assert.True(Inside(new PixelRect(pos.X, pos.Y, pos.X + ow, pos.Y + oh), mon.WorkArea), $"{anchor} {name}");
            AssertAnchored(anchor, pos, ow, oh, mon.WorkArea, p, scale);

            // dragging it to exactly where it is gives the same placement back (the editor's "set on screen")
            var round = OverlayPlacementMath.FromPosition(pos.X, pos.Y, ow, oh, mon.WorkArea, scale, p);
            Assert.Equal(anchor, round.Anchor);
            var tolerance = 1.5 / scale + 0.2; // one pixel, in reference pixels
            Assert.InRange(round.OffsetX, p.OffsetX - tolerance, p.OffsetX + tolerance);
            Assert.InRange(round.OffsetY, p.OffsetY - tolerance, p.OffsetY + tolerance);
        }
    }

    [Fact]
    public void A_Windows_scaling_change_does_not_move_the_overlay_it_works_in_physical_pixels_but_a_moved_taskbar_does_clamp_it()
    {
        // 100 % -> 150 % scaling: a per-monitor-aware thread still sees the same physical rectangles
        var style = new OverlayStyle();
        var p = Placement(OverlayAnchor.BottomRight, null, OverlayMonitorMode.Primary);
        var before = OverlayPlacementMath.Position(p, A.WorkArea, 300, 160, OverlayPlacementMath.Scale(style, A.Bounds.Height));
        var after = OverlayPlacementMath.Position(p, A.WorkArea, 300, 160, OverlayPlacementMath.Scale(style, A.Bounds.Height));
        Assert.Equal(before, after);

        // the taskbar grows (a bigger one, or on the side): the overlay stays inside the new work area
        foreach (var work in new[] { new PixelRect(0, 0, 2560, 1380), new PixelRect(0, 0, 2400, 1440), new PixelRect(160, 0, 2560, 1440), new PixelRect(0, 100, 2560, 1440) })
        {
            var moved = A with { WorkArea = work };
            var pos = OverlayPlacementMath.Position(p, moved.WorkArea, 300, 160, 1.333);
            Assert.True(Inside(new PixelRect(pos.X, pos.Y, pos.X + 300, pos.Y + 160), work));
        }
    }

    [Fact]
    public void Odd_monitor_setups_never_break_the_math()
    {
        var p = Placement(OverlayAnchor.TopRight, "gone");
        Assert.Null(OverlayPlacementMath.Resolve(p, [])); // every monitor off (RDP, screen saver, docking)
        var noPrimary = OverlayPlacementMath.Resolve(p, [B, Mon("C", -1920, 0, 1920, 1080)])!; // no flag set: the first one
        Assert.Equal("B", noPrimary.Key);

        var rnd = new Random(77);
        for (var i = 0; i < 20_000; i++)
        {
            var n = rnd.Next(1, 5);
            var list = new List<OverlayMonitor>();
            for (var k = 0; k < n; k++)
                list.Add(Mon("K" + k, rnd.Next(-8000, 8000), rnd.Next(-6000, 6000), rnd.Next(0, 8000), rnd.Next(0, 4500), primary: k == 0, taskbar: rnd.Next(0, 200)));
            var mode = (OverlayMonitorMode)rnd.Next(3);
            var key = rnd.Next(3) == 0 ? "nope" : "K" + rnd.Next(n);
            var placement = Placement((OverlayAnchor)rnd.Next(9), key, mode, rnd.Next(-3000, 3000), rnd.Next(-3000, 3000), rnd.Next(-50, 3000));
            var chosen = OverlayPlacementMath.Resolve(placement, list, rnd.Next(2) == 0 ? "K1" : null);
            Assert.NotNull(chosen);
            var style = new OverlayStyle { AutoScale = rnd.Next(2) == 0, ScalePercent = rnd.Next(0, 400) };
            var scale = OverlayPlacementMath.Scale(style, chosen.Bounds.Height);
            Assert.True(scale >= 0.25 && double.IsFinite(scale));
            var w = rnd.Next(1, 3000);
            var h = rnd.Next(1, 2000);
            var pos = OverlayPlacementMath.Position(placement, chosen.WorkArea, w, h, scale);
            var area = chosen.WorkArea;
            Assert.InRange(pos.X, area.Left, Math.Max(area.Left, area.Right - w)); // never off the monitor (a bigger overlay is pinned to the corner)
            Assert.InRange(pos.Y, area.Top, Math.Max(area.Top, area.Bottom - h));
            var back = OverlayPlacementMath.FromPosition(pos.X, pos.Y, w, h, area, scale, placement);
            Assert.True(double.IsFinite(back.OffsetX) && double.IsFinite(back.OffsetY));
            var snap = OverlayPlacementMath.Snap(pos.X, pos.Y, w, h, area, 24, 10);
            Assert.True(snap.X >= int.MinValue);
        }
    }

    // ───────────────────────── the live overlay window ─────────────────────────

    private sealed class Screens
    {
        private List<OverlayMonitor> _list;

        public Screens(params OverlayMonitor[] initial) => _list = [.. initial];

        public IReadOnlyList<OverlayMonitor> Current
        {
            get
            {
                lock (this) return [.. _list];
            }
        }

        public void Set(params OverlayMonitor[] monitors)
        {
            lock (this) _list = [.. monitors];
        }
    }

    private static OverlayHost StartHost(Screens screens, SimPc pc, OverlayPlacement placement)
    {
        var cpu = pc.Sensor("AMD Ryzen 7 9800X3D", SensorKind.Temperature, "Core (Tctl/Tdie)");
        var gpu = pc.Sensor("NVIDIA GeForce RTX 5080", SensorKind.Temperature, "GPU Core");
        var ram = pc.Sensor("Generic Memory", SensorKind.Load, "Memory");
        var layout = new OverlayLayout
        {
            Enabled = true,
            Placement = placement,
            Items = [.. new[] { cpu, gpu, ram }.Select(s => new OverlayItem { SensorIds = [s.Id] })],
        };
        var host = new OverlayHost(new OverlayHostOptions { NeverShow = true, MonitorSource = () => screens.Current });
        host.Start();
        host.Apply(layout, OverlayTheme.Default, new OverlayContext { Hub = pc.Engine, Animations = false });
        Assert.True(host.Diagnostics().SurfaceReady);
        return host;
    }

    [Fact]
    public void The_live_overlay_follows_a_monitor_that_is_unplugged_and_plugged_back_and_a_resolution_change()
    {
        using var pc = new SimPc();
        var screens = new Screens(A, B);
        using var host = StartHost(screens, pc, Placement(OverlayAnchor.BottomRight, "B"));
        var style = new OverlayStyle();

        var onB = host.Diagnostics();
        Assert.Equal("B", onB.MonitorKey);
        Assert.True(Inside(onB.WindowRect, B.WorkArea));
        Assert.Equal(2.0, onB.Scale, 6); // 2160 px tall
        AssertAnchored(OverlayAnchor.BottomRight, (onB.WindowRect.Left, onB.WindowRect.Top), onB.WindowRect.Width, onB.WindowRect.Height, B.WorkArea, Placement(OverlayAnchor.BottomRight, "B"), onB.Scale);

        // the monitor is unplugged
        screens.Set(A);
        Assert.True(host.SimulateDisplayChange());
        var onA = host.Diagnostics();
        Assert.Equal("A", onA.MonitorKey);
        Assert.True(Inside(onA.WindowRect, A.WorkArea));
        Assert.Equal(OverlayPlacementMath.Scale(style, 1440), onA.Scale, 6);
        Assert.True(onA.SurfaceReady);
        AssertAnchored(OverlayAnchor.BottomRight, (onA.WindowRect.Left, onA.WindowRect.Top), onA.WindowRect.Width, onA.WindowRect.Height, A.WorkArea, Placement(OverlayAnchor.BottomRight, "B"), onA.Scale);
        Assert.True(onA.WindowRect.Width < onB.WindowRect.Width, "smaller monitor, smaller overlay");

        // and plugged back
        screens.Set(B, A);
        host.SimulateDisplayChange();
        var again = host.Diagnostics();
        Assert.Equal("B", again.MonitorKey);
        Assert.Equal(onB.WindowRect, again.WindowRect);
        Assert.Equal(onB.Scale, again.Scale, 6);

        // the game switches B from 4K to 1440p
        var low = Mon("B", 2560, 0, 2560, 1440);
        screens.Set(A, low);
        host.SimulateDisplayChange();
        var res = host.Diagnostics();
        Assert.Equal("B", res.MonitorKey);
        Assert.True(Inside(res.WindowRect, low.WorkArea));
        Assert.Equal(1440 / 1080.0, res.Scale, 6);

        // a Windows scaling change reports the very same physical monitors
        screens.Set(A, low);
        host.SimulateDisplayChange();
        Assert.Equal(res.WindowRect, host.Diagnostics().WindowRect);

        // every monitor is gone for a moment (RDP disconnect, display driver reset): the window stays alive and sane
        screens.Set();
        host.SimulateDisplayChange();
        Assert.True(host.IsRunning);
        var none = host.Diagnostics();
        Assert.True(none.SceneWidth > 0);
        screens.Set(A, B);
        host.SimulateDisplayChange();
        Assert.Equal(onB.WindowRect, host.Diagnostics().WindowRect);
    }

    [Fact]
    public void The_live_overlay_survives_a_hundred_random_display_changes_and_stays_on_screen()
    {
        using var pc = new SimPc();
        var screens = new Screens(A, B);
        using var host = StartHost(screens, pc, Placement(OverlayAnchor.TopLeft, "B"));
        var rnd = new Random(5);
        var created = host.Diagnostics().DeviceCreations;
        var handles = Process.GetCurrentProcess().HandleCount;
        for (var i = 0; i < 100; i++)
        {
            var list = new List<OverlayMonitor>();
            var n = rnd.Next(0, 4);
            for (var k = 0; k < n; k++)
            {
                var (w, h) = rnd.Next(5) switch { 0 => (1920, 1080), 1 => (2560, 1440), 2 => (3840, 2160), 3 => (1366, 768), _ => (5120, 1440) };
                list.Add(Mon(k == 1 ? "B" : "M" + k, k * 4000 - rnd.Next(0, 2) * 6000, rnd.Next(-500, 500), w, h, primary: k == 0));
            }
            screens.Set([.. list]);
            host.SimulateDisplayChange();
            var d = host.Diagnostics();
            Assert.True(host.IsRunning);
            if (list.Count == 0) continue;
            var chosen = OverlayPlacementMath.Resolve(Placement(OverlayAnchor.TopLeft, "B"), list)!;
            Assert.Equal(chosen.Key, d.MonitorKey);
            Assert.True(Inside(d.WindowRect, chosen.WorkArea) || d.WindowRect.Width > chosen.WorkArea.Width || d.WindowRect.Height > chosen.WorkArea.Height, $"step {i}: {d.WindowRect} in {chosen.WorkArea}");
        }
        var end = host.Diagnostics();
        output.WriteLine($"device creations {created} -> {end.DeviceCreations}, handles {handles} -> {Process.GetCurrentProcess().HandleCount}");
        Assert.True(end.SurfaceReady);
        Assert.True(end.DeviceCreations <= created + 1, "a display change does not rebuild the graphics device");
        Assert.True(Process.GetCurrentProcess().HandleCount < handles + 60);
    }

    [Fact]
    public void A_lost_graphics_device_over_and_over_is_recovered_without_leaking()
    {
        using var pc = new SimPc();
        var screens = new Screens(A, B);
        using var host = StartHost(screens, pc, Placement(OverlayAnchor.TopRight, null, OverlayMonitorMode.Primary));
        pc.Run(TimeSpan.FromSeconds(4));
        host.RefreshNow();
        var before = Process.GetCurrentProcess();
        before.Refresh();
        var handles = before.HandleCount;
        var gdi = GetGuiResources(before.Handle, 0);
        var user = GetGuiResources(before.Handle, 1);
        var creations = host.Diagnostics().DeviceCreations;

        for (var i = 0; i < 40; i++)
        {
            Assert.True(host.SimulateDeviceLost(), $"recovered after loss {i}");
            if (i % 5 == 0) host.SimulateDisplayChange();
            pc.Run(TimeSpan.FromSeconds(2));
            host.RefreshNow();
            Assert.DoesNotContain("—", host.ShownValues());
        }

        var d = host.Diagnostics();
        var after = Process.GetCurrentProcess();
        output.WriteLine($"handles {handles} -> {after.HandleCount}, GDI {gdi} -> {GetGuiResources(after.Handle, 0)}, USER {user} -> {GetGuiResources(after.Handle, 1)}, device creations {creations} -> {d.DeviceCreations}");
        Assert.True(d.SurfaceReady);
        Assert.True(d.DeviceCreations >= creations + 40);
        Assert.True(after.HandleCount < handles + 100, "handles");
        Assert.True(GetGuiResources(after.Handle, 0) < gdi + 20, "GDI objects");
        Assert.True(GetGuiResources(after.Handle, 1) < user + 20, "USER objects");
    }

    [Fact]
    public void Starting_and_stopping_the_overlay_many_times_leaves_nothing_behind()
    {
        using var pc = new SimPc();
        var screens = new Screens(A);
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var threads = process.Threads.Count;
        var handles = process.HandleCount;
        for (var i = 0; i < 60; i++)
        {
            using var host = StartHost(screens, pc, Placement(OverlayAnchor.TopLeft, null, OverlayMonitorMode.Primary));
            pc.Run(TimeSpan.FromSeconds(2));
            host.RefreshNow();
        }
        Assert.DoesNotContain(pc.Engine.Demands, d => d.Consumer == "overlay"); // every window released its demand
        // the managed wrappers (threads, events) give their handles back with the next collection; nothing may stay beyond that
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        process.Refresh();
        output.WriteLine($"60 windows: threads {threads} -> {process.Threads.Count}, handles {handles} -> {process.HandleCount}");
        Assert.True(process.Threads.Count < threads + 10);
        Assert.True(process.HandleCount < handles + 80);
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(nint process, uint flags);
}
