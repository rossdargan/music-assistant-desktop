using System.Windows;
using MaMini.Core.Diagnostics;
using Velopack;
using Velopack.Sources;

namespace MaMini.App.Services;

internal sealed class AppUpdateService
{
    private bool _busy;

    public bool IsBusy => _busy;

    public async Task CheckForUpdatesAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            var manager = new UpdateManager(new GithubSource(
                "https://github.com/rossdargan/music-assistant-desktop", null, false));
            if (!manager.IsInstalled)
            {
                MessageBox.Show(
                    "This copy is not installed with the MA Mini installer. Install MA Mini using the Setup.exe from GitHub Releases to enable in-app updates.",
                    "MA Mini updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                MessageBox.Show($"MA Mini {manager.CurrentVersion} is up to date.",
                    "MA Mini updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var answer = MessageBox.Show(
                $"MA Mini {update.TargetFullRelease.Version} is available (installed: {manager.CurrentVersion}). Download, install and restart now?",
                "MA Mini updates", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            await manager.DownloadUpdatesAsync(update);
            manager.ApplyUpdatesAndRestart(update);
        }
        catch (Exception ex)
        {
            Log.Error("Could not check for or install an MA Mini update.", ex);
            MessageBox.Show(
                "Could not check for or install an update. Check your internet connection and try again. Details are in the MA Mini log.",
                "MA Mini updates", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
        }
    }
}
