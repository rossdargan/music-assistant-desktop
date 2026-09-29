using System.Diagnostics.CodeAnalysis;

namespace MaMini.Core.Input;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>A global hotkey: modifiers (RegisterHotKey MOD_* values) plus a Win32 virtual-key code.</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int VirtualKey)
{
    private static readonly Dictionary<int, string> Names = BuildNames();
    private static readonly Dictionary<string, int> Codes = BuildCodes();

    /// <summary>Hotkeys need at least one modifier, except for F-keys and media keys.</summary>
    public bool IsValid => VirtualKey != 0 && (Modifiers != HotkeyModifiers.None || IsStandaloneKey(VirtualKey));

    public static bool IsModifierKey(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    public static bool TryParse(string? text, [NotNullWhen(true)] out Hotkey? hotkey)
    {
        hotkey = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        int? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= HotkeyModifiers.Control;
                    continue;
                case "ALT":
                    modifiers |= HotkeyModifiers.Alt;
                    continue;
                case "SHIFT":
                    modifiers |= HotkeyModifiers.Shift;
                    continue;
                case "WIN":
                case "WINDOWS":
                    modifiers |= HotkeyModifiers.Win;
                    continue;
            }

            if (key is not null || !Codes.TryGetValue(raw.ToUpperInvariant(), out var vk))
            {
                return false;
            }

            key = vk;
        }

        if (key is null)
        {
            return false;
        }

        var result = new Hotkey(modifiers, key.Value);
        if (!result.IsValid)
        {
            return false;
        }

        hotkey = result;
        return true;
    }

    public static string KeyName(int vk) => Names.TryGetValue(vk, out var name) ? name : $"0x{vk:X2}";

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Win))
        {
            parts.Add("Win");
        }

        parts.Add(KeyName(VirtualKey));
        return string.Join("+", parts);
    }

    private static bool IsStandaloneKey(int vk) => vk is >= 0x70 and <= 0x87 or >= 0xAD and <= 0xB3;

    private static Dictionary<int, string> BuildNames()
    {
        var map = new Dictionary<int, string>();
        for (var c = 'A'; c <= 'Z'; c++)
        {
            map[c] = c.ToString();
        }

        for (var d = '0'; d <= '9'; d++)
        {
            map[d] = d.ToString();
        }

        for (var f = 1; f <= 24; f++)
        {
            map[0x6F + f] = $"F{f}";
        }

        for (var n = 0; n <= 9; n++)
        {
            map[0x60 + n] = $"Num{n}";
        }

        map[0x08] = "Backspace";
        map[0x09] = "Tab";
        map[0x0D] = "Enter";
        map[0x1B] = "Esc";
        map[0x20] = "Space";
        map[0x21] = "PageUp";
        map[0x22] = "PageDown";
        map[0x23] = "End";
        map[0x24] = "Home";
        map[0x25] = "Left";
        map[0x26] = "Up";
        map[0x27] = "Right";
        map[0x28] = "Down";
        map[0x2C] = "PrintScreen";
        map[0x2D] = "Insert";
        map[0x2E] = "Delete";
        map[0x13] = "Pause";
        map[0x6A] = "NumMultiply";
        map[0x6B] = "NumPlus";
        map[0x6D] = "NumMinus";
        map[0x6E] = "NumDecimal";
        map[0x6F] = "NumDivide";
        map[0xAD] = "VolumeMute";
        map[0xAE] = "VolumeDown";
        map[0xAF] = "VolumeUp";
        map[0xB0] = "MediaNext";
        map[0xB1] = "MediaPrevious";
        map[0xB2] = "MediaStop";
        map[0xB3] = "MediaPlayPause";
        map[0xBA] = ";";
        map[0xBB] = "=";
        map[0xBC] = ",";
        map[0xBD] = "-";
        map[0xBE] = ".";
        map[0xBF] = "/";
        map[0xC0] = "`";
        map[0xDB] = "[";
        map[0xDC] = "\\";
        map[0xDD] = "]";
        map[0xDE] = "'";
        return map;
    }

    private static Dictionary<string, int> BuildCodes()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (vk, name) in Names)
        {
            map[name.ToUpperInvariant()] = vk;
        }

        map["ESCAPE"] = 0x1B;
        map["RETURN"] = 0x0D;
        map["PGUP"] = 0x21;
        map["PGDN"] = 0x22;
        map["DEL"] = 0x2E;
        map["INS"] = 0x2D;
        return map;
    }
}
