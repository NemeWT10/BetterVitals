using System.IO;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterVitals.Core;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.Services;
using BetterVitals.UI.Controls;
using BetterVitals.UI.Explain;
using BetterVitals.UI.Flyout;
using BetterVitals.UI.Panel;
using BetterVitals.UI.Themes;
using BetterVitals.UI.Toasts;
using BetterVitals.UI.Tray;

namespace BetterVitals.UI.Dev;

/// <summary>
/// <c>--snapshots &lt;dir&gt; [lang] [theme]</c>: visual QA without a screen. Drives the app programmatically (no input, nothing
/// is read from the screen) and renders every window and page from its own visual tree (RenderTargetBitmap at 1.25×) into
/// PNGs — flyout, tray menu, every panel page, the style gallery, the explanation bubble, a toast, the icon sheet — in each
/// theme and language. Windows sit at -32000 and are never activated; run it on the hidden desktop (tools\snapshot.ps1).
/// Writes report.txt (missing Loc keys, Loc table problems, clipped / overflowing text) and index.txt, then quits.
/// </summary>
internal static partial class SnapshotMode
{
    private const double Scale = 1.25;
    private static string _dir = "";
    private static readonly List<string> Files = [];
    private static readonly List<string> Failures = [];
    private static readonly List<string> Layout = [];

    public static void Start(string dir, string? language, string? theme)
    {
        _dir = Path.GetFullPath(dir);
        Directory.CreateDirectory(_dir);
        PageRegistry.Register(new PageInfo(GalleryPage.Id, "dev.gallery", "Icon.Sparkle", 999, _ => new GalleryPage()) { InNav = false, Priority = 0 });
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, async () =>
        {
            try
            {
                await RunAll(language, theme);
            }
            catch (Exception ex)
            {
                Failures.Add("exception: " + ex);
            }
            Finish();
        });
    }

    /// <summary>An unhandled UI exception during the run counts as a failure.</summary>
    public static void ReportCrash(Exception ex) => Failures.Add("ui exception: " + ex.Message);

    private static async Task RunAll(string? language, string? theme)
    {
        var store = SettingsStore.I;
        // end states only (no half-faded entrances); a recording set so the footers show it
        store.Mutate("general", s => s.General.Animations = false);
        store.Mutate("recording", s =>
        {
            s.Recording.Enabled = true;
            foreach (var x in AppServices.Hub.Sensors.Take(14)) s.Recording.SensorIds.Add(x.Id);
        });
        store.Mutate("overlay", s => s.Overlay.Enabled = true);
        (AppServices.Hub as PreviewHub)?.Warm(80);

        await Guard("icons", IconSheet);

        var themes = theme is null ? Palettes.All.Select(p => p.Id).ToArray() : [theme];
        var languages = language is null ? new[] { "en", "pl" } : [language];
        foreach (var id in themes)
        {
            foreach (var lang in languages)
            {
                store.Mutate("general", s =>
                {
                    s.General.Theme = id;
                    s.General.Language = lang;
                });
                await Pump(120);
                var tag = $"{id}_{lang}";
                await Guard("flyout " + tag, () => FlyoutShot(tag));
                await Guard("menu " + tag, () => MenuShot(tag));
                await Guard("panel " + tag, () => PanelShots(tag));
            }
        }
        WriteReport();
    }

    private static async Task Guard(string what, Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (Exception ex)
        {
            Failures.Add($"{what}: {ex}");
        }
    }

    // ───────────── scenarios ─────────────

    private static async Task FlyoutShot(string tag)
    {
        var vm = new FlyoutViewModel(AppServices.Hub);
        if (AppServices.Hub is PreviewHub hub) vm.Prefill(s => hub.Recent(s.Index, 48));
        var w = new FlyoutWindow(vm) { Offscreen = true };
        try
        {
            FloatingPlacement.ShowOffscreen(w);
            await Settle(w);
            Check(w, "flyout_" + tag);
            SaveOnBackdrop(w, "flyout_" + tag);
        }
        finally
        {
            w.Close();
        }
    }

    private static async Task MenuShot(string tag)
    {
        var w = new TrayMenuWindow { Offscreen = true };
        try
        {
            FloatingPlacement.ShowOffscreen(w);
            await Settle(w);
            Check(w, "traymenu_" + tag);
            SaveOnBackdrop(w, "traymenu_" + tag);
        }
        finally
        {
            w.Close();
        }
    }

    private static async Task PanelShots(string tag)
    {
        var w = new PanelWindow { Width = 1120, Height = 740 };
        try
        {
            FloatingPlacement.ShowOffscreen(w);
            foreach (var page in PageRegistry.All.Append(PageRegistry.Find(GalleryPage.Id)!).Distinct())
            {
                w.Navigate(page.Id, null);
                await Settle(w, 120);
                var name = $"panel_{page.Id}_{tag}";
                Check(w, name);
                Save(w, name);
            }

            // the explanation bubble over the settings page, as it would sit under "Parameter explanations"
            w.Navigate(PageIds.Settings, null);
            await Settle(w, 80);
            var sensor = AppServices.Hub.Sensors.FirstOrDefault(s => s.Name == "GPU Hot Spot");
            var content = sensor is not null && ExplainService.Resolve(SensorProbe(sensor)) is { } fromGlossary
                ? fromGlossary
                : new ExplainContent(sensor?.Name ?? "GPU Hot Spot", Loc.T("settings.explanations.info"), Loc.T("settings.explanations.desc"),
                    Loc.T("sensor.note.unofficialHotspot"));
            var card = new ExplainCard();
            card.SetContent(content);
            card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.35, Color = ThemeManager.ShadowColor };
            var holder = new Border { Padding = new Thickness(12), Child = card };
            // above the ⓘ of "Parameter explanations" (the bubble flips up when there is no room below)
            SaveWithOverlay(w, holder, new Point(470, 452), "explain_" + tag);

            w.Navigate(PageIds.Settings, SettingsPageParameter.About);
            await Settle(w, 120);
            Save(w, "panel_settings-about_" + tag);

            // a toast with an action
            w.Toast(Loc.T("toast.overlayOn"), ToastKind.Success, Loc.T("common.undo"), () => { });
            await Settle(w, 80);
            Save(w, "toast_" + tag);
        }
        finally
        {
            w.Close();
        }
    }

    /// <summary>An unrendered element carrying the sensor, so the glossary text can be resolved like a hover would.</summary>
    private static FrameworkElement SensorProbe(Core.Sensors.SensorDescriptor s)
    {
        var e = new Border();
        Explain.Explain.SetSensor(e, s);
        return e;
    }

    /// <summary>Every icon of Icons.xaml in a labelled grid, plus the tray icon at 16 / 20 / 24 / 32 px.</summary>
    private static Task IconSheet()
    {
        var keys = Application.Current.Resources.MergedDictionaries
            .SelectMany(Flatten)
            .SelectMany(d => d.Keys.OfType<string>().Where(k => k.StartsWith("Icon.", StringComparison.Ordinal) && d[k] is Geometry).Select(k => (k, (Geometry)d[k])))
            .DistinctBy(x => x.k)
            .ToList();
        const int cols = 8;
        const double cell = 120;
        var rows = (keys.Count + cols - 1) / cols + 1;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0F, 0x11, 0x17)), null, new Rect(0, 0, cols * cell, rows * cell));
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xEE, 0xF0, 0xF7)), 1.5 / (40 / 24.0)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 0.5 / (40 / 24.0));
            for (var i = 0; i < keys.Count; i++)
            {
                var (key, g) = keys[i];
                var x = i % cols * cell;
                var y = i / cols * cell;
                dc.PushTransform(new TranslateTransform(x + (cell - 40) / 2, y + 18));
                dc.PushTransform(new ScaleTransform(40 / 24.0, 40 / 24.0));
                dc.DrawRectangle(null, grid, new Rect(0, 0, 24, 24));
                dc.DrawGeometry(null, pen, g);
                dc.Pop();
                dc.Pop();
                var label = new FormattedText(key[5..], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10,
                    new SolidColorBrush(Color.FromRgb(0xA2, 0xA8, 0xB8)), 1.0) { MaxTextWidth = cell - 6, TextAlignment = TextAlignment.Center };
                dc.DrawText(label, new Point(x + 3, y + 70));
            }
            var ty = (rows - 1) * cell + 20;
            var tx = 16.0;
            foreach (var size in new[] { 16, 20, 24, 32, 48 })
            {
                dc.DrawImage(TrayIconArt.Render(size), new Rect(tx, ty, size, size));
                tx += size + 24;
            }
        }
        var bmp = new RenderTargetBitmap((int)(cols * cell * Scale), (int)(rows * cell * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bmp.Render(dv);
        WritePng(bmp, "icons");
        return Task.CompletedTask;
    }

    private static IEnumerable<ResourceDictionary> Flatten(ResourceDictionary d) => d.MergedDictionaries.SelectMany(Flatten).Prepend(d);

    // ───────────── rendering ─────────────

    private static async Task Pump(int ms)
    {
        await Task.Delay(ms);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
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

    private static void Save(FrameworkElement element, string name)
    {
        element.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * Scale), (int)Math.Ceiling(element.ActualHeight * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bmp.Render(element);
        WritePng(bmp, name);
    }

    /// <summary>Transparent windows (flyout, menu) over a wallpaper-like backdrop, so their glass and shadow show.</summary>
    private static void SaveOnBackdrop(Window w, string name)
    {
        w.UpdateLayout();
        var width = w.ActualWidth + 80;
        var height = w.ActualHeight + 60;
        var front = new RenderTargetBitmap((int)Math.Ceiling(w.ActualWidth * Scale), (int)Math.Ceiling(w.ActualHeight * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        front.Render(w);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var dark = ThemeManager.I.Current.IsDark;
            var bg = new LinearGradientBrush(
                dark ? Color.FromRgb(0x1C, 0x22, 0x38) : Color.FromRgb(0xC9, 0xD6, 0xEE),
                dark ? Color.FromRgb(0x3A, 0x24, 0x48) : Color.FromRgb(0xE9, 0xD5, 0xE6), 35);
            dc.DrawRectangle(bg, null, new Rect(0, 0, width, height));
            dc.DrawImage(front, new Rect(40, 30, w.ActualWidth, w.ActualHeight));
        }
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(width * Scale), (int)Math.Ceiling(height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bmp.Render(dv);
        WritePng(bmp, name);
    }

    /// <summary>A window with a floating element (bubble) drawn over it at <paramref name="at"/>.</summary>
    private static void SaveWithOverlay(Window w, FrameworkElement overlay, Point at, string name)
    {
        w.UpdateLayout();
        overlay.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        overlay.Arrange(new Rect(overlay.DesiredSize));
        overlay.UpdateLayout();
        var back = new RenderTargetBitmap((int)Math.Ceiling(w.ActualWidth * Scale), (int)Math.Ceiling(w.ActualHeight * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        back.Render(w);
        var front = new RenderTargetBitmap((int)Math.Ceiling(overlay.ActualWidth * Scale), (int)Math.Ceiling(overlay.ActualHeight * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        front.Render(overlay);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(back, new Rect(0, 0, w.ActualWidth, w.ActualHeight));
            dc.DrawImage(front, new Rect(at.X, at.Y, overlay.ActualWidth, overlay.ActualHeight));
        }
        var bmp = new RenderTargetBitmap(back.PixelWidth, back.PixelHeight, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bmp.Render(dv);
        WritePng(bmp, name);
    }

    private static void WritePng(BitmapSource bmp, string name)
    {
        var path = Path.Combine(_dir, name + ".png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(path)) encoder.Save(fs);
        Files.Add(name + ".png");
    }

    // ───────────── layout checks ─────────────

    /// <summary>Visible single-line text cut off (or trimmed with an ellipsis), elements reaching past the window.</summary>
    private static void Check(Window w, string name)
    {
        var root = w.Content as FrameworkElement;
        if (root is null) return;
        var dpi = VisualTreeHelper.GetDpi(w).PixelsPerDip;
        foreach (var tb in Descendants<TextBlock>(w))
        {
            if (!tb.IsVisible || string.IsNullOrEmpty(tb.Text) || tb.ActualWidth <= 0) continue;
            if (tb.TextWrapping != TextWrapping.NoWrap) continue;
            var ft = new FormattedText(tb.Text, CultureInfo.CurrentUICulture, tb.FlowDirection,
                new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, Brushes.Black, dpi);
            var need = ft.WidthIncludingTrailingWhitespace + tb.Padding.Left + tb.Padding.Right;
            if (need > tb.ActualWidth + 1.5)
                Layout.Add($"{name}: {(tb.TextTrimming == TextTrimming.None ? "clipped" : "trimmed")} text \"{Short(tb.Text)}\" needs {need:0} px, has {tb.ActualWidth:0}");
        }
        foreach (var fe in Descendants<FrameworkElement>(w))
        {
            if (!fe.IsVisible || fe.ActualWidth <= 0 || InsideScroller(fe, root)) continue;
            Rect r;
            try
            {
                r = fe.TransformToAncestor(root).TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
            }
            catch (InvalidOperationException)
            {
                continue;
            }
            if (r.Right > root.ActualWidth + 1 || r.Bottom > root.ActualHeight + 1)
                Layout.Add($"{name}: {fe.GetType().Name} {(string.IsNullOrEmpty(fe.Name) ? "" : "'" + fe.Name + "' ")}reaches past the window ({r.Right:0}×{r.Bottom:0} of {root.ActualWidth:0}×{root.ActualHeight:0})");
        }
    }

    private static bool InsideScroller(DependencyObject d, DependencyObject root)
    {
        for (var p = VisualTreeHelper.GetParent(d); p is not null && p != root; p = VisualTreeHelper.GetParent(p))
            if (p is ScrollContentPresenter) return true;
        return false;
    }

    private static string Short(string s) => s.Length <= 48 ? s : s[..47] + "…";

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

    // ───────────── report ─────────────

    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex Placeholder();

    private static void WriteReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"BetterVitals snapshot report — {AppEnv.Version}, {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"{Files.Count} PNG files");
        sb.AppendLine();

        var missing = Loc.MissingKeys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        sb.AppendLine($"Missing Loc keys ({missing.Count}):");
        foreach (var k in missing) sb.AppendLine("  " + k);
        sb.AppendLine();

        var table = new List<string>();
        foreach (var (key, (pl, en)) in Loc.AllEntries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(pl) || string.IsNullOrWhiteSpace(en)) table.Add($"{key}: empty translation");
            var a = Placeholder().Matches(pl).Select(m => m.Groups[1].Value).Order().ToArray();
            var b = Placeholder().Matches(en).Select(m => m.Groups[1].Value).Order().ToArray();
            if (!a.SequenceEqual(b)) table.Add($"{key}: placeholders differ (pl {string.Join(",", a)} / en {string.Join(",", b)})");
        }
        sb.AppendLine($"Loc table problems ({table.Count}):");
        foreach (var t in table) sb.AppendLine("  " + t);
        sb.AppendLine();

        var layout = Layout.Distinct().ToList();
        sb.AppendLine($"Layout problems ({layout.Count}):");
        foreach (var l in layout) sb.AppendLine("  " + l);
        sb.AppendLine();

        sb.AppendLine("Contrast (WCAG, text on its background):");
        foreach (var p in Palettes.All)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"  {p.Id,-9} Text/Bg {ThemeManager.Contrast(p.Text, p.Bg):0.0}  TextDim/Bg {ThemeManager.Contrast(p.TextDim, p.Bg):0.0}  TextFaint/Bg {ThemeManager.Contrast(p.TextFaint, p.Bg):0.0}  " +
                $"TextDim/Surface {ThemeManager.Contrast(p.TextDim, p.Surface):0.0}  OnAccent/fill min {ThemeManager.FillStops(p).Min(c => ThemeManager.Contrast(p.OnAccent, c)):0.0}"));
        }
        File.WriteAllText(Path.Combine(_dir, "report.txt"), sb.ToString());
    }

    private static void Finish()
    {
        try
        {
            File.WriteAllLines(Path.Combine(_dir, "index.txt"), Files);
            var failed = Path.Combine(_dir, "FAILED.txt");
            if (Failures.Count > 0) File.WriteAllLines(failed, Failures);
            else File.Delete(failed);
        }
        catch (Exception ex)
        {
            Log.Error("snapshot: cannot write index", ex);
        }
        if (Failures.Count == 0) Log.Info($"SNAPSHOT OK ({Files.Count} files)");
        else foreach (var f in Failures) Log.Error("SNAPSHOT FAIL: " + f);
        Application.Current.Shutdown(Failures.Count == 0 ? 0 : 1);
    }
}
