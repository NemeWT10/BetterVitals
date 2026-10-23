using System.Windows;
using System.Windows.Input;
using BetterVitals.Core.Sensors;

namespace BetterVitals.UI.Explain;

/// <summary>
/// Attached properties that give an element the app-wide explanation bubble (see <see cref="ExplainService"/>):
/// <code>
/// &lt;TextBlock Text="{Binding Name}" x:Explain.Sensor="{Binding Descriptor}"/&gt;   sensor: verbatim name + glossary text
/// &lt;Border x:Explain.Key="settings.explanations.info"/&gt;                         a Loc key (live language)
/// &lt;Border x:Explain.Text="Plain text" x:Explain.Title="Title"/&gt;                literal text
/// </code>
/// Hooks only the element's own MouseEnter / MouseLeave (no static events, nothing to leak). Changing any of these
/// properties on the element that shows the bubble closes it (a recycled list row gets a new sensor).
/// </summary>
public static class Explain
{
    public static readonly DependencyProperty SensorProperty = DependencyProperty.RegisterAttached(
        "Sensor", typeof(SensorDescriptor), typeof(Explain), new PropertyMetadata(null, OnContentChanged));

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Explain), new PropertyMetadata(null, OnContentChanged));

    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(Explain), new PropertyMetadata(null, OnContentChanged));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.RegisterAttached(
        "Title", typeof(string), typeof(Explain), new PropertyMetadata(null, OnContentChanged));

    private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
        "Hooked", typeof(bool), typeof(Explain), new PropertyMetadata(false));

    /// <summary>The sensor whose verbatim name and glossary explanation the bubble shows.</summary>
    public static SensorDescriptor? GetSensor(DependencyObject d) => (SensorDescriptor?)d.GetValue(SensorProperty);

    public static void SetSensor(DependencyObject d, SensorDescriptor? value) => d.SetValue(SensorProperty, value);

    /// <summary>Literal explanation text (already localized by the caller).</summary>
    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);

    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);

    /// <summary>Loc key of the explanation text (read when the bubble opens, so it follows the language).</summary>
    public static string? GetKey(DependencyObject d) => (string?)d.GetValue(KeyProperty);

    public static void SetKey(DependencyObject d, string? value) => d.SetValue(KeyProperty, value);

    /// <summary>Optional bold first line for Text / Key bubbles.</summary>
    public static string? GetTitle(DependencyObject d) => (string?)d.GetValue(TitleProperty);

    public static void SetTitle(DependencyObject d, string? value) => d.SetValue(TitleProperty, value);

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        if (!(bool)el.GetValue(HookedProperty))
        {
            el.SetValue(HookedProperty, true);
            el.MouseEnter += OnMouseEnter;
            el.MouseLeave += OnMouseLeave;
        }
        ExplainService.I.OnContentChanged(el);
    }

    private static void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement el) ExplainService.I.OnEnter(el);
    }

    private static void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement el) ExplainService.I.OnLeave(el);
    }

    /// <summary>Does the element carry anything to explain?</summary>
    public static bool HasContent(DependencyObject d)
        => GetSensor(d) is not null || !string.IsNullOrEmpty(GetText(d)) || !string.IsNullOrEmpty(GetKey(d));
}
