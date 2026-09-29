using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MaMini.App.Interop;
using MaMini.Core.Geometry;
using MaMini.Core.Settings;

namespace MaMini.App.Services;

/// <summary>
/// Snaps the widget to work-area edges while dragging, remembers where it was left, and keeps it on-screen
/// when monitors change. Everything is done in physical pixels to behave across mixed-DPI setups.
/// </summary>
internal sealed class WindowPlacementService : IDisposable
{
    private const int Margin = 12;
    private const int SnapThresholdDip = 14;
    private const uint MONITORINFOF_PRIMARY = 1;

    private readonly Window _window;
    private HwndSource? _source;
    private (IntRect Rect, NativeMethods.POINT Cursor)? _dragStart;

    public WindowPlacementService(Window window)
    {
        _window = window;
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            Attach();
        }
        else
        {
            window.SourceInitialized += (_, _) => Attach();
        }
    }

    /// <summary>Raised after the user finishes dragging the window.</summary>
    public event EventHandler<WindowPlacement>? PlacementChanged;

    public bool SnapToEdges { get; set; } = true;

    public WidgetCorner DefaultCorner { get; set; } = WidgetCorner.BottomRight;

    private IntPtr Handle => new WindowInteropHelper(_window).Handle;

    public void Restore(WindowPlacement? placement)
    {
        if (!NativeMethods.GetWindowRect(Handle, out var rect))
        {
            return;
        }

        var current = IntRect.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        var monitors = GetMonitors();
        var workAreas = monitors.Select(m => m.WorkArea).ToList();

        IntRect target;
        if (placement is null)
        {
            target = SnapMath.DefaultPosition(current.Width, current.Height, workAreas.FirstOrDefault(), DefaultCorner, ScaleToPixels(Margin));
            MoveTo(target);
        }
        else
        {
            // Move first so a DPI change on the target monitor resizes the window, then clamp using the real size.
            MoveTo(current.MoveTo(placement.X, placement.Y));
        }

        EnsureOnScreen();
    }

    public void ResetToCorner(WidgetCorner corner)
    {
        DefaultCorner = corner;
        Restore(null);
        RaisePlacementChanged();
    }

    /// <summary>Re-clamps after a size change or display change so nothing is left off-screen.</summary>
    public void EnsureOnScreen()
    {
        if (!NativeMethods.GetWindowRect(Handle, out var rect))
        {
            return;
        }

        var current = IntRect.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        var target = SnapMath.EnsureVisible(current, GetMonitors().Select(m => m.WorkArea).ToList(), DefaultCorner, ScaleToPixels(Margin));
        if (target != current)
        {
            MoveTo(target);
        }
    }

    /// <summary>True when the widget sits in the right half of its monitor (compact mode grows leftwards).</summary>
    public bool IsOnRightHalf()
    {
        if (!NativeMethods.GetWindowRect(Handle, out var rect))
        {
            return true;
        }

        var window = IntRect.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        var monitor = NativeMethods.MonitorFromWindow(Handle, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return !NativeMethods.TryGetMonitorInfo(monitor, out var info) || SnapMath.IsOnRightHalf(window, ToIntRect(info.rcWork));
    }

    /// <summary>Physical-pixel bounds of the monitor the widget is on.</summary>
    public IntRect? CurrentMonitorBounds()
    {
        var monitor = NativeMethods.MonitorFromWindow(Handle, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return NativeMethods.TryGetMonitorInfo(monitor, out var info) ? ToIntRect(info.rcMonitor) : null;
    }

    public void Dispose() => _source?.RemoveHook(WndProc);

    private static IntRect ToIntRect(NativeMethods.RECT r) => IntRect.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);

    private static List<(string Device, IntRect WorkArea)> GetMonitors()
    {
        var result = new List<(string Device, IntRect WorkArea, bool Primary)>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref NativeMethods.RECT _, IntPtr _) =>
        {
            if (NativeMethods.TryGetMonitorInfo(monitor, out var info))
            {
                result.Add((info.szDevice, ToIntRect(info.rcWork), (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }

            return true;
        }, IntPtr.Zero);

        // Primary first: SnapMath uses the first work area as the fallback.
        return result.OrderByDescending(m => m.Primary).Select(m => (m.Device, m.WorkArea)).ToList();
    }

    private void Attach()
    {
        _source = HwndSource.FromHwnd(Handle);
        _source?.AddHook(WndProc);
    }

    private int ScaleToPixels(int dip)
    {
        var dpi = Handle == IntPtr.Zero ? 96u : NativeMethods.GetDpiForWindow(Handle);
        return (int)Math.Round(dip * (dpi == 0 ? 96 : dpi) / 96.0);
    }

    private void MoveTo(IntRect target) =>
        NativeMethods.SetWindowPos(Handle, IntPtr.Zero, target.Left, target.Top, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

    /// <summary>Persists the current position (e.g. after the widget changed size).</summary>
    public void SaveCurrent() => RaisePlacementChanged();

    private void RaisePlacementChanged()
    {
        if (!NativeMethods.GetWindowRect(Handle, out var rect))
        {
            return;
        }

        var monitor = NativeMethods.MonitorFromWindow(Handle, NativeMethods.MONITOR_DEFAULTTONEAREST);
        NativeMethods.TryGetMonitorInfo(monitor, out var info);
        PlacementChanged?.Invoke(this, new WindowPlacement { X = rect.Left, Y = rect.Top, Monitor = info.szDevice });
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_ENTERSIZEMOVE:
                _dragStart = NativeMethods.GetWindowRect(hwnd, out var startRect) && NativeMethods.GetCursorPos(out var startCursor)
                    ? (ToIntRect(startRect), startCursor)
                    : null;
                break;

            case NativeMethods.WM_MOVING when SnapToEdges:
            {
                var rect = Marshal.PtrToStructure<NativeMethods.RECT>(lParam);
                var proposed = ToIntRect(rect);

                // Windows proposes positions relative to the last (possibly snapped) one, which makes edges sticky.
                // Work from where the cursor has moved since the drag began instead.
                if (_dragStart is { } start && NativeMethods.GetCursorPos(out var cursor))
                {
                    proposed = start.Rect.MoveTo(start.Rect.Left + cursor.X - start.Cursor.X, start.Rect.Top + cursor.Y - start.Cursor.Y);
                }

                var monitor = NativeMethods.MonitorFromRect(ref rect, NativeMethods.MONITOR_DEFAULTTONEAREST);
                if (NativeMethods.TryGetMonitorInfo(monitor, out var info))
                {
                    var snapped = SnapMath.Snap(proposed, ToIntRect(info.rcWork), ScaleToPixels(SnapThresholdDip));
                    rect.Left = snapped.Left;
                    rect.Top = snapped.Top;
                    rect.Right = snapped.Right;
                    rect.Bottom = snapped.Bottom;
                    Marshal.StructureToPtr(rect, lParam, false);
                    handled = true;
                    return new IntPtr(1);
                }

                break;
            }

            case NativeMethods.WM_EXITSIZEMOVE:
                _dragStart = null;
                RaisePlacementChanged();
                break;

            case NativeMethods.WM_DISPLAYCHANGE:
                // Let Windows finish rearranging monitors first.
                _window.Dispatcher.BeginInvoke(EnsureOnScreen, System.Windows.Threading.DispatcherPriority.Background);
                break;
        }

        return IntPtr.Zero;
    }
}
