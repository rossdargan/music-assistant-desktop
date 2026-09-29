using System.Windows.Interop;
using MaMini.App.Interop;

namespace MaMini.App.Services;

/// <summary>
/// Hidden top-level window that owns app-wide registrations (hotkeys, appbar notifications, SMTC),
/// so they don't depend on the widget being visible.
/// </summary>
internal sealed class MessageWindow : IDisposable
{
    private readonly HwndSource _source;

    public MessageWindow()
    {
        var parameters = new HwndSourceParameters("MaMiniMessageWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = NativeMethods.WS_POPUP,
            ExtendedWindowStyle = NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public delegate void MessageHandler(int msg, IntPtr wParam, IntPtr lParam, ref bool handled);

    public event MessageHandler? MessageReceived;

    public IntPtr Handle => _source.Handle;

    public void Dispose()
    {
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        MessageReceived?.Invoke(msg, wParam, lParam, ref handled);
        return IntPtr.Zero;
    }
}
