using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using BetterVitals.Core;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.UI.Controls;

namespace BetterVitals.UI.Panel;

/// <summary>
/// Code-built building blocks of the Settings sections, About and the first-run guide. Everything references styles and
/// brushes by key (SetResourceReference), so the controls follow theme switches and render offscreen in tests without an
/// Application. Texts are Loc keys bound to the current language.
/// </summary>
internal static class SettingsUi
{
    [ThreadStatic]
    private static ResourceDictionary? _styles;

    /// <summary>SettingsStyles.xaml, loaded once per UI thread and merged into each section (shared instance).</summary>
    public static ResourceDictionary Styles => _styles ??=
        (ResourceDictionary)Application.LoadComponent(new Uri("/BetterVitals;component/UI/Panel/Settings/SettingsStyles.xaml", UriKind.Relative));

    public static Binding Tr(string key) => new($"[{key}]") { Source = Loc.I, Mode = BindingMode.OneWay };

    public static T Bind<T>(T element, DependencyProperty dp, string key) where T : DependencyObject
    {
        BindingOperations.SetBinding(element, dp, Tr(key));
        return element;
    }

    public static T Style<T>(T element, string styleKey) where T : FrameworkElement
    {
        element.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
        return element;
    }

    // ───────────── text ─────────────

    /// <summary>A wrapped text bound to a Loc key (null key = set Text yourself).</summary>
    public static TextBlock Text(string? key, double size = 12.5, string brush = "Brush.TextDim", FontWeight? weight = null)
    {
        var t = new TextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = weight ?? FontWeights.Normal };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        if (key is not null) t.SetBinding(TextBlock.TextProperty, Tr(key));
        return t;
    }

    public static TextBlock Hint(string? key)
    {
        var t = new TextBlock();
        t.SetResourceReference(FrameworkElement.StyleProperty, "Hint");
        if (key is not null) t.SetBinding(TextBlock.TextProperty, Tr(key));
        return t;
    }

    // ───────────── cards and rows ─────────────

    /// <summary>A card of SettingRows (rows draw their own separators; the last one is marked automatically).</summary>
    public static Border Rows(params FrameworkElement[] rows)
    {
        var panel = new StackPanel();
        foreach (var r in rows) panel.Children.Add(r);
        for (var i = panel.Children.Count - 1; i >= 0; i--)
        {
            if (panel.Children[i] is SettingRow { Visibility: Visibility.Visible } last)
            {
                last.IsLast = true;
                break;
            }
        }
        var card = new Border { Child = panel };
        card.SetResourceReference(FrameworkElement.StyleProperty, "CardRows");
        return card;
    }

    /// <summary>Re-marks the last visible row after rows were shown / hidden.</summary>
    public static void MarkLast(Border rowsCard)
    {
        if (rowsCard.Child is not System.Windows.Controls.Panel panel) return;
        var found = false;
        for (var i = panel.Children.Count - 1; i >= 0; i--)
        {
            if (panel.Children[i] is not SettingRow row) continue;
            row.IsLast = !found && row.Visibility == Visibility.Visible;
            if (row.IsLast) found = true;
        }
    }

    public static SettingRow Row(string titleKey, string? descKey, string? iconKey, object? content = null, bool stacked = false, string? infoKey = null)
    {
        var row = new SettingRow { Content = content, IsStacked = stacked, Info = infoKey ?? "" };
        row.SetBinding(SettingRow.TitleProperty, Tr(titleKey));
        if (descKey is not null) row.SetBinding(SettingRow.DescriptionProperty, Tr(descKey));
        if (iconKey is not null) row.SetResourceReference(SettingRow.IconProperty, iconKey);
        return row;
    }

    // ───────────── controls ─────────────

    public static CheckBox Switch(string nameKey)
    {
        var s = new CheckBox();
        s.SetResourceReference(FrameworkElement.StyleProperty, "Switch");
        BindingOperations.SetBinding(s, AutomationProperties.NameProperty, Tr(nameKey));
        return s;
    }

    /// <summary>A button with a Loc label and an optional leading icon.</summary>
    public static Button Button(string styleKey, string? labelKey, string? iconKey = null)
    {
        var b = new Button();
        b.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        if (labelKey is not null) label.SetBinding(TextBlock.TextProperty, Tr(labelKey));
        if (iconKey is null)
        {
            b.Content = label;
        }
        else
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = new Controls.Icon { Size = 15, Margin = new Thickness(0, 0, 7, 0) };
            icon.SetResourceReference(Controls.Icon.DataProperty, iconKey);
            row.Children.Add(icon);
            row.Children.Add(label);
            b.Content = row;
        }
        return b;
    }

    /// <summary>The label TextBlock of a button made by <see cref="Button"/> (to change the text later).</summary>
    public static TextBlock? ButtonLabel(Button b) => b.Content switch
    {
        TextBlock t => t,
        StackPanel p => p.Children.OfType<TextBlock>().FirstOrDefault(),
        _ => null,
    };

    public static void SetButtonText(Button b, string key)
    {
        if (ButtonLabel(b) is { } label) label.SetBinding(TextBlock.TextProperty, Tr(key));
    }

    public static StackPanel HStack(double gap, params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < children.Length; i++)
        {
            if (i > 0 && children[i] is FrameworkElement fe && fe.Margin == default) fe.Margin = new Thickness(gap, 0, 0, 0);
            p.Children.Add(children[i]);
        }
        return p;
    }

    public static Controls.Icon Glyph(string key, double size = 16, string brush = "Brush.TextDim")
    {
        var i = new Controls.Icon { Size = size };
        i.SetResourceReference(Controls.Icon.DataProperty, key);
        i.SetResourceReference(Controls.Icon.ForegroundProperty, brush);
        return i;
    }

    /// <summary>A section intro line under the section title (one plain sentence).</summary>
    public static TextBlock Lead(string key)
    {
        var t = Text(key, 12.5);
        t.Margin = new Thickness(4, -2, 0, 10);
        return t;
    }

    /// <summary>Megabytes / kilobytes in the UI language ("12,4 MB").</summary>
    public static string Bytes(long bytes)
    {
        var c = Loc.I.Culture;
        if (bytes < 0) bytes = 0;
        if (bytes < 1024 * 1024)
            return (bytes / 1024.0).ToString(bytes < 10 * 1024 ? "0.#" : "0", c) + " KB";
        if (bytes < 1024L * 1024 * 1024)
        {
            var mb = bytes / (1024.0 * 1024);
            return mb.ToString(mb < 10 ? "0.#" : "0", c) + " MB";
        }
        return (bytes / (1024.0 * 1024 * 1024)).ToString("0.##", c) + " GB";
    }
}

public enum PillKind : byte
{
    Neutral,
    Success,
    Warning,
    Danger,
    Accent,
}

/// <summary>A small status pill: a colored dot + short text on a soft tint ("Active", "Not installed").</summary>
internal sealed class StatusPill : Border
{
    private readonly TextBlock _text;
    private readonly System.Windows.Shapes.Ellipse _dot;

    public StatusPill()
    {
        CornerRadius = new CornerRadius(11);
        Padding = new Thickness(9, 3, 10, 4);
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Left;
        _dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        _text = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _dot, _text } };
        Set("", PillKind.Neutral);
    }

    public string Text => _text.Text;

    public PillKind Kind { get; private set; }

    /// <summary>A fixed label that follows the UI language.</summary>
    public void Bind(string key, PillKind kind)
    {
        Set("", kind);
        _text.SetBinding(TextBlock.TextProperty, SettingsUi.Tr(key));
    }

    public void Set(string text, PillKind kind)
    {
        BindingOperations.ClearBinding(_text, TextBlock.TextProperty);
        _text.Text = text;
        Kind = kind;
        var (fg, bg) = kind switch
        {
            PillKind.Success => ("Brush.Success", "Brush.SuccessSoft"),
            PillKind.Warning => ("Brush.Warning", "Brush.WarningSoft"),
            PillKind.Danger => ("Brush.Danger", "Brush.DangerSoft"),
            PillKind.Accent => ("Brush.AccentText", "Brush.AccentSoft"),
            _ => ("Brush.TextDim", "Brush.SurfaceHover"),
        };
        _dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, fg);
        _text.SetResourceReference(TextBlock.ForegroundProperty, kind == PillKind.Neutral ? "Brush.TextDim" : fg);
        SetResourceReference(BackgroundProperty, bg);
    }
}

/// <summary>A thin rounded progress track (downloads, installs). <see cref="Value"/> 0–1, or an endless sweep.</summary>
internal sealed class ProgressTrack : Grid
{
    private readonly Border _fill;
    private readonly ScaleTransform _scale = new(0, 1);
    private readonly TranslateTransform _move = new();

    public ProgressTrack(bool indeterminate = false)
    {
        Height = 6;
        ClipToBounds = true;
        var track = new Border { CornerRadius = new CornerRadius(3) };
        track.SetResourceReference(Border.BackgroundProperty, "Brush.ToggleOff");
        _fill = new Border
        {
            CornerRadius = new CornerRadius(3),
            RenderTransform = new TransformGroup { Children = { _scale, _move } },
            RenderTransformOrigin = new Point(0, 0.5),
        };
        _fill.SetResourceReference(Border.BackgroundProperty, "Brush.AccentGradientH");
        Children.Add(track);
        Children.Add(_fill);
        if (!indeterminate) return;
        Value = 0.3;
        // the sweep runs only while the track is on screen
        Motion.WhileVisible(this,
            () => _move.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(
                -0.3 * Math.Max(ActualWidth, 1), Math.Max(ActualWidth, 1), TimeSpan.FromMilliseconds(1300))
            {
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                EasingFunction = Motion.EaseOut,
            }),
            () => _move.BeginAnimation(TranslateTransform.XProperty, null));
    }

    public double Value
    {
        get => _scale.ScaleX;
        set => _scale.ScaleX = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
    }
}

/// <summary>A Combo of typed choices whose labels follow the UI language.</summary>
internal sealed class ChoiceCombo<T> : ComboBox where T : notnull
{
    private readonly List<(T Value, Func<string> Label)> _choices;
    private bool _syncing;

    public ChoiceCombo(IEnumerable<(T Value, Func<string> Label)> choices, double minWidth = 150)
    {
        _choices = choices.ToList();
        SetResourceReference(StyleProperty, "Combo");
        MinWidth = minWidth;
        foreach (var (v, _) in _choices) Items.Add(new ComboBoxItem { Tag = v });
        Relabel();
        SelectionChanged += (_, _) =>
        {
            if (!_syncing && SelectedItem is ComboBoxItem { Tag: T v }) ValueChanged?.Invoke(v);
        };
    }

    /// <summary>The user picked a value (not raised by <see cref="Value"/>).</summary>
    public event Action<T>? ValueChanged;

    public T? Value
    {
        get => SelectedItem is ComboBoxItem { Tag: T v } ? v : default;
        set
        {
            _syncing = true;
            try
            {
                var index = _choices.FindIndex(c => EqualityComparer<T>.Default.Equals(c.Value, value!));
                SelectedIndex = index;
            }
            finally
            {
                _syncing = false;
            }
        }
    }

    public IReadOnlyList<T> Values => _choices.Select(c => c.Value).ToList();

    /// <summary>Picks a value as if the user did (tests).</summary>
    internal void Pick(T value) => SelectedIndex = _choices.FindIndex(c => EqualityComparer<T>.Default.Equals(c.Value, value));

    public void Relabel()
    {
        _syncing = true;
        try
        {
            var selected = SelectedIndex;
            for (var i = 0; i < _choices.Count; i++) ((ComboBoxItem)Items[i]).Content = _choices[i].Label();
            // re-select so the closed box shows the new text
            SelectedIndex = -1;
            SelectedIndex = selected;
        }
        finally
        {
            _syncing = false;
        }
    }
}

/// <summary>Segmented choice (Base.xaml Segment / SegmentHost) of typed values.</summary>
internal sealed class Segments<T> : Border where T : notnull
{
    private readonly Dictionary<RadioButton, T> _values = new();
    private bool _syncing;

    public Segments(string group, IEnumerable<(T Value, string LabelKey)> options)
    {
        SetResourceReference(StyleProperty, "SegmentHost");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        // one group per instance (two panels / tests never share the radio group)
        var name = group + "-" + Guid.NewGuid().ToString("N");
        foreach (var (value, key) in options)
        {
            var rb = new RadioButton { GroupName = name };
            rb.SetResourceReference(StyleProperty, "Segment");
            rb.SetBinding(ContentControl.ContentProperty, SettingsUi.Tr(key));
            BindingOperations.SetBinding(rb, AutomationProperties.NameProperty, SettingsUi.Tr(key));
            rb.Checked += (_, _) =>
            {
                if (!_syncing) ValueChanged?.Invoke(value);
            };
            _values[rb] = value;
            row.Children.Add(rb);
        }
        Child = row;
    }

    public event Action<T>? ValueChanged;

    public T? Value
    {
        get => _values.FirstOrDefault(kv => kv.Key.IsChecked == true).Value;
        set
        {
            _syncing = true;
            try
            {
                foreach (var (rb, v) in _values) rb.IsChecked = EqualityComparer<T>.Default.Equals(v, value!);
            }
            finally
            {
                _syncing = false;
            }
        }
    }

    /// <summary>Picks a value as if the user clicked it (tests).</summary>
    internal void Pick(T value) => _values.First(kv => EqualityComparer<T>.Default.Equals(kv.Value, value)).Key.IsChecked = true;

    internal RadioButton ButtonFor(T value) => _values.First(kv => EqualityComparer<T>.Default.Equals(kv.Value, value)).Key;
}

/// <summary>
/// Base of the code-built Settings sections: merges the section styles, re-reads its state on language / settings changes
/// while it is loaded (and only then — a cached, hidden page costs nothing), and guards writes against its own syncing.
/// Derived constructors build the UI and then call <see cref="Initialize"/>.
/// </summary>
internal abstract class SectionView : UserControl
{
    private bool _hooked;

    protected SectionView(PageContext? context)
    {
        Context = context;
        Resources.MergedDictionaries.Add(SettingsUi.Styles);
        Loaded += (_, _) => Hook();
        Unloaded += (_, _) => Unhook();
    }

    protected PageContext? Context { get; }

    /// <summary>True while <see cref="Refresh"/> writes controls (their change handlers must not write settings back).</summary>
    protected bool Syncing { get; private set; }

    protected static SettingsStore Store => SettingsStore.I;

    protected static AppSettings Current => SettingsStore.I.Current;

    /// <summary>Settings sections ("general", "recording"…) this view shows; a change of another one is ignored.</summary>
    protected virtual IReadOnlyCollection<string> Watches => [];

    protected void Initialize() => Sync();

    /// <summary>Re-reads settings / services into the controls. Called inside <see cref="Syncing"/>.</summary>
    protected abstract void Refresh();

    /// <summary>Language changed: texts formatted in code (bound texts update by themselves).</summary>
    protected virtual void Relabel()
    {
    }

    /// <summary>Extra subscriptions while loaded (platform state, hub events).</summary>
    protected virtual void OnAttached()
    {
    }

    protected virtual void OnDetached()
    {
    }

    protected void Sync()
    {
        if (SettingsStore.I is null) return;
        Syncing = true;
        try
        {
            Refresh();
        }
        catch (Exception ex)
        {
            Log.Error($"settings: {GetType().Name} refresh failed", ex);
        }
        finally
        {
            Syncing = false;
        }
    }

    /// <summary>Runs on the UI thread (events from other threads are marshalled).</summary>
    protected void OnUi(Action a)
    {
        if (Dispatcher.CheckAccess()) a();
        else Dispatcher.BeginInvoke(a);
    }

    protected void Toast(string key, Toasts.ToastKind kind = Toasts.ToastKind.Info, params object?[] args)
        => Context?.Toast(args.Length == 0 ? Loc.T(key) : Loc.T(key, args), kind);

    /// <summary>Hooks as if loaded (tests that never put the view in a window).</summary>
    internal void AttachForTests() => Hook();

    internal void DetachForTests() => Unhook();

    private void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        Loc.LanguageChanged += OnLanguage;
        if (SettingsStore.I is { } s) s.Changed += OnSettings;
        OnAttached();
        Sync();
    }

    private void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        Loc.LanguageChanged -= OnLanguage;
        if (SettingsStore.I is { } s) s.Changed -= OnSettings;
        OnDetached();
    }

    private void OnLanguage() => OnUi(() =>
    {
        Relabel();
        Sync();
    });

    private void OnSettings(string section)
    {
        if (Watches.Count > 0 && !Watches.Contains(section)) return;
        OnUi(Sync);
    }
}
