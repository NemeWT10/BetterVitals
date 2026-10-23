using System.Windows;

namespace BetterVitals.UI.Pages.History;

/// <summary>The History page's own styles and templates (merged into the page's resources).</summary>
public partial class HistoryStyles : ResourceDictionary
{
    [ThreadStatic]
    private static HistoryStyles? _shared;

    public HistoryStyles() => InitializeComponent();

    /// <summary>One instance per UI thread for every page instance (styles belong to the thread that loaded them).</summary>
    internal static HistoryStyles Shared => _shared ??= new HistoryStyles();
}
