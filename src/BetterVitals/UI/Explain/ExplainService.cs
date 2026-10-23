using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BetterVitals.Core.Glossary;
using BetterVitals.Localization;

namespace BetterVitals.UI.Explain;

/// <summary>What the bubble shows: the verbatim sensor name (or a title), what it is, what to watch, a sensor note.</summary>
public sealed record ExplainContent(string? Title, string What, string? Watch = null, string? Note = null);

/// <summary>Draws the bubble. The real one is <see cref="ExplainBubble"/>; tests use a fake.</summary>
internal interface IExplainPresenter
{
    void Show(FrameworkElement anchor, ExplainContent content);

    void Hide();

    /// <summary>Destroy the window (tray rest).</summary>
    void Release();
}

/// <summary>
/// The one explanation bubble of the app (PROJEKT §8 "Objaśnienia parametrów"): ~600 ms after the pointer rests on an
/// element with <see cref="Explain"/> content it opens next to it; it never takes focus and never hangs — it closes on
/// mouse leave, a click or key, wheel / scroll, window deactivation, minimize, move or close, the element unloading or
/// hiding, its DataContext / content changing (recycled list rows), a language change and the Explanations setting off.
/// Window and language hooks exist only while a bubble is pending or open.
/// </summary>
public sealed class ExplainService
{
    public static ExplainService I { get; internal set; } = new();

    private IExplainPresenter? _presenter;
    private DispatcherTimer? _timer;
    private FrameworkElement? _target;
    private Window? _window;
    private UIElement? _root;
    private bool _enabled = true;

    /// <summary>Time the pointer has to rest before the bubble opens.</summary>
    public TimeSpan HoverDelay { get; set; } = TimeSpan.FromMilliseconds(600);

    /// <summary>The "Explanations" setting (General.Explanations). Off closes the bubble and keeps it closed.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (!value) Close("disabled");
        }
    }

    /// <summary>A bubble is on screen.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>A bubble waits for the hover delay.</summary>
    public bool IsPending => _timer?.IsEnabled == true;

    /// <summary>The element the bubble belongs to (pending or open).</summary>
    public FrameworkElement? Target => _target;

    /// <summary>Why the last bubble closed (tests, logs).</summary>
    public string? LastCloseReason { get; private set; }

    /// <summary>What the open bubble shows.</summary>
    public ExplainContent? Content { get; private set; }

    internal IExplainPresenter Presenter
    {
        get => _presenter ??= new ExplainBubble();
        set => _presenter = value;
    }

    /// <summary>Can the element show a bubble right now (tests replace it: their elements are never rendered)?</summary>
    internal Func<FrameworkElement, bool> IsShowable { get; set; } = static el => el.IsVisible;

    // ───────────── element events (from Explain) ─────────────

    internal void OnEnter(FrameworkElement el)
    {
        if (!_enabled || !Explain.HasContent(el)) return;
        if (ReferenceEquals(_target, el) && (IsOpen || IsPending)) return;
        Close("retarget");

        _target = el;
        Attach(el);
        if (_timer is null || _timer.Dispatcher != el.Dispatcher)
        {
            _timer?.Stop();
            _timer = new DispatcherTimer(DispatcherPriority.Normal, el.Dispatcher);
            _timer.Tick += OnTimer;
        }
        _timer.Interval = HoverDelay;
        _timer.Start();
    }

    internal void OnLeave(FrameworkElement el)
    {
        if (ReferenceEquals(_target, el)) Close("leave");
    }

    internal void OnContentChanged(FrameworkElement el)
    {
        if (ReferenceEquals(_target, el)) Close("content");
    }

    private void OnTimer(object? sender, EventArgs e)
    {
        _timer?.Stop();
        if (_target is not { } el) return;
        if (!_enabled || !IsShowable(el))
        {
            Close("hidden");
            return;
        }
        ExplainContent? content;
        try
        {
            content = Resolve(el);
        }
        catch (Exception ex)
        {
            BetterVitals.Core.Log.Warn("explain: " + ex.Message);
            content = null;
        }
        if (content is null)
        {
            Close("empty");
            return;
        }
        try
        {
            Presenter.Show(el, content);
            Content = content;
            IsOpen = true;
        }
        catch (Exception ex)
        {
            BetterVitals.Core.Log.Warn("explain: show failed: " + ex.Message);
            Close("error");
        }
    }

    /// <summary>Closes the bubble (or cancels a pending one) and drops every hook. Safe to call any time.</summary>
    public void Close(string reason = "close")
    {
        _timer?.Stop();
        if (_target is null && !IsOpen) return;
        Detach();
        if (IsOpen)
        {
            try
            {
                _presenter?.Hide();
            }
            catch (Exception ex)
            {
                BetterVitals.Core.Log.Warn("explain: hide failed: " + ex.Message);
            }
        }
        IsOpen = false;
        Content = null;
        _target = null;
        LastCloseReason = reason;
    }

    /// <summary>Tray rest: close and destroy the bubble window (it is created again on the next hover).</summary>
    public void Release()
    {
        Close("release");
        _presenter?.Release();
    }

    /// <summary>Bubble text for an element: sensor (glossary in the UI language), Loc key or literal text.</summary>
    public static ExplainContent? Resolve(DependencyObject el)
    {
        if (Explain.GetSensor(el) is { } s)
        {
            var polish = Loc.I.IsPolish;
            var x = Glossary.Lookup(s.Kind, s.HardwareKind, s.Name, polish);
            if (x is null || string.IsNullOrWhiteSpace(x.What)) return null;
            var note = s.NoteKey is { Length: > 0 } key && Loc.Has(key) ? Loc.T(key) : null;
            return new ExplainContent(s.Name, x.What, string.IsNullOrWhiteSpace(x.Watch) ? null : x.Watch, note);
        }
        var title = Explain.GetTitle(el);
        if (Explain.GetKey(el) is { Length: > 0 } k) return new ExplainContent(title, Loc.T(k));
        if (Explain.GetText(el) is { Length: > 0 } t) return new ExplainContent(title, t);
        return null;
    }

    // ───────────── hooks while pending / open ─────────────

    private void Attach(FrameworkElement el)
    {
        el.Unloaded += OnTargetUnloaded;
        el.IsVisibleChanged += OnTargetVisibleChanged;
        el.DataContextChanged += OnTargetDataContextChanged;

        _window = Window.GetWindow(el);
        if (_window is not null)
        {
            _window.Deactivated += OnWindowDeactivated;
            _window.StateChanged += OnWindowStateChanged;
            _window.LocationChanged += OnWindowMoved;
            _window.Closed += OnWindowClosed;
            _root = _window;
        }
        else
        {
            _root = PresentationSource.FromVisual(el)?.RootVisual as UIElement ?? TopAncestor(el);
        }
        if (_root is not null)
        {
            _root.AddHandler(UIElement.PreviewMouseWheelEvent, (MouseWheelEventHandler)OnWheel, true);
            _root.AddHandler(ScrollViewer.ScrollChangedEvent, (ScrollChangedEventHandler)OnScroll, true);
            _root.AddHandler(UIElement.PreviewMouseDownEvent, (MouseButtonEventHandler)OnMouseDown, true);
            _root.AddHandler(UIElement.PreviewKeyDownEvent, (KeyEventHandler)OnKeyDown, true);
        }
        Loc.LanguageChanged += OnLanguageChanged;
    }

    private void Detach()
    {
        if (_target is { } el)
        {
            el.Unloaded -= OnTargetUnloaded;
            el.IsVisibleChanged -= OnTargetVisibleChanged;
            el.DataContextChanged -= OnTargetDataContextChanged;
        }
        if (_window is not null)
        {
            _window.Deactivated -= OnWindowDeactivated;
            _window.StateChanged -= OnWindowStateChanged;
            _window.LocationChanged -= OnWindowMoved;
            _window.Closed -= OnWindowClosed;
            _window = null;
        }
        if (_root is not null)
        {
            _root.RemoveHandler(UIElement.PreviewMouseWheelEvent, (MouseWheelEventHandler)OnWheel);
            _root.RemoveHandler(ScrollViewer.ScrollChangedEvent, (ScrollChangedEventHandler)OnScroll);
            _root.RemoveHandler(UIElement.PreviewMouseDownEvent, (MouseButtonEventHandler)OnMouseDown);
            _root.RemoveHandler(UIElement.PreviewKeyDownEvent, (KeyEventHandler)OnKeyDown);
            _root = null;
        }
        Loc.LanguageChanged -= OnLanguageChanged;
    }

    private static UIElement? TopAncestor(DependencyObject el)
    {
        DependencyObject? cur = el;
        UIElement? top = el as UIElement;
        while (cur is not null)
        {
            if (cur is UIElement u) top = u;
            cur = cur is Visual ? VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur);
        }
        return top;
    }

    private void OnTargetUnloaded(object sender, RoutedEventArgs e) => Close("unloaded");

    private void OnTargetVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false) Close("hidden");
    }

    private void OnTargetDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => Close("recycled");

    private void OnWindowDeactivated(object? sender, EventArgs e) => Close("deactivated");

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (sender is Window { WindowState: WindowState.Minimized }) Close("minimized");
    }

    private void OnWindowMoved(object? sender, EventArgs e) => Close("moved");

    private void OnWindowClosed(object? sender, EventArgs e) => Close("window-closed");

    private void OnWheel(object sender, MouseWheelEventArgs e) => Close("wheel");

    private void OnScroll(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0 || e.HorizontalChange != 0 || e.ExtentHeightChange != 0) Close("scroll");
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e) => Close("click");

    private void OnKeyDown(object sender, KeyEventArgs e) => Close("key");

    private void OnLanguageChanged() => Close("language");
}
