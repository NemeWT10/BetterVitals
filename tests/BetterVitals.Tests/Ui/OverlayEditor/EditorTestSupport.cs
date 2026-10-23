using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterVitals.Core.Catalog;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.Sensors.Derived;
using BetterVitals.Sensors.Engine;
using BetterVitals.Sensors.Fake;
using BetterVitals.UI.Pages.OverlayEditor;
using BetterVitals.UI.Themes;

namespace BetterVitals.Tests.Ui.OverlayEditor;

/// <summary>
/// The overlay editor under test: a fake-sensor engine (realistic names and values, deterministic clock), an in-memory
/// settings store in a temp folder, and the page rendered offscreen from its own visual tree (never shown, never the
/// screen). PNGs go to tests/output/overlay-editor.
/// </summary>
internal sealed class EditorFixture : IDisposable
{
    private long _now = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc).Ticks;
    private readonly string _dir;
    private readonly string? _language;

    public EditorFixture(string language = "en", string theme = "aurora", AppSettings? settings = null)
    {
        _dir = Path.Combine(Path.GetTempPath(), "bv-oved-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Store = SettingsStore.ForTests(Path.Combine(_dir, "settings.json"), settings ?? new AppSettings());
        _language = Loc.I.Language;
        Loc.I.SetLanguage(language);
        ThemeManager.I.Apply(theme, mica: false);
        Engine = new SensorEngine([new DerivedSource(), new FakeSensorSource(FakeProfile.DesktopAmdNvidia, 1, () => _now)], () => _now);
        Engine.DiscoverNow();
        Demand = Engine.Demand("test", Engine.Sensors.Select(s => s.Id), TimeSpan.FromSeconds(1));
        Advance(6);
        Friendly = FriendlyCatalog.Resolve(Engine.Hardware, Engine.Sensors);
    }

    public SettingsStore Store { get; }

    public SensorEngine Engine { get; }

    public IDisposable Demand { get; }

    public IReadOnlyList<FriendlyItem> Friendly { get; }

    public string ProfileDir => Path.Combine(_dir, "profiles");

    /// <summary>Runs the engine for <paramref name="seconds"/> (fake clock).</summary>
    public void Advance(int seconds)
    {
        for (var i = 0; i < seconds * 4; i++)
        {
            _now += SensorEngine.BaseTick.Ticks;
            Engine.ApplyDemandNow();
            Engine.Tick(_now);
        }
    }

    public FriendlyItem F(string key) => Friendly.First(f => f.Key == key);

    public OverlayEditorPage Page()
    {
        var page = new OverlayEditorPage(null, Engine, Store, ProfileDir) { AssumeVisible = true };
        return page;
    }

    public void Dispose()
    {
        Demand.Dispose();
        Engine.Dispose();
        Store.Flush();
        if (_language is not null) Loc.I.SetLanguage(_language);
        ThemeManager.I.Apply("aurora", mica: false);
        try { Directory.Delete(_dir, true); } catch { /* temp */ }
    }
}

/// <summary>Hosts an element with the app's theme + Base.xaml resources and renders it to PNG.</summary>
internal static class Offscreen
{
    public const double Scale = 1.25;

    public static string OutputDir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BetterVitals.sln"))) dir = dir.Parent;
            var path = Path.Combine(dir?.FullName ?? AppContext.BaseDirectory, "tests", "output", "overlay-editor");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <summary>A page-sized host like the panel's page area (theme palette + base styles as its resources).</summary>
    public static Border Host(FrameworkElement content, double width, double height)
    {
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(ThemeManager.Build(ThemeManager.I.Current, mica: false));
        resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/BetterVitals;component/UI/Themes/Base.xaml", UriKind.Relative)));
        var host = new Border { Width = width, Height = height, Resources = resources, Child = content };
        host.SetResourceReference(Border.BackgroundProperty, "Brush.Bg");
        TextElement(host);
        Layout(host);
        return host;
    }

    private static void TextElement(Border host)
    {
        host.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, new FontFamily("Segoe UI Variable Text, Segoe UI"));
        host.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, 13.0);
        host.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Brush.Text");
    }

    public static void Layout(FrameworkElement e)
    {
        e.Measure(new Size(e.Width, e.Height));
        e.Arrange(new Rect(0, 0, e.Width, e.Height));
        e.UpdateLayout();
    }

    public static string Save(FrameworkElement element, string name)
    {
        Layout(element);
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * Scale), (int)Math.Ceiling(element.ActualHeight * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bmp.Render(element);
        var path = Path.Combine(OutputDir, name + ".png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(path)) encoder.Save(fs);
        return path;
    }
}
