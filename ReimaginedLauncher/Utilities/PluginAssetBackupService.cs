using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using ReimaginedLauncher.Generators;

namespace ReimaginedLauncher.Utilities;

/// <summary>
/// Tracks original copies of mod files that have been replaced by plugin asset
/// operations so they can be restored when a plugin is later disabled or
/// deleted. Backups and the manifest live under the launcher's app data
/// directory and are keyed by absolute target path so the same plugin can
/// safely target multiple installations.
/// </summary>
public static class PluginAssetBackupService
{
    private const string BackupRootDirectoryName = "plugin-asset-backups";
    private const string ManifestFileName = "manifest.json";

    private static readonly JsonSerializerOptions ReadOptions = SerializerOptions.PropertyNameCaseInsensitive;
    private static readonly JsonSerializerOptions WriteOptions = SerializerOptions.CamelCase;

    private static readonly object SyncRoot = new();

    public static string BackupRootDirectory =>
        Path.Combine(SettingsManager.AppDirectoryPath, BackupRootDirectoryName);

    private static string ManifestPath => Path.Combine(BackupRootDirectory, ManifestFileName);

    /// <summary>
    /// Records that <paramref name="pluginId"/> is about to overwrite the file
    /// at <paramref name="destinationAbsolutePath"/>. The first time a target
    /// is seen, the existing file (if any) is copied into the backup store so
    /// it can be restored later. Subsequent calls only register the plugin as
    /// an additional claimant.
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

        var normalizedTarget = NormalizePath(destinationAbsolutePath);
        var manifest = LoadManifest();
        var entry = manifest.Entries.FirstOrDefault(item =>
            string.Equals(item.TargetAbsolutePath, normalizedTarget, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            entry = new PluginAssetBackupEntry { TargetAbsolutePath = normalizedTarget };
            manifest.Entries.Add(entry);

            if (File.Exists(normalizedTarget))
            {
                var backupPath = AllocateBackupPath(normalizedTarget);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                await CopyFileAsync(normalizedTarget, backupPath).ConfigureAwait(false);

                entry.OriginalExisted = true;
                entry.BackupAbsolutePath = backupPath;
            }
            else
            {
                entry.OriginalExisted = false;
                entry.BackupAbsolutePath = null;
            }
        }

        if (!entry.ClaimingPluginIds.Contains(pluginId, StringComparer.OrdinalIgnoreCase))
        {
            entry.ClaimingPluginIds.Add(pluginId);
        }

        SaveManifest(manifest);
    }

    /// <summary>
    /// Restores every target previously claimed by <paramref name="pluginId"/>.
    /// If other enabled plugins still claim the same target the restore is
    /// still performed; the next plugin apply pass will reapply that plugin's
    /// version on top of the restored original.
    /// </summary>
    public static async Task RestoreForPluginAsync(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return;
        }

        var manifest = LoadManifest();
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

        foreach (var entry in affectedEntries)
        {
            entry.ClaimingPluginIds.RemoveAll(id => string.Equals(id, pluginId, StringComparison.OrdinalIgnoreCase));

            // Only restore once the last claimant releases the target so we
            // don't ping-pong the file when multiple plugins target it.
            if (entry.ClaimingPluginIds.Count > 0)
            {
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

                    await CopyFileAsync(entry.BackupAbsolutePath, entry.TargetAbsolutePath).ConfigureAwait(false);
                    TryDeleteFile(entry.BackupAbsolutePath);
                }
                else if (!entry.OriginalExisted && File.Exists(entry.TargetAbsolutePath))
                {
                    // The plugin introduced a brand-new file; remove it so the
                    // mod folder returns to its pre-plugin state.
                    TryDeleteFile(entry.TargetAbsolutePath);
                }
            }
            catch (Exception ex)
            {
                LaunchDiagnostics.LogException(
                    $"Failed to restore plugin asset '{entry.TargetAbsolutePath}'", ex);
                Notifications.SendNotification(
                    $"Failed to restore '{Path.GetFileName(entry.TargetAbsolutePath)}': {ex.Message}",
                    "Warning");
            }
        }

        manifest.Entries.RemoveAll(entry => entry.ClaimingPluginIds.Count == 0);
        SaveManifest(manifest);
    }

    private static PluginAssetBackupManifest LoadManifest()
    {
        lock (SyncRoot)
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
                return new PluginAssetBackupManifest();
            }
        }
    }

    private static void SaveManifest(PluginAssetBackupManifest manifest)
    {
        lock (SyncRoot)
        {
            Directory.CreateDirectory(BackupRootDirectory);
            var json = JsonSerializer.Serialize(manifest, WriteOptions);
            File.WriteAllText(ManifestPath, json);
        }
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

    private static async Task CopyFileAsync(string sourcePath, string destinationPath)
    {
        await using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var destinationStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await sourceStream.CopyToAsync(destinationStream).ConfigureAwait(false);
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
        }
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private sealed class PluginAssetBackupManifest
    {
        [JsonPropertyName("entries")]
        public List<PluginAssetBackupEntry> Entries { get; set; } = new();
    }

    private sealed class PluginAssetBackupEntry
    {
        [JsonPropertyName("targetAbsolutePath")]
        public string TargetAbsolutePath { get; set; } = string.Empty;

        [JsonPropertyName("backupAbsolutePath")]
        public string? BackupAbsolutePath { get; set; }

        [JsonPropertyName("originalExisted")]
        public bool OriginalExisted { get; set; }

        [JsonPropertyName("claimingPluginIds")]
        public List<string> ClaimingPluginIds { get; set; } = new();
    }
}
