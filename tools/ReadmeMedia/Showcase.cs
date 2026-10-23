using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterVitals.Core.Catalog;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.Overlay;
using BetterVitals.Services;
using BetterVitals.UI;
using BetterVitals.UI.Explain;
using BetterVitals.UI.Flyout;
using BetterVitals.UI.Pages.History;
using BetterVitals.UI.Pages.OverlayEditor;
using BetterVitals.UI.Pages.Overview;
using BetterVitals.UI.Pages.Sensors;
using BetterVitals.UI.Panel;
using BetterVitals.UI.Themes;
using BetterVitals.UI.Tray;

namespace ReadmeMedia;

/// <summary>
/// The pictures of the README, in its order. Each one opens the real window / page on the fake PC, waits until it has
/// settled, captures it and places it on the made-up desktop (<see cref="Stage"/>).
/// </summary>
internal static class Showcase
{
    public static readonly string[] Names = ["hero", "history", "overlay", "overlay-editor", "sensors"];

    /// <summary>The overlay in the pictures: what a gamer would pick in the simple editor.</summary>
    private static readonly string[] OverlayKeys = ["fps.fps", "cpu.temp", "cpu.clocks", "gpu.temp", "gpu.hotspot", "gpu.clock"];

    private static FakeRig _rig = null!;
    private static readonly StringBuilder Report = new();
    private static readonly List<string> Failures = [];

    /// <summary>Cut-off text, missing values: the picture is written, but needs a look.</summary>
    private static readonly List<string> Warnings = [];

    public static async Task<int> RunAsync(string outDir, IReadOnlySet<string> only)
    {
        Directory.CreateDirectory(outDir);
        // two and a half minutes of the fake PC: values, session min / max and a full minute of sparklines
        using var rig = _rig = new FakeRig(seconds: 150);
        Configure();

        foreach (var name in Names)
        {
            if (only.Count > 0 && !only.Contains(name)) continue;
            try
            {
                var bmp = name switch
                {
                    "hero" => await Hero(),
                    "history" => await History(),
                    "overlay" => InGame(),
                    "overlay-editor" => await OverlayEditor(),
                    _ => await Sensors(),
                };
                var bytes = Stage.SavePng(bmp, Path.Combine(outDir, name + ".png"));
                var line = string.Create(CultureInfo.InvariantCulture, $"{name}.png: {bmp.PixelWidth}x{bmp.PixelHeight}, {bytes / 1024.0:0} KB");
                Report.AppendLine(line);
                Console.WriteLine(line);
            }
            catch (Exception ex)
            {
                Failures.Add($"{name}: {ex}");
                Console.Error.WriteLine($"{name} failed: {ex}");
            }
        }

        foreach (var (title, list) in new[] { ("Warnings", Warnings), ("Failures", Failures) })
        {
            if (list.Count == 0) continue;
            Report.AppendLine().AppendLine(title + ":");
            foreach (var p in list.Distinct()) Report.AppendLine("  " + p);
        }
        File.WriteAllText(Path.Combine(outDir, "report.txt"), Report.ToString());
        foreach (var w in Warnings.Distinct()) Console.WriteLine("warning: " + w);
        return Failures.Count > 0 ? 1 : 0;
    }

    /// <summary>The app's services on the fake PC, the key sensors recorded, the overlay set up, pages on the pinned clock.</summary>
    private static void Configure()
    {
        var hub = _rig.Engine;
        var store = SettingsStore.I;
        var friendly = FriendlyCatalog.Resolve(hub.Hardware, hub.Sensors);
        store.Mutate("recording", s =>
        {
            s.Recording.Enabled = true;
            s.Recording.IntervalSeconds = 2;
            s.Recording.SensorIds = FriendlyCatalog.KeyPresetSensorIds(friendly);
            s.Recording.KeyPresetApplied = true;
        });
        store.Mutate("overlay", s =>
        {
            var layout = new OverlayLayout();
            foreach (var key in OverlayKeys)
            {
                var item = friendly.FirstOrDefault(f => f.Key == key) ?? throw new InvalidOperationException("no friendly item " + key);
                layout.Items.Add(new OverlayItem { Id = "readme-" + key, SensorIds = [.. item.SensorIds] });
            }
            layout.Placement.Anchor = OverlayAnchor.TopLeft;
            s.Overlay = layout;
            s.Overlay.Enabled = true;
        });

        AppServices.Hub = hub;
        AppServices.History = new PreviewHistory(hub, () => _rig.Now, Recorded, FakeRig.Zone);
        AppServices.WatchHistory = null;
        // the History page on the same pinned clock and time zone as the history
        PageRegistry.Register(new PageInfo(PageIds.History, "nav.history", "Icon.History", 20,
            ctx => new HistoryPage(ctx) { Clock = () => _rig.Now, Zone = FakeRig.Zone }) { Priority = 9 });
    }

    private static bool Recorded(string id)
    {
        var r = SettingsStore.I.Current.Recording;
        return r.Enabled && r.SensorIds.Contains(id);
    }

    // ───────────── 1. hero: the panel's Overview and the tray flyout ─────────────

    private static async Task<BitmapSource> Hero()
    {
        const double w = 1280, h = 800;
        BitmapSource panelShot, flyoutShot;
        Rect card;

        // the panel ends where the flyout begins (one card per row, four big values each)
        var panel = await OpenPanel(PageIds.Overview, 848, 680);
        try
        {
            var page = (OverviewPage)panel.PageElement(PageIds.Overview)!;
            page.OnNavigatedFrom(); // the fake PC stands still: no live ticks; the sparklines come from its last minutes
            foreach (var c in page.Cards)
            {
                foreach (var t in c.Tiles)
                {
                    t.Series.Clear();
                    t.Series.PushRange(_rig.Recent(t.Key.Sensor, t.Series.Capacity));
                }
            }
            page.Apply();
            await Settle(panel);
            Check(panel, "hero/panel");
            panelShot = Stage.Capture(panel);
        }
        finally
        {
            panel.Close();
        }

        // the flyout fills its sparklines from the history's live buffer on the real clock: give it the fake PC's last minute
        var history = AppServices.History;
        AppServices.History = null;
        var vm = new FlyoutViewModel(_rig.Engine);
        var flyout = new FlyoutWindow(vm) { Offscreen = true };
        try
        {
            FloatingPlacement.ShowOffscreen(flyout);
            await Settle(flyout);
            foreach (var t in vm.Tiles) t.Series.Clear();
            vm.Prefill(s => _rig.Recent(s, vm.Tiles[0].Series.Capacity));
            await Settle(flyout);
            Check(flyout, "hero/flyout");
            flyoutShot = Stage.Capture(flyout);
            card = flyout.Card.TransformToAncestor(flyout).TransformBounds(new Rect(0, 0, flyout.Card.ActualWidth, flyout.Card.ActualHeight));
        }
        finally
        {
            flyout.Close();
            AppServices.History = history;
        }

        var tray = TrayIconArt.Render((int)Math.Round(16 * Stage.Scale));
        var canvas = Stage.Desktop(w, h, taskbar: true, tray, trayActive: true, clock: FakeRig.LocalNow);
        Stage.Window(canvas, panelShot, 32, 32);
        // the flyout above the tray, 12 DIP from the screen edge and the taskbar (its bitmap carries the shadow margin)
        var fx = Snap(w - 12 - card.Right);
        var fy = Snap(h - Stage.TaskbarHeight - 12 - card.Bottom);
        Stage.Place(canvas, flyoutShot, fx, fy);
        var icon = Stage.TrayIconCenter(w, h);
        Stage.Pointer(canvas, new Point(icon.X + 3, icon.Y + 5));
        return Stage.Render(canvas, w, h);
    }

    // ───────────── 2. history: hours of data, a dragged range with min / avg / max ─────────────

    private static async Task<BitmapSource> History()
    {
        var panel = await OpenPanel(PageIds.History, 1120, 740);
        try
        {
            var page = (HistoryPage)panel.PageElement(PageIds.History)!;
            // three temperatures on one scale: CPU, GPU and the GPU's hot spot (instead of the default CPU load)
            page.TogglePicked(_rig.Sensor(ShowcasePc.Cpu, SensorKind.Load, "CPU Total"));
            page.TogglePicked(_rig.Sensor(ShowcasePc.Gpu, SensorKind.Temperature, "GPU Hot Spot"));
            page.ApplyPreset(RangePreset.Hour6, animate: false);
            await SettleHistory(panel, page);
            var now = _rig.Now;
            // the evening game (it started just before 8 PM): how hot did it get?
            page.Chart.SetSelection((now - TimeSpan.FromMinutes(126).Ticks, now - TimeSpan.FromMinutes(18).Ticks));
            await SettleHistory(panel, page);
            if (page.StatsPanel.Rows.Count == 0) Warnings.Add("history: no stats rows");
            Check(panel, "history");
            var shot = Stage.Capture(panel);

            // the pointer where the drag ended
            var plot = page.Chart.PlotRect;
            var sel = page.Chart.Selection!.Value;
            var view = page.Chart.View;
            var endX = plot.Left + (sel.To - view.From) / (double)(view.To - view.From) * plot.Width;
            var tip = page.Chart.TransformToAncestor(panel).Transform(new Point(endX, plot.Top + plot.Height * 0.42));

            const double margin = 32;
            var canvas = Stage.Desktop(shot.PixelWidth / Stage.Scale + 2 * margin, shot.PixelHeight / Stage.Scale + 2 * margin, taskbar: false);
            Stage.Window(canvas, shot, margin, margin);
            Stage.Pointer(canvas, new Point(margin + tip.X, margin + tip.Y));
            return Stage.Render(canvas, canvas.Width, canvas.Height);
        }
        finally
        {
            panel.Close();
        }
    }

    private static async Task SettleHistory(Window panel, HistoryPage page)
    {
        await PumpUntil(() => (page.PendingQuery?.IsCompleted ?? true) && (page.PendingStats?.IsCompleted ?? true), 10_000);
        await Settle(panel, 120);
        await PumpUntil(() => (page.PendingQuery?.IsCompleted ?? true) && (page.PendingStats?.IsCompleted ?? true), 10_000);
        await Settle(panel, 60);
        page.Chart.RenderNow();
    }

    // ───────────── 3. the overlay in a game ─────────────

    private static BitmapSource InGame()
    {
        // the upper three quarters of a 16:9 game frame (sky, sun, horizon, crosshair) at 1280 × 720 px, the overlay at
        // 1.4× its 1080p size (like the size slider at about 150 %), so it stays readable at the README's width
        const double gw = 1024, gh = 576, crop = 0.75, overlayScale = 1.4;
        var scene = new GameScene { Aspect = 16.0 / 9, Viewport = new Rect(0.1, 0, crop, crop) };
        var game = Stage.Render(scene, gw, gh);

        // the overlay of the other pictures, with a small FPS graph (an advanced option)
        var layout = LayoutJson.Clone(SettingsStore.I.Current.Overlay);
        layout.Items[0].ShowGraph = true;
        var values = PreviewValues(layout);
        var theme = ThemeManager.I.ResolveOverlayTheme();
        var monitorHeight = (int)Math.Round(OverlayPlacementMath.ReferenceHeight * overlayScale);
        var overlay = OverlayHost.RenderToBitmap(layout, theme, values, monitorHeight, Loc.I.Culture, TemperatureUnit.Celsius);
        var overlayBmp = BitmapSource.Create(overlay.Width, overlay.Height, 96 * Stage.Scale, 96 * Stage.Scale, PixelFormats.Pbgra32, null, overlay.Pixels, overlay.Stride);
        overlayBmp.Freeze();

        // placed by the overlay's own rule (anchor + margin, scaled like the overlay), on the frame's pixels
        var scale = OverlayPlacementMath.Scale(layout.Style, monitorHeight);
        var area = new PixelRect(0, 0, game.PixelWidth, game.PixelHeight);
        var (ox, oy) = OverlayPlacementMath.Position(layout.Placement, area, overlay.Width, overlay.Height, scale);

        var frame = new Canvas { Width = gw, Height = gh };
        Stage.Place(frame, game, 0, 0);
        Stage.Place(frame, overlayBmp, ox / Stage.Scale, oy / Stage.Scale);
        var composed = Stage.Render(frame, gw, gh);

        const double margin = 32;
        var canvas = Stage.Desktop(gw + 2 * margin, gh + 2 * margin, taskbar: false);
        Stage.Window(canvas, composed, margin, margin);
        return Stage.Render(canvas, canvas.Width, canvas.Height);
    }

    /// <summary>The overlay's sensors with their current value and the last 40 seconds (sparklines, aggregation).</summary>
    private static List<PreviewSensor> PreviewValues(OverlayLayout layout)
    {
        var result = new List<PreviewSensor>();
        foreach (var id in layout.Items.SelectMany(i => i.SensorIds).Distinct(StringComparer.Ordinal))
        {
            var s = _rig.Engine.Find(id) ?? throw new InvalidOperationException("unknown sensor " + id);
            var value = _rig.Engine.Latest[s.Index];
            if (float.IsNaN(value)) Warnings.Add($"overlay: {s.Name} has no value");
            result.Add(new PreviewSensor(s, value, _rig.Recent(s, 40)));
        }
        return result;
    }

    // ───────────── 4. the overlay editor, simple mode ─────────────

    private static async Task<BitmapSource> OverlayEditor()
    {
        // a bit taller than the default, so the close-up of the preview shows the whole overlay
        var panel = await OpenPanel(PageIds.Overlay, 1120, 840);
        try
        {
            var page = (OverlayEditorPage)panel.PageElement(PageIds.Overlay)!;
            page.Prefill(s => _rig.Recent(s, PreviewFeed.HistoryLength));
            // "What to show" on the GPU tab: the overlay's GPU values (Hot Spot among them) picked
            var gpu = Descendants<RadioButton>(page.Simple ?? throw new InvalidOperationException("the editor is not in simple mode"))
                .FirstOrDefault(r => Descendants<TextBlock>(r).Any(t => t.Text == "GPU"))
                ?? throw new InvalidOperationException("no GPU tab in the simple editor");
            gpu.IsChecked = true;
            await Settle(panel, 80);
            page.TickNow();
            await PumpUntil(() => !page.Preview.IsRendering && page.Preview.RenderCount > 0, 15_000);
            await PumpUntil(() => FontSamples.Pending == 0, 15_000);
            await Settle(panel, 120);
            Check(panel, "overlay-editor");
            var shot = Stage.Capture(panel);

            const double margin = 32;
            var canvas = Stage.Desktop(shot.PixelWidth / Stage.Scale + 2 * margin, shot.PixelHeight / Stage.Scale + 2 * margin, taskbar: false);
            Stage.Window(canvas, shot, margin, margin);
            return Stage.Render(canvas, canvas.Width, canvas.Height);
        }
        finally
        {
            panel.Close();
        }
    }

    // ───────────── 5. sensors: what to record and show, with the explanation bubble ─────────────

    private static async Task<BitmapSource> Sensors()
    {
        var panel = await OpenPanel(PageIds.Sensors, 1120, 780);
        try
        {
            var page = (SensorsPage)panel.PageElement(PageIds.Sensors)!;
            // the CPU folded, so the graphics card's sensors come first
            page.Model.Sections.First(s => s.Group.Kind == HardwareKind.Cpu).ToggleCollapsed();
            await Settle(panel, 120);
            page.Apply();
            await Settle(panel, 80);

            var hotspot = _rig.Sensor(ShowcasePc.Gpu, SensorKind.Temperature, "GPU Hot Spot");
            var anchor = Descendants<FrameworkElement>(panel)
                .FirstOrDefault(e => e.IsVisible && ReferenceEquals(BetterVitals.UI.Explain.Explain.GetSensor(e), hotspot))
                ?? throw new InvalidOperationException("the GPU Hot Spot row is not visible");
            var name = anchor.TransformToAncestor(panel).TransformBounds(new Rect(0, 0, anchor.ActualWidth, anchor.ActualHeight));
            var content = ExplainService.Resolve(anchor) ?? throw new InvalidOperationException("no explanation for GPU Hot Spot");
            Check(panel, "sensors");
            var shot = Stage.Capture(panel);

            // the bubble as it opens under the hovered name (same card, theme and shadow as the app's)
            var card = new ExplainCard();
            card.SetContent(content);
            card.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.35, Color = ThemeManager.ShadowColor };
            const double pad = 12; // room for the card's shadow, like the bubble window's margin
            var holder = new Border { Padding = new Thickness(pad), Child = card };
            holder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            holder.Arrange(new Rect(holder.DesiredSize));
            var bubble = Stage.Capture(holder);

            const double margin = 32;
            var canvas = Stage.Desktop(shot.PixelWidth / Stage.Scale + 2 * margin, shot.PixelHeight / Stage.Scale + 2 * margin, taskbar: false);
            Stage.Window(canvas, shot, margin, margin);
            // where the app puts it: under the name (6 DIP gap), flipped above when the screen ends
            var at = new ExplainPlacement.PxRect(margin + name.Left, margin + name.Top, name.Width, name.Height);
            var (bx, by, _) = ExplainPlacement.Compute(at, card.ActualWidth, card.ActualHeight,
                new ExplainPlacement.PxRect(0, 0, canvas.Width, canvas.Height), 6);
            Stage.Place(canvas, bubble, Snap(bx - pad), Snap(by - pad));
            Stage.Pointer(canvas, new Point(margin + name.Left + Math.Min(name.Width * 0.62, 78), margin + name.Top + name.Height * 0.62));
            return Stage.Render(canvas, canvas.Width, canvas.Height);
        }
        finally
        {
            panel.Close();
        }
    }

    // ───────────── windows ─────────────

    private static async Task<PanelWindow> OpenPanel(string pageId, double width, double height)
    {
        var panel = new PanelWindow { Width = width, Height = height };
        FloatingPlacement.ShowOffscreen(panel);
        panel.Navigate(pageId, null);
        await Settle(panel, 120);
        return panel;
    }

    private static async Task Settle(Window w, int delayMs = 80)
    {
        for (var i = 0; i < 2; i++)
        {
            await w.Dispatcher.InvokeAsync(w.UpdateLayout, DispatcherPriority.Background);
            await Task.Delay(delayMs);
            await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        w.UpdateLayout();
    }

    private static async Task PumpUntil(Func<bool> done, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!done())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("the page did not settle");
            await Task.Delay(20);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
    }

    /// <summary>On the pixel grid of the output (multiples of 1 / <see cref="Stage.Scale"/>).</summary>
    private static double Snap(double dip) => Math.Round(dip * Stage.Scale) / Stage.Scale;

    // ───────────── checks ─────────────

    /// <summary>Visible single-line text that is cut off or trimmed (reported, the picture is still written).</summary>
    private static void Check(Window w, string name)
    {
        var dpi = VisualTreeHelper.GetDpi(w).PixelsPerDip;
        foreach (var tb in Descendants<TextBlock>(w))
        {
            if (!tb.IsVisible || string.IsNullOrEmpty(tb.Text) || tb.ActualWidth <= 0 || tb.TextWrapping != TextWrapping.NoWrap) continue;
            var ft = new FormattedText(tb.Text, CultureInfo.CurrentUICulture, tb.FlowDirection,
                new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, Brushes.Black, dpi);
            var need = ft.WidthIncludingTrailingWhitespace + tb.Padding.Left + tb.Padding.Right;
            if (need > tb.ActualWidth + 1.5) Warnings.Add($"{name}: text \"{tb.Text}\" needs {need:0} DIP, has {tb.ActualWidth:0}");
        }
        foreach (var tb in Descendants<TextBlock>(w))
        {
            if (tb.IsVisible && tb.Text is "—" or "NaN") Warnings.Add($"{name}: text \"{tb.Text}\" (a missing value)");
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }
}
