using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace BetterVitals.UI.Controls;

/// <summary>
/// Theme resources picked by key from a binding (a view model says "Brush.Cat.Gpu"; the element keeps following
/// theme switches like a DynamicResource): <c>c:Res.Foreground="{Binding ValueBrushKey}"</c>, <c>c:Res.Background</c>,
/// <c>c:Res.Icon</c> (Icon.Data), <c>c:Res.Stroke</c> (Sparkline.Stroke).
/// </summary>
public static class Res
{
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.RegisterAttached(
        "Foreground", typeof(string), typeof(Res), new PropertyMetadata(null, (d, e) => Apply(d, TextElement.ForegroundProperty, e.NewValue)));

    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.RegisterAttached(
        "Background", typeof(string), typeof(Res), new PropertyMetadata(null, (d, e) =>
        {
            var dp = d switch
            {
                Border => Border.BackgroundProperty,
                System.Windows.Controls.Panel => System.Windows.Controls.Panel.BackgroundProperty,
                Control => Control.BackgroundProperty,
                _ => null,
            };
            if (dp is not null) Apply(d, dp, e.NewValue);
        }));

    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(string), typeof(Res), new PropertyMetadata(null, (d, e) => Apply(d, Icon.DataProperty, e.NewValue)));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.RegisterAttached(
        "Stroke", typeof(string), typeof(Res), new PropertyMetadata(null, (d, e) => Apply(d, Sparkline.StrokeProperty, e.NewValue)));

    public static string? GetForeground(DependencyObject d) => (string?)d.GetValue(ForegroundProperty);

    public static void SetForeground(DependencyObject d, string? value) => d.SetValue(ForegroundProperty, value);

    public static string? GetBackground(DependencyObject d) => (string?)d.GetValue(BackgroundProperty);

    public static void SetBackground(DependencyObject d, string? value) => d.SetValue(BackgroundProperty, value);

    public static string? GetIcon(DependencyObject d) => (string?)d.GetValue(IconProperty);

    public static void SetIcon(DependencyObject d, string? value) => d.SetValue(IconProperty, value);

    public static string? GetStroke(DependencyObject d) => (string?)d.GetValue(StrokeProperty);

    public static void SetStroke(DependencyObject d, string? value) => d.SetValue(StrokeProperty, value);

    private static void Apply(DependencyObject d, DependencyProperty dp, object? key)
    {
        if (d is not FrameworkElement fe) return;
        if (key is string { Length: > 0 } k) fe.SetResourceReference(dp, k);
        else fe.ClearValue(dp);
    }
}
