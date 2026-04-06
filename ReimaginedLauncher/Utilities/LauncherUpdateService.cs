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
    
    public static bool IsUpdateAvailable { get; private set; }
    public static bool IsDownloading { get; private set; }
    public static bool IsUpdateDownloaded { get; private set; }
    public static string? LatestVersion { get; private set; }
    public static event EventHandler? UpdateDownloaded;
    public static event EventHandler? UpdateStateChanged;

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

            IsUpdateAvailable = true;
            LatestVersion = _updateInfo.TargetFullRelease.Version.ToString();
            UpdateStateChanged?.Invoke(null, EventArgs.Empty);

            IsDownloading = true;
            UpdateStateChanged?.Invoke(null, EventArgs.Empty);

            try
            {
                await _updateManager.DownloadUpdatesAsync(_updateInfo);
                IsUpdateDownloaded = true;
            }
            finally
            {
                IsDownloading = false;
            }

            UpdateDownloaded?.Invoke(null, EventArgs.Empty);
            UpdateStateChanged?.Invoke(null, EventArgs.Empty);
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
