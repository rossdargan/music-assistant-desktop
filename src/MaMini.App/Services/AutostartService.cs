using System.IO;
using MaMini.Core.Diagnostics;
using Microsoft.Win32;

namespace MaMini.App.Services;

/// <summary>Start-with-Windows via the per-user Run key (no admin needed).</summary>
internal static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MaMini";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (exe is not null)
                {
                    key.SetValue(ValueName, $"\"{exe}\"");
                }
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warn("Could not update the autostart registry entry.", ex);
        }
    }
}
