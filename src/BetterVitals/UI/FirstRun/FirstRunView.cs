using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using BetterVitals.Core;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.UI.Panel;
using static BetterVitals.UI.Panel.SettingsUi;

namespace BetterVitals.UI.Onboarding;

/// <summary>
/// The guide's content (hosted by <see cref="FirstRunWindow"/>, rendered on its own in tests): a header with the step
/// counter, one screen per step, and a footer with Back / dots / Next. All state lives in <see cref="FirstRunFlow"/>.
/// </summary>
internal sealed class FirstRunView : UserControl
{
    private readonly FirstRunFlow _flow;
    private readonly Dictionary<FirstRunStep, FrameworkElement> _screens = new();
    private readonly Button _back;
    private readonly Button _next;
    private readonly StackPanel _dots;
    private readonly TextBlock _stepText;
    private readonly Border _footer;
    private readonly TextBlock _pawnIoInstalled;
    private readonly StackPanel _pawnIoRow;
    private readonly Border _fullNote;
    private readonly ScrollViewer _scroll;
    private FirstRunStep _shown = (FirstRunStep)(-1);
    private bool _syncing;

    public FirstRunView(FirstRunFlow flow)
    {
        _flow = flow;
        Resources.MergedDictionaries.Add(Styles);
        Language = Loc.I.XmlLang;
        SetResourceReference(TextElement.FontFamilyProperty, "Font.Ui");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // header: mark + name, step counter (the window's close button sits to the right of it)
        var header = new DockPanel { Margin = new Thickness(28, 18, 64, 0), Height = 30 };
        var logo = new Image { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center };
        logo.SetResourceReference(Image.SourceProperty, "Logo.Image.Small");
        header.Children.Add(logo);
        _stepText = Text(null, 12, "Brush.TextFaint");
        _stepText.VerticalAlignment = VerticalAlignment.Center;
        _stepText.TextWrapping = TextWrapping.NoWrap;
        DockPanel.SetDock(_stepText, Dock.Right);
        header.Children.Add(_stepText);
        var name = Text(null, 14, "Brush.Text", FontWeights.SemiBold);
        name.Text = AppEnv.ProductName;
        name.VerticalAlignment = VerticalAlignment.Center;
        name.Margin = new Thickness(10, 0, 0, 1);
        name.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Display");
        header.Children.Add(name);
        root.Children.Add(header);

        // screens
        var host = new Grid { Margin = new Thickness(44, 22, 44, 18) };
        _screens[FirstRunStep.Welcome] = Welcome();
        (_screens[FirstRunStep.Mode], _pawnIoRow, _pawnIoInstalled) = ModeScreen();
        _screens[FirstRunStep.History] = HistoryScreen();
        (_screens[FirstRunStep.Tray], _fullNote) = TrayScreen();
        _screens[FirstRunStep.Working] = WorkingScreen();
        _screens[FirstRunStep.Failed] = FailedScreen();
        foreach (var s in _screens.Values)
        {
            s.Visibility = Visibility.Collapsed;
            host.Children.Add(s);
        }
        _scroll = new ScrollViewer { Content = host, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        Grid.SetRow(_scroll, 1);
        root.Children.Add(_scroll);

        // footer
        _footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(28, 14, 28, 16) };
        _footer.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        var bar = new Grid();
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _back = Button("GhostButton", "firstrun.back", "Icon.ChevronLeft");
        _back.HorizontalAlignment = HorizontalAlignment.Left;
        _back.Click += (_, _) => _flow.Back();
        bar.Children.Add(_back);
        _dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_dots, 1);
        bar.Children.Add(_dots);
        _next = Button("PrimaryButton", "firstrun.next");
        _next.HorizontalAlignment = HorizontalAlignment.Right;
        _next.MinWidth = 120;
        _next.IsDefault = true;
        _next.Click += async (_, _) => await NextAsync();
        Grid.SetColumn(_next, 2);
        bar.Children.Add(_next);
        _footer.Child = bar;
        Grid.SetRow(_footer, 2);
        root.Children.Add(_footer);

        Content = root;
        _flow.PropertyChanged += OnFlowChanged;
        Loaded += (_, _) => Loc.LanguageChanged += OnLanguage;
        Unloaded += (_, _) => Loc.LanguageChanged -= OnLanguage;
        Update();
    }

    /// <summary>The guide finished (settings saved): close the window, maybe exit for the elevated copy.</summary>
    public event Action<FirstRunResult>? Completed;

    internal FirstRunFlow Flow => _flow;

    internal Segments<string> LanguageChoice { get; private set; } = null!;

    internal RadioButton FullCard { get; private set; } = null!;

    internal RadioButton BasicCard { get; private set; } = null!;

    internal CheckBox PawnIoSwitch { get; private set; } = null!;

    internal RadioButton HistoryYes { get; private set; } = null!;

    internal RadioButton HistoryNo { get; private set; } = null!;

    internal CheckBox AutostartSwitch { get; private set; } = null!;

    internal Button NextButton => _next;

    internal Button RetryButton { get; private set; } = null!;

    internal Button BasicButton { get; private set; } = null!;

    internal FrameworkElement Screen(FirstRunStep step) => _screens[step];

    // ───────────── screens ─────────────

    private FrameworkElement Welcome()
    {
        var p = new StackPanel();
        var hero = new Grid { Width = 76, Height = 76, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        var glow = new Ellipse { Width = 64, Height = 64, Opacity = 0.55, Effect = new BlurEffect { Radius = 26 } };
        glow.SetResourceReference(Shape.FillProperty, "Logo.Tile");
        hero.Children.Add(glow);
        var mark = new Image { Width = 64, Height = 64 };
        mark.SetResourceReference(Image.SourceProperty, "Logo.Image");
        Motion.SetHoverLift(mark, 3);
        hero.Children.Add(mark);
        p.Children.Add(hero);
        p.Children.Add(Title("firstrun.welcome.title", 18));
        p.Children.Add(Body("firstrun.welcome.body"));

        var points = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        foreach (var key in new[] { "firstrun.welcome.point1", "firstrun.welcome.point2", "firstrun.welcome.point3" })
        {
            var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var tile = Tile("Icon.Check", 24, 13, "Brush.SuccessSoft", "Brush.Success");
            tile.Margin = new Thickness(0, 0, 12, 0);
            dock.Children.Add(tile);
            var t = Text(key, 13.5, "Brush.Text");
            t.VerticalAlignment = VerticalAlignment.Center;
            dock.Children.Add(t);
            points.Children.Add(dock);
        }
        p.Children.Add(points);

        LanguageChoice = new Segments<string>("fr-language", [("pl", "settings.language.pl"), ("en", "settings.language.en")]);
        LanguageChoice.ValueChanged += lang => _flow.SetLanguage(lang);
        var card = Card(new Thickness(18, 14, 18, 14));
        var dockLang = new DockPanel();
        DockPanel.SetDock(LanguageChoice, Dock.Right);
        LanguageChoice.VerticalAlignment = VerticalAlignment.Center;
        dockLang.Children.Add(LanguageChoice);
        var langIcon = Tile("Icon.Globe", 34, 18, "Brush.AccentSoft", "Brush.AccentText");
        langIcon.Margin = new Thickness(0, 0, 14, 0);
        dockLang.Children.Add(langIcon);
        var langText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        langText.Children.Add(Text("firstrun.welcome.language", 13.5, "Brush.Text", FontWeights.SemiBold));
        var hint = Text("firstrun.welcome.languageHint", 12);
        hint.Margin = new Thickness(0, 3, 0, 0);
        langText.Children.Add(hint);
        dockLang.Children.Add(langText);
        card.Child = dockLang;
        card.Margin = new Thickness(0, 12, 0, 0);
        p.Children.Add(card);
        return p;
    }

    private (FrameworkElement, StackPanel, TextBlock) ModeScreen()
    {
        var p = new StackPanel();
        p.Children.Add(Title("firstrun.mode.title", 0));
        p.Children.Add(Body("firstrun.mode.body"));

        PawnIoSwitch = Switch("firstrun.mode.pawnio");
        PawnIoSwitch.Checked += (_, _) => Write(() => _flow.InstallPawnIo = true);
        PawnIoSwitch.Unchecked += (_, _) => Write(() => _flow.InstallPawnIo = false);
        var pawnLabel = Text("firstrun.mode.pawnio", 13, "Brush.Text", FontWeights.SemiBold);
        pawnLabel.VerticalAlignment = VerticalAlignment.Center;
        pawnLabel.Margin = new Thickness(10, 0, 0, 0);
        var pawnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(50, 12, 0, 0), Children = { PawnIoSwitch, pawnLabel } };
        var installed = Text(null, 12.5, "Brush.Success", FontWeights.SemiBold);
        installed.Margin = new Thickness(50, 10, 0, 0);

        FullCard = Option("firstrun.mode.full", "firstrun.mode.full.desc", "Icon.Shield", recommended: true, extra: [pawnRow, installed]);
        FullCard.Checked += (_, _) => Write(() => _flow.Mode = ReadMode.Full);
        BasicCard = Option("firstrun.mode.basic", "firstrun.mode.basic.desc", "Icon.Gauge", recommended: false);
        BasicCard.Checked += (_, _) => Write(() => _flow.Mode = ReadMode.Basic);
        FullCard.GroupName = BasicCard.GroupName = "fr-mode-" + Guid.NewGuid().ToString("N");
        FullCard.Margin = new Thickness(0, 18, 0, 12);
        p.Children.Add(FullCard);
        p.Children.Add(BasicCard);
        return (p, pawnRow, installed);
    }

    private FrameworkElement HistoryScreen()
    {
        var p = new StackPanel();
        p.Children.Add(Title("firstrun.history.title", 0));
        p.Children.Add(Body("firstrun.history.body"));
        HistoryYes = Option("firstrun.history.yes", "firstrun.history.yes.desc", "Icon.Chart", recommended: true);
        HistoryYes.Checked += (_, _) => Write(() => _flow.RecordHistory = true);
        HistoryNo = Option("firstrun.history.no", "firstrun.history.no.desc", "Icon.Pulse", recommended: false);
        HistoryNo.Checked += (_, _) => Write(() => _flow.RecordHistory = false);
        HistoryYes.GroupName = HistoryNo.GroupName = "fr-history-" + Guid.NewGuid().ToString("N");
        HistoryYes.Margin = new Thickness(0, 18, 0, 12);
        p.Children.Add(HistoryYes);
        p.Children.Add(HistoryNo);

        AutostartSwitch = Switch("firstrun.autostart");
        AutostartSwitch.Checked += (_, _) => Write(() => _flow.Autostart = true);
        AutostartSwitch.Unchecked += (_, _) => Write(() => _flow.Autostart = false);
        var card = Card(new Thickness(18, 14, 18, 14));
        card.Margin = new Thickness(0, 22, 0, 0);
        var dock = new DockPanel();
        DockPanel.SetDock(AutostartSwitch, Dock.Right);
        dock.Children.Add(AutostartSwitch);
        var icon = Tile("Icon.Power", 34, 18, "Brush.AccentSoft", "Brush.AccentText");
        icon.Margin = new Thickness(0, 0, 14, 0);
        dock.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(Text("firstrun.autostart", 13.5, "Brush.Text", FontWeights.SemiBold));
        var desc = Text("firstrun.autostart.desc", 12);
        desc.Margin = new Thickness(0, 3, 0, 0);
        text.Children.Add(desc);
        dock.Children.Add(text);
        card.Child = dock;
        p.Children.Add(card);
        return p;
    }

    private (FrameworkElement, Border) TrayScreen()
    {
        var p = new StackPanel();
        p.Children.Add(Title("firstrun.tray.title", 0));
        p.Children.Add(Body("firstrun.tray.body"));
        var hint = new TrayHint { Margin = new Thickness(0, 20, 0, 0) };
        p.Children.Add(hint);
        var note = Note("Icon.Shield", "firstrun.tray.fullNote", "Brush.AccentSoft", "Brush.AccentText");
        note.Margin = new Thickness(0, 18, 0, 0);
        p.Children.Add(note);
        return (p, note);
    }

    private FrameworkElement WorkingScreen()
    {
        var p = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 460, Margin = new Thickness(0, 40, 0, 0) };
        var tile = Tile("Icon.Shield", 60, 30, "Brush.AccentSoft", "Brush.AccentText");
        tile.HorizontalAlignment = HorizontalAlignment.Center;
        p.Children.Add(tile);
        var title = Title("firstrun.working.title", 20);
        title.TextAlignment = TextAlignment.Center;
        p.Children.Add(title);
        var body = Body("firstrun.working.body");
        body.TextAlignment = TextAlignment.Center;
        p.Children.Add(body);
        p.Children.Add(new ProgressTrack(indeterminate: true) { Width = 260, Margin = new Thickness(0, 26, 0, 0) });
        return p;
    }

    private FrameworkElement FailedScreen()
    {
        var p = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 520, Margin = new Thickness(0, 30, 0, 0) };
        var tile = Tile("Icon.Warning", 60, 30, "Brush.WarningSoft", "Brush.Warning");
        tile.HorizontalAlignment = HorizontalAlignment.Center;
        p.Children.Add(tile);
        var title = Title("firstrun.failed.title", 20);
        title.TextAlignment = TextAlignment.Center;
        p.Children.Add(title);
        var body = Body("firstrun.failed.body");
        body.TextAlignment = TextAlignment.Center;
        p.Children.Add(body);
        RetryButton = Button("PrimaryButton", "firstrun.failed.retry", "Icon.Refresh");
        RetryButton.Click += async (_, _) => Done(await _flow.RetryAsync());
        BasicButton = Button("SecondaryButton", "firstrun.failed.basic");
        BasicButton.Click += async (_, _) => Done(await _flow.ContinueBasicAsync());
        var buttons = HStack(10, RetryButton, BasicButton);
        buttons.HorizontalAlignment = HorizontalAlignment.Center;
        buttons.Margin = new Thickness(0, 26, 0, 0);
        p.Children.Add(buttons);
        return p;
    }

    // ───────────── pieces ─────────────

    private static TextBlock Title(string key, double top)
    {
        var t = Text(key, 26, "Brush.Text", FontWeights.SemiBold);
        t.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Display");
        t.Margin = new Thickness(0, top, 0, 0);
        return t;
    }

    private static TextBlock Body(string key)
    {
        var t = Text(key, 14);
        t.Margin = new Thickness(0, 8, 0, 0);
        t.MaxWidth = 620;
        t.HorizontalAlignment = HorizontalAlignment.Left;
        t.LineHeight = 21;
        return t;
    }

    private static Border Card(Thickness padding)
    {
        var b = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = padding };
        b.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        b.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        return b;
    }

    internal static Border Tile(string icon, double size, double iconSize, string bg, string fg)
    {
        var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.28), VerticalAlignment = VerticalAlignment.Top };
        b.SetResourceReference(Border.BackgroundProperty, bg);
        b.Child = Glyph(icon, iconSize, fg);
        return b;
    }

    internal static Border Note(string icon, string key, string bg, string fg)
    {
        var b = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 11, 14, 12) };
        b.SetResourceReference(Border.BackgroundProperty, bg);
        var dock = new DockPanel();
        var i = Glyph(icon, 17, fg);
        i.VerticalAlignment = VerticalAlignment.Top;
        i.Margin = new Thickness(0, 1, 11, 0);
        dock.Children.Add(i);
        dock.Children.Add(Text(key, 12.5, "Brush.Text"));
        b.Child = dock;
        return b;
    }

    private static RadioButton Option(string titleKey, string descKey, string icon, bool recommended, FrameworkElement[]? extra = null)
    {
        var card = new RadioButton();
        card.SetResourceReference(StyleProperty, "OptionCard");
        System.Windows.Data.BindingOperations.SetBinding(card, System.Windows.Automation.AutomationProperties.NameProperty, Tr(titleKey));
        var body = new StackPanel();
        var head = new DockPanel();
        var tile = Tile(icon, 36, 19, "Brush.AccentSoft", "Brush.AccentText");
        tile.Margin = new Thickness(0, 0, 14, 0);
        head.Children.Add(tile);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new WrapPanel { Orientation = Orientation.Horizontal };
        var title = Text(titleKey, 15, "Brush.Text", FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.TextWrapping = TextWrapping.NoWrap;
        titleRow.Children.Add(title);
        if (recommended)
        {
            var pill = new StatusPill { Margin = new Thickness(10, 1, 0, 0) };
            pill.Bind("firstrun.recommended", PillKind.Accent);
            titleRow.Children.Add(pill);
        }
        titles.Children.Add(titleRow);
        var desc = Text(descKey, 12.5);
        desc.Margin = new Thickness(0, 5, 0, 0);
        desc.LineHeight = 18.5;
        titles.Children.Add(desc);
        head.Children.Add(titles);
        body.Children.Add(head);
        foreach (var e in extra ?? []) body.Children.Add(e);
        card.Content = body;
        return card;
    }

    // ───────────── state ─────────────

    private void Write(Action a)
    {
        if (!_syncing) a();
    }

    private async Task NextAsync()
    {
        if (!_flow.IsLastStep)
        {
            _flow.Next();
            return;
        }
        Done(await _flow.FinishAsync());
    }

    private void Done(FirstRunResult? result)
    {
        if (result is not null) Completed?.Invoke(result);
    }

    private void OnFlowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Dispatcher.CheckAccess()) Update();
        else Dispatcher.BeginInvoke(Update);
    }

    private void OnLanguage()
    {
        Language = Loc.I.XmlLang;
        Update();
    }

    internal void Update()
    {
        _syncing = true;
        try
        {
            var step = _flow.Step;
            if (step != _shown)
            {
                foreach (var (s, screen) in _screens) screen.Visibility = s == step ? Visibility.Visible : Visibility.Collapsed;
                if ((int)_shown >= 0 && IsLoaded) Motion.SlideIn(_screens[step], fromX: step > _shown ? 22 : -22, ms: 280);
                _shown = step;
                _scroll.ScrollToTop();
            }

            LanguageChoice.Value = _flow.Language;
            FullCard.IsChecked = _flow.Mode == ReadMode.Full;
            BasicCard.IsChecked = _flow.Mode == ReadMode.Basic;
            _pawnIoRow.Visibility = _flow.PawnIoNeeded ? Visibility.Visible : Visibility.Collapsed;
            PawnIoSwitch.IsChecked = _flow.InstallPawnIo;
            _pawnIoRow.IsEnabled = _flow.Mode == ReadMode.Full;
            _pawnIoInstalled.Visibility = _flow.PawnIoNeeded ? Visibility.Collapsed : Visibility.Visible;
            if (_flow.Platform.PawnIoVersion is { } v) _pawnIoInstalled.Text = Loc.T("firstrun.mode.pawnio.installed", v.ToString(3));
            HistoryYes.IsChecked = _flow.RecordHistory;
            HistoryNo.IsChecked = !_flow.RecordHistory;
            AutostartSwitch.IsChecked = _flow.Autostart;
            _fullNote.Visibility = _flow.Mode == ReadMode.Full ? Visibility.Visible : Visibility.Collapsed;

            var onSteps = step <= FirstRunStep.Tray;
            _footer.Visibility = onSteps ? Visibility.Visible : Visibility.Hidden;
            _stepText.Text = onSteps ? Loc.T("firstrun.stepOf", _flow.StepIndex + 1, FirstRunFlow.VisibleSteps) : "";
            _back.Visibility = _flow.CanGoBack ? Visibility.Visible : Visibility.Hidden;
            _next.IsEnabled = !_flow.IsBusy;
            SetButtonText(_next, !_flow.IsLastStep ? "firstrun.next" : _flow.Mode == ReadMode.Full ? "firstrun.finishFull" : "firstrun.finish");
            RetryButton.IsEnabled = BasicButton.IsEnabled = !_flow.IsBusy;
            UpdateDots();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void UpdateDots()
    {
        if (_dots.Children.Count == 0)
        {
            for (var i = 0; i < FirstRunFlow.VisibleSteps; i++)
                _dots.Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(3, 0, 3, 0) });
        }
        for (var i = 0; i < _dots.Children.Count; i++)
        {
            var dot = (Border)_dots.Children[i];
            var current = i == _flow.StepIndex;
            dot.Width = current ? 20 : 6;
            dot.SetResourceReference(Border.BackgroundProperty, current ? "Brush.AccentGradientH" : i < _flow.StepIndex ? "Brush.AccentGlow" : "Brush.BorderStrong");
        }
    }
}
