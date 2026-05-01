using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities.Casc;

/// <summary>
/// Action performed for a single path during orphan recovery.
/// </summary>
public enum CascOrphanRecoveryAction
{
    /// <summary>Path was not tracked in the manifest; on-disk file (if present) was deleted.</summary>
    NotTracked,

    /// <summary>File was re-extracted from CASC; <c>mod</c> token stripped, <c>casc</c> ownership restored.</summary>
    Restored,

    /// <summary>On-disk bytes deleted; manifest entry dropped (no remaining owners).</summary>
    Deleted,

    /// <summary>
    /// On-disk bytes left intact (a non-CASC owner — typically a plugin — still
    /// claims the path); only the <c>mod</c> token was stripped from the manifest.
    /// </summary>
    SourceUpdated,

    /// <summary>An exception occurred; the entry was left untouched on disk.</summary>
    Failed
}

/// <summary>
/// Per-path outcome for orphan recovery.
/// </summary>
public sealed record CascOrphanRecoveryItem(
    string Path,
    CascOrphanRecoveryAction Action,
    long BytesWritten = 0,
    string? Error = null);

/// <summary>
/// Knobs for <see cref="CascOrphanRecoveryService.ReconcileRemovedPathsAsync"/>.
/// </summary>
public sealed record CascOrphanRecoveryOptions(
    bool PruneEmptyDirectories = true)
{
    public static readonly CascOrphanRecoveryOptions Default = new();
}

/// <summary>
/// Aggregate result of an orphan-recovery pass.
/// </summary>
public sealed record CascOrphanRecoveryResult(
    int Restored,
    int Deleted,
    int SourceUpdated,
    int NotTracked,
    int Failed,
    long BytesWritten,
    int DirectoriesPruned,
    TimeSpan Elapsed,
    IReadOnlyList<CascOrphanRecoveryItem> Items);

/// <summary>
/// Phase 1h — reconciles paths that the active mod payload no longer ships.
/// For each removed path the service either re-extracts the underlying CASC
/// default (when one is recorded in the manifest and a live storage handle
/// is supplied), deletes the stale on-disk bytes, or simply strips the
/// <c>mod</c> ownership token while leaving a remaining overlay (e.g. a
/// plugin) intact.
/// </summary>
/// <remarks>
/// Intended to be invoked immediately after a mod install/update has applied
/// new files to <paramref name="destinationRoot"/>: callers compute
/// <c>removedPaths = oldModPayload \ newModPayload</c> and pass them in. The
/// service is deliberately tolerant of a null <see cref="SafeCascStorageHandle"/>
/// — when CASC is unavailable, restorable entries fall through to the delete
/// path and keep their <c>casc</c> token in the manifest so a later fastload
/// or delta pass can re-materialise them.
/// </remarks>
public sealed class CascOrphanRecoveryService
{
    private readonly CascExtractionService _extraction;
    private readonly CascFastloadManifestService _manifestService;

    public CascOrphanRecoveryService(
        CascExtractionService extraction,
        CascFastloadManifestService manifestService)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(manifestService);
        _extraction = extraction;
        _manifestService = manifestService;
    }

    /// <summary>
    /// Reconciles every path in <paramref name="removedPaths"/> against the
    /// manifest at <paramref name="destinationRoot"/>. See the class remarks
    /// for the full decision table.
    /// </summary>
    public async Task<CascOrphanRecoveryResult> ReconcileRemovedPathsAsync(
        IEnumerable<string> removedPaths,
        SafeCascStorageHandle? storage,
        string destinationRoot,
        CascOrphanRecoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(removedPaths);

        if (string.IsNullOrWhiteSpace(destinationRoot))
        {
            throw new ArgumentException("Destination root must be provided.", nameof(destinationRoot));
        }

        options ??= CascOrphanRecoveryOptions.Default;
        var sw = Stopwatch.StartNew();

        var manifest = await _manifestService.LoadAsync(cancellationToken).ConfigureAwait(false);

        // Index live CASC entries lazily — only opened the first time we need
        // a CKey lookup so the no-storage path stays cheap.
        Dictionary<string, CascFileEntry>? cascIndex = null;

        var items = new List<CascOrphanRecoveryItem>();
        var directoriesToCheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manifestRemovals = new List<string>();
        var manifestUpdates = new List<CascFastloadEntry>();

        long bytesWritten = 0;

        foreach (var rawPath in removedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            var diskPath = Path.Combine(destinationRoot, CascExtractionService.NormalizeRelativePath(rawPath));
            var entry = CascFastloadManifestService.FindEntry(manifest, rawPath);

            if (entry is null)
            {
                // Untracked: best-effort delete of the stale mod-only file.
                if (TryDeleteFile(diskPath, out _, out var error))
                {
                    QueueParent(directoriesToCheck, diskPath);
                    items.Add(new CascOrphanRecoveryItem(rawPath, CascOrphanRecoveryAction.NotTracked));
                }
                else
                {
                    items.Add(new CascOrphanRecoveryItem(rawPath, CascOrphanRecoveryAction.NotTracked, Error: error));
                }
                continue;
            }

            var withoutMod = StripModToken(entry.Source);
            var hasCascAfter = ContainsToken(withoutMod, CascFastloadEntry.SourceTokens.Casc);
            var hasPluginAfter = ContainsToken(withoutMod, CascFastloadEntry.SourceTokens.Plugin);
            var hasCascCKey = !string.IsNullOrEmpty(entry.CascCKey);

            // Try CASC restore first when both the manifest fingerprint and a
            // live storage handle are available.
            if (hasCascAfter && hasCascCKey && storage is not null)
            {
                cascIndex ??= await BuildCascIndexAsync(storage, cancellationToken).ConfigureAwait(false);

                if (cascIndex.TryGetValue(entry.Path, out var cascEntry))
                {
                    try
                    {
                        var written = await _extraction
                            .ExtractEntryAsync(storage, cascEntry, diskPath, cancellationToken)
                            .ConfigureAwait(false);

                        bytesWritten += written;

                        entry.Source = string.IsNullOrEmpty(withoutMod)
                            ? CascFastloadEntry.SourceTokens.Casc
                            : withoutMod;
                        entry.ModVersion = null;
                        entry.CKey = HexEncode(cascEntry.CKey);
                        entry.CascCKey = entry.CKey;
                        entry.Size = (long)cascEntry.FileSize;

                        manifestUpdates.Add(entry);
                        items.Add(new CascOrphanRecoveryItem(rawPath, CascOrphanRecoveryAction.Restored, written));
                        continue;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        items.Add(new CascOrphanRecoveryItem(rawPath, CascOrphanRecoveryAction.Failed, Error: ex.Message));
                        continue;
                    }
                }
            }

            if (hasPluginAfter && !hasCascAfter)
            {
                // A plugin still owns the on-disk bytes; leave them alone and
                // let plugin reconciliation (Phase 1i) re-apply on next launch.
                entry.Source = withoutMod;
                entry.ModVersion = null;
                manifestUpdates.Add(entry);
                items.Add(new CascOrphanRecoveryItem(rawPath, CascOrphanRecoveryAction.SourceUpdated));
                continue;
            }

            // Either no remaining owners (mod-only entry) or a CASC default
            // we cannot currently materialise. Delete the stale bytes; drop
            // the manifest row when nothing is left, otherwise keep a slim
            // entry so a future fastload pass can re-extract from CASC.
            var deleted = TryDeleteFile(diskPath, out _, out var delError);
            if (deleted)
            {
                QueueParent(directoriesToCheck, diskPath);
            }

            if (string.IsNullOrEmpty(withoutMod))
            {
                manifestRemovals.Add(entry.Path);
                items.Add(deleted
                    ? new CascOrphanRecoveryItem(rawPath, CascOrphanRecoveryAction.Deleted)
                    : new CascOrphanRecoveryItem(rawPath, CascOrphanRecoveryAction.Deleted, Error: delError));
            }
            else
            {
                entry.Source = withoutMod;
                entry.ModVersion = null;
                manifestUpdates.Add(entry);
                items.Add(deleted
                    ? new CascOrphanRecoveryItem(rawPath, CascOrphanRecoveryAction.SourceUpdated)
                    : new CascOrphanRecoveryItem(rawPath, CascOrphanRecoveryAction.SourceUpdated, Error: delError));
            }
        }

        // Persist manifest changes atomically.
        if (manifestRemovals.Count > 0 || manifestUpdates.Count > 0)
        {
            await _manifestService.UpdateAsync(m =>
            {
                foreach (var path in manifestRemovals)
                {
                    CascFastloadManifestService.Remove(m, path);
                }

                foreach (var updated in manifestUpdates)
                {
                    CascFastloadManifestService.AddOrUpdate(m, updated);
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        var directoriesPruned = 0;
        if (options.PruneEmptyDirectories && directoriesToCheck.Count > 0)
        {
            directoriesPruned = PruneEmptyDirectories(destinationRoot, directoriesToCheck, cancellationToken);
        }

        sw.Stop();

        var restored = items.Count(i => i.Action == CascOrphanRecoveryAction.Restored);
        var deletedCount = items.Count(i => i.Action == CascOrphanRecoveryAction.Deleted);
        var sourceUpdated = items.Count(i => i.Action == CascOrphanRecoveryAction.SourceUpdated);
        var notTracked = items.Count(i => i.Action == CascOrphanRecoveryAction.NotTracked);
        var failed = items.Count(i => i.Action == CascOrphanRecoveryAction.Failed);

        return new CascOrphanRecoveryResult(
            Restored: restored,
            Deleted: deletedCount,
            SourceUpdated: sourceUpdated,
            NotTracked: notTracked,
            Failed: failed,
            BytesWritten: bytesWritten,
            DirectoriesPruned: directoriesPruned,
            Elapsed: sw.Elapsed,
            Items: items);
    }

    private async Task<Dictionary<string, CascFileEntry>> BuildCascIndexAsync(
        SafeCascStorageHandle storage,
        CancellationToken cancellationToken)
    {
        var entries = await _extraction
            .IndexAsync(storage, filter: null, cancellationToken)
            .ConfigureAwait(false);

        var dict = new Dictionary<string, CascFileEntry>(entries.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            dict[e.Path] = e;
        }
        return dict;
    }

    private static bool TryDeleteFile(string path, out long bytesDeleted, out string? error)
    {
        bytesDeleted = 0;
        error = null;

        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            try
            {
                bytesDeleted = new FileInfo(path).Length;
            }
            catch
            {
                // Best-effort size lookup; deletion is what matters.
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void QueueParent(HashSet<string> directories, string filePath)
    {
        var parent = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(parent))
        {
            directories.Add(parent);
        }
    }

    private static int PruneEmptyDirectories(
        string destinationRoot,
        HashSet<string> directories,
        CancellationToken cancellationToken)
    {
        var pruned = 0;
        string rootFull;
        try
        {
            rootFull = Path.GetFullPath(destinationRoot);
        }
        catch
        {
            return 0;
        }

        foreach (var initial in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? dir;
            try
            {
                dir = Path.GetFullPath(initial);
            }
            catch
            {
                continue;
            }

            try
            {
                while (!string.IsNullOrEmpty(dir) &&
                       dir.Length > rootFull.Length &&
                       dir.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) &&
                       Directory.Exists(dir) &&
                       Directory.EnumerateFileSystemEntries(dir).FirstOrDefault() is null)
                {
                    Directory.Delete(dir);
                    pruned++;
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch
            {
                // Best-effort; leaving an empty directory is harmless.
            }
        }

        return pruned;
    }

    private static bool ContainsToken(string? source, string token)
    {
        if (string.IsNullOrEmpty(source))
        {
            return false;
        }

        foreach (var t in source.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(t, token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string StripModToken(string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return string.Empty;
        }

        var parts = source
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !string.Equals(t, CascFastloadEntry.SourceTokens.Mod, StringComparison.OrdinalIgnoreCase));

        return string.Join('+', parts);
    }

    private static string HexEncode(byte[] bytes)
    {
        return bytes.Length == 0 ? string.Empty : Convert.ToHexString(bytes);
    }
}
