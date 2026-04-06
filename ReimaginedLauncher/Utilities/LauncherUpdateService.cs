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
    private static UpdateManager? _updateManager;
    private static UpdateInfo? _updateInfo;
    
    public static bool IsUpdateDownloaded { get; private set; }
    public static string? LatestVersion { get; private set; }
    public static event EventHandler? UpdateDownloaded;

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
            _updateManager = new UpdateManager(source);

            if (!_updateManager.IsInstalled)
            {
                return;
            }

            _updateInfo = await _updateManager.CheckForUpdatesAsync();
            if (_updateInfo == null)
            {
                return;
            }

            LatestVersion = _updateInfo.TargetFullRelease.Version.ToString();

            await Dispatcher.UIThread.InvokeAsync(() =>
                Notifications.SendNotification(
                    $"Launcher update {LatestVersion} available. Downloading...",
                    "Info"));

            await _updateManager.DownloadUpdatesAsync(_updateInfo);

            IsUpdateDownloaded = true;
            UpdateDownloaded?.Invoke(null, EventArgs.Empty);

            await Dispatcher.UIThread.InvokeAsync(() =>
                Notifications.SendNotification(
                    $"Launcher update {LatestVersion} downloaded. Restart the launcher to apply it.",
                    "Success"));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Launcher update check failed: {ex}");
        }
    }

    public static void ApplyUpdateAndRestart()
    {
        if (_updateManager != null && _updateInfo != null && IsUpdateDownloaded)
        {
            _updateManager.ApplyUpdatesAndRestart(_updateInfo);
        }
    }

}
