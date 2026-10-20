using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReimaginedLauncher.Utilities.Casc;
using ReimaginedLauncher.Utilities.Json;

namespace ReimaginedLauncher.Utilities;

/// <summary>
/// Installs <c>mods/Reimagined</c> from a mod archive in place, so CASC-extracted vanilla files
/// under <c>Reimagined.mpq</c> survive. Every archive file is rewritten byte for byte; files the
/// previous version shipped and this one does not are reconciled against the CASC fastload
/// manifest, and the manifest records which paths (and which bytes) the mod now owns.
/// </summary>
internal static class ModArchiveInstallService
{
    private const string ModFolderEntryPrefix = "mods/Reimagined/";
    private const string ModMpqEntryPrefix = "mods/Reimagined/Reimagined.mpq/";

    private static readonly string[] FastloadRoots =
    [
        "data\\global\\",
        "data\\hd\\",
        "data\\local\\"
    ];

    public static async Task InstallAsync(
        string zipPath,
        InstallationType installationType,
        string installDirectory,
        CancellationToken cancellationToken = default)
    {
        string archiveRoot;
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            archiveRoot = FindArchiveRoot(archive.Entries.Select(entry => entry.FullName))
                          ?? throw new InvalidDataException("The Reimagined archive does not contain a mods/Reimagined folder.");
        }

        var modDir = Path.Combine(installDirectory, "mods", "Reimagined");
        var modRoot = Path.Combine(modDir, "Reimagined.mpq");

        HashSet<string> previousPaths;
        try
        {
            previousPaths = EnumerateModRelativePaths(modRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LaunchDiagnostics.Log($"Failed to snapshot existing mod payload for orphan recovery: {ex.Message}");
            previousPaths = new HashSet<string>(CascPaths.FileSystemComparer);
        }

        var loaderDir = Path.Combine(modDir, "d2rloader");
        // Holds the user's D2RLoader configs/plugins across the reinstall; a leftover
        // copy from an interrupted install is restored on the next run.
        var preservedLoaderDir = Path.Combine(installDirectory, "mods", "Reimagined.d2rloader");
        if (Directory.Exists(loaderDir))
        {
            if (Directory.Exists(preservedLoaderDir))
            {
                Directory.Delete(preservedLoaderDir, recursive: true);
            }
            Directory.Move(loaderDir, preservedLoaderDir);
        }

        IReadOnlyList<string> written;
        try
        {
            written = await FileCopyHelper
                .ExtractZipAsync(zipPath, installDirectory, archiveRoot, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(preservedLoaderDir))
            {
                if (Directory.Exists(loaderDir))
                {
                    Directory.Delete(loaderDir, recursive: true);
                }
                Directory.CreateDirectory(modDir);
                Directory.Move(preservedLoaderDir, loaderDir);
            }
        }

        var archivePaths = ToModRelativePaths(written);

        // Invalidate per-launch "clean" snapshots so they are retaken from the new mod files.
        try
        {
            ModTweaksService.InvalidateCleanSnapshots(installDirectory);
        }
        catch (Exception ex)
        {
            LaunchDiagnostics.Log($"Failed to invalidate launcher_clean snapshots: {ex.Message}");
        }

        var manifestService = new CascFastloadManifestService(installationType, installDirectory);

        try
        {
            await RemoveDroppedFilesAsync(manifestService, previousPaths, archivePaths, modRoot, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LaunchDiagnostics.Log($"Mod orphan recovery failed: {ex.Message}");
        }

        try
        {
            await RecordModOwnershipAsync(manifestService, archivePaths, modRoot, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LaunchDiagnostics.Log($"CASC manifest mod ownership update failed: {ex.Message}");
        }

        // Migration: remove any leftover mods/Reimagined.backup tree from previous launcher versions.
        try
        {
            var legacyBackupDir = Path.Combine(installDirectory, "mods", "Reimagined.backup");
            if (Directory.Exists(legacyBackupDir))
            {
                Directory.Delete(legacyBackupDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            LaunchDiagnostics.Log($"Failed to remove legacy mods/Reimagined.backup: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the folder prefix (<c>""</c> or e.g. <c>"release/"</c>) that holds <c>mods/Reimagined/</c>
    /// in the archive, or <c>null</c> when the archive has no such folder.
    /// </summary>
    internal static string? FindArchiveRoot(IEnumerable<string> entryNames)
    {
        string? root = null;
        foreach (var rawName in entryNames)
        {
            var name = rawName.Replace('\\', '/');
            var index = 0;
            while ((index = name.IndexOf(ModFolderEntryPrefix, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                if (index == 0 || name[index - 1] == '/')
                {
                    if (root is null || index < root.Length)
                    {
                        root = name[..index];
                    }
                    break;
                }
                index++;
            }
        }

        return root;
    }

    /// <summary>
    /// Paths the previous payload had on disk that the new archive does not ship and the mod owned
    /// (or nobody tracks). CASC-owned vanilla files and plugin-only files are left alone.
    /// </summary>
    internal static IReadOnlyList<string> SelectDroppedModPaths(
        IEnumerable<string> previousPaths,
        ISet<string> archivePaths,
        CascFastloadManifest manifest)
    {
        var manifestByPath = new Dictionary<string, CascFastloadEntry>(manifest.Files.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            manifestByPath[entry.Path] = entry;
        }

        // On Linux "Armor.txt" and "armor.txt" are different files, so a case-only rename leaves a stale copy.
        var shipped = new HashSet<string>(archivePaths, CascPaths.FileSystemComparer);
        return previousPaths
            .Where(path => !shipped.Contains(path))
            .Where(path => !manifestByPath.TryGetValue(path, out var entry) ||
                           CascFastloadEntry.SourceTokens.Contains(entry.Source, CascFastloadEntry.SourceTokens.Mod))
            .ToList();
    }

    private static async Task RemoveDroppedFilesAsync(
        CascFastloadManifestService manifestService,
        HashSet<string> previousPaths,
        HashSet<string> archivePaths,
        string modRoot,
        CancellationToken cancellationToken)
    {
        if (previousPaths.Count == 0)
        {
            return;
        }

        var manifest = await manifestService.LoadAsync(cancellationToken).ConfigureAwait(false);
        var removed = SelectDroppedModPaths(previousPaths, archivePaths, manifest);
        if (removed.Count == 0)
        {
            return;
        }

        var orphanService = new CascOrphanRecoveryService(new CascExtractionService(new NativeCascLib()), manifestService);

        // storage: null — no open CASC handle here; dropped CASC paths are re-extracted by the next delta.
        var result = await orphanService
            .ReconcileRemovedPathsAsync(removed, storage: null, destinationRoot: modRoot, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        LaunchDiagnostics.Log(
            $"Mod orphan recovery: {removed.Count} removed, " +
            $"{result.Deleted} deleted, {result.Restored} restored, " +
            $"{result.SourceUpdated} source-updated, {result.NotTracked} not-tracked, " +
            $"{result.Failed} failed, {result.DirectoriesPruned} dirs pruned.");
    }

    // Marks every path the archive wrote as mod-owned (adding rows for mod-only fastload paths)
    // and records the MD5 of the bytes on disk plus the modinfo.json version.
    private static async Task RecordModOwnershipAsync(
        CascFastloadManifestService manifestService,
        HashSet<string> archivePaths,
        string modRoot,
        CancellationToken cancellationToken)
    {
        if (archivePaths.Count == 0)
        {
            return;
        }

        var pre = await manifestService.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (pre.Files.Count == 0)
        {
            return;
        }

        var hashes = new Dictionary<string, (string Md5, long Size)>(archivePaths.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var path in archivePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diskPath = CascPaths.ResolveUnderRoot(modRoot, path);
            if (diskPath is null)
            {
                continue;
            }

            hashes[path] = (CascFileHash.ComputeMd5Hex(diskPath), new FileInfo(diskPath).Length);
        }

        var installedVersion = CharacterSelectPanelService.GetModVersion(modRoot);
        var flipped = 0;
        var added = 0;

        await manifestService.UpdateAsync(manifest =>
        {
            var byPath = new Dictionary<string, CascFastloadEntry>(manifest.Files.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in manifest.Files)
            {
                byPath[entry.Path] = entry;
            }

            foreach (var (path, hash) in hashes)
            {
                if (byPath.TryGetValue(path, out var entry))
                {
                    var newSource = CascFastloadEntry.SourceTokens.With(entry.Source, CascFastloadEntry.SourceTokens.Mod);
                    if (!string.Equals(newSource, entry.Source, StringComparison.Ordinal))
                    {
                        entry.Source = newSource;
                        flipped++;
                    }
                }
                else if (IsFastloadPath(path))
                {
                    entry = new CascFastloadEntry
                    {
                        Path = path,
                        Size = hash.Size,
                        Source = CascFastloadEntry.SourceTokens.Mod
                    };
                    manifest.Files.Add(entry);
                    added++;
                }
                else
                {
                    continue;
                }

                entry.ModMd5 = hash.Md5;
                if (!string.IsNullOrWhiteSpace(installedVersion))
                {
                    entry.ModVersion = installedVersion;
                }
            }
        }, cancellationToken).ConfigureAwait(false);

        LaunchDiagnostics.Log(
            $"CASC manifest mod ownership: {flipped} entries now flagged as mod overlays, {added} mod-only entries added, " +
            $"{hashes.Count} MD5s recorded (version: {installedVersion ?? "<unknown>"}).");
    }

    private static HashSet<string> ToModRelativePaths(IEnumerable<string> writtenPaths)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in writtenPaths)
        {
            if (path.StartsWith(ModMpqEntryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // Manifest paths use backslashes regardless of platform.
                var relative = CascPaths.FromArchiveRelative(path[ModMpqEntryPrefix.Length..]);
                if (CascPaths.IsSafeRelative(relative))
                {
                    set.Add(relative);
                }
            }
        }

        return set;
    }

    /// <summary>Returns relative paths under <paramref name="modRoot"/> in CASC-style backslash form.</summary>
    private static HashSet<string> EnumerateModRelativePaths(string modRoot)
    {
        var set = new HashSet<string>(CascPaths.FileSystemComparer);
        if (string.IsNullOrWhiteSpace(modRoot) || !Directory.Exists(modRoot))
        {
            return set;
        }

        foreach (var fullPath in Directory.EnumerateFiles(modRoot, "*", SearchOption.AllDirectories))
        {
            var relative = CascPaths.FromNativeRelative(Path.GetRelativePath(modRoot, fullPath));
            if (relative is null)
            {
                // A Linux file name containing '\' would be misread as nested directories; never touch it.
                LaunchDiagnostics.Log($"Mod orphan recovery: skipping '{fullPath}' (file name contains '\\').");
                continue;
            }

            set.Add(relative);
        }

        return set;
    }

    private static bool IsFastloadPath(string path)
    {
        foreach (var root in FastloadRoots)
        {
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
