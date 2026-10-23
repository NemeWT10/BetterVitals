using System.Runtime.InteropServices;
using BetterVitals.Core;

namespace BetterVitals.UI.Tray;

/// <summary>
/// Notification-area icon through Shell_NotifyIcon (no WinForms), ported from BetterShot: left click = flyout,
/// right click = tray menu, double click = panel. Re-added after Explorer restarts (TaskbarCreated), redrawn at the
/// shell's icon size when the DPI changes, tooltip text asked from <see cref="TooltipProvider"/> when the pointer
/// arrives (no timer). Like NotifyIcon: clicks fire on button-up, the button-up ending a double click is ignored.
/// </summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    private const int WM_TRAYMOUSEMESSAGE = Native.WM_USER + 1024; // the callback message WinForms uses
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4;
    private const uint IconId = 1;

    private static readonly int WM_TASKBARCREATED = (int)Native.RegisterWindowMessage("TaskbarCreated");

    private readonly MessageWindow _window;
    private nint _hicon;
    private int _iconSize;
    private string _text = AppEnv.ProductName;
    private bool _added, _doubleClick, _disposed;
    private long _tipAt;

    public event Action? FlyoutRequested, MenuRequested, PanelRequested;

    /// <summary>Tooltip text (key values), asked for when the pointer comes over the icon, at most once a second.</summary>
    public Func<string>? TooltipProvider { get; set; }

    public TrayIcon(MessageWindow window)
    {
        _window = window;
        _window.Message += OnMessage;
        RefreshIcon();
        UpdateIcon();
    }

    /// <summary>The icon's rectangle on screen (physical px), or null when the shell does not say (overflow closed).</summary>
    public Native.RECT? GetRect()
    {
        var id = new Native.NOTIFYICONIDENTIFIER { cbSize = (uint)sizeof(Native.NOTIFYICONIDENTIFIER), hWnd = _window.Handle, uID = IconId };
        return Native.Shell_NotifyIconGetRect(ref id, out var r) == 0 && r.Width > 0 ? r : null;
    }

    /// <summary>Redraws the icon if the shell's small-icon size changed (DPI / scaling change).</summary>
    private void RefreshIcon()
    {
        var dpi = Native.GetDpiForWindow(_window.Handle);
        if (dpi == 0) dpi = 96;
        var size = Native.GetSystemMetricsForDpi(Native.SM_CXSMICON, dpi);
        if (size <= 0) size = 16;
        if (size == _iconSize && _hicon != 0) return;
        var old = _hicon;
        _hicon = TrayIconArt.Create(size);
        _iconSize = size;
        if (old != 0) Native.DestroyIcon(old);
    }

    private void UpdateTooltip()
    {
        var now = Environment.TickCount64;
        if (now - _tipAt < 1000 || TooltipProvider is null) return;
        _tipAt = now;
        string text;
        try
        {
            text = TooltipProvider();
        }
        catch (Exception ex)
        {
            Log.Warn("tray: tooltip failed: " + ex.Message);
            return;
        }
        if (text == _text) return;
        _text = text;
        UpdateIcon();
    }

    /// <summary>Adds the icon, or updates it once it is there.</summary>
    private void UpdateIcon()
    {
        if (_disposed) return;
        var data = NewData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_TRAYMOUSEMESSAGE;
        data.hIcon = _hicon;
        var tip = _text.AsSpan(0, Math.Min(_text.Length, 127));
        tip.CopyTo(new Span<char>(data.szTip, 128));
        data.szTip[tip.Length] = '\0';
        // after NIM_ADD the icon counts as added even if the shell was not ready; TaskbarCreated adds it again
        if (!Shell_NotifyIconW(_added ? NIM_MODIFY : NIM_ADD, &data) && _added)
        {
            _added = false;
            Shell_NotifyIconW(NIM_ADD, &data);
        }
        _added = true;
    }

    private void RemoveIcon()
    {
        if (!_added) return;
        var data = NewData();
        Shell_NotifyIconW(NIM_DELETE, &data);
        _added = false;
    }

    private NOTIFYICONDATAW NewData() => new() { cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = _window.Handle, uID = IconId };

    private void OnMessage(int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_TRAYMOUSEMESSAGE)
        {
            switch ((int)(lParam & 0xFFFF))
            {
                case Native.WM_MOUSEMOVE:
                    UpdateTooltip();
                    break;
                case Native.WM_LBUTTONDBLCLK:
                    _doubleClick = true;
                    PanelRequested?.Invoke();
                    break;
                case Native.WM_RBUTTONDBLCLK or Native.WM_MBUTTONDBLCLK:
                    _doubleClick = true;
                    break;
                case Native.WM_LBUTTONUP:
                    if (!_doubleClick) FlyoutRequested?.Invoke();
                    _doubleClick = false;
                    break;
                case Native.WM_RBUTTONUP:
                    if (!_doubleClick) MenuRequested?.Invoke();
                    _doubleClick = false;
                    break;
                case Native.WM_MBUTTONUP:
                    _doubleClick = false;
                    break;
            }
            handled = true;
        }
        else if (msg == WM_TASKBARCREATED && WM_TASKBARCREATED != 0)
        {
            _added = false; // Explorer (re)started: its notification area does not know us
            RefreshIcon();
            UpdateIcon();
        }
        else if (msg is Native.WM_DPICHANGED or Native.WM_DISPLAYCHANGE)
        {
            var before = _iconSize;
            RefreshIcon();
            if (_iconSize != before) UpdateIcon();
        }
        else if (msg == Native.WM_DESTROY)
        {
            RemoveIcon(); // the window is going away: no ghost icon
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        RemoveIcon();
        _disposed = true;
        _window.Message -= OnMessage;
        if (_hicon != 0) Native.DestroyIcon(_hicon);
        _hicon = 0;
    }

    // ───── interop ─────

    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState, dwStateMask;
        public fixed char szInfo[256];
        public uint uTimeoutOrVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll")]
    private static extern bool Shell_NotifyIconW(uint message, NOTIFYICONDATAW* data);
}
