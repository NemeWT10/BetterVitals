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
/// The second overlay window (PROJEKT §7): a window of its own with the same safety properties as the main one, on one
/// shared graphics device; each follows its own placement through unplugged / replugged monitors and resolution changes,
/// and "Set on screen" moves them one by one. Synthetic monitors through the host's monitor source; windows never shown.
/// </summary>
[Collection(HeavyCollection.Name)]
public sealed class WindowScenarios(ITestOutputHelper output)
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

    private static OverlayItem Cell(SensorDescriptor s, int row, int column) => new() { SensorIds = [s.Id], Row = row, Column = column };

    /// <summary>The main window top-left on the primary monitor (CPU temp | load, RAM); the second one bottom-right on B (GPU | hot spot, FPS).</summary>
    private static OverlayLayout TwoWindows(SimPc pc)
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
            Cells = [Cell(cpu, 0, 0), Cell(load, 0, 1), Cell(ram, 1, 0)],
            Second = new OverlaySecondWindow
            {
                Enabled = true,
                Placement = { Anchor = OverlayAnchor.BottomRight, MonitorMode = OverlayMonitorMode.Specific, MonitorKey = "B", OffsetX = 30, OffsetY = 40 },
                Cells = [Cell(gpu, 0, 0), Cell(hot, 0, 1), new OverlayItem { SensorIds = ["game/foreground/0/fps/fps"], Row = 1, Column = 1 }],
            },
        };
    }

    private static OverlayHost Start(Screens screens, SimPc pc, OverlayLayout layout)
    {
        var host = new OverlayHost(new OverlayHostOptions { NeverShow = true, MonitorSource = () => screens.Current });
        host.Start();
        host.Apply(layout, OverlayTheme.Default, new OverlayContext { Hub = pc.Engine, Animations = false });
        return host;
    }

    private static WindowDiagnostics Win(OverlayDiagnostics d, string id) => d.Windows.Single(b => b.Id == id);

    private const string Main = OverlayLayout.MainWindowId;
    private const string Second = OverlayLayout.SecondWindowId;

    [Fact]
    public void Each_window_is_a_safe_window_of_its_own_on_one_shared_device()
    {
        using var pc = new SimPc();
        var screens = new Screens(A, B);
        using var host = Start(screens, pc, TwoWindows(pc));
        pc.Run(TimeSpan.FromSeconds(3));
        host.RefreshNow();
        var d = host.Diagnostics();
        Assert.Equal([Main, Second], d.Windows.Select(b => b.Id));
        Assert.Equal(2, d.Windows.Select(b => b.Hwnd).Distinct().Count());
        Assert.Equal(1, d.DeviceCreations); // one device for both windows
        const uint want = Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_LAYERED | Win32.WS_EX_NOREDIRECTIONBITMAP;
        foreach (var b in d.Windows)
        {
            Assert.True(b.Shown, b.Id);
            Assert.True(b.SurfaceReady, b.Id);
            Assert.Equal(want, b.ExStyle & want);
            Assert.Equal(0u, b.ExStyle & 0x00040000u); // no taskbar button
            Assert.Equal(Win32.WDA_EXCLUDEFROMCAPTURE, b.DisplayAffinity);
            Assert.Equal((b.SceneWidth, b.SceneHeight), (b.WindowRect.Width, b.WindowRect.Height)); // exactly as big as its content
            Assert.True(b.SceneWidth < 1000 && b.SceneHeight < 400, "never a monitor-sized surface");
        }
        Assert.Equal("A", Win(d, Main).MonitorKey);
        Assert.Equal("B", Win(d, Second).MonitorKey);
        Assert.Equal(2.0, Win(d, Second).Scale, 6); // its own monitor's scale
        Assert.Equal(1440 / 1080.0, Win(d, Main).Scale, 6);
        Assert.True(Inside(Win(d, Second).WindowRect, B.WorkArea));
        Assert.Equal(6, host.ShownValues().Length);
        Assert.True(d.HasDemand);
        var asked = pc.Engine.Demands.Where(x => x.Consumer == "overlay").Sum(x => x.Sensors);
        Assert.Equal(6, asked); // one demand for both windows' sensors
    }

    [Fact]
    public void Both_windows_follow_their_own_monitors_through_unplug_replug_and_a_resolution_change()
    {
        using var pc = new SimPc();
        var screens = new Screens(A, B);
        var layout = TwoWindows(pc);
        using var host = Start(screens, pc, layout);
        var first = host.Diagnostics();
        var secondOnB = Win(first, Second);
        var mainOnA = Win(first, Main);

        screens.Set(A); // B unplugged: the second window waits on the primary, same corner
        host.SimulateDisplayChange();
        var d = host.Diagnostics();
        var second = Win(d, Second);
        Assert.Equal("A", second.MonitorKey);
        Assert.True(Inside(second.WindowRect, A.WorkArea));
        Assert.True(second.WindowRect.Right > A.WorkArea.Width * 3 / 4 && second.WindowRect.Bottom > A.WorkArea.Height * 3 / 4, "bottom right");
        Assert.Equal(mainOnA.WindowRect, Win(d, Main).WindowRect); // the main one did not move

        screens.Set(B, A); // back: exactly where it was
        host.SimulateDisplayChange();
        Assert.Equal(secondOnB.WindowRect, Win(host.Diagnostics(), Second).WindowRect);

        var low = Mon("B", 2560, 0, 2560, 1440);
        screens.Set(A, low); // the game switches B to 1440p
        host.SimulateDisplayChange();
        second = Win(host.Diagnostics(), Second);
        Assert.Equal(1440 / 1080.0, second.Scale, 6);
        Assert.True(Inside(second.WindowRect, low.WorkArea));

        screens.Set(); // every monitor gone for a moment
        host.SimulateDisplayChange();
        Assert.True(host.IsRunning);
        screens.Set(A, B);
        host.SimulateDisplayChange();
        Assert.Equal(secondOnB.WindowRect, Win(host.Diagnostics(), Second).WindowRect);

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
            foreach (var window in layout.Windows())
            {
                var chosen = OverlayPlacementMath.Resolve(window.Placement, list)!;
                var b = Win(now, window.Id);
                Assert.Equal(chosen.Key, b.MonitorKey);
                Assert.True(Inside(b.WindowRect, chosen.WorkArea), $"step {i} {window.Id}: {b.WindowRect} in {chosen.WorkArea}");
            }
        }
        Assert.True(host.Diagnostics().DeviceCreations <= 2, "display changes do not rebuild the device");
    }

    [Fact]
    public void Set_on_screen_moves_the_selected_window_tab_selects_the_other_and_enter_saves_both()
    {
        using var pc = new SimPc();
        var screens = new Screens(A, B);
        using var host = Start(screens, pc, TwoWindows(pc));
        IReadOnlyDictionary<string, OverlayPlacement>? committed = null;
        host.EnterEditMode(p => committed = p, () => { });
        var d0 = host.Diagnostics();
        Assert.Equal(Main, d0.ActiveWindow);
        Assert.All(d0.Windows, b => Assert.Equal(0u, b.ExStyle & Win32.WS_EX_TRANSPARENT)); // every window takes the mouse

        host.EditKey(Win32.VK_TAB, shift: false);
        Assert.Equal(Second, host.Diagnostics().ActiveWindow);
        host.EditKey(Win32.VK_LEFT, shift: true); // 10 px toward the middle of B
        host.EditKey(Win32.VK_UP, shift: false);
        var d1 = host.Diagnostics();
        Assert.Equal(Win(d0, Second).WindowRect.Left - 10, Win(d1, Second).WindowRect.Left);
        Assert.Equal(Win(d0, Second).WindowRect.Top - 1, Win(d1, Second).WindowRect.Top);
        Assert.Equal(Win(d0, Main).WindowRect, Win(d1, Main).WindowRect);
        host.EditKey(Win32.VK_TAB, shift: true); // back to the main window
        Assert.Equal(Main, host.Diagnostics().ActiveWindow);

        host.EditKey(Win32.VK_RETURN, shift: false);
        Assert.NotNull(committed);
        Assert.Equal([Main, Second], committed!.Keys.Order());
        var p = committed[Second];
        Assert.Equal(OverlayAnchor.BottomRight, p.Anchor);
        Assert.Equal(OverlayMonitorMode.Specific, p.MonitorMode);
        Assert.Equal("B", p.MonitorKey);
        Assert.Equal(30 + 10 / 2.0, p.OffsetX, 1); // 10 px at scale 2 = 5 reference px
        Assert.Equal(40 + 1 / 2.0, p.OffsetY, 1);
        Assert.Equal(0, committed[Main].OffsetX, 1);
        var after = host.Diagnostics();
        Assert.False(after.Editing);
        Assert.All(after.Windows, b => Assert.NotEqual(0u, b.ExStyle & Win32.WS_EX_TRANSPARENT)); // click-through again
        Assert.Equal(Win(d1, Second).WindowRect, Win(after, Second).WindowRect);

        // Esc puts both back
        host.EnterEditMode(_ => throw new InvalidOperationException("no commit"), () => { });
        host.EditKey(Win32.VK_TAB, shift: false);
        host.EditKey(Win32.VK_RIGHT, shift: true);
        host.EditKey(Win32.VK_ESCAPE, shift: false);
        Assert.Equal(Win(after, Second).WindowRect, Win(host.Diagnostics(), Second).WindowRect);
    }

    [Fact]
    public void The_second_window_off_destroys_its_window_and_an_empty_one_stays_hidden()
    {
        using var pc = new SimPc();
        var screens = new Screens(A);
        var layout = TwoWindows(pc);
        var ctx = new OverlayContext { Hub = pc.Engine, Animations = false };
        using var host = Start(screens, pc, layout);
        var secondHwnd = Win(host.Diagnostics(), Second).Hwnd;

        layout.SetSecondWindow(false); // its cells come back to the main window
        host.Apply(layout, OverlayTheme.Default, ctx);
        var d = host.Diagnostics();
        Assert.Equal([Main], d.Windows.Select(b => b.Id));
        Assert.False(IsWindow(secondHwnd));
        Assert.Equal(6, host.ShownValues().Length);

        // on again but still empty: a window, never shown
        layout.SetSecondWindow(true);
        host.Apply(layout, OverlayTheme.Default, ctx);
        d = host.Diagnostics();
        Assert.Equal([Main, Second], d.Windows.Select(b => b.Id));
        Assert.False(Win(d, Second).Shown);
        Assert.False(Win(d, Second).WindowVisible);

        // every cell in the second window: the main one hides (it keeps the timers), the second one shows
        foreach (var cell in layout.Cells.ToList())
            layout.MoveCell(cell.Id, new GridDrop(Second, GridDropKind.NewRow, OverlayGrid.RowCount(layout.Second.Cells), 0));
        host.Apply(layout, OverlayTheme.Default, ctx);
        d = host.Diagnostics();
        Assert.False(Win(d, Main).Shown);
        Assert.True(d.Shown);
        Assert.True(Win(d, Second).Shown && Win(d, Second).SurfaceReady);
        Assert.Equal(6, host.ShownValues().Length);
    }

    [Fact]
    public void A_lost_device_comes_back_for_both_windows_and_hundreds_of_window_changes_leak_nothing()
    {
        using var pc = new SimPc();
        var screens = new Screens(A, B);
        var layout = TwoWindows(pc);
        using var host = Start(screens, pc, layout);
        var ctx = new OverlayContext { Hub = pc.Engine, Animations = false };
        pc.Run(TimeSpan.FromSeconds(2));
        for (var i = 0; i < 3; i++) Assert.True(host.SimulateDeviceLost());
        Assert.All(host.Diagnostics().Windows, b => Assert.True(b.SurfaceReady));

        // warm up, then turn the second window on and off and move cells over and over
        var rnd = new Random(3);
        void Churn(int n)
        {
            for (var i = 0; i < n; i++)
            {
                var l = TwoWindows(pc);
                l.Normalize();
                if (rnd.Next(3) == 0) l.SetSecondWindow(false);
                else if (l.Cells.Count > 0) l.MoveCell(l.Cells[rnd.Next(l.Cells.Count)].Id, new GridDrop(Second, (GridDropKind)rnd.Next(3), rnd.Next(3), rnd.Next(3)));
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
        Assert.Equal(2, host.Diagnostics().Windows.Count);
        Assert.True(after.User < before.User + 30, "the second window is destroyed when it goes");
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
