using System.Windows;
using System.Windows.Media;
using MaMini.Core.Settings;
using Microsoft.Win32;

namespace MaMini.App.Services;

/// <summary>Applies the light/dark/high-contrast palette and follows the Windows setting when on "System".</summary>
internal sealed class ThemeService : IDisposable
{
    private readonly Application _app;
    private ThemeMode _mode = ThemeMode.System;
    private string? _applied;

    public ThemeService(Application app)
    {
        _app = app;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
    }

    public void Apply(ThemeMode mode)
    {
        _mode = mode;
        Refresh();
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
    }

    private static bool SystemUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
    }

    private void Refresh()
    {
        string name;
        if (SystemParameters.HighContrast)
        {
            name = "HighContrast";
        }
        else
        {
            var light = _mode switch
            {
                ThemeMode.Light => true,
                ThemeMode.Dark => false,
                _ => SystemUsesLightTheme(),
            };
            name = light ? "Light" : "Dark";
        }

        if (name == _applied)
        {
            return;
        }

        _applied = name;
        var dictionary = name == "HighContrast"
            ? BuildHighContrast()
            : new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{name}.xaml") };
        _app.Resources.MergedDictionaries[0] = dictionary;
    }

    private static ResourceDictionary BuildHighContrast() => new()
    {
        ["Brush.Background"] = SystemColors.WindowBrush,
        ["Brush.Border"] = SystemColors.WindowTextBrush,
        ["Brush.Foreground"] = SystemColors.WindowTextBrush,
        ["Brush.Subtle"] = SystemColors.GrayTextBrush,
        ["Brush.Hover"] = SystemColors.HighlightBrush,
        ["Brush.Pressed"] = SystemColors.HighlightBrush,
        ["Brush.Accent"] = SystemColors.HotTrackBrush,
        ["Brush.Heart"] = SystemColors.HotTrackBrush,
        ["Brush.ArtPlaceholder"] = SystemColors.ControlBrush,
        ["Brush.ProgressTrack"] = SystemColors.ControlBrush,
        ["Brush.Overlay"] = SystemColors.WindowBrush,
    };

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility)
        {
            _app.Dispatcher.BeginInvoke(Refresh);
        }
    }

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            _app.Dispatcher.BeginInvoke(Refresh);
        }
    }
}
