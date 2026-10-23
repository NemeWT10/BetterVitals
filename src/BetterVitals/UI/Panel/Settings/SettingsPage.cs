using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BetterVitals.Localization;
using BetterVitals.Services;

namespace BetterVitals.UI.Panel;

/// <summary>Parameters understood by the Settings page's Navigate.</summary>
public static class SettingsPageParameter
{
    /// <summary>Scroll to the About section.</summary>
    public const string About = "about";

    /// <summary>Scroll to Recording (history on / off, retention, disk use).</summary>
    public const string Recording = "recording";

    /// <summary>Scroll to the overlay behavior section.</summary>
    public const string Overlay = "overlay";

    /// <summary>Scroll to Startup (start with Windows).</summary>
    public const string Startup = "startup";

    /// <summary>Scroll to Updates.</summary>
    public const string Updates = "updates";

    /// <summary>Scroll to Permissions and driver (full / basic mode, PawnIO).</summary>
    public const string Permissions = "permissions";
}

/// <summary>
/// A section of the Settings page (title + its card). Wave-2 areas add theirs (Recording, Overlay, Updates, Permissions
/// and driver…) from a [ModuleInitializer] without touching the page; Order: General 10, About 1000.
/// </summary>
public sealed record SettingsSection(string Id, string TitleKey, int Order, Func<PageContext, FrameworkElement> Create)
{
    public int Priority { get; init; } = 1;
}

public static class SettingsSections
{
    private static readonly Dictionary<string, SettingsSection> Sections = new(StringComparer.Ordinal);

    public static void Register(SettingsSection section)
    {
        lock (Sections)
        {
            if (Sections.TryGetValue(section.Id, out var existing) && existing.Priority > section.Priority) return;
            Sections[section.Id] = section;
        }
    }

    public static IReadOnlyList<SettingsSection> All
    {
        get
        {
            lock (Sections) return Sections.Values.OrderBy(s => s.Order).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
        }
    }
}

/// <summary>The Settings page: header, then every registered section in order (created with the page).</summary>
public sealed class SettingsPage : UserControl, IPage
{
    private readonly ScrollViewer _scroll;
    private readonly Dictionary<string, FrameworkElement> _anchors = new(StringComparer.Ordinal);

    public SettingsPage(PageContext context)
    {
        var root = new StackPanel();
        var title = new TextBlock();
        title.SetResourceReference(StyleProperty, "PageTitle");
        title.SetBinding(TextBlock.TextProperty, Tr("settings.title"));
        root.Children.Add(title);
        var subtitle = new TextBlock { Margin = new Thickness(1, 4, 0, 22) };
        subtitle.SetResourceReference(StyleProperty, "PageSubtitle");
        subtitle.SetBinding(TextBlock.TextProperty, Tr("settings.subtitle"));
        root.Children.Add(subtitle);

        foreach (var section in SettingsSections.All)
        {
            var head = new TextBlock();
            head.SetResourceReference(StyleProperty, "SectionTitle");
            head.SetBinding(TextBlock.TextProperty, Tr(section.TitleKey));
            root.Children.Add(head);
            FrameworkElement body;
            try
            {
                body = section.Create(context);
            }
            catch (Exception ex)
            {
                BetterVitals.Core.Log.Error($"settings: section '{section.Id}' failed", ex);
                continue;
            }
            root.Children.Add(body);
            _anchors[section.Id] = head;
        }

        _scroll = PageLayout.Scroll(root, 860);
        Content = _scroll;
    }

    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is string id && _anchors.TryGetValue(id, out var anchor))
            Dispatcher.BeginInvoke(() => anchor.BringIntoView(new Rect(0, 0, 1, 400)), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void OnNavigatedFrom()
    {
    }

    private static Binding Tr(string key) => new($"[{key}]") { Source = Loc.I, Mode = BindingMode.OneWay };
}

/// <summary>The shell's own pages and settings sections; wave-2 registrations with a higher priority replace the placeholders.</summary>
internal static class ShellPages
{
    [ModuleInitializer]
    internal static void Register()
    {
        if (!PlatformCommands.IsHelperProcess) RegisterNow();
    }

    // never even compiled in a helper process (--install…): it must not load WPF
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterNow()
    {
        PageRegistry.Register(Placeholder(PageIds.Overview, "nav.overview", "Icon.Dashboard", 10));
        PageRegistry.Register(Placeholder(PageIds.History, "nav.history", "Icon.Chart", 20));
        PageRegistry.Register(Placeholder(PageIds.Sensors, "nav.sensors", "Icon.List", 30));
        PageRegistry.Register(Placeholder(PageIds.Overlay, "nav.overlay", "Icon.Overlay", 40));
        PageRegistry.Register(new PageInfo(PageIds.Settings, "nav.settings", "Icon.Settings", 90, ctx => new SettingsPage(ctx)) { Priority = 0 });

        SettingsSections.Register(new SettingsSection("general", "settings.general", 10, _ => new GeneralSection()) { Priority = 0 });
        SettingsSections.Register(new SettingsSection(SettingsPageParameter.About, "settings.about", 1000, ctx => new About.AboutView(ctx)) { Priority = 0 });
    }

    private static PageInfo Placeholder(string id, string titleKey, string icon, int order)
    {
        PageInfo? self = null;
        self = new PageInfo(id, titleKey, icon, order, _ => new PlaceholderPage(self!)) { Priority = 0 };
        return self;
    }
}
