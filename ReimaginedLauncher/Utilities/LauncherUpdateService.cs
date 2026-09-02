using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace ReimaginedLauncher.Utilities;

public enum LauncherUpdateCheckStatus
{
    UpToDate,
    UpdateReady,
    Disabled,
    InProgress,
    NotInstalled,
    Failed
}

public sealed record LauncherUpdateCheckResult(LauncherUpdateCheckStatus Status, string? ErrorMessage = null);

public static class LauncherUpdateService
{
    private const string RepoUrl = "https://github.com/D2R-Reimagined/reimagined-launcher";
    private static readonly SemaphoreSlim UpdateCheckLock = new(1, 1);
    private static UpdateManager? _updateManager;
    private static UpdateInfo? _updateInfo;
    
    public static bool IsUpdateAvailable { get; private set; }
    public static bool IsDownloading { get; private set; }
    public static bool IsUpdateDownloaded { get; private set; }

    // When true, all automatic update behavior is suppressed: update checks (startup,
    // hourly poll, and the version-label click) are skipped, no update is downloaded so
    // no reminder banner appears, and any previously downloaded update is never applied
    // on close/restart. Driven by the "Disable automatic launcher updates" setting.
    public static bool AreUpdatesDisabled { get; set; }

    public static string? LatestVersion { get; private set; }
    public static event EventHandler? UpdateDownloaded;
    public static event EventHandler? UpdateStateChanged;

    public static Task<LauncherUpdateCheckResult> CheckForUpdatesAsync()
    {
        return CheckForUpdatesAsync(() =>
            new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false)));
    }

    internal static async Task<LauncherUpdateCheckResult> CheckForUpdatesAsync(Func<UpdateManager> createUpdateManager)
    {
        if (AreUpdatesDisabled)
        {
            return new(LauncherUpdateCheckStatus.Disabled);
        }

        if (!await UpdateCheckLock.WaitAsync(0))
        {
            return new(LauncherUpdateCheckStatus.InProgress);
        }

        try
        {
            var updateManager = createUpdateManager();

            if (!updateManager.IsInstalled)
            {
                return new(LauncherUpdateCheckStatus.NotInstalled);
            }

            var updateInfo = await updateManager.CheckForUpdatesAsync();
            if (AreUpdatesDisabled)
            {
                return new(LauncherUpdateCheckStatus.Disabled);
            }

            if (updateInfo == null)
            {
                _updateInfo = null;
                IsUpdateAvailable = false;
                IsUpdateDownloaded = false;
                LatestVersion = null;
                return new(LauncherUpdateCheckStatus.UpToDate);
            }

            if (IsUpdateDownloaded && _updateInfo?.TargetFullRelease.Version == updateInfo.TargetFullRelease.Version)
            {
                return new(LauncherUpdateCheckStatus.UpdateReady);
            }

            _updateManager = updateManager;
            _updateInfo = updateInfo;
            IsUpdateAvailable = true;
            IsUpdateDownloaded = false;
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
            return new(LauncherUpdateCheckStatus.UpdateReady);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Launcher update check failed: {ex}");
            return new(LauncherUpdateCheckStatus.Failed, ex.Message);
        }
        finally
        {
            UpdateCheckLock.Release();
            UpdateStateChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static void ApplyUpdateAndRestart()
    {
        if (AreUpdatesDisabled)
        {
            return;
        }

        if (_updateManager != null && _updateInfo != null && IsUpdateDownloaded)
        {
            _updateManager.ApplyUpdatesAndRestart(_updateInfo);
        }
    }

}
