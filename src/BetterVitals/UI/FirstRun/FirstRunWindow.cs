using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using BetterVitals.UI.Panel;
using System.Windows.Shell;
using BetterVitals.Core.Settings;
using BetterVitals.Localization;
using BetterVitals.Services;
using BetterVitals.UI.Themes;

namespace BetterVitals.UI.Onboarding;

/// <summary>
/// The first-run guide window: Mica (Windows 11), the app's own title area with a close button, the
/// <see cref="FirstRunView"/> inside. Closing it before the end returns <see cref="FirstRunOutcome.Closed"/>; it cannot
/// be closed while the full-mode install waits for UAC.
/// </summary>
public sealed class FirstRunWindow : Window
{
    private readonly FirstRunView _view;
    private FirstRunResult? _result;

    public FirstRunWindow(FirstRunFlow flow)
    {
        Title = "BetterVitals";
        Width = 800;
        Height = 660;
        MinWidth = 640;
        MinHeight = 560;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;
        FontSize = 13;
        Language = Loc.I.XmlLang;
        SetResourceReference(BackgroundProperty, "Brush.WindowBg");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        SetResourceReference(FontFamilyProperty, "Font.Ui");
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 52,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(-1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        _view = new FirstRunView(flow);
        _view.Completed += r =>
        {
            _result = r;
            Close();
        };

        var close = new Button { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        close.SetResourceReference(StyleProperty, "ChromeCloseButton");
        var x = new Controls.Icon { Size = 16, StrokeThickness = 1 };
        x.SetResourceReference(Controls.Icon.DataProperty, "Icon.Close");
        close.Content = x;
        close.SetBinding(ToolTipProperty, SettingsUi.Tr("window.close"));
        close.Click += (_, _) => Close();
        WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        flow.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FirstRunFlow.IsBusy)) close.IsEnabled = !flow.IsBusy;
        };

        Content = new Grid { Children = { _view, close } };

        Loaded += (_, _) =>
        {
            ThemeManager.Changed += OnThemeChanged;
            Loc.LanguageChanged += OnLanguageChanged;
        };
        Closed += (_, _) =>
        {
            ThemeManager.Changed -= OnThemeChanged;
            Loc.LanguageChanged -= OnLanguageChanged;
        };
    }

    /// <summary>How the guide ended (null until it closed).</summary>
    public FirstRunResult? Result => _result;

    internal FirstRunView View => _view;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.Apply(this);
        WindowEffects.RemoveSystemCaptionButtons(this);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Motion.Appear(_view, rise: 10, fromScale: 0.985, ms: 320);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // never leave the user without an answer while Windows shows the UAC prompt
        if (_view.Flow.IsBusy) e.Cancel = true;
        base.OnClosing(e);
    }

    private void OnThemeChanged() => WindowEffects.Apply(this);

    private void OnLanguageChanged() => Language = Loc.I.XmlLang;
}

/// <summary>
/// Entry point of the first-run guide for the app shell:
/// <code>
/// if (FirstRun.IsNeeded)
/// {
///     var r = await FirstRun.RunAsync();
///     if (r.Outcome == FirstRunOutcome.FullModeStarting) Quit();   // the elevated copy takes over
/// }
/// </code>
/// </summary>
public static class FirstRun
{
    /// <summary>The guide has not been finished yet (settings.FirstRunDone).</summary>
    public static bool IsNeeded => SettingsStore.I is { } s && !s.Current.FirstRunDone;

    /// <summary>
    /// Shows the guide and completes when it closes. The settings are saved by then (Driver.Mode, Recording.Enabled,
    /// General.Autostart, FirstRunDone). <see cref="FirstRunOutcome.FullModeStarting"/> = exit this process now.
    /// </summary>
    public static Task<FirstRunResult> RunAsync(IPlatformService? platform = null)
    {
        var flow = new FirstRunFlow(platform ?? AppServices.Platform, SettingsStore.I);
        var window = new FirstRunWindow(flow);
        var done = new TaskCompletionSource<FirstRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => done.TrySetResult(window.Result ?? new FirstRunResult(
            FirstRunOutcome.Closed, SettingsStore.I.Current.Driver.Mode, SettingsStore.I.Current.Recording.Enabled, SettingsStore.I.Current.General.Autostart));
        window.Show();
        window.Activate();
        return done.Task;
    }
}
