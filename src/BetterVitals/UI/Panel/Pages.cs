using System.Windows;
using System.Windows.Controls;
using BetterVitals.Core.History;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.UI.Toasts;

namespace BetterVitals.UI.Panel;

/// <summary>Ids of the panel's pages (navigation, tray actions, the single-instance "open:&lt;id&gt;" command).</summary>
public static class PageIds
{
    public const string Overview = "overview";
    public const string History = "history";
    public const string Sensors = "sensors";
    public const string Overlay = "overlay";
    public const string Settings = "settings";
}

/// <summary>
/// Optional lifecycle of a page (a UserControl). Pages are created lazily on first navigation, kept while the panel is
/// open and released when it closes (an <see cref="IDisposable"/> page is disposed then). Start live work (demands,
/// FrameReady, timers) in <see cref="OnNavigatedTo"/> and stop it in <see cref="OnNavigatedFrom"/>: a cached page that
/// is not shown must cost nothing.
/// </summary>
public interface IPage
{
    /// <summary>The page is shown (first time or again); <paramref name="parameter"/> comes from Navigate.</summary>
    void OnNavigatedTo(object? parameter);

    /// <summary>Another page took its place or the panel is closing.</summary>
    void OnNavigatedFrom();
}

/// <summary>
/// A page of the panel. <paramref name="TitleKey"/> is the nav label (Loc), <paramref name="IconKey"/> a geometry from
/// Icons.xaml, <paramref name="Order"/> the nav position. A registration with a higher <see cref="Priority"/> replaces
/// one with the same id (the shell's placeholders have priority 0, real pages default to 1).
/// </summary>
public sealed record PageInfo(string Id, string TitleKey, string IconKey, int Order, Func<PageContext, FrameworkElement> Create)
{
    public int Priority { get; init; } = 1;

    /// <summary>Listed in the nav rail (false: reachable by Navigate only).</summary>
    public bool InNav { get; init; } = true;
}

/// <summary>
/// The panel's page registry. Wave-2 pages register themselves from a [ModuleInitializer] in their own file, without
/// touching the shell:
/// <code>
/// static class OverviewPageRegistration
/// {
///     [ModuleInitializer]
///     internal static void Register() => PageRegistry.Register(new PageInfo(PageIds.Overview, "nav.overview", "Icon.Dashboard", 10, ctx => new OverviewPage(ctx)));
/// }
/// </code>
/// </summary>
public static class PageRegistry
{
    private static readonly Dictionary<string, PageInfo> Pages = new(StringComparer.Ordinal);

    public static void Register(PageInfo page)
    {
        lock (Pages)
        {
            if (Pages.TryGetValue(page.Id, out var existing) && existing.Priority > page.Priority) return;
            Pages[page.Id] = page;
        }
    }

    /// <summary>All pages in nav order.</summary>
    public static IReadOnlyList<PageInfo> All
    {
        get
        {
            lock (Pages) return Pages.Values.OrderBy(p => p.Order).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
        }
    }

    public static PageInfo? Find(string id)
    {
        lock (Pages) return Pages.GetValueOrDefault(id);
    }
}

/// <summary>What a page gets from the shell.</summary>
public sealed class PageContext
{
    private readonly IPanelHost _host;

    internal PageContext(IPanelHost host, ISensorHub hub, IHistoryQuery? history)
    {
        _host = host;
        Hub = hub;
        History = history;
    }

    public ISensorHub Hub { get; }

    /// <summary>Null until the history store is attached (show "live only" then).</summary>
    public IHistoryQuery? History { get; }

    public SettingsStore Settings => SettingsStore.I;

    /// <summary>The panel window (owner for dialogs).</summary>
    public Window Window => _host.Window;

    public void Navigate(string pageId, object? parameter = null) => _host.Navigate(pageId, parameter);

    /// <summary>A toast in the panel's corner, optionally with an action ("Undo").</summary>
    public void Toast(string text, ToastKind kind = ToastKind.Info, string? actionText = null, Action? action = null)
        => _host.Toast(text, kind, actionText, action);
}

internal interface IPanelHost
{
    Window Window { get; }

    void Navigate(string pageId, object? parameter);

    void Toast(string text, ToastKind kind, string? actionText, Action? action);
}

/// <summary>Page layout helpers shared by the shell's pages (and handy for wave-2 pages).</summary>
public static class PageLayout
{
    /// <summary>Standard page margin: 36 left / right, 26 top.</summary>
    public static readonly Thickness Margin = new(36, 26, 36, 36);

    /// <summary>
    /// A vertically scrolling page whose content is left-aligned, fills the width and stops growing at
    /// <paramref name="maxWidth"/> (wide windows keep a readable line length).
    /// </summary>
    public static ScrollViewer Scroll(FrameworkElement content, double maxWidth = 900)
    {
        content.Margin = Margin;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = maxWidth + Margin.Left + Margin.Right });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) });
        grid.Children.Add(content);
        return new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    }
}
