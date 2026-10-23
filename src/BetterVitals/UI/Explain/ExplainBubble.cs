using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using BetterVitals.Localization;

namespace BetterVitals.UI.Explain;

/// <summary>The bubble's card: name (semibold), what it is, what to watch (dim), a sensor note (warning color).</summary>
public sealed class ExplainCard : Border
{
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _what = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 17 };
    private readonly TextBlock _watch = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 17, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _note = new() { FontSize = 11.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    public ExplainCard()
    {
        MaxWidth = 320;
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(12, 9, 13, 10);
        SnapsToDevicePixels = true;
        SetResourceReference(BackgroundProperty, "Brush.PanelSolid");
        SetResourceReference(BorderBrushProperty, "Brush.PanelBorder");
        SetResourceReference(TextElement.FontFamilyProperty, "Font.Ui");
        _title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelText");
        _what.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelText");
        _watch.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelTextDim");
        _note.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Warning");
        var stack = new StackPanel();
        stack.Children.Add(_title);
        stack.Children.Add(_what);
        stack.Children.Add(_watch);
        stack.Children.Add(_note);
        Child = stack;
    }

    public void SetContent(ExplainContent c)
    {
        _title.Text = c.Title ?? "";
        _title.Visibility = string.IsNullOrEmpty(c.Title) ? Visibility.Collapsed : Visibility.Visible;
        _what.Text = c.What;
        _what.Margin = new Thickness(0, string.IsNullOrEmpty(c.Title) ? 0 : 3, 0, 0);
        _watch.Text = c.Watch ?? "";
        _watch.Visibility = string.IsNullOrEmpty(c.Watch) ? Visibility.Collapsed : Visibility.Visible;
        _note.Text = c.Note ?? "";
        _note.Visibility = string.IsNullOrEmpty(c.Note) ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// <summary>
/// The bubble window: top-most, click-through, never activated (WS_EX_NOACTIVATE | TOOLWINDOW | TRANSPARENT), reused while
/// the app has windows and destroyed in the tray. Placed in physical pixels on the anchor's monitor (moved there first so
/// it takes that monitor's scaling), inside the work area. Fades in within 110 ms; hides at once.
/// </summary>
internal sealed class ExplainBubble : IExplainPresenter
{
    /// <summary>Transparent margin around the card (room for its shadow), DIP.</summary>
    private const double ShadowMargin = 12;

    /// <summary>Gap between the anchor and the card, DIP.</summary>
    private const double Gap = 6;

    private Window? _window;
    private ExplainCard? _card;

    public void Show(FrameworkElement anchor, ExplainContent content)
    {
        if (PresentationSource.FromVisual(anchor) is null) return;
        var w = Ensure();
        _card!.SetContent(content);
        w.Language = Loc.I.XmlLang;

        var topLeft = anchor.PointToScreen(new Point(0, 0));
        var bottomRight = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
        var a = new Native.RECT
        {
            Left = (int)Math.Min(topLeft.X, bottomRight.X), Top = (int)Math.Min(topLeft.Y, bottomRight.Y),
            Right = (int)Math.Max(topLeft.X, bottomRight.X), Bottom = (int)Math.Max(topLeft.Y, bottomRight.Y),
        };
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        _card.Opacity = 0;
        // onto the anchor's monitor first: the window takes that monitor's DPI before it is measured
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, a.Left, a.Bottom, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        if (!w.IsVisible) w.Show();
        w.UpdateLayout();
        Native.GetWindowRect(hwnd, out var r);
        var pxPerDip = r.Width / Math.Max(1, w.ActualWidth);
        var margin = ShadowMargin * pxPerDip;
        var (_, work) = Native.MonitorOf(a);
        var (x, y, _) = ExplainPlacement.Compute(
            new ExplainPlacement.PxRect(a.Left, a.Top, a.Width, a.Height),
            r.Width - 2 * margin, r.Height - 2 * margin,
            new ExplainPlacement.PxRect(work.Left, work.Top, work.Width, work.Height),
            Gap * pxPerDip);
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, (int)(x - margin), (int)(y - margin), 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

        if (Motion.I.Enabled)
            _card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110)) { EasingFunction = Motion.EaseOut });
        else
        {
            _card.BeginAnimation(UIElement.OpacityProperty, null);
            _card.Opacity = 1;
        }
    }

    public void Hide()
    {
        if (_window is null) return;
        _card?.BeginAnimation(UIElement.OpacityProperty, null);
        _window.Hide();
    }

    public void Release()
    {
        var w = _window;
        _window = null;
        _card = null;
        w?.Close();
    }

    private Window Ensure()
    {
        if (_window is not null) return _window;
        _card = new ExplainCard
        {
            Margin = new Thickness(ShadowMargin),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.35, Color = Themes.ThemeManager.ShadowColor },
        };
        var w = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Focusable = false,
            IsHitTestVisible = false,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Left = -32000,
            Top = -32000,
            Title = "BetterVitals",
            Content = _card,
            UseLayoutRounding = true,
        };
        w.SourceInitialized += (_, _) => WindowEffects.MakeNoActivate(w, clickThrough: true);
        TextOptions.SetTextFormattingMode(w, TextFormattingMode.Ideal);
        _window = w;
        return w;
    }
}
