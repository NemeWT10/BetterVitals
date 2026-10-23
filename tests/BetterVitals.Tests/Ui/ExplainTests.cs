using Xunit;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using BetterVitals.Core.Glossary;
using BetterVitals.Core.Sensors;
using BetterVitals.Localization;
using BetterVitals.UI.Explain;
using ExplainProps = BetterVitals.UI.Explain.Explain;

namespace BetterVitals.Tests.Ui;

/// <summary>
/// The explanation bubble must open after the hover delay and never hang: every closing trigger of PROJEKT §8 is
/// checked here headless (a fake presenter instead of the bubble window; windows are never shown).
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ExplainTests
{
    private sealed class FakePresenter : IExplainPresenter
    {
        public int Shows, Hides, Releases;
        public ExplainContent? Last;

        public void Show(FrameworkElement anchor, ExplainContent content)
        {
            Shows++;
            Last = content;
        }

        public void Hide() => Hides++;

        public void Release() => Releases++;
    }

    /// <summary>A window that lets the test raise what Windows would (deactivation, state change) without showing it.</summary>
    private sealed class TestWindow : Window
    {
        public void FireDeactivated() => OnDeactivated(EventArgs.Empty);

        public void FireStateChanged() => OnStateChanged(EventArgs.Empty);
    }

    private static (ExplainService Service, FakePresenter Presenter) NewService()
    {
        var presenter = new FakePresenter();
        var service = new ExplainService { HoverDelay = TimeSpan.FromMilliseconds(40), Presenter = presenter, IsShowable = _ => true };
        ExplainService.I = service;
        return (service, presenter);
    }

    private static Border Target(string text = "Hello")
    {
        var el = new Border();
        ExplainProps.SetText(el, text);
        return el;
    }

    private static void Enter(UIElement el) => el.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });

    private static void Leave(UIElement el) => el.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseLeaveEvent });

    private static void OpenOn(ExplainService s, FrameworkElement el)
    {
        Enter(el);
        Assert.True(Sta.PumpUntil(() => s.IsOpen), "the bubble did not open");
    }

    [Fact]
    public void Opens_after_the_hover_delay_with_the_text() => Sta.Run(() =>
    {
        var (s, p) = NewService();
        s.HoverDelay = TimeSpan.FromMilliseconds(150);
        var el = Target("Core voltage requested by the CPU");
        Enter(el);
        Assert.False(s.IsOpen);
        Assert.True(s.IsPending);
        Sta.Pump(60);
        Assert.False(s.IsOpen); // still waiting
        Assert.True(Sta.PumpUntil(() => s.IsOpen));
        Assert.Equal(1, p.Shows);
        Assert.Equal("Core voltage requested by the CPU", p.Last!.What);
        Assert.Same(el, s.Target);
    });

    [Fact]
    public void Default_hover_delay_is_about_600_ms() => Assert.InRange(new ExplainService().HoverDelay.TotalMilliseconds, 500, 700);

    [Fact]
    public void Leaving_before_the_delay_never_opens() => Sta.Run(() =>
    {
        var (s, p) = NewService();
        var el = Target();
        Enter(el);
        Leave(el);
        Sta.Pump(150);
        Assert.False(s.IsOpen);
        Assert.Equal(0, p.Shows);
        Assert.Equal("leave", s.LastCloseReason);
        Assert.Null(s.Target);
    });

    [Fact]
    public void Mouse_leave_closes() => Sta.Run(() =>
    {
        var (s, p) = NewService();
        var el = Target();
        OpenOn(s, el);
        Leave(el);
        Assert.False(s.IsOpen);
        Assert.Equal(1, p.Hides);
        Assert.Equal("leave", s.LastCloseReason);
    });

    [Fact]
    public void Another_element_takes_over() => Sta.Run(() =>
    {
        var (s, p) = NewService();
        var a = Target("A");
        var b = Target("B");
        OpenOn(s, a);
        Enter(b);
        Assert.False(s.IsOpen);
        Assert.True(Sta.PumpUntil(() => s.IsOpen));
        Assert.Equal("B", p.Last!.What);
        Assert.Equal(2, p.Shows);
        Assert.Equal(1, p.Hides);
    });

    [Fact]
    public void Mouse_wheel_closes() => Sta.Run(() =>
    {
        var (s, _) = NewService();
        var el = Target();
        var w = new TestWindow { Content = el };
        OpenOn(s, el);
        w.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
        Assert.False(s.IsOpen);
        Assert.Equal("wheel", s.LastCloseReason);
    });

    [Fact]
    public void Click_closes() => Sta.Run(() =>
    {
        var (s, _) = NewService();
        var el = Target();
        var w = new TestWindow { Content = el };
        OpenOn(s, el);
        w.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
        Assert.False(s.IsOpen);
        Assert.Equal("click", s.LastCloseReason);
    });

    [Fact]
    public void Window_deactivation_closes() => Sta.Run(() =>
    {
        var (s, _) = NewService();
        var el = Target();
        var w = new TestWindow { Content = el };
        OpenOn(s, el);
        w.FireDeactivated();
        Assert.False(s.IsOpen);
        Assert.Equal("deactivated", s.LastCloseReason);
    });

    [Fact]
    public void Minimize_closes() => Sta.Run(() =>
    {
        var (s, _) = NewService();
        var el = Target();
        var w = new TestWindow { Content = el };
        OpenOn(s, el);
        w.WindowState = WindowState.Minimized;
        w.FireStateChanged();
        Assert.False(s.IsOpen);
        Assert.Equal("minimized", s.LastCloseReason);
    });

    [Fact]
    public void Window_hooks_are_released_after_closing() => Sta.Run(() =>
    {
        var (s, p) = NewService();
        var el = Target();
        var w = new TestWindow { Content = el };
        OpenOn(s, el);
        Leave(el);
        // after closing, window events must not reach the service any more
        w.FireDeactivated();
        w.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
        Assert.Equal("leave", s.LastCloseReason);
        Assert.Equal(1, p.Hides);
    });

    [Fact]
    public void Recycled_container_closes() => Sta.Run(() =>
    {
        var (s, _) = NewService();
        var el = Target();
        el.DataContext = "row 1";
        OpenOn(s, el);
        el.DataContext = "row 7"; // a virtualizing list reused the row for another sensor
        Assert.False(s.IsOpen);
        Assert.Equal("recycled", s.LastCloseReason);
    });

    [Fact]
    public void New_sensor_on_the_element_closes() => Sta.Run(() =>
    {
        var (s, _) = NewService();
        var el = new Border();
        ExplainProps.SetText(el, "x");
        OpenOn(s, el);
        ExplainProps.SetSensor(el, Sensor("Core #3 VID", SensorKind.Voltage, HardwareKind.Cpu));
        Assert.False(s.IsOpen);
        Assert.Equal("content", s.LastCloseReason);
    });

    [Fact]
    public void Language_change_closes() => Sta.Run(() =>
    {
        var (s, _) = NewService();
        var before = Loc.I.Language;
        try
        {
            var el = Target();
            OpenOn(s, el);
            Loc.I.SetLanguage(before == "pl" ? "en" : "pl");
            Assert.False(s.IsOpen);
            Assert.Equal("language", s.LastCloseReason);
        }
        finally
        {
            Loc.I.SetLanguage(before);
        }
    });

    [Fact]
    public void Setting_off_closes_and_keeps_it_closed() => Sta.Run(() =>
    {
        var (s, p) = NewService();
        var el = Target();
        OpenOn(s, el);
        s.Enabled = false;
        Assert.False(s.IsOpen);
        Assert.Equal("disabled", s.LastCloseReason);
        Enter(el);
        Sta.Pump(150);
        Assert.False(s.IsOpen);
        Assert.Equal(1, p.Shows);
    });

    [Fact]
    public void Element_unloaded_closes() => Sta.Run(() =>
    {
        var (s, _) = NewService();
        var el = Target();
        var host = new Grid();
        host.Children.Add(el);
        using var source = new HwndSource(new HwndSourceParameters("BetterVitals.Tests") { WindowStyle = 0, Width = 200, Height = 200 }) { RootVisual = host };
        Assert.True(Sta.PumpUntil(() => el.IsLoaded));
        OpenOn(s, el);
        host.Children.Remove(el); // the page went away / the row was removed
        Assert.True(Sta.PumpUntil(() => !s.IsOpen));
        Assert.Contains(s.LastCloseReason, new[] { "unloaded", "hidden" });
    });

    [Fact]
    public void Element_hidden_closes() => Sta.Run(() =>
    {
        var (s, _) = NewService();
        var el = Target();
        var host = new Grid();
        host.Children.Add(el);
        using var source = new HwndSource(new HwndSourceParameters("BetterVitals.Tests") { WindowStyle = 0, Width = 200, Height = 200 }) { RootVisual = host };
        Assert.True(Sta.PumpUntil(() => el.IsLoaded));
        OpenOn(s, el);
        el.Visibility = Visibility.Collapsed;
        Assert.True(Sta.PumpUntil(() => !s.IsOpen));
        Assert.Equal("hidden", s.LastCloseReason);
    });

    [Fact]
    public void Scrolling_closes() => Sta.Run(() =>
    {
        var (s, _) = NewService();
        var el = Target();
        el.Height = 40;
        var stack = new StackPanel();
        stack.Children.Add(el);
        stack.Children.Add(new Border { Height = 2000 });
        var scroller = new ScrollViewer { Content = stack };
        using var source = new HwndSource(new HwndSourceParameters("BetterVitals.Tests") { WindowStyle = 0, Width = 200, Height = 200 }) { RootVisual = scroller };
        Assert.True(Sta.PumpUntil(() => el.IsLoaded && scroller.ScrollableHeight > 0));
        Sta.Pump(50);
        OpenOn(s, el);
        scroller.ScrollToVerticalOffset(300);
        Assert.True(Sta.PumpUntil(() => !s.IsOpen));
        Assert.Contains(s.LastCloseReason, new[] { "scroll", "wheel" });
    });

    [Fact]
    public void Release_destroys_the_bubble() => Sta.Run(() =>
    {
        var (s, p) = NewService();
        var el = Target();
        OpenOn(s, el);
        s.Release();
        Assert.False(s.IsOpen);
        Assert.Equal(1, p.Releases);
    });

    [Fact]
    public void Element_without_content_never_opens() => Sta.Run(() =>
    {
        var (s, p) = NewService();
        var el = new Border();
        ExplainProps.SetText(el, "");
        Enter(el);
        Sta.Pump(120);
        Assert.False(s.IsOpen);
        Assert.Equal(0, p.Shows);
    });

    [Fact]
    public void Sensor_bubble_shows_the_verbatim_name_and_the_note() => Sta.Run(() =>
    {
        var (s, p) = NewService();
        var sensor = Sensor("GPU Hot Spot", SensorKind.Temperature, HardwareKind.Gpu, "sensor.note.unofficialHotSpot");
        var el = new Border();
        ExplainProps.SetSensor(el, sensor);
        Enter(el);
        var glossary = Glossary.Lookup(sensor.Kind, sensor.HardwareKind, sensor.Name, Loc.I.IsPolish);
        if (glossary is null)
        {
            // no glossary.json in this build: nothing to explain, so no bubble at all
            Sta.Pump(150);
            Assert.False(s.IsOpen);
            Assert.Equal("empty", s.LastCloseReason);
            return;
        }
        Assert.True(Sta.PumpUntil(() => s.IsOpen));
        Assert.Equal("GPU Hot Spot", p.Last!.Title);
        Assert.Equal(glossary.What, p.Last.What);
        Assert.Equal(Loc.T("sensor.note.unofficialHotSpot"), p.Last.Note);
    });

    [Fact]
    public void Key_bubble_follows_the_language() => Sta.Run(() =>
    {
        NewService();
        var before = Loc.I.Language;
        try
        {
            var el = new Border();
            ExplainProps.SetKey(el, "settings.explanations.info");
            Loc.I.SetLanguage("pl");
            Assert.Equal(Loc.AllEntries["settings.explanations.info"].Pl, ExplainService.Resolve(el)!.What);
            Loc.I.SetLanguage("en");
            Assert.Equal(Loc.AllEntries["settings.explanations.info"].En, ExplainService.Resolve(el)!.What);
        }
        finally
        {
            Loc.I.SetLanguage(before);
        }
    });

    private static SensorDescriptor Sensor(string name, SensorKind kind, HardwareKind hw, string? note = null) => new()
    {
        Id = "test/" + SensorIds.Slug(name),
        HardwareId = "test",
        Name = name,
        Kind = kind,
        HardwareKind = hw,
        NoteKey = note,
    };
}
