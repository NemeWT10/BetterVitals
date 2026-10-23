using System.Windows.Interop;

namespace BetterVitals.UI.Tray;

/// <summary>
/// A hidden top-level window (never shown) that receives the tray icon's callbacks and system broadcasts:
/// TaskbarCreated (Explorer restarted), WM_SETTINGCHANGE (light / dark switch), WM_DPICHANGED / WM_DISPLAYCHANGE.
/// Not a message-only window: those miss broadcasts.
/// </summary>
internal sealed class MessageWindow : IDisposable
{
    public delegate void MessageHandler(int msg, nint wParam, nint lParam, ref bool handled);

    private readonly HwndSource _source;

    public nint Handle => _source.Handle;

    public event MessageHandler? Message;

    public MessageWindow()
    {
        _source = new HwndSource(new HwndSourceParameters("BetterVitals.Messages") { WindowStyle = 0, Width = 0, Height = 0 });
        _source.AddHook(WndProc);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        Message?.Invoke(msg, wParam, lParam, ref handled);
        return 0;
    }

    public void Dispose() => _source.Dispose();
}
