using System.Runtime.InteropServices;

namespace MaMini.App.Interop;

internal static partial class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_APPWINDOW = 0x00040000;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_POPUP = unchecked((int)0x80000000);

    public const int WM_MOVING = 0x0216;
    public const int WM_ENTERSIZEMOVE = 0x0231;
    public const int WM_EXITSIZEMOVE = 0x0232;
    public const int WM_HOTKEY = 0x0312;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;

    public const int MOD_NOREPEAT = 0x4000;

    public const int WH_KEYBOARD_LL = 13;
    public const int LLKHF_INJECTED = 0x10;

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    public const uint ABM_NEW = 0;
    public const uint ABM_REMOVE = 1;
    public const int ABN_FULLSCREENAPP = 2;

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0;

    public const int SM_CXSMICON = 49;

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    public delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, int modifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc proc, IntPtr hMod, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    public static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("shell32.dll")]
    public static extern UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    public static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc proc, uint idProcess, uint idThread, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    public static extern int GetClassName(IntPtr hWnd, char[] className, int maxCount);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    public static string ClassNameOf(IntPtr hwnd)
    {
        var buffer = new char[256];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(0, length));
    }

    public static void SetExtendedStyle(IntPtr hwnd, int add, int remove)
    {
        var style = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        var updated = (style | (uint)add) & ~(long)(uint)remove;
        if (updated != style)
        {
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(updated));
        }
    }

    public static bool TryGetMonitorInfo(IntPtr monitor, out MONITORINFOEX info)
    {
        info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
        return GetMonitorInfo(monitor, ref info);
    }
}
