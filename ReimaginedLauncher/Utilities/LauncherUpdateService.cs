using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Threading;
using Velopack;
using Velopack.Sources;

namespace ReimaginedLauncher.Utilities;

public static class LauncherUpdateService
{
    private const string RepoUrl = "https://github.com/D2R-Reimagined/reimagined-launcher";
    private static bool _hasCheckedForUpdates;

    public static async Task CheckForUpdatesAsync()
    {
        if (_hasCheckedForUpdates || !OperatingSystem.IsWindows())
        {
            return;
        }

        _hasCheckedForUpdates = true;

        try
        {
            var source = new GithubSource(RepoUrl, accessToken: null, prerelease: false);
            var updateManager = new UpdateManager(source);

            if (!updateManager.IsInstalled)
            {
                return;
            }

            var updateInfo = await updateManager.CheckForUpdatesAsync();
            if (updateInfo == null)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
                Notifications.SendNotification(
                    $"Launcher update {updateInfo.TargetFullRelease.Version} available. Downloading...",
                    "Info"));

            await updateManager.DownloadUpdatesAsync(updateInfo);

            await Dispatcher.UIThread.InvokeAsync(() =>
                Notifications.SendNotification(
                    $"Launcher update {updateInfo.TargetFullRelease.Version} downloaded. Restart the launcher to apply it.",
                    "Success"));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Launcher update check failed: {ex}");
        }
    }
}
