using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterVitals.Core.History;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.Platform;
using BetterVitals.Platform.Updates;
using BetterVitals.Services;
using BetterVitals.UI.Panel;
using BetterVitals.UI.Themes;
using BetterVitals.UI.Toasts;

namespace BetterVitals.Tests.Ui.Settings;

/// <summary>
/// A platform that never touches Windows: no UAC, no install, no task, no registry. Every call is recorded; results are
/// whatever the test sets.
/// </summary>
internal sealed class FakePlatform : IPlatformService
{
    public List<string> Calls { get; } = [];

    public bool IsElevated { get; set; }

    public bool IsInstalled { get; set; }

    public Version? PawnIoVersion { get; set; }

    public bool PawnIoOutdated => PawnIoVersion is { } v && v < new Version(2, 1, 0);

    public Version PawnIoBundledVersion { get; set; } = new(2, 2, 0);

    public SecurityInfo Security { get; set; } = new(true, true, true);

    public string? AntiCheatPauseReason { get; set; }

    /// <summary>What InstallFullModeAsync answers (false = the user declined UAC).</summary>
    public bool FullModeResult { get; set; } = true;

    /// <summary>Completes InstallFullModeAsync when set (to observe the waiting state).</summary>
    public TaskCompletionSource<bool>? FullModeGate { get; set; }

    public PawnIoInstallOutcome? PawnIoResult { get; set; } = PawnIoInstallOutcome.Installed;

    /// <summary>What SetAutostartAsync applies; null = what was asked.</summary>
    public bool? AutostartResult { get; set; }

    public bool AutostartEnabled { get; set; }

    public UpdateUiState UpdateState { get; set; }

    public ReleaseInfo? AvailableRelease { get; set; }

    public double UpdateProgress { get; set; }

    public event Action? StateChanged;

    public void RaiseChanged() => StateChanged?.Invoke();

    public int StateChangedSubscribers => StateChanged?.GetInvocationList().Length ?? 0;

    public async Task<bool> InstallFullModeAsync(bool autostart, bool installPawnIo)
    {
        Calls.Add($"InstallFullMode(autostart={autostart}, pawnIo={installPawnIo})");
        return FullModeGate is { } gate ? await gate.Task : FullModeResult;
    }

    public Task<PawnIoInstallOutcome?> InstallPawnIoAsync()
    {
        Calls.Add("InstallPawnIo");
        if (PawnIoResult is PawnIoInstallOutcome.Installed or PawnIoInstallOutcome.RestartRequired) PawnIoVersion = PawnIoBundledVersion;
        return Task.FromResult(PawnIoResult);
    }

    public Task<bool> SetAutostartAsync(bool enabled)
    {
        Calls.Add($"SetAutostart({enabled})");
        AutostartEnabled = AutostartResult ?? enabled;
        return Task.FromResult(AutostartEnabled);
    }

    public Task CheckForUpdatesAsync()
    {
        Calls.Add("CheckForUpdates");
        return Task.CompletedTask;
    }

    public Task DownloadUpdateAsync()
    {
        Calls.Add("DownloadUpdate");
        return Task.CompletedTask;
    }

    public void RestartToUpdate() => Calls.Add("RestartToUpdate");

    public void OpenUrl(string url) => Calls.Add("OpenUrl " + url);

    public void OpenFolder(string path) => Calls.Add("OpenFolder " + path);

    public static ReleaseInfo Release(string version = "0.2.0", string notes = "## New\n- **Faster** charts\n- A [link](https://example.invalid) here\n\nPlain line.")
        => new(Version.Parse(version), "v" + version, notes, new Uri("https://example.invalid/a.exe"), new Uri("https://example.invalid/a.sha256"),
            new Uri("https://example.invalid/a.sig"), 31_457_280, new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc));
}

internal sealed class FakeHistory : IHistoryQuery
{
    public HistoryUsage Usage { get; set; } = new(62_914_560, 2_100_000, new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc));

    public int Cleared { get; private set; }

    public Task<IReadOnlyList<HistoryPoint>> GetSeriesAsync(string sensorId, DateTime fromUtc, DateTime toUtc, int maxPoints, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<HistoryPoint>>([]);

    public Task<RangeStats?> GetStatsAsync(string sensorId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => Task.FromResult<RangeStats?>(null);

    public (DateTime FromUtc, DateTime ToUtc)? GetCoverage(string sensorId) => null;

    public HistoryUsage GetUsage() => Usage;

    public long EstimateExportBytes(ExportRequest request) => 0;

    public Task ExportAsync(ExportRequest request, Stream output, IProgress<double>? progress = null, CancellationToken ct = default) => Task.CompletedTask;

    public Task ClearAsync()
    {
        Cleared++;
        Usage = new HistoryUsage(0, Usage.EstimatedBytesPerDay, null);
        return Task.CompletedTask;
    }
}

/// <summary>A hub with fixed source statuses (and nothing else).</summary>
internal sealed class StatusHub : ISensorHub
{
    public List<(string Source, SourceStatus Status)> Statuses { get; } = [];

    public IReadOnlyList<HardwareDescriptor> Hardware => [];

    public IReadOnlyList<SensorDescriptor> Sensors => [];

    public bool IsPresent(int index) => false;

    public SensorDescriptor? Find(string sensorId) => null;

    public HardwareDescriptor? FindHardware(string hardwareId) => null;

    public SensorFrame Latest { get; } = new(0);

    public SessionStats Stats { get; } = new();

    public IDemand Demand(string consumer, IEnumerable<string> sensorIds, TimeSpan interval) => throw new NotSupportedException();

    public event Action<SensorFrame>? FrameReady { add { } remove { } }

    public event Action? InventoryChanged;

    public void RaiseInventory() => InventoryChanged?.Invoke();

    public IReadOnlyList<(string Source, SourceStatus Status)> SourceStatuses => Statuses;
}

/// <summary>Records navigation and toasts of a page.</summary>
internal sealed class FakeHost : IPanelHost
{
    public List<string> Navigations { get; } = [];

    public List<(string Text, ToastKind Kind)> Toasts { get; } = [];

    public Window Window => null!;

    public void Navigate(string pageId, object? parameter) => Navigations.Add(pageId);

    public void Toast(string text, ToastKind kind, string? actionText, Action? action) => Toasts.Add((text, kind));

    public PageContext Context(ISensorHub? hub = null, IHistoryQuery? history = null) => new(this, hub ?? NullSensorHub.Instance, history);
}

/// <summary>A temporary settings store (never the user's), language and services restored afterwards.</summary>
internal sealed class TestEnv : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bv-settings-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _language = Loc.I.Language;
    private readonly IPlatformService _platform = AppServices.Platform;

    public TestEnv(AppSettings? settings = null, string language = "en")
    {
        Store = SettingsStore.ForTests(Path.Combine(_dir, "settings.json"), settings ?? new AppSettings());
        Loc.I.SetLanguage(language);
        AppServices.Platform = Platform;
        // end states only: renders never catch an entrance half-faded
        BetterVitals.UI.Motion.I.Update(false);
    }

    public SettingsStore Store { get; }

    public FakePlatform Platform { get; } = new();

    public FakeHost Host { get; } = new();

    public void Dispose()
    {
        AppServices.Platform = _platform;
        Loc.I.SetLanguage(_language);
        BetterVitals.UI.Motion.I.Update(true);
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // temp
        }
    }
}

/// <summary>
/// Offscreen renders of code-built UI with the real theme and base styles (no Application, no window, nothing shown):
/// tests\output\ui-settings\&lt;name&gt;.png, reviewed by eye.
/// </summary>
internal static class Render
{
    public const double Scale = 1.25;

    public static string OutputDir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BetterVitals.sln"))) dir = dir.Parent;
            var root = dir?.FullName ?? Path.GetTempPath();
            var output = Path.Combine(root, "tests", "output", "ui-settings");
            Directory.CreateDirectory(output);
            return output;
        }
    }

    /// <summary>Theme + Base.xaml on a host, like the panel's page layer.</summary>
    public static Border Host(FrameworkElement content, string palette, double width, bool layer = true)
    {
        var host = new Border { Width = width, Padding = new Thickness(layer ? 32 : 0, layer ? 24 : 0, layer ? 32 : 0, layer ? 16 : 0) };
        host.Resources.MergedDictionaries.Add(ThemeManager.Build(Palettes.ById(palette), mica: false));
        host.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/BetterVitals;component/UI/Themes/Base.xaml", UriKind.Relative)));
        host.SetResourceReference(Border.BackgroundProperty, "Brush.Bg");
        host.SetResourceReference(TextElement.FontFamilyProperty, "Font.Ui");
        host.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text");
        TextElement.SetFontSize(host, 13);
        host.Language = Loc.I.XmlLang;
        host.UseLayoutRounding = true;
        if (layer)
        {
            var sheet = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(24, 20, 24, 8), Child = content, BorderThickness = new Thickness(1) };
            sheet.SetResourceReference(Border.BackgroundProperty, "Brush.Layer");
            sheet.SetResourceReference(Border.BorderBrushProperty, "Brush.LayerBorder");
            host.Child = sheet;
        }
        else
        {
            host.Child = content;
        }
        return host;
    }

    /// <summary>Lays out the host (fixed height when given) and writes the PNG; returns its path.</summary>
    public static string Save(FrameworkElement host, string name, double? height = null)
    {
        Layout(host, height);
        var w = (int)Math.Ceiling(host.ActualWidth * Scale);
        var h = (int)Math.Ceiling(host.ActualHeight * Scale);
        var bmp = new RenderTargetBitmap(w, h, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bmp.Render(host);
        var path = Path.Combine(OutputDir, name + ".png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(path)) encoder.Save(fs);
        return path;
    }

    public static void Layout(FrameworkElement host, double? height = null)
    {
        var size = new Size(host.Width, height ?? double.PositiveInfinity);
        for (var i = 0; i < 3; i++)
        {
            host.Measure(size);
            host.Arrange(new Rect(0, 0, host.Width, height ?? host.DesiredSize.Height));
            host.UpdateLayout();
            Sta.DoEvents();
        }
    }

    /// <summary>Visible single-line texts that do not fit (the snapshot report's check, for offscreen trees).</summary>
    public static List<string> Clipped(FrameworkElement root)
    {
        var problems = new List<string>();
        var dpi = VisualTreeHelper.GetDpi(root).PixelsPerDip;
        foreach (var tb in Descendants<TextBlock>(root))
        {
            if (!tb.IsVisible && tb.Visibility != Visibility.Visible) continue;
            if (string.IsNullOrEmpty(tb.Text) || tb.ActualWidth <= 0 || tb.TextWrapping != TextWrapping.NoWrap || !IsShown(tb, root)) continue;
            var ft = new FormattedText(tb.Text, System.Globalization.CultureInfo.CurrentUICulture, tb.FlowDirection,
                new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, Brushes.Black, dpi);
            if (ft.WidthIncludingTrailingWhitespace + tb.Padding.Left + tb.Padding.Right > tb.ActualWidth + 1.5)
                problems.Add($"\"{tb.Text}\" needs {ft.WidthIncludingTrailingWhitespace:0} px, has {tb.ActualWidth:0}");
        }
        return problems;
    }

    private static bool IsShown(UIElement e, DependencyObject root)
    {
        for (DependencyObject? d = e; d is not null && d != root; d = VisualTreeHelper.GetParent(d))
            if (d is UIElement u && u.Visibility != Visibility.Visible) return false;
        return true;
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
