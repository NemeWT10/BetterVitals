using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BetterVitals.Core;

namespace BetterVitals.UI;

/// <summary>
/// Micro-interactions (≤ 400 ms) — "dopamine = motion, not colors". Everything respects <see cref="Enabled"/> (the
/// Animations setting AND Windows "Animation effects"; XAML templates bind {Binding Enabled, Source={x:Static ui:Motion.I}}).
/// Endless animations only through <see cref="WhileVisible"/>. Ported from BetterNotepad.
/// </summary>
public sealed class Motion : ObservableObject
{
    public static Motion I { get; } = new();

    private bool _enabled = true;

    /// <summary>Animations setting AND Windows "Animation effects".</summary>
    public bool Enabled { get => _enabled; private set => Set(ref _enabled, value); }

    public void Update(bool settingEnabled) => Enabled = settingEnabled && SystemParameters.ClientAreaAnimation;

    public static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction EaseIn = Freeze(new CubicEase { EasingMode = EasingMode.EaseIn });
    public static readonly IEasingFunction EaseBack = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });
    public static readonly IEasingFunction EaseSpring = Freeze(new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 6 });

    private static IEasingFunction Freeze(EasingFunctionBase e)
    {
        e.Freeze();
        return e;
    }

    // ───────────── transforms ─────────────

    /// <summary>Gives the element a Scale + Translate transform pair we can animate.</summary>
    public static (ScaleTransform Scale, TranslateTransform Translate) Transforms(UIElement el)
    {
        if (el.RenderTransform is TransformGroup g && g.Children.Count == 2 &&
            g.Children[0] is ScaleTransform s && g.Children[1] is TranslateTransform t && !g.IsFrozen)
            return (s, t);
        var scale = new ScaleTransform();
        var translate = new TranslateTransform();
        el.RenderTransform = new TransformGroup { Children = { scale, translate } };
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        return (scale, translate);
    }

    private static void Animate(IAnimatable target, DependencyProperty dp, double to, double ms, IEasingFunction? ease = null, double? from = null)
    {
        var a = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = ease ?? EaseOut };
        if (from.HasValue) a.From = from;
        target.BeginAnimation(dp, a);
    }

    private static void AnimateKeys(IAnimatable target, DependencyProperty dp, params (double Value, double Ms)[] keys)
    {
        var a = new DoubleAnimationUsingKeyFrames();
        foreach (var (v, ms) in keys)
            a.KeyFrames.Add(new EasingDoubleKeyFrame(v, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), EaseOut));
        target.BeginAnimation(dp, a);
    }

    // ───────────── one-shots ─────────────

    /// <summary>Icon bounce: 1 → 1.28 → 0.92 → 1 (BetterShot's selection bounce).</summary>
    public static void Bounce(UIElement el, double peak = 1.28)
    {
        if (!I.Enabled) return;
        var (s, _) = Transforms(el);
        (double, double)[] keys = [(peak, 120), (0.92, 260), (1, 400)];
        AnimateKeys(s, ScaleTransform.ScaleXProperty, keys);
        AnimateKeys(s, ScaleTransform.ScaleYProperty, keys);
    }

    /// <summary>Panel / popover appear: fade + small rise + spring scale.</summary>
    public static void Appear(UIElement el, double rise = 8, double fromScale = 0.97, double ms = 260)
    {
        if (!I.Enabled)
        {
            el.Opacity = 1;
            return;
        }
        var (s, t) = Transforms(el);
        Animate(el, UIElement.OpacityProperty, 1, ms * 0.8, EaseOut, 0);
        Animate(t, TranslateTransform.YProperty, 0, ms, EaseBack, rise);
        Animate(s, ScaleTransform.ScaleXProperty, 1, ms, EaseBack, fromScale);
        Animate(s, ScaleTransform.ScaleYProperty, 1, ms, EaseBack, fromScale);
    }

    /// <summary>Fade + sink, then <paramref name="done"/>.</summary>
    public static void Disappear(UIElement el, Action? done = null, double sink = 6, double ms = 160)
    {
        if (!I.Enabled)
        {
            done?.Invoke();
            return;
        }
        var (_, t) = Transforms(el);
        var fade = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = EaseOut };
        if (done is not null) fade.Completed += (_, _) => done();
        el.BeginAnimation(UIElement.OpacityProperty, fade);
        Animate(t, TranslateTransform.YProperty, sink, ms);
    }

    /// <summary>Slide in horizontally (e.g. the settings page).</summary>
    public static void SlideIn(UIElement el, double fromX = 24, double ms = 300)
    {
        if (!I.Enabled)
        {
            el.Opacity = 1;
            return;
        }
        var (_, t) = Transforms(el);
        Animate(el, UIElement.OpacityProperty, 1, ms * 0.7, EaseOut, 0);
        Animate(t, TranslateTransform.XProperty, 0, ms, EaseBack, fromX);
    }

    /// <summary>Width spring from 0 (e.g. the active-tab accent line).</summary>
    public static void GrowX(UIElement el, double ms = 280)
    {
        if (!I.Enabled) return;
        var (s, _) = Transforms(el);
        Animate(s, ScaleTransform.ScaleXProperty, 1, ms, EaseBack, 0.2);
    }

    /// <summary>Gentle attention shake (invalid input).</summary>
    public static void Shake(UIElement el)
    {
        if (!I.Enabled) return;
        var (_, t) = Transforms(el);
        AnimateKeys(t, TranslateTransform.XProperty, (-6, 60), (5, 120), (-3, 180), (2, 240), (0, 300));
    }

    // ───────────── endless animations, only while visible ─────────────

    /// <summary>
    /// An endless ambient animation (a breathing dot, a sweep) at a capped frame rate: at WPF's default 60 fps a small
    /// pulse redraws the whole transparent window and costs most of a core in software rendering.
    /// </summary>
    public static T Throttled<T>(T animation, int fps = 20) where T : Timeline
    {
        Timeline.SetDesiredFrameRate(animation, fps);
        return animation;
    }

    /// <summary>
    /// An endless animation of one property whose clock really stops. <c>BeginAnimation(dp, null)</c> only takes the
    /// animation off the property: its endless clock stays active in WPF's time manager and keeps it rendering frames
    /// (hundreds of UI-thread wake-ups a second, also in the tray) until a garbage collection happens to collect it, and
    /// each restart left one more. <see cref="Start"/> is a no-op while running; <see cref="Stop"/> removes the clock.
    /// </summary>
    public sealed class Loop(IAnimatable target, DependencyProperty property, Func<AnimationTimeline> animation)
    {
        private AnimationClock? _clock;

        public bool IsRunning => _clock is not null;

        public void Start()
        {
            if (_clock is not null) return;
            _clock = animation().CreateClock();
            target.ApplyAnimationClock(property, _clock);
        }

        public void Stop()
        {
            if (_clock is not { } clock) return;
            _clock = null;
            clock.Controller?.Remove();
            target.ApplyAnimationClock(property, null);
        }
    }

    /// <summary><see cref="WhileVisible"/> for a <see cref="Loop"/>.</summary>
    public static Loop LoopWhileVisible(FrameworkElement el, IAnimatable target, DependencyProperty property, Func<AnimationTimeline> animation)
    {
        var loop = new Loop(target, property, animation);
        WhileVisible(el, loop.Start, loop.Stop);
        return loop;
    }

    /// <summary>
    /// Runs <paramref name="start"/> while the element is visible and its window is not minimized
    /// and calls <paramref name="stop"/> otherwise — WPF would otherwise render frames forever, also in the tray.
    /// </summary>
    public static void WhileVisible(FrameworkElement el, Action start, Action stop)
    {
        var running = false;
        Window? window = null;

        void Evaluate()
        {
            var shouldRun = I.Enabled && el.IsVisible && window is { WindowState: not WindowState.Minimized, IsVisible: true };
            if (shouldRun == running) return;
            running = shouldRun;
            if (running) start();
            else stop();
        }

        void OnWindowState(object? s, EventArgs e) => Evaluate();
        void OnVisible(object s, DependencyPropertyChangedEventArgs e) => Evaluate();
        void OnMotion(object? s, System.ComponentModel.PropertyChangedEventArgs e) => Evaluate();

        el.Loaded += (_, _) =>
        {
            window = Window.GetWindow(el);
            if (window is not null) window.StateChanged += OnWindowState;
            el.IsVisibleChanged += OnVisible;
            I.PropertyChanged += OnMotion;
            Evaluate();
        };
        el.Unloaded += (_, _) =>
        {
            if (window is not null) window.StateChanged -= OnWindowState;
            el.IsVisibleChanged -= OnVisible;
            I.PropertyChanged -= OnMotion;
            if (running) stop();
            running = false;
            window = null;
        };
    }

    // ───────────── attached: hover lift ─────────────

    public static readonly DependencyProperty HoverLiftProperty = DependencyProperty.RegisterAttached(
        "HoverLift", typeof(double), typeof(Motion), new PropertyMetadata(0.0, OnHoverLiftChanged));

    public static double GetHoverLift(DependencyObject d) => (double)d.GetValue(HoverLiftProperty);

    public static void SetHoverLift(DependencyObject d, double value) => d.SetValue(HoverLiftProperty, value);

    private static void OnHoverLiftChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        el.MouseEnter -= LiftEnter;
        el.MouseLeave -= LiftLeave;
        if ((double)e.NewValue != 0)
        {
            el.MouseEnter += LiftEnter;
            el.MouseLeave += LiftLeave;
        }
    }

    private static void LiftEnter(object sender, MouseEventArgs e)
    {
        if (!I.Enabled || sender is not UIElement el) return;
        var (_, t) = Transforms(el);
        Animate(t, TranslateTransform.YProperty, -GetHoverLift(el), 140, EaseBack);
    }

    private static void LiftLeave(object sender, MouseEventArgs e)
    {
        if (sender is not UIElement el) return;
        var (_, t) = Transforms(el);
        Animate(t, TranslateTransform.YProperty, 0, 120);
    }

    // ───────────── attached: checked / unchecked transitions of toggle templates ─────────────

    /// <summary>
    /// Animated transition of a ToggleButton template (Switch, Segment, NavItem). The template's triggers set the end state
    /// with plain setters; this only animates a real change of IsChecked: never on first display, never with animations off.
    /// Spec: "Part.Property:from&gt;to[|from&gt;to]:ms[:back]" separated by ';'. The first pair plays when checking, the second
    /// (default: the first reversed) when unchecking. Animations stop at their end, so the setters' values remain.
    /// </summary>
    public static readonly DependencyProperty ToggleProperty = DependencyProperty.RegisterAttached(
        "Toggle", typeof(string), typeof(Motion), new PropertyMetadata(null, OnToggleChanged));

    public static string? GetToggle(DependencyObject d) => (string?)d.GetValue(ToggleProperty);

    public static void SetToggle(DependencyObject d, string? value) => d.SetValue(ToggleProperty, value);

    private static void OnToggleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not System.Windows.Controls.Primitives.ToggleButton tb) return;
        tb.Checked -= OnToggled;
        tb.Unchecked -= OnToggled;
        if (e.NewValue is string { Length: > 0 })
        {
            tb.Checked += OnToggled;
            tb.Unchecked += OnToggled;
        }
    }

    private static void OnToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton tb || !ReferenceEquals(e.OriginalSource, tb)) return;
        if (!I.Enabled || !tb.IsLoaded || !tb.IsVisible) return;
        var on = tb.IsChecked == true;
        foreach (var (target, dp, onFrom, onTo, offFrom, offTo, ms, back) in ToggleParts(tb))
        {
            var a = new DoubleAnimation(on ? onFrom : offFrom, on ? onTo : offTo, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = on && back ? EaseBack : EaseOut,
                FillBehavior = FillBehavior.Stop,
            };
            target.BeginAnimation(dp, a);
        }
    }

    /// <summary>The parts named by the spec, resolved in the applied template (for tests too).</summary>
    internal static List<(IAnimatable Target, DependencyProperty Dp, double OnFrom, double OnTo, double OffFrom, double OffTo, double Ms, bool Back)> ToggleParts(FrameworkElement control)
    {
        var list = new List<(IAnimatable, DependencyProperty, double, double, double, double, double, bool)>();
        if (GetToggle(control) is not { } spec || (control as System.Windows.Controls.Control)?.Template is not { } template) return list;
        static double N(string s) => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = part.Split(':');
            var dot = f[0].LastIndexOf('.');
            if (f.Length < 3 || dot <= 0) continue;
            if (template.FindName(f[0][..dot], control) is not IAnimatable target) continue;
            DependencyProperty? dp = f[0][(dot + 1)..] switch
            {
                "Opacity" => UIElement.OpacityProperty,
                "X" => TranslateTransform.XProperty,
                "Y" => TranslateTransform.YProperty,
                "ScaleX" => ScaleTransform.ScaleXProperty,
                "ScaleY" => ScaleTransform.ScaleYProperty,
                _ => null,
            };
            if (dp is null) continue;
            var pairs = f[1].Split('|');
            var onPair = pairs[0].Split('>');
            var offPair = pairs.Length > 1 ? pairs[1].Split('>') : [onPair[1], onPair[0]];
            list.Add((target, dp, N(onPair[0]), N(onPair[1]), N(offPair[0]), N(offPair[1]), N(f[2]), f.Length > 3 && f[3] == "back"));
        }
        return list;
    }
}
