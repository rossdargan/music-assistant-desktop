using System.Runtime.InteropServices;
using MaMini.App.Interop;
using MaMini.Core.Diagnostics;
using MaMini.Core.Input;

namespace MaMini.App.Services;

/// <summary>RegisterHotKey wrapper. Registrations are grouped so each feature can clear its own set.</summary>
internal sealed class HotkeyService : IDisposable
{
    private readonly MessageWindow _window;
    private readonly Dictionary<int, (string Group, Action Action)> _registrations = new();
    private int _nextId = 0x100;

    public HotkeyService(MessageWindow window)
    {
        _window = window;
        _window.MessageReceived += OnMessage;
    }

    /// <summary>Returns false if another application already owns the combination.</summary>
    public bool Register(string group, Hotkey hotkey, Action action)
    {
        var id = _nextId++;
        if (!NativeMethods.RegisterHotKey(_window.Handle, id, (int)hotkey.Modifiers | NativeMethods.MOD_NOREPEAT, hotkey.VirtualKey))
        {
            Log.Warn($"Hotkey {hotkey} could not be registered (error {Marshal.GetLastWin32Error()}).");
            return false;
        }

        _registrations[id] = (group, action);
        return true;
    }

    public void Clear(string group)
    {
        foreach (var id in _registrations.Where(r => r.Value.Group == group).Select(r => r.Key).ToList())
        {
            NativeMethods.UnregisterHotKey(_window.Handle, id);
            _registrations.Remove(id);
        }
    }

    public void Dispose()
    {
        foreach (var id in _registrations.Keys)
        {
            NativeMethods.UnregisterHotKey(_window.Handle, id);
        }

        _registrations.Clear();
        _window.MessageReceived -= OnMessage;
    }

    private void OnMessage(int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _registrations.TryGetValue(wParam.ToInt32(), out var registration))
        {
            handled = true;
            registration.Action();
        }
    }
}
