using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BetterVitals.Core.Overlay;
using BetterVitals.Localization;
using BetterVitals.UI.Themes;

namespace BetterVitals.UI.Pages.OverlayEditor;

/// <summary>
/// The editor's one color popover: "theme color" (null), swatches of the active theme, and a hex field ("#RRGGBB",
/// "#AARRGGBB", short forms) that applies as soon as it is valid. Created on first use, reused for every color button.
/// </summary>
internal sealed class ColorPopover
{
    private Popup? _popup;
    private WrapPanel? _swatches;
    private TextBox? _hex;
    private Border? _sample;
    private Button? _themeButton;
    private Action<string?>? _pick;
    private bool _syncing;

    public bool IsOpen => _popup?.IsOpen == true;

    /// <summary>Opens under <paramref name="target"/>; <paramref name="pick"/> gets the normalized color or null (theme).</summary>
    public void Open(FrameworkElement target, string? current, bool allowTheme, Action<string?> pick)
    {
        Build();
        _pick = pick;
        FillSwatches();
        _themeButton!.Visibility = allowTheme ? Visibility.Visible : Visibility.Collapsed;
        _syncing = true;
        _hex!.Text = ColorText.Normalize(current) ?? "";
        _syncing = false;
        ShowSample(current);
        _popup!.PlacementTarget = target;
        _popup.IsOpen = true;
        _hex.Focus();
        _hex.SelectAll();
    }

    public void Close()
    {
        if (_popup is not null) _popup.IsOpen = false;
    }

    private void Build()
    {
        if (_popup is not null) return;
        var root = new StackPanel { Width = 236 };
        var title = Ui.Text("ovl.color.title", size: 12.5);
        title.FontWeight = FontWeights.SemiBold;
        title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelText");
        title.Margin = new Thickness(2, 0, 0, 8);
        root.Children.Add(title);

        _swatches = new WrapPanel();
        root.Children.Add(_swatches);

        var hexRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        _sample = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(8, 0, 0, 0) };
        _sample.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderStrong");
        DockPanel.SetDock(_sample, Dock.Right);
        hexRow.Children.Add(_sample);
        _hex = new TextBox { Tag = "#RRGGBB", FontFamily = new FontFamily("Cascadia Mono, Consolas") };
        _hex.SetResourceReference(FrameworkElement.StyleProperty, "OvEd.SmallField");
        _hex.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            var normalized = ColorText.Normalize(_hex.Text);
            _hex.SetResourceReference(Control.ForegroundProperty, normalized is null && _hex.Text.Trim().Length > 0 ? "Brush.Danger" : "Brush.Text");
            if (normalized is null) return;
            ShowSample(normalized);
            _pick?.Invoke(normalized);
        };
        _hex.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
        hexRow.Children.Add(_hex);
        root.Children.Add(hexRow);

        _themeButton = Ui.Button("GhostButton", "ovl.color.theme", "Icon.Palette", (_, _) =>
        {
            _pick?.Invoke(null);
            Close();
        });
        _themeButton.Margin = new Thickness(0, 10, 0, 0);
        _themeButton.HorizontalAlignment = HorizontalAlignment.Left;
        root.Children.Add(_themeButton);

        var panel = new Border { Child = root, Margin = new Thickness(6, 4, 14, 16), Padding = new Thickness(12) };
        panel.SetResourceReference(FrameworkElement.StyleProperty, "Ov.Panel");
        _popup = new Popup
        {
            Child = panel,
            AllowsTransparency = true,
            StaysOpen = false,
            Placement = PlacementMode.Bottom,
            PopupAnimation = PopupAnimation.Fade,
            Focusable = false,
        };
        _popup.Closed += (_, _) => _pick = null;
    }

    private void FillSwatches()
    {
        _swatches!.Children.Clear();
        var p = ThemeManager.I.Current;
        Color[] colors =
        [
            p.Accent, p.Accent2, .. p.Series, p.Text, p.TextDim, p.Success, p.Warning, p.Danger, p.Info,
            Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0x0B, 0x0D, 0x12),
        ];
        foreach (var c in colors.Distinct())
        {
            var b = new Button { Background = new SolidColorBrush(c) };
            b.SetResourceReference(FrameworkElement.StyleProperty, "OvEd.Swatch");
            var text = ColorText.Format(ColorText.FromColor(Color.FromRgb(c.R, c.G, c.B)));
            b.ToolTip = text;
            b.Click += (_, _) =>
            {
                _syncing = true;
                _hex!.Text = text;
                _syncing = false;
                ShowSample(text);
                _pick?.Invoke(text);
                Close();
            };
            _swatches.Children.Add(b);
        }
    }

    private void ShowSample(string? color)
    {
        if (ColorText.TryParse(color, out Rgba c)) _sample!.Background = new SolidColorBrush(ColorText.ToColor(c));
        else _sample!.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceHover");
    }

    /// <summary>The button that shows a stored color (dot + hex, or "Theme") and opens the popover.</summary>
    public static Button ColorButton(EditorContext ctx, Func<string?> get, Action<string?> set, bool allowTheme = true, Func<Color>? themeColor = null)
    {
        var dot = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 7, 0) };
        dot.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderStrong");
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(dot);
        row.Children.Add(label);
        var button = new Button { Content = row, MinWidth = 112 };
        button.SetResourceReference(FrameworkElement.StyleProperty, "OvEd.ColorButton");

        void Show()
        {
            var v = get();
            if (ColorText.TryParse(v, out var c))
            {
                dot.Background = new SolidColorBrush(ColorText.ToColor(c));
                label.Text = ColorText.Format(c);
                label.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            }
            else
            {
                dot.Background = new SolidColorBrush(themeColor?.Invoke() ?? ThemeManager.I.Current.Text);
                label.Text = Loc.T("ovl.color.theme");
                label.ClearValue(TextBlock.FontFamilyProperty);
            }
        }

        Show();
        button.Click += (_, _) => ctx.Colors.Open(button, get(), allowTheme, v =>
        {
            set(v);
            Show();
        });
        return button;
    }
}
