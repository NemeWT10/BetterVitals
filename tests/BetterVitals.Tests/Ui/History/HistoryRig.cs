using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterVitals.Core.History;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.UI;
using BetterVitals.UI.Dev;
using BetterVitals.UI.Pages.History;
using BetterVitals.UI.Panel;
using BetterVitals.UI.Themes;
using BetterVitals.UI.Toasts;

namespace BetterVitals.Tests.Ui.History;

/// <summary>The shell side of a page in tests: records navigations and toasts; its window is never shown.</summary>
internal sealed class FakePanelHost : IPanelHost
{
    public Window Window { get; } = new();

    public List<(string Id, object? Parameter)> Navigations { get; } = [];

    public List<(string Text, ToastKind Kind, string? ActionText, Action? Action)> Toasts { get; } = [];

    public void Navigate(string pageId, object? parameter) => Navigations.Add((pageId, parameter));

    public void Toast(string text, ToastKind kind, string? actionText, Action? action) => Toasts.Add((text, kind, actionText, action));
}

/// <summary>
/// A History page on an STA thread, never shown: preview hub + deterministic preview history (Warsaw time, a fixed
/// "now"), theme resources on a host border, laid out at a panel-like size; renders to PNG under tests/output/ui-history.
/// </summary>
internal sealed class HistoryRig : IDisposable
{
    public static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");

    /// <summary>Thursday 23 Oct 2026, 20:40 in Warsaw — an evening with a game running.</summary>
    public static readonly DateTime NowUtc = new(2026, 10, 23, 18, 40, 0, DateTimeKind.Utc);

    private readonly string _dir;
    private readonly string _before;

    public HistoryRig(string theme = "aurora", string language = "pl", bool recording = true, bool withHistory = true, ISensorHub? hub = null,
        IHistoryQuery? history = null, double width = 900, double height = 700, Func<SensorDescriptor, bool>? record = null)
    {
        _before = Loc.I.Language;
        _dir = Path.Combine(OutputDir, "tmp-" + Guid.NewGuid().ToString("N")[..8]);
        Store = SettingsStore.ForTests(Path.Combine(_dir, "settings.json"), new AppSettings());
        Loc.I.SetLanguage(language);
        Motion.I.Update(false);
        ThemeManager.I.Apply(theme, false);
        Preview = new PreviewHub(start: false);
        Preview.Warm(60);
        Hub = hub ?? Preview;
        record ??= _ => true;
        Store.Mutate("recording", s =>
        {
            s.Recording.Enabled = recording;
            s.Recording.IntervalSeconds = 2;
            foreach (var d in Hub.Sensors.Where(record)) s.Recording.SensorIds.Add(d.Id);
        });
        History = history ?? (withHistory ? new PreviewHistory(Hub, () => Now, id => Store.Current.Recording.Enabled && Store.Current.Recording.SensorIds.Contains(id), Warsaw) : null);
        Panel = new FakePanelHost();
        Width = width;
        Height = height;
        Page = new HistoryPage(new PageContext(Panel, Hub, History)) { Clock = () => Now, Zone = Warsaw };
        var layer = new Border { Child = Page };
        layer.SetResourceReference(Border.BackgroundProperty, "Brush.Layer");
        Host = new Border { Width = width, Height = height, Child = layer, Resources = Resources(theme) };
        Host.SetResourceReference(Border.BackgroundProperty, "Brush.Bg");
        Layout();
    }

    public static string OutputDir
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "BetterVitals.sln"))) d = d.Parent;
            return Path.Combine(d!.FullName, "tests", "output", "ui-history");
        }
    }

    public long Now { get; set; } = NowUtc.Ticks;

    public SettingsStore Store { get; }

    public PreviewHub Preview { get; }

    public ISensorHub Hub { get; }

    public IHistoryQuery? History { get; }

    public FakePanelHost Panel { get; }

    public HistoryPage Page { get; }

    public Border Host { get; }

    public double Width { get; }

    public double Height { get; }

    public SensorDescriptor Sensor(string name, SensorKind kind) => Hub.Sensors.First(s => s.Name == name && s.Kind == kind);

    public static ResourceDictionary Resources(string theme)
    {
        var d = new ResourceDictionary();
        d.MergedDictionaries.Add(ThemeManager.Build(Palettes.ById(theme), mica: false));
        d.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/BetterVitals;component/UI/Themes/Base.xaml", UriKind.Relative)));
        return d;
    }

    public void Layout()
    {
        Host.Measure(new Size(Width, Height));
        Host.Arrange(new Rect(0, 0, Width, Height));
        Host.UpdateLayout();
    }

    /// <summary>Navigates to the page and lets queries / stats land.</summary>
    public void Show(object? parameter = null)
    {
        Page.OnNavigatedTo(parameter);
        Settle();
    }

    /// <summary>Layout + dispatcher until the page's pending queries and their UI updates are done.</summary>
    public void Settle()
    {
        for (var round = 0; round < 4; round++)
        {
            Layout();
            Sta.PumpUntil(() => (Page.PendingQuery?.IsCompleted ?? true) && (Page.PendingStats?.IsCompleted ?? true), 5000);
            Sta.Pump(25);
        }
        Layout();
        Page.Chart.RenderNow();
    }

    /// <summary>Renders the host at 1.25× into tests/output/ui-history/&lt;name&gt;.png; returns the path.</summary>
    public string Save(string name) => SavePng(Host, name, Width, Height);

    public static string SavePng(FrameworkElement element, string name, double width, double height)
    {
        const double scale = 1.25;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(element);
        Directory.CreateDirectory(OutputDir);
        var path = Path.Combine(OutputDir, name + ".png");
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(path)) enc.Save(fs);
        return path;
    }

    /// <summary>A popover's content on a backdrop like the page, for its own PNG.</summary>
    public string SavePanel(FrameworkElement panel, string name, double width, double height, string theme)
    {
        if (panel.Parent is Border previous) previous.Child = null;
        var host = new Border { Width = width, Height = height, Resources = Resources(theme), Padding = new Thickness(16), Child = panel };
        host.SetResourceReference(Border.BackgroundProperty, "Brush.Bg");
        return SavePng(host, name, width, height);
    }

    public void Dispose()
    {
        Page.Dispose();
        Preview.Dispose();
        Loc.I.SetLanguage(_before);
        Motion.I.Update(true);
        Store.Flush();
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }
}
