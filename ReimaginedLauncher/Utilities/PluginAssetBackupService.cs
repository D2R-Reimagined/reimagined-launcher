using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReimaginedLauncher.Generators;

namespace ReimaginedLauncher.Utilities;

/// <summary>
/// Tracks original mod files replaced by plugin asset ops so they can be restored on disable/delete. Backups are keyed by absolute target path under the launcher's app data dir.
/// </summary>
public static class PluginAssetBackupService
{
    private const string BackupRootDirectoryName = "plugin-asset-backups";
    private const string ManifestFileName = "manifest.json";

    private static readonly JsonSerializerOptions ReadOptions = SerializerOptions.PropertyNameCaseInsensitive;
    private static readonly JsonSerializerOptions WriteOptions = SerializerOptions.CamelCase;

    // Single-writer guard: all public mutators serialize through this semaphore.
    private static readonly SemaphoreSlim ServiceLock = new(1, 1);

    public static string BackupRootDirectory =>
        Path.Combine(SettingsManager.AppDirectoryPath, BackupRootDirectoryName);

    private static string ManifestPath => Path.Combine(BackupRootDirectory, ManifestFileName);

    /// <summary>
    /// Records that <paramref name="pluginId"/> is about to overwrite the file
    /// at <paramref name="destinationAbsolutePath"/>. The first time a target
    /// is seen, the existing file (if any) is copied into the backup store so
    /// it can be restored later. Subsequent calls only register the plugin as
    /// an additional claimant; the snapshot is never refreshed mid-launch,
    /// because <see cref="RestoreAllAsync"/> is invoked at the start of every
    /// Start Game pass and clears the manifest, so any entry observed during a
    /// pass already represents the true pre-plugin original.
    /// </summary>
    public static async Task RegisterReplacementAsync(string pluginId, string destinationAbsolutePath)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            throw new ArgumentException("Plugin id is required.", nameof(pluginId));
        }

        if (string.IsNullOrWhiteSpace(destinationAbsolutePath))
        {
            throw new ArgumentException("Destination path is required.", nameof(destinationAbsolutePath));
        }

        if (!Path.IsPathRooted(destinationAbsolutePath))
        {
            throw new ArgumentException(
                "Destination path must be absolute.", nameof(destinationAbsolutePath));
        }

        var normalizedTarget = NormalizePath(destinationAbsolutePath);

        await ServiceLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var manifest = LoadManifestUnsafe();
            var entry = manifest.Entries.FirstOrDefault(item =>
                string.Equals(item.TargetAbsolutePath, normalizedTarget, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                entry = new PluginAssetBackupEntry { TargetAbsolutePath = normalizedTarget };
                manifest.Entries.Add(entry);
                await CaptureSnapshotAsync(entry, normalizedTarget).ConfigureAwait(false);
            }

            // If the entry already exists we deliberately do NOT re-snapshot:
            // the on-disk file at this point is whatever a prior step wrote
            // (e.g. the same plugin's asset copied during an earlier excel
            // directory pass within this launch), so re-capturing would
            // overwrite the genuine original with the plugin asset itself --
            // exactly the bug that left files on disk after disabling.

            if (!entry.ClaimingPluginIds.Contains(pluginId, StringComparer.OrdinalIgnoreCase))
            {
                entry.ClaimingPluginIds.Add(pluginId);
            }

            SaveManifestUnsafe(manifest);
        }
        finally
        {
            ServiceLock.Release();
        }
    }

    /// <summary>
    /// Restores every tracked target back to its pre-plugin state regardless
    /// of which plugin currently claims it. Intended to be called at the very
    /// start of a Start Game pass so the on-disk mod folder is genuinely
    /// pristine before tweaks and plugins are reapplied; this prevents the
    /// next snapshot from capturing a previous run's plugin asset as the
    /// "original". Entries whose restore fails are preserved in the manifest
    /// so the failure can be retried on the next launch.
    /// </summary>
    public static async Task RestoreAllAsync()
    {
        await ServiceLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var manifest = LoadManifestUnsafe();
            if (manifest.Entries.Count == 0)
            {
                return;
            }

            var restoredEntries = new List<PluginAssetBackupEntry>();

            foreach (var entry in manifest.Entries)
            {
                try
                {
                    if (entry.OriginalExisted &&
                        !string.IsNullOrWhiteSpace(entry.BackupAbsolutePath) &&
                        File.Exists(entry.BackupAbsolutePath))
                    {
                        var destinationFolder = Path.GetDirectoryName(entry.TargetAbsolutePath);
                        if (!string.IsNullOrWhiteSpace(destinationFolder))
                        {
                            Directory.CreateDirectory(destinationFolder);
                        }

                        await FileCopyHelper.CopyFileAsync(entry.BackupAbsolutePath, entry.TargetAbsolutePath)
                            .ConfigureAwait(false);
                        TryDeleteFile(entry.BackupAbsolutePath);
                    }
                    else if (!entry.OriginalExisted && File.Exists(entry.TargetAbsolutePath))
                    {
                        TryDeleteFile(entry.TargetAbsolutePath);
                    }

                    restoredEntries.Add(entry);
                }
                catch (Exception ex)
                {
                    LaunchDiagnostics.LogException(
                        $"Failed to restore plugin asset '{entry.TargetAbsolutePath}'", ex);
                    Notifications.SendNotification(
                        $"Failed to restore '{Path.GetFileName(entry.TargetAbsolutePath)}': {ex.Message}",
                        "Warning");

                    // Leave the entry intact so the next Start Game pass can
                    // retry the restore instead of orphaning the backup.
                }
            }

            if (restoredEntries.Count > 0)
            {
                manifest.Entries.RemoveAll(restoredEntries.Contains);
                SaveManifestUnsafe(manifest);
            }

            CleanupOrphanGuidFoldersUnsafe(manifest);
        }
        finally
        {
            ServiceLock.Release();
        }
    }

    /// <summary>
    /// Restores every target previously claimed by <paramref name="pluginId"/>.
    /// Other enabled plugins that still claim the same target keep the backup
    /// in place; the actual copy back to disk only happens once the last
    /// claimant releases the target. If a restore fails the entry is preserved
    /// so the user can retry later.
    /// </summary>
    public static async Task RestoreForPluginAsync(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return;
        }

        await ServiceLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var manifest = LoadManifestUnsafe();
            if (manifest.Entries.Count == 0)
            {
                return;
            }

            var affectedEntries = manifest.Entries
                .Where(entry => entry.ClaimingPluginIds.Contains(pluginId, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (affectedEntries.Count == 0)
            {
                return;
            }

            var dirty = false;

            foreach (var entry in affectedEntries)
            {
                var remainingClaimants = entry.ClaimingPluginIds
                    .Where(id => !string.Equals(id, pluginId, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (remainingClaimants.Count > 0)
                {
                    // A peer plugin still claims this target; just drop our id
                    // and leave the on-disk file alone.
                    entry.ClaimingPluginIds = remainingClaimants;
                    dirty = true;
                    continue;
                }

                try
                {
                    if (entry.OriginalExisted &&
                        !string.IsNullOrWhiteSpace(entry.BackupAbsolutePath) &&
                        File.Exists(entry.BackupAbsolutePath))
                    {
                        var destinationFolder = Path.GetDirectoryName(entry.TargetAbsolutePath);
                        if (!string.IsNullOrWhiteSpace(destinationFolder))
                        {
                            Directory.CreateDirectory(destinationFolder);
                        }

                        await FileCopyHelper.CopyFileAsync(entry.BackupAbsolutePath, entry.TargetAbsolutePath)
                            .ConfigureAwait(false);
                        TryDeleteFile(entry.BackupAbsolutePath);
                    }
                    else if (!entry.OriginalExisted && File.Exists(entry.TargetAbsolutePath))
                    {
                        // The plugin introduced a brand-new file; remove it so the
                        // mod folder returns to its pre-plugin state.
                        TryDeleteFile(entry.TargetAbsolutePath);
                    }

                    // Restore succeeded; releasing the last claim drops the entry
                    // when the manifest is compacted below.
                    entry.ClaimingPluginIds = remainingClaimants;
                    dirty = true;
                }
                catch (Exception ex)
                {
                    LaunchDiagnostics.LogException(
                        $"Failed to restore plugin asset '{entry.TargetAbsolutePath}'", ex);
                    Notifications.SendNotification(
                        $"Failed to restore '{Path.GetFileName(entry.TargetAbsolutePath)}': {ex.Message}",
                        "Warning");

                    // Leave the plugin id so a future disable/delete can retry the restore.
                }
            }

            if (dirty)
            {
                manifest.Entries.RemoveAll(entry => entry.ClaimingPluginIds.Count == 0);
                SaveManifestUnsafe(manifest);
            }

            CleanupOrphanGuidFoldersUnsafe(manifest);
        }
        finally
        {
            ServiceLock.Release();
        }
    }

    private static async Task CaptureSnapshotAsync(PluginAssetBackupEntry entry, string targetPath)
    {
        if (File.Exists(targetPath))
        {
            var backupPath = AllocateBackupPath(targetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            await FileCopyHelper.CopyFileAsync(targetPath, backupPath).ConfigureAwait(false);

            entry.OriginalExisted = true;
            entry.BackupAbsolutePath = backupPath;
        }
        else
        {
            entry.OriginalExisted = false;
            entry.BackupAbsolutePath = null;
        }
    }

    private static PluginAssetBackupManifest LoadManifestUnsafe()
    {
        if (!File.Exists(ManifestPath))
        {
            return new PluginAssetBackupManifest();
        }

        try
        {
            var json = File.ReadAllText(ManifestPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new PluginAssetBackupManifest();
            }

            return JsonSerializer.Deserialize<PluginAssetBackupManifest>(json, ReadOptions)
                   ?? new PluginAssetBackupManifest();
        }
        catch (Exception ex)
        {
            LaunchDiagnostics.LogException("Failed to read plugin asset backup manifest", ex);
            TryQuarantineCorruptManifest();
            return new PluginAssetBackupManifest();
        }
    }

    private static void TryQuarantineCorruptManifest()
    {
        try
        {
            if (!File.Exists(ManifestPath))
            {
                return;
            }

            var quarantinePath = Path.Combine(
                BackupRootDirectory,
                $"manifest.bad-{DateTime.UtcNow:yyyyMMddHHmmss}.json");
            File.Move(ManifestPath, quarantinePath, overwrite: true);
        }
        catch (Exception ex)
        {
            LaunchDiagnostics.LogException(
                "Failed to quarantine corrupt plugin asset backup manifest", ex);
        }
    }

    private static void SaveManifestUnsafe(PluginAssetBackupManifest manifest)
    {
        Directory.CreateDirectory(BackupRootDirectory);
        var json = JsonSerializer.Serialize(manifest, WriteOptions);

        // Write atomically: an interrupted save must not leave a truncated manifest behind.
        var tempPath = ManifestPath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, ManifestPath, overwrite: true);
    }

    private static string AllocateBackupPath(string targetAbsolutePath)
    {
        var fileName = Path.GetFileName(targetAbsolutePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "asset.bak";
        }

        var subfolder = Guid.NewGuid().ToString("N");
        return Path.Combine(BackupRootDirectory, subfolder, fileName);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            LaunchDiagnostics.LogException($"Failed to delete plugin asset backup file '{path}'", ex);
            return;
        }

        TryDeleteEmptyBackupSubfolder(Path.GetDirectoryName(path));
    }

    private static void TryDeleteEmptyBackupSubfolder(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            var normalized = NormalizePath(directory);
            var root = NormalizePath(BackupRootDirectory);
            if (!string.Equals(NormalizePath(Path.GetDirectoryName(normalized) ?? string.Empty), root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!Directory.Exists(normalized))
            {
                return;
            }

            if (Directory.EnumerateFileSystemEntries(normalized).Any())
            {
                return;
            }

            Directory.Delete(normalized);
        }
        catch (Exception ex)
        {
            LaunchDiagnostics.LogException($"Failed to delete empty plugin asset backup folder '{directory}'", ex);
        }
    }

    private static void CleanupOrphanGuidFoldersUnsafe(PluginAssetBackupManifest manifest)
    {
        if (!Directory.Exists(BackupRootDirectory))
        {
            return;
        }

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.BackupAbsolutePath))
            {
                continue;
            }

            var parent = Path.GetDirectoryName(entry.BackupAbsolutePath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                referenced.Add(NormalizePath(parent));
            }
        }

        IEnumerable<string> subfolders;
        try
        {
            subfolders = Directory.EnumerateDirectories(BackupRootDirectory).ToList();
        }
        catch (Exception ex)
        {
            LaunchDiagnostics.LogException(
                "Failed to enumerate plugin asset backup folders for orphan cleanup", ex);
            return;
        }

        foreach (var directory in subfolders)
        {
            var name = Path.GetFileName(directory);
            if (!Guid.TryParseExact(name, "N", out _))
            {
                continue;
            }

            var normalized = NormalizePath(directory);
            if (referenced.Contains(normalized))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex)
            {
                LaunchDiagnostics.LogException(
                    $"Failed to delete orphan plugin asset backup folder '{directory}'", ex);
            }
        }
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private sealed class PluginAssetBackupManifest
    {
        public List<PluginAssetBackupEntry> Entries { get; set; } = new();
    }

    private sealed class PluginAssetBackupEntry
    {
        public string TargetAbsolutePath { get; set; } = string.Empty;
        public string? BackupAbsolutePath { get; set; }
        public bool OriginalExisted { get; set; }
        public List<string> ClaimingPluginIds { get; set; } = new();
    }
}
