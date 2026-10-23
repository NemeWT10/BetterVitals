using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using BetterVitals.Localization;
using BetterVitals.UI.Controls;

namespace BetterVitals.UI.Pages.OverlayEditor;

/// <summary>Small builders for the editor's code-built UI (shell styles from Base.xaml, texts as live Loc bindings).</summary>
internal static class Ui
{
    public static Binding Tr(string key) => new($"[{key}]") { Source = Loc.I, Mode = BindingMode.OneWay };

    public static TextBlock Text(string key, string? style = null, double? size = null, bool wrap = false)
    {
        var t = new TextBlock();
        if (style is not null) t.SetResourceReference(FrameworkElement.StyleProperty, style);
        t.SetBinding(TextBlock.TextProperty, Tr(key));
        if (size is { } s) t.FontSize = s;
        if (wrap) t.TextWrapping = TextWrapping.Wrap;
        return t;
    }

    /// <summary>A literal (already localized / verbatim) text.</summary>
    public static TextBlock Plain(string text, double size = 13, string brush = "Brush.Text", FontWeight? weight = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    public static TextBlock Hint(string key, Thickness? margin = null)
    {
        var t = Text(key, "Hint");
        t.Margin = margin ?? new Thickness(0, 4, 0, 0);
        return t;
    }

    public static Icon Icon(string key, double size = 18, string brush = "Brush.TextDim", double stroke = 1.5)
    {
        var i = new Icon { Size = size, StrokeThickness = stroke };
        i.SetResourceReference(Controls.Icon.DataProperty, key);
        i.SetResourceReference(Controls.Icon.ForegroundProperty, brush);
        return i;
    }

    /// <summary>An icon that takes the button's foreground (hover / checked states).</summary>
    public static Icon InheritIcon(string key, double size = 16, double stroke = 1.5)
    {
        var i = new Icon { Size = size, StrokeThickness = stroke };
        i.SetResourceReference(Controls.Icon.DataProperty, key);
        return i;
    }

    public static Border Card(UIElement child, Thickness? padding = null)
    {
        var b = new Border { Child = child };
        b.SetResourceReference(FrameworkElement.StyleProperty, "Card");
        if (padding is { } p) b.Padding = p;
        return b;
    }

    /// <summary>A card with a numbered or iconned header: title, one plain sentence, then the content.</summary>
    public static Border Section(string titleKey, string? hintKey, UIElement content, string? iconKey = null, string? step = null, UIElement? headerRight = null)
    {
        var root = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = true };
        if (headerRight is not null)
        {
            DockPanel.SetDock(headerRight, Dock.Right);
            head.Children.Add(headerRight);
        }
        if (step is not null || iconKey is not null)
        {
            var badge = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
            badge.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSoft");
            if (step is not null)
            {
                var n = new TextBlock { Text = step, FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                n.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentText");
                badge.Child = n;
            }
            else
            {
                badge.Child = Icon(iconKey!, 16, "Brush.AccentText");
            }
            head.Children.Add(badge);
        }
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = Text(titleKey, size: 14.5);
        title.FontWeight = FontWeights.SemiBold;
        title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        titles.Children.Add(title);
        if (hintKey is not null) titles.Children.Add(Hint(hintKey, new Thickness(0, 2, 0, 0)));
        head.Children.Add(titles);
        root.Children.Add(head);
        root.Children.Add(content);
        return Card(root);
    }

    public static Button Button(string style, string key, string? iconKey = null, RoutedEventHandler? click = null)
    {
        var b = new Button();
        b.SetResourceReference(FrameworkElement.StyleProperty, style);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (iconKey is not null)
        {
            var icon = InheritIcon(iconKey, 15);
            icon.Margin = new Thickness(0, 0, 7, 0);
            row.Children.Add(icon);
        }
        var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        t.SetBinding(TextBlock.TextProperty, Tr(key));
        row.Children.Add(t);
        b.Content = row;
        if (click is not null) b.Click += click;
        return b;
    }

    public static Button IconButton(string iconKey, string tipKey, RoutedEventHandler click, double size = 16)
    {
        var b = new Button { Content = InheritIcon(iconKey, size) };
        b.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
        b.SetBinding(FrameworkElement.ToolTipProperty, Tr(tipKey));
        b.Click += click;
        return b;
    }

    public static CheckBox Switch(bool value, Action<bool> changed)
    {
        var c = new CheckBox { IsChecked = value };
        c.SetResourceReference(FrameworkElement.StyleProperty, "Switch");
        c.Checked += (_, _) => changed(true);
        c.Unchecked += (_, _) => changed(false);
        return c;
    }

    /// <summary>Label on the left, control on the right (a compact setting line inside a card).</summary>
    public static DockPanel Line(string key, UIElement control, string? hintKey = null, double top = 10)
    {
        var d = new DockPanel { Margin = new Thickness(0, top, 0, 0), LastChildFill = true };
        DockPanel.SetDock(control, Dock.Right);
        if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        d.Children.Add(control);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        var t = Text(key, size: 13);
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        t.TextWrapping = TextWrapping.Wrap;
        texts.Children.Add(t);
        if (hintKey is not null) texts.Children.Add(Hint(hintKey, new Thickness(0, 2, 0, 0)));
        d.Children.Add(texts);
        return d;
    }

    /// <summary>A label above a full-width control.</summary>
    public static StackPanel Stacked(string key, UIElement control, double top = 12)
    {
        var s = new StackPanel { Margin = new Thickness(0, top, 0, 0) };
        var t = Text(key, size: 12.5);
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        t.Margin = new Thickness(0, 0, 0, 6);
        s.Children.Add(t);
        s.Children.Add(control);
        return s;
    }

    public static System.Windows.Shapes.Rectangle Divider(double top = 14, double bottom = 4)
    {
        var r = new System.Windows.Shapes.Rectangle { Margin = new Thickness(0, top, 0, bottom) };
        r.SetResourceReference(FrameworkElement.StyleProperty, "Divider");
        return r;
    }

    public static string NewGroup() => "oved-" + Guid.NewGuid().ToString("N");
}

/// <summary>A segmented choice (Segment + SegmentHost from Base.xaml) bound to a value of <typeparamref name="T"/>.</summary>
internal sealed class SegmentBar<T> : Border where T : notnull
{
    private readonly List<(T Value, RadioButton Button)> _buttons = [];
    private readonly Action<T> _changed;
    private bool _syncing;

    public SegmentBar(IEnumerable<(T Value, string Key)> options, T current, Action<T> changed, bool literal = false, double fontSize = 12.5)
    {
        _changed = changed;
        SetResourceReference(StyleProperty, "SegmentHost");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var group = Ui.NewGroup();
        foreach (var (value, key) in options)
        {
            var rb = new RadioButton { GroupName = group, FontSize = fontSize };
            rb.SetResourceReference(StyleProperty, "Segment");
            // "=text" is a literal label (1080p, 4K…), anything else a Loc key
            if (literal) rb.Content = key;
            else if (key.StartsWith('=')) rb.Content = key[1..];
            else rb.SetBinding(ContentControl.ContentProperty, Ui.Tr(key));
            var v = value;
            rb.Checked += (_, _) =>
            {
                if (!_syncing) _changed(v);
            };
            _buttons.Add((value, rb));
            row.Children.Add(rb);
        }
        Child = row;
        Set(current);
    }

    /// <summary>Content of one option (icons instead of text).</summary>
    public RadioButton ButtonOf(T value) => _buttons.First(b => EqualityComparer<T>.Default.Equals(b.Value, value)).Button;

    public void Set(T value)
    {
        _syncing = true;
        try
        {
            foreach (var (v, b) in _buttons) b.IsChecked = EqualityComparer<T>.Default.Equals(v, value);
        }
        finally
        {
            _syncing = false;
        }
    }
}

/// <summary>A slider with its value on the right; drags are deferred-committed (debounced by the page).</summary>
internal sealed class SliderLine : DockPanel
{
    private readonly Slider _slider;
    private readonly TextBlock _value;
    private readonly Func<double, string> _format;
    private bool _syncing;

    public SliderLine(double min, double max, double value, double step, Func<double, string> format, Action<double> changed, double width = 170)
    {
        _format = format;
        LastChildFill = false;
        _value = new TextBlock { MinWidth = 52, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5, Margin = new Thickness(10, 0, 0, 0) };
        _value.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        System.Windows.Documents.Typography.SetNumeralAlignment(_value, FontNumeralAlignment.Tabular);
        SetDock(_value, Dock.Right);
        Children.Add(_value);
        _slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            SmallChange = step,
            LargeChange = step * 4,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            Width = width,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _slider.SetResourceReference(StyleProperty, "Slider");
        SetDock(_slider, Dock.Right);
        Children.Add(_slider);
        _value.Text = format(value);
        _slider.ValueChanged += (_, e) =>
        {
            _value.Text = _format(e.NewValue);
            if (!_syncing) changed(e.NewValue);
        };
    }

    public Slider Slider => _slider;

    public void Set(double value)
    {
        _syncing = true;
        try
        {
            _slider.Value = value;
            _value.Text = _format(value);
        }
        finally
        {
            _syncing = false;
        }
    }
}
