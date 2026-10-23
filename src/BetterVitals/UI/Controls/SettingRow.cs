using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BetterVitals.UI.Controls;

/// <summary>
/// One option (BetterShot's setting row): optional icon tile, bold name, one plain sentence about what it changes, and its
/// control (switch, segments, combo…) on the right — or below with <see cref="IsStacked"/>. Template in Base.xaml.
/// </summary>
public sealed class SettingRow : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow), new PropertyMetadata(""));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata(""));
    public static readonly DependencyProperty WarningProperty = DependencyProperty.Register(nameof(Warning), typeof(string), typeof(SettingRow), new PropertyMetadata(""));
    public static readonly DependencyProperty InfoProperty = DependencyProperty.Register(nameof(Info), typeof(string), typeof(SettingRow), new PropertyMetadata(""));
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(Geometry), typeof(SettingRow), new PropertyMetadata(null));
    public static readonly DependencyProperty IsStackedProperty = DependencyProperty.Register(nameof(IsStacked), typeof(bool), typeof(SettingRow), new PropertyMetadata(false));
    public static readonly DependencyProperty IsLastProperty = DependencyProperty.Register(nameof(IsLast), typeof(bool), typeof(SettingRow), new PropertyMetadata(false));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    /// <summary>Shown under the description in the warning color.</summary>
    public string Warning { get => (string)GetValue(WarningProperty); set => SetValue(WarningProperty, value); }

    /// <summary>Optional Loc key of an explanation shown by a small ⓘ next to the title (use sparingly).</summary>
    public string Info { get => (string)GetValue(InfoProperty); set => SetValue(InfoProperty, value); }

    /// <summary>Optional leading icon (24×24 geometry, e.g. {StaticResource Icon.Globe}).</summary>
    public Geometry? Icon { get => (Geometry?)GetValue(IconProperty); set => SetValue(IconProperty, value); }

    /// <summary>Put the control below the text instead of on the right (wide controls).</summary>
    public bool IsStacked { get => (bool)GetValue(IsStackedProperty); set => SetValue(IsStackedProperty, value); }

    /// <summary>The last row of a card draws no separator.</summary>
    public bool IsLast { get => (bool)GetValue(IsLastProperty); set => SetValue(IsLastProperty, value); }
}
