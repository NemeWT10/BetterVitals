using System.Runtime.InteropServices;
using BetterVitals.Core.Overlay;
using BetterVitals.Overlay.Interop;

namespace BetterVitals.Overlay.Host;

/// <summary>
/// Snap guide lines shown while dragging in edit mode: two thin, click-through, top-most GDI windows (one vertical,
/// one horizontal) filled with the accent color. No GPU resources; created lazily, destroyed when edit mode ends.
/// </summary>
internal sealed unsafe partial class GuideWindows : IDisposable
{
    private const string ClassName = "BetterVitals.OverlayGuide";
    private static bool _registered;
    private static nint _brush;

    private readonly nint _vertical;
    private readonly nint _horizontal;

    public GuideWindows(Rgba color, bool excludeFromCapture)
    {
        Register();
        if (_brush != 0) Win32.DeleteObject(_brush);
        _brush = Win32.CreateSolidBrush((uint)(color.R | (color.G << 8) | (color.B << 16)));
        _vertical = Create(excludeFromCapture);
        _horizontal = Create(excludeFromCapture);
    }

    private static void Register()
    {
        if (_registered) return;
        fixed (char* name = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &Proc,
                hInstance = Win32.GetModuleHandle(null),
                lpszClassName = name,
            };
            Win32.RegisterClassEx(&wc);
        }
        _registered = true;
    }

    private static nint Create(bool excludeFromCapture)
    {
        nint hwnd;
        fixed (char* cls = ClassName)
            hwnd = Win32.CreateWindowEx(
                Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_LAYERED,
                cls, null, Win32.WS_POPUP, 0, 0, 1, 1, 0, 0, Win32.GetModuleHandle(null), 0);
        if (hwnd != 0)
        {
            Win32.SetLayeredWindowAttributes(hwnd, 0, 200, Win32.LWA_ALPHA);
            if (excludeFromCapture) Win32.SetWindowDisplayAffinity(hwnd, Win32.WDA_EXCLUDEFROMCAPTURE);
        }
        return hwnd;
    }

    /// <summary>Shows the vertical guide at <paramref name="x"/> and / or the horizontal one at <paramref name="y"/> across <paramref name="area"/>.</summary>
    public void Show(int? x, int? y, PixelRect area, int thickness, bool neverShow)
    {
        Place(_vertical, x is { } gx ? new RECT(gx - thickness / 2, area.Top, gx - thickness / 2 + thickness, area.Bottom) : null, neverShow);
        Place(_horizontal, y is { } gy ? new RECT(area.Left, gy - thickness / 2, area.Right, gy - thickness / 2 + thickness) : null, neverShow);
    }

    private static void Place(nint hwnd, RECT? r, bool neverShow)
    {
        if (hwnd == 0) return;
        if (r is not { } rect || neverShow)
        {
            Win32.ShowWindow(hwnd, Win32.SW_HIDE);
            return;
        }
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height, Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
    }

    public void Hide()
    {
        if (_vertical != 0) Win32.ShowWindow(_vertical, Win32.SW_HIDE);
        if (_horizontal != 0) Win32.ShowWindow(_horizontal, Win32.SW_HIDE);
    }

    [UnmanagedCallersOnly]
    private static nint Proc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case Win32.WM_NCHITTEST:
                return Win32.HTTRANSPARENT;
            case Win32.WM_MOUSEACTIVATE:
                return Win32.MA_NOACTIVATE;
            case Win32.WM_ERASEBKGND:
                RECT r;
                GetClientRect(hwnd, &r);
                FillRect(wParam, &r, _brush);
                return 1;
        }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint hwnd, RECT* rect);

    [LibraryImport("user32.dll")]
    private static partial int FillRect(nint hdc, RECT* rect, nint brush);

    public void Dispose()
    {
        if (_vertical != 0) Win32.DestroyWindow(_vertical);
        if (_horizontal != 0) Win32.DestroyWindow(_horizontal);
    }
}
