using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using BetterVitals.Core;
using BetterVitals.UI.Themes;

namespace BetterVitals.UI;

/// <summary>Windows 11 window look: Mica backdrop, dark title, rounded corners, themed 1 px border (ported from BetterNotepad).</summary>
public static class WindowEffects
{
    /// <summary>Mica needs Windows 11 22H2+. Test renders never show it, so test mode stays solid.</summary>
    public static bool IsBackdropSupported => !AppEnv.TestMode && Environment.OSVersion.Version.Build >= 22621;

    /// <summary>Call from OnSourceInitialized and again after a theme change.</summary>
    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        // a per-pixel transparent window (flyout, menu, bubble) draws its own rounded card and shadow
        if (hwnd == 0 || window.AllowsTransparency) return;
        var theme = ThemeManager.I;
        var p = theme.Current;

        var dark = p.IsDark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        var corner = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        var border = Native.ColorRef(p.BorderStrong);
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));

        var source = HwndSource.FromHwnd(hwnd);
        if (IsBackdropSupported)
        {
            var type = theme.MicaActive ? Native.DWMSBT_MAINWINDOW : Native.DWMSBT_NONE;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));
        }
        if (source?.CompositionTarget is { } target)
            target.BackgroundColor = theme.MicaActive ? Colors.Transparent : p.Bg;
        if (theme.MicaActive)
        {
            var m = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Native.DwmExtendFrameIntoClientArea(hwnd, ref m);
        }
    }

    /// <summary>Hides the DWM-drawn caption buttons; the window draws its own.</summary>
    public static void RemoveSystemCaptionButtons(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;
        var style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE);
        Native.SetWindowLongPtr(hwnd, Native.GWL_STYLE, style & ~(nint)Native.WS_SYSMENU);
    }

    /// <summary>
    /// Tool windows that never take focus (bubble, toasts, snapshot renders): no taskbar / Alt+Tab entry, no activation.
    /// </summary>
    public static void MakeNoActivate(Window window, bool clickThrough = false)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;
        Native.AddExStyle(hwnd, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | (clickThrough ? Native.WS_EX_TRANSPARENT : 0));
    }
}
