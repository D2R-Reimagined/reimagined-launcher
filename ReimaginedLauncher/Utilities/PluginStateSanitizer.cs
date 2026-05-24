using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

/// <summary>
/// Startup-time housekeeping for plugin-related on-disk state. Reconciles the
/// asset backup manifest against the plugins currently registered in
/// settings, prunes unreferenced empty subdirectories under
/// <c>%AppData%\ReimaginedLauncher\plugins</c> and
/// <c>plugin-asset-backups</c>, and clears stale plugin zip downloads left
/// behind in <c>%TEMP%</c>.
/// </summary>
public static class PluginStateSanitizer
{
    private const string TempPluginDownloadsRoot = "d2r-reimagined-launcher";

    public static async Task RunStartupSanitizationAsync()
    {
        try
        {
            var settings = await SettingsManager.LoadAsync().ConfigureAwait(false);

            var knownIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var knownFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var profile in settings.Profiles)
            {
                foreach (var registration in profile.Plugins)
                {
                    if (!string.IsNullOrWhiteSpace(registration.Id))
                    {
                        knownIds.Add(registration.Id);
                    }

                    if (!string.IsNullOrWhiteSpace(registration.FolderName))
                    {
                        knownFolders.Add(registration.FolderName);
                    }
                }
            }

            await PluginAssetBackupService.ReconcileWithKnownPluginIdsAsync(knownIds).ConfigureAwait(false);

            RemoveUnreferencedEmptySubdirectories(PluginsService.PluginsDirectoryPath, knownFolders);
            RemoveUnreferencedEmptyBackupSubdirectories();
            ClearTempPluginDownloads();
        }
        catch (Exception ex)
        {
            LaunchDiagnostics.LogException("Plugin state sanitization failed", ex);
        }
    }

    private static void RemoveUnreferencedEmptySubdirectories(string root, ICollection<string> referencedNames)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        IEnumerable<string> subdirs;
        try
        {
            subdirs = Directory.EnumerateDirectories(root).ToList();
        }
        catch (Exception ex)
        {
            LaunchDiagnostics.LogException($"Failed to enumerate '{root}' for sanitization", ex);
            return;
        }

        foreach (var dir in subdirs)
        {
            var name = Path.GetFileName(dir);
            if (referencedNames.Contains(name))
            {
                continue;
            }

            TryDeleteIfEmpty(dir);
        }
    }

    private static void RemoveUnreferencedEmptyBackupSubdirectories()
    {
        // Backup payload folders are GUID-named and managed by
        // PluginAssetBackupService's own orphan sweep, which has already run
        // by this point. Anything still empty here is unreferenced by
        // design, so prune it.
        var root = PluginAssetBackupService.BackupRootDirectory;
        if (!Directory.Exists(root))
        {
            return;
        }

        IEnumerable<string> subdirs;
        try
        {
            subdirs = Directory.EnumerateDirectories(root).ToList();
        }
        catch (Exception ex)
        {
            LaunchDiagnostics.LogException($"Failed to enumerate '{root}' for sanitization", ex);
            return;
        }

        foreach (var dir in subdirs)
        {
            TryDeleteIfEmpty(dir);
        }
    }

    private static void TryDeleteIfEmpty(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            if (Directory.EnumerateFileSystemEntries(directory).Any())
            {
                return;
            }

            Directory.Delete(directory);
        }
        catch (Exception ex)
        {
            LaunchDiagnostics.LogException($"Failed to delete empty plugin subdirectory '{directory}'", ex);
        }
    }

    private static void ClearTempPluginDownloads()
    {
        string tempRoot;
        try
        {
            tempRoot = Path.Combine(Path.GetTempPath(), TempPluginDownloadsRoot);
        }
        catch (Exception ex)
        {
            LaunchDiagnostics.LogException("Failed to resolve temp path for plugin download cleanup", ex);
            return;
        }

        if (!Directory.Exists(tempRoot))
        {
            return;
        }

        try
        {
            Directory.Delete(tempRoot, recursive: true);
        }
        catch (Exception ex)
        {
            LaunchDiagnostics.LogException(
                $"Failed to clear stale plugin download cache '{tempRoot}'", ex);
        }
    }
}
