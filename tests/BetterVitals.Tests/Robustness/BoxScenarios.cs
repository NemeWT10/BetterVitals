using System.Diagnostics;
using System.Runtime.InteropServices;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Overlay;
using BetterVitals.Overlay.Interop;
using Xunit;
using Xunit.Abstractions;

namespace BetterVitals.Tests.Robustness;

/// <summary>
/// Detached overlay boxes (PROJEKT §7): every box is a window of its own with the same safety properties, on one shared
/// graphics device; each follows its own placement through unplugged / replugged monitors and resolution changes, and
/// "Set on screen" moves them one by one. Synthetic monitors through the host's monitor source; windows never shown.
/// </summary>
[Collection(HeavyCollection.Name)]
public sealed class BoxScenarios(ITestOutputHelper output)
{
    private static OverlayMonitor Mon(string key, int x, int y, int w, int h, bool primary = false)
        => new(key, $"\\\\.\\DISPLAY{key}", $"Sim {key}", new PixelRect(x, y, x + w, y + h), new PixelRect(x, y, x + w, y + h - (primary ? 48 : 0)), primary);

    private static readonly OverlayMonitor A = Mon("A", 0, 0, 2560, 1440, primary: true);
    private static readonly OverlayMonitor B = Mon("B", 2560, 0, 3840, 2160);

    private sealed class Screens(params OverlayMonitor[] initial)
    {
        private List<OverlayMonitor> _list = [.. initial];

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

    private static bool Inside(PixelRect inner, PixelRect outer)
        => inner.Left >= outer.Left && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    /// <summary>Main box top-left on the primary monitor; a GPU box bottom-right on B; an FPS box top-center on the primary.</summary>
    private static OverlayLayout ThreeBoxes(SimPc pc)
    {
        var cpu = pc.Sensor("AMD Ryzen 7 9800X3D", SensorKind.Temperature, "Core (Tctl/Tdie)");
        var load = pc.Sensor("AMD Ryzen 7 9800X3D", SensorKind.Load, "CPU Total");
        var gpu = pc.Sensor("NVIDIA GeForce RTX 5080", SensorKind.Temperature, "GPU Core");
        var hot = pc.Sensor("NVIDIA GeForce RTX 5080", SensorKind.Temperature, "GPU Hot Spot");
        var ram = pc.Sensor("Generic Memory", SensorKind.Load, "Memory");
        return new OverlayLayout
        {
            Enabled = true,
            Placement = { Anchor = OverlayAnchor.TopLeft },
            Rows = [new() { Items = [new() { SensorIds = [cpu.Id] }, new() { SensorIds = [load.Id] }] }, new() { Items = [new() { SensorIds = [ram.Id] }] }],
            Boxes =
            [
                new() { Id = "gpu", Placement = { Anchor = OverlayAnchor.BottomRight, MonitorMode = OverlayMonitorMode.Specific, MonitorKey = "B", OffsetX = 30, OffsetY = 40 }, Rows = [new() { Items = [new() { SensorIds = [gpu.Id] }, new() { SensorIds = [hot.Id] }] }] },
                new() { Id = "fps", Placement = { Anchor = OverlayAnchor.TopCenter }, Rows = [new() { Items = [new() { SensorIds = ["game/foreground/0/fps/fps"] }] }] },
            ],
        };
    }

    private static OverlayHost Start(Screens screens, SimPc pc, OverlayLayout layout)
    {
        var host = new OverlayHost(new OverlayHostOptions { NeverShow = true, MonitorSource = () => screens.Current });
        host.Start();
        host.Apply(layout, OverlayTheme.Default, new OverlayContext { Hub = pc.Engine, Animations = false });
        return host;
    }

    private static BoxDiagnostics Box(OverlayDiagnostics d, string id) => d.Boxes.Single(b => b.Id == id);

    [Fact]
    public void Every_box_is_a_safe_window_of_its_own_on_one_shared_device()
    {
        using var pc = new SimPc();
        var screens = new Screens(A, B);
        using var host = Start(screens, pc, ThreeBoxes(pc));
        pc.Run(TimeSpan.FromSeconds(3));
        host.RefreshNow();
        var d = host.Diagnostics();
        Assert.Equal([OverlayLayout.MainBoxId, "gpu", "fps"], d.Boxes.Select(b => b.Id));
        Assert.Equal(3, d.Boxes.Select(b => b.Hwnd).Distinct().Count());
        Assert.Equal(1, d.DeviceCreations); // one device for all boxes
        const uint want = Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_LAYERED | Win32.WS_EX_NOREDIRECTIONBITMAP;
        foreach (var b in d.Boxes)
        {
            Assert.True(b.Shown, b.Id);
            Assert.True(b.SurfaceReady, b.Id);
            Assert.Equal(want, b.ExStyle & want);
            Assert.Equal(0u, b.ExStyle & 0x00040000u); // no taskbar button
            Assert.Equal(Win32.WDA_EXCLUDEFROMCAPTURE, b.DisplayAffinity);
            Assert.Equal((b.SceneWidth, b.SceneHeight), (b.WindowRect.Width, b.WindowRect.Height)); // exactly as big as its content
            Assert.True(b.SceneWidth < 1000 && b.SceneHeight < 400, "never a monitor-sized surface");
        }
        Assert.Equal("A", Box(d, OverlayLayout.MainBoxId).MonitorKey);
        Assert.Equal("B", Box(d, "gpu").MonitorKey);
        Assert.Equal(2.0, Box(d, "gpu").Scale, 6); // its own monitor's scale
        Assert.Equal(1440 / 1080.0, Box(d, OverlayLayout.MainBoxId).Scale, 6);
        Assert.True(Inside(Box(d, "gpu").WindowRect, B.WorkArea));
        var fps = Box(d, "fps").WindowRect;
        Assert.InRange(fps.Left + fps.Width / 2.0, A.WorkArea.Width / 2.0 - 1, A.WorkArea.Width / 2.0 + 1); // top center
        Assert.Equal(3 + 2 + 1, host.ShownValues().Length);
        Assert.True(d.HasDemand);
        var asked = pc.Engine.Demands.Where(x => x.Consumer == "overlay").Sum(x => x.Sensors);
        Assert.Equal(6, asked); // one demand for every box's sensors
    }

    [Fact]
    public void Boxes_follow_their_own_monitors_through_unplug_replug_and_a_resolution_change()
    {
        using var pc = new SimPc();
        var screens = new Screens(A, B);
        var layout = ThreeBoxes(pc);
        using var host = Start(screens, pc, layout);
        var first = host.Diagnostics();
        var gpuOnB = Box(first, "gpu");
        var mainOnA = Box(first, OverlayLayout.MainBoxId);

        screens.Set(A); // B unplugged: the GPU box waits on the primary, same corner
        host.SimulateDisplayChange();
        var d = host.Diagnostics();
        var gpu = Box(d, "gpu");
        Assert.Equal("A", gpu.MonitorKey);
        Assert.True(Inside(gpu.WindowRect, A.WorkArea));
        Assert.True(gpu.WindowRect.Right > A.WorkArea.Width * 3 / 4 && gpu.WindowRect.Bottom > A.WorkArea.Height * 3 / 4, "bottom right");
        Assert.Equal(mainOnA.WindowRect, Box(d, OverlayLayout.MainBoxId).WindowRect); // the others did not move

        screens.Set(B, A); // back: exactly where it was
        host.SimulateDisplayChange();
        Assert.Equal(gpuOnB.WindowRect, Box(host.Diagnostics(), "gpu").WindowRect);

        var low = Mon("B", 2560, 0, 2560, 1440);
        screens.Set(A, low); // the game switches B to 1440p
        host.SimulateDisplayChange();
        gpu = Box(host.Diagnostics(), "gpu");
        Assert.Equal(1440 / 1080.0, gpu.Scale, 6);
        Assert.True(Inside(gpu.WindowRect, low.WorkArea));

        screens.Set(); // every monitor gone for a moment
        host.SimulateDisplayChange();
        Assert.True(host.IsRunning);
        screens.Set(A, B);
        host.SimulateDisplayChange();
        Assert.Equal(gpuOnB.WindowRect, Box(host.Diagnostics(), "gpu").WindowRect);

        var rnd = new Random(12);
        for (var i = 0; i < 60; i++)
        {
            var list = new List<OverlayMonitor>();
            for (var k = rnd.Next(0, 3); k > 0; k--)
                list.Add(Mon(k == 1 ? "B" : "M" + k, k * 4000, rnd.Next(-300, 300), rnd.Next(2) == 0 ? 1920 : 3840, rnd.Next(2) == 0 ? 1080 : 2160, primary: list.Count == 0));
            screens.Set([.. list]);
            host.SimulateDisplayChange();
            var now = host.Diagnostics();
            if (list.Count == 0) continue;
            foreach (var box in layout.AllBoxes())
            {
                var chosen = OverlayPlacementMath.Resolve(box.Placement, list)!;
                var b = Box(now, box.Id);
                Assert.Equal(chosen.Key, b.MonitorKey);
                Assert.True(Inside(b.WindowRect, chosen.WorkArea), $"step {i} {box.Id}: {b.WindowRect} in {chosen.WorkArea}");
            }
        }
        Assert.True(host.Diagnostics().DeviceCreations <= 2, "display changes do not rebuild the device");
    }

    [Fact]
    public void Set_on_screen_moves_the_selected_box_tab_selects_the_next_and_enter_saves_all()
    {
        using var pc = new SimPc();
        var screens = new Screens(A, B);
        using var host = Start(screens, pc, ThreeBoxes(pc));
        IReadOnlyDictionary<string, OverlayPlacement>? committed = null;
        host.EnterEditMode(p => committed = p, () => { });
        var d0 = host.Diagnostics();
        Assert.Equal(OverlayLayout.MainBoxId, d0.ActiveBox);
        Assert.All(d0.Boxes, b => Assert.Equal(0u, b.ExStyle & Win32.WS_EX_TRANSPARENT)); // every box takes the mouse

        host.EditKey(Win32.VK_TAB, shift: false);
        Assert.Equal("gpu", host.Diagnostics().ActiveBox);
        host.EditKey(Win32.VK_LEFT, shift: true); // 10 px toward the middle of B
        host.EditKey(Win32.VK_UP, shift: false);
        var d1 = host.Diagnostics();
        Assert.Equal(Box(d0, "gpu").WindowRect.Left - 10, Box(d1, "gpu").WindowRect.Left);
        Assert.Equal(Box(d0, "gpu").WindowRect.Top - 1, Box(d1, "gpu").WindowRect.Top);
        Assert.Equal(Box(d0, OverlayLayout.MainBoxId).WindowRect, Box(d1, OverlayLayout.MainBoxId).WindowRect);
        host.EditKey(Win32.VK_TAB, shift: true); // back to the main box
        Assert.Equal(OverlayLayout.MainBoxId, host.Diagnostics().ActiveBox);

        host.EditKey(Win32.VK_RETURN, shift: false);
        Assert.NotNull(committed);
        Assert.Equal(3, committed!.Count);
        var p = committed["gpu"];
        Assert.Equal(OverlayAnchor.BottomRight, p.Anchor);
        Assert.Equal(OverlayMonitorMode.Specific, p.MonitorMode);
        Assert.Equal("B", p.MonitorKey);
        Assert.Equal(30 + 10 / 2.0, p.OffsetX, 1); // 10 px at scale 2 = 5 reference px
        Assert.Equal(40 + 1 / 2.0, p.OffsetY, 1);
        Assert.Equal(0, committed[OverlayLayout.MainBoxId].OffsetX, 1);
        var after = host.Diagnostics();
        Assert.False(after.Editing);
        Assert.All(after.Boxes, b => Assert.NotEqual(0u, b.ExStyle & Win32.WS_EX_TRANSPARENT)); // click-through again
        Assert.Equal(Box(d1, "gpu").WindowRect, Box(after, "gpu").WindowRect);

        // Esc puts every box back
        host.EnterEditMode(_ => throw new InvalidOperationException("no commit"), () => { });
        host.EditKey(Win32.VK_TAB, shift: false);
        host.EditKey(Win32.VK_RIGHT, shift: true);
        host.EditKey(Win32.VK_ESCAPE, shift: false);
        Assert.Equal(Box(after, "gpu").WindowRect, Box(host.Diagnostics(), "gpu").WindowRect);
    }

    [Fact]
    public void Removing_and_adding_boxes_destroys_and_creates_their_windows()
    {
        using var pc = new SimPc();
        var screens = new Screens(A);
        var layout = ThreeBoxes(pc);
        using var host = Start(screens, pc, layout);
        var gpuHwnd = Box(host.Diagnostics(), "gpu").Hwnd;
        layout.RemoveBox("gpu");
        host.Apply(layout, OverlayTheme.Default, new OverlayContext { Hub = pc.Engine, Animations = false });
        var d = host.Diagnostics();
        Assert.Equal([OverlayLayout.MainBoxId, "fps"], d.Boxes.Select(b => b.Id));
        Assert.False(IsWindow(gpuHwnd));
        Assert.Equal(6, host.ShownValues().Length); // its items are in the main box now

        // everything detached: the main window stays (it owns the timers), hidden; the boxes show
        foreach (var row in layout.Rows.ToList()) layout.MoveRowToNewBox(row.Id, "b-" + row.Id, layout.NewBoxPlacement());
        host.Apply(layout, OverlayTheme.Default, new OverlayContext { Hub = pc.Engine, Animations = false });
        d = host.Diagnostics();
        Assert.False(Box(d, OverlayLayout.MainBoxId).Shown);
        Assert.True(d.Shown);
        Assert.All(d.Boxes.Where(b => b.Id != OverlayLayout.MainBoxId), b => Assert.True(b.Shown && b.SurfaceReady));
    }

    [Fact]
    public void A_lost_device_comes_back_for_every_box_and_hundreds_of_box_changes_leak_nothing()
    {
        using var pc = new SimPc();
        var screens = new Screens(A, B);
        var layout = ThreeBoxes(pc);
        using var host = Start(screens, pc, layout);
        var ctx = new OverlayContext { Hub = pc.Engine, Animations = false };
        pc.Run(TimeSpan.FromSeconds(2));
        for (var i = 0; i < 3; i++) Assert.True(host.SimulateDeviceLost());
        Assert.All(host.Diagnostics().Boxes, b => Assert.True(b.SurfaceReady));

        // warm up, then add and remove boxes over and over
        var rnd = new Random(3);
        void Churn(int n)
        {
            for (var i = 0; i < n; i++)
            {
                var l = ThreeBoxes(pc);
                for (var k = rnd.Next(0, 4); k > 0; k--)
                    if (l.Rows.Count > 0) l.MoveRowToNewBox(l.Rows[0].Id, "x" + i + "-" + k, l.NewBoxPlacement());
                if (rnd.Next(3) == 0) l.RemoveBox("fps");
                host.Apply(l, OverlayTheme.Default, ctx);
                if (i % 7 == 0) host.SimulateDeviceLost();
                host.RefreshNow();
            }
        }
        Churn(20);
        var before = Resources.Now();
        Churn(200);
        host.Apply(layout, OverlayTheme.Default, ctx);
        var after = Resources.Now();
        output.WriteLine($"before: {before}");
        output.WriteLine($"after:  {after}");
        Assert.Equal(3, host.Diagnostics().Boxes.Count);
        Assert.True(after.User < before.User + 30, "box windows are destroyed with their boxes");
        Assert.True(after.Gdi < before.Gdi + 30);
        Assert.True(after.Handles < before.Handles + 250);
        Assert.True(after.PrivateBytes - before.PrivateBytes < 150L << 20);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(nint process, uint flags);

    private readonly record struct Resources(int Handles, long PrivateBytes, uint Gdi, uint User)
    {
        public static Resources Now()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using var p = Process.GetCurrentProcess();
            return new Resources(p.HandleCount, p.PrivateMemorySize64, GetGuiResources(p.Handle, 0), GetGuiResources(p.Handle, 1));
        }

        public override string ToString() => $"handles {Handles}, private {PrivateBytes >> 20} MB, GDI {Gdi}, USER {User}";
    }
}
