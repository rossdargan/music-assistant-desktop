using System.Text.Json.Serialization;

namespace MaMini.Core.Settings;

public enum MediaKeyMode
{
    /// <summary>Windows media overlay (System Media Transport Controls).</summary>
    Smtc,

    /// <summary>RegisterHotKey on the media keys; exclusive, other players stop receiving them.</summary>
    Hotkey,

    /// <summary>Low-level keyboard hook; can also take over the volume keys.</summary>
    Hook,

    Off,
}

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

public enum WidgetCorner
{
    BottomRight,
    BottomLeft,
    TopRight,
    TopLeft,
}

public sealed class WindowPlacement
{
    /// <summary>Left edge in physical pixels (virtual-screen coordinates).</summary>
    public int X { get; set; }

    /// <summary>Top edge in physical pixels (virtual-screen coordinates).</summary>
    public int Y { get; set; }

    /// <summary>Device name of the monitor the widget was on (e.g. \\.\DISPLAY1).</summary>
    public string? Monitor { get; set; }
}

public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    // Connection
    public string? ServerUrl { get; set; }

    /// <summary>DPAPI-protected, base64. Use <see cref="SettingsService"/> to read/write the token.</summary>
    public string? ProtectedToken { get; set; }

    public string? SelectedPlayerId { get; set; }

    public bool FollowActivePlayer { get; set; }

    // Window
    public WindowPlacement? Placement { get; set; }

    public WidgetCorner DefaultCorner { get; set; } = WidgetCorner.BottomRight;

    public bool AlwaysOnTop { get; set; } = true;

    public bool Locked { get; set; }

    public bool ClickThrough { get; set; }

    public bool Compact { get; set; }

    public bool Hidden { get; set; }

    public bool SnapToEdges { get; set; } = true;

    public double Opacity { get; set; } = 0.95;

    public bool HideWhenFullscreen { get; set; } = true;

    public bool ShowProgress { get; set; } = true;

    public ThemeMode Theme { get; set; } = ThemeMode.System;

    // Behaviour
    public bool StartWithWindows { get; set; }

    public bool NotifyOnTrackChange { get; set; }

    public MediaKeyMode MediaKeys { get; set; } = MediaKeyMode.Smtc;

    /// <summary>Hook mode only: also take over the volume up/down/mute keys.</summary>
    public bool CaptureVolumeKeys { get; set; }

    public int VolumeStep { get; set; } = 5;

    // Global hotkeys (text form, e.g. "Ctrl+Alt+M"); null or empty = disabled.
    public string? HotkeyToggleWidget { get; set; } = "Ctrl+Alt+M";

    public string? HotkeyPlayPause { get; set; }

    public string? HotkeyNext { get; set; }

    public string? HotkeyFavorite { get; set; }

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Unknown { get; set; }

    public AppSettings Clone()
    {
        var clone = (AppSettings)MemberwiseClone();
        clone.Placement = Placement is null ? null : new WindowPlacement { X = Placement.X, Y = Placement.Y, Monitor = Placement.Monitor };
        return clone;
    }

    public void Normalize()
    {
        Opacity = Math.Clamp(double.IsFinite(Opacity) ? Opacity : 0.95, 0.3, 1.0);
        VolumeStep = Math.Clamp(VolumeStep, 1, 25);
        if (!Enum.IsDefined(MediaKeys))
        {
            MediaKeys = MediaKeyMode.Smtc;
        }

        if (!Enum.IsDefined(Theme))
        {
            Theme = ThemeMode.System;
        }

        if (!Enum.IsDefined(DefaultCorner))
        {
            DefaultCorner = WidgetCorner.BottomRight;
        }
    }
}
