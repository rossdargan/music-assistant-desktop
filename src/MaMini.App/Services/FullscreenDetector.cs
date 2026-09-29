using System.Runtime.InteropServices;
using MaMini.App.Interop;
using MaMini.Core.Geometry;

namespace MaMini.App.Services;

/// <summary>
/// Detects a fullscreen app (game, video, presentation) on the widget's monitor so the widget can step aside.
/// Uses the shell's appbar notification plus foreground-window changes as triggers.
/// </summary>
internal sealed class FullscreenDetector : IDisposable
{
    private readonly MessageWindow _window;
    private readonly Func<IntRect?> _widgetMonitor;
    private readonly Func<IntPtr> _widgetHandle;
    private readonly NativeMethods.WinEventProc _winEventProc;
    private readonly uint _callbackMessage;
    private IntPtr _winEventHook;
    private bool _appBarRegistered;
    private bool _enabled;

    public FullscreenDetector(MessageWindow window, Func<IntRect?> widgetMonitor, Func<IntPtr> widgetHandle)
    {
        _window = window;
        _widgetMonitor = widgetMonitor;
        _widgetHandle = widgetHandle;
        _winEventProc = OnWinEvent;
        _callbackMessage = NativeMethods.RegisterWindowMessage("MaMini.AppBarNotify");
        _window.MessageReceived += OnMessage;
    }

    public event EventHandler<bool>? FullscreenChanged;

    public bool IsFullscreen { get; private set; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            if (value)
            {
                Register();
                Evaluate();
            }
            else
            {
                Unregister();
                SetFullscreen(false);
            }
        }
    }

    public void Evaluate()
    {
        if (!_enabled)
        {
            return;
        }

        SetFullscreen(IsForegroundFullscreen());
    }

    public void Dispose()
    {
        Unregister();
        _window.MessageReceived -= OnMessage;
    }

    private bool IsForegroundFullscreen()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == _widgetHandle() || foreground == _window.Handle
            || foreground == NativeMethods.GetShellWindow() || foreground == NativeMethods.GetDesktopWindow())
        {
            return false;
        }

        var className = NativeMethods.ClassNameOf(foreground);
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(foreground, out var rect) || _widgetMonitor() is not { } monitor)
        {
            return false;
        }

        return rect.Left <= monitor.Left && rect.Top <= monitor.Top && rect.Right >= monitor.Right && rect.Bottom >= monitor.Bottom;
    }

    private void SetFullscreen(bool value)
    {
        if (value == IsFullscreen)
        {
            return;
        }

        IsFullscreen = value;
        FullscreenChanged?.Invoke(this, value);
    }

    private void Register()
    {
        var data = new NativeMethods.APPBARDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.APPBARDATA>(),
            hWnd = _window.Handle,
            uCallbackMessage = _callbackMessage,
        };
        _appBarRegistered = NativeMethods.SHAppBarMessage(NativeMethods.ABM_NEW, ref data) != UIntPtr.Zero;
        _winEventHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _winEventProc,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);
    }

    private void Unregister()
    {
        if (_appBarRegistered)
        {
            var data = new NativeMethods.APPBARDATA { cbSize = Marshal.SizeOf<NativeMethods.APPBARDATA>(), hWnd = _window.Handle };
            NativeMethods.SHAppBarMessage(NativeMethods.ABM_REMOVE, ref data);
            _appBarRegistered = false;
        }

        if (_winEventHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
    }

    private void OnMessage(int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_enabled && msg == (int)_callbackMessage && wParam.ToInt32() == NativeMethods.ABN_FULLSCREENAPP)
        {
            // lParam says whether *a* fullscreen app opened/closed; re-check against our monitor.
            Evaluate();
            handled = true;
        }
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time) => Evaluate();
}
