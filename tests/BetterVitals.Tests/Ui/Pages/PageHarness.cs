using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.Sensors.Derived;
using BetterVitals.Sensors.Engine;
using BetterVitals.Sensors.Fake;
using BetterVitals.UI;
using BetterVitals.UI.Pages.Shared;
using BetterVitals.UI.Themes;
using BetterVitals.UI.Toasts;

namespace BetterVitals.Tests.Ui.Pages;

/// <summary>A deterministic engine over a fake PC, driven by a test clock (no thread).</summary>
internal sealed class FakePc : IDisposable
{
    private long _now = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc).Ticks;
    private readonly IDemand _all;

    public FakePc(FakeProfile profile = FakeProfile.DesktopAmdNvidia)
    {
        Fake = new FakeSensorSource(profile, 1, () => _now);
        Engine = new SensorEngine([new DerivedSource(), Fake], () => _now);
        Engine.DiscoverNow();
        // everything read every second, like a recorder that records all (fills values and session stats)
        _all = Engine.Demand("test", Engine.Sensors.Select(s => s.Id), TimeSpan.FromSeconds(1));
        Engine.ApplyDemandNow();
    }

    public SensorEngine Engine { get; }

    public FakeSensorSource Fake { get; }

    public void Run(TimeSpan duration)
    {
        var end = _now + duration.Ticks;
        while (_now < end)
        {
            _now += SensorEngine.BaseTick.Ticks;
            Engine.ApplyDemandNow();
            Engine.Tick(_now);
        }
    }

    /// <summary>Re-discovers now (after SetPresent) — what the engine does after its debounce.</summary>
    public void Rediscover()
    {
        Engine.DiscoverNow();
        Engine.ApplyDemandNow();
    }

    public SensorDescriptor Sensor(string hardwareName, SensorKind kind, string name)
        => Engine.Sensors.Single(s => s.Name == name && s.Kind == kind && Engine.FindHardware(s.HardwareId)!.Name == hardwareName);

    public HardwareDescriptor Hardware(string name) => Engine.Hardware.Single(h => h.Name == name);

    public void Dispose()
    {
        _all.Dispose();
        Engine.Dispose();
    }
}

/// <summary>Settings store, toasts and navigations recorded for a test; restores the language.</summary>
internal sealed class PageEnv : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bv-pages-" + Guid.NewGuid().ToString("N"));
    private readonly string _language = Loc.I.Language;

    public PageEnv(AppSettings? settings = null, string language = "en")
    {
        Directory.CreateDirectory(_dir);
        Store = SettingsStore.ForTests(Path.Combine(_dir, "settings.json"), settings ?? new AppSettings());
        Exclusions = new RecordingExclusions(Path.Combine(_dir, "recording-removed.json"));
        Actions = new SensorActions(() => Store, (t, k, a, act) => Toasts.Add((t, k, a, act)), (p, x) => Navigations.Add((p, x)), Exclusions);
        Loc.I.SetLanguage(language);
        Motion.I.Update(false);
    }

    public SettingsStore Store { get; }

    public RecordingExclusions Exclusions { get; }

    public SensorActions Actions { get; }

    public List<(string Text, ToastKind Kind, string? ActionText, Action? Action)> Toasts { get; } = [];

    public List<(string Page, object? Parameter)> Navigations { get; } = [];

    /// <summary>Clicks Undo on the last toast.</summary>
    public void Undo() => Toasts[^1].Action!.Invoke();

    public void Dispose()
    {
        Loc.I.SetLanguage(_language);
        try { Directory.Delete(_dir, true); } catch { /* temp */ }
    }
}

/// <summary>
/// Hosts an element in a hidden HwndSource — a window that is never shown or activated (Loaded and layout run as in the
/// panel, nothing reaches the screen) — with the app's theme and base styles, and renders it to PNG.
/// </summary>
internal sealed class HiddenHost : IDisposable
{
    private readonly HwndSource _source;

    public HiddenHost(FrameworkElement content, double width, double height, string theme = "aurora")
    {
        Root = new Border { Width = width, Height = height, Child = content, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        Root.SetResourceReference(Border.BackgroundProperty, "Brush.Bg");
        var res = new ResourceDictionary();
        res.MergedDictionaries.Add(ThemeManager.Build(Palettes.ById(theme), mica: false));
        res.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/BetterVitals;component/UI/Themes/Base.xaml", UriKind.Relative)));
        Root.Resources = res;
        Root.Language = Loc.I.XmlLang;
        TextElementFont(Root);

        var p = new HwndSourceParameters("bv-test-host")
        {
            PositionX = -32000,
            PositionY = -32000,
            // in device pixels: room for the root at any system DPI (the root has its own size)
            Width = (int)(width * 3),
            Height = (int)(height * 3),
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, no WS_VISIBLE: never shown
            UsesPerPixelOpacity = false,
        };
        _source = new HwndSource(p) { RootVisual = Root, SizeToContent = SizeToContent.Manual };
        Settle();
    }

    public Border Root { get; }

    public void Settle(int ms = 60)
    {
        Root.UpdateLayout();
        Sta.Pump(ms);
        Root.UpdateLayout();
    }

    /// <summary>Renders at 1.25× (like the snapshot mode) to tests/output/pages/&lt;name&gt;.png; returns the path.</summary>
    public string Save(string name)
    {
        Settle();
        const double scale = 1.25;
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(Root.ActualWidth * scale), (int)Math.Ceiling(Root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(Root);
        var dir = Path.Combine(OutputDir(), "pages");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".png");
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(path)) enc.Save(fs);
        return path;
    }

    public void Dispose()
    {
        _source.RootVisual = null; // Unloaded runs, like a closing panel
        _source.Dispose();
    }

    private static void TextElementFont(FrameworkElement e)
    {
        System.Windows.Documents.TextElement.SetFontFamily(e, new FontFamily("Segoe UI Variable Text, Segoe UI"));
        System.Windows.Documents.TextElement.SetFontSize(e, 13);
    }

    /// <summary>tests/output of the repository (next to the test project).</summary>
    public static string OutputDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "BetterVitals.sln"))) return Path.Combine(d.FullName, "tests", "output");
        return Path.Combine(dir, "output");
    }

    public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
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
