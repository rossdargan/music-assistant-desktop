using System.Windows.Controls;
using System.Windows.Input;
using MaMini.Core.Input;

namespace MaMini.App.Controls;

/// <summary>Read-only text box that records a key combination. Backspace/Delete clears it.</summary>
public sealed class HotkeyBox : TextBox
{
    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        ContextMenu = null;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Tab)
        {
            // Keep keyboard navigation working.
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;
        if (key is Key.Back or Key.Delete or Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            Text = string.Empty;
            return;
        }

        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0 || Hotkey.IsModifierKey(vk))
        {
            return;
        }

        var modifiers = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
        {
            modifiers |= HotkeyModifiers.Win;
        }

        var hotkey = new Hotkey(modifiers, vk);
        if (hotkey.IsValid)
        {
            Text = hotkey.ToString();
        }
    }
}
