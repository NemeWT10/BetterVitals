using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BetterVitals.Core;
using BetterVitals.Localization;

namespace BetterVitals.UI.Panel;

/// <summary>Settings ▸ About: the mark, name, version (with commit and build date), one-line pitch, licenses.</summary>
internal sealed class AboutSection : UserControl
{
    public AboutSection()
    {
        var card = new Border();
        card.SetResourceReference(StyleProperty, "Card");
        var row = new DockPanel();
        var logo = new Image { Width = 52, Height = 52, VerticalAlignment = VerticalAlignment.Top };
        logo.SetResourceReference(Image.SourceProperty, "Logo.Image");
        Motion.SetHoverLift(logo, 3);
        row.Children.Add(logo);

        var text = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        var name = new TextBlock { Text = AppEnv.ProductName, FontSize = 18, FontWeight = FontWeights.SemiBold };
        name.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Display");
        name.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        text.Children.Add(name);

        var version = new TextBlock { FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
        version.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
        void SetVersion() => version.Text = Loc.T("about.version", AppEnv.Version);
        SetVersion();
        Loaded += (_, _) => Loc.LanguageChanged += SetVersion;
        Unloaded += (_, _) => Loc.LanguageChanged -= SetVersion;
        text.Children.Add(version);

        var tagline = new TextBlock { FontSize = 13, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
        tagline.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        tagline.SetBinding(TextBlock.TextProperty, Tr("about.tagline"));
        text.Children.Add(tagline);

        var license = new TextBlock { FontSize = 12, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        license.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
        license.SetBinding(TextBlock.TextProperty, Tr("about.license"));
        text.Children.Add(license);

        row.Children.Add(text);
        card.Child = row;
        Content = card;
    }

    private static Binding Tr(string key) => new($"[{key}]") { Source = Loc.I, Mode = BindingMode.OneWay };
}
