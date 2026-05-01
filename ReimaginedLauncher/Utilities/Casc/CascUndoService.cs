using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities.Casc;

/// <summary>
/// Knobs for <see cref="CascUndoService.UndoAsync"/>.
/// </summary>
/// <param name="DeleteManifestWhenEmpty">
/// When the undo pass leaves zero entries behind, drop the manifest file
/// from disk so a future "is fastload installed?" check (manifest existence)
/// reports cleanly. Defaults to <c>true</c>.
/// </param>
/// <param name="PruneEmptyDirectories">
/// Remove now-empty parent directories under <c>data\global|hd|local</c>
/// after files are deleted. Pruning stops at the install root and never
/// touches sibling paths the launcher does not own. Defaults to <c>true</c>.
/// </param>
public sealed record CascUndoOptions(
    bool DeleteManifestWhenEmpty = true,
    bool PruneEmptyDirectories = true)
{
    public static readonly CascUndoOptions Default = new();
}

/// <summary>
/// Periodic progress heartbeat emitted while <see cref="CascUndoService.UndoAsync"/>
/// walks the manifest. <paramref name="EntriesProcessed"/> climbs from 0 to
/// <paramref name="EntriesTotal"/>; <paramref name="FilesDeleted"/> is the
/// running count of files actually removed from disk so the UI can show
/// e.g. "Undoing 12,345 / 148,217 (12,300 deleted)...".
/// </summary>
public sealed record CascUndoProgress(int EntriesProcessed, int EntriesTotal, int FilesDeleted);

/// <summary>
/// Outcome of an undo pass. <see cref="OverlaysPreserved"/> counts paths
/// where the on-disk bytes were left intact because a mod or plugin overlay
/// still owns them; for those entries only the <c>casc</c> token (and the
/// underlying <c>CascCKey</c>) was stripped from the manifest.
/// </summary>
public sealed record CascUndoResult(
    int FilesDeleted,
    long BytesDeleted,
    int OverlaysPreserved,
    int DirectoriesPruned,
    int EntriesDropped,
    bool ManifestDeleted,
    TimeSpan Elapsed);

/// <summary>
/// Phase 1g — manifest-driven removal of every file the launcher previously
/// extracted from CASC into <paramref name="destinationRoot"/>. Mod and
/// plugin overlays are preserved (the launcher cannot safely delete bytes
/// it did not write); for those paths the undo pass merely drops the CASC
/// contribution from the manifest so a subsequent reconciliation correctly
/// treats the path as "no CASC default known" rather than "extracted".
/// </summary>
/// <remarks>
/// The undo pass intentionally does <strong>not</strong> consult the live
/// CASC storage — undo is meant to work even when the CASC archives are
/// inaccessible (e.g. user uninstalled D2R or moved the install). Anything
/// the launcher touched is recorded in the manifest, which is the single
/// source of truth for rollback.
/// </remarks>
public sealed class CascUndoService
{
    private static readonly string[] FastloadRoots =
    [
        "data\\global",
        "data\\hd",
        "data\\local"
    ];

    private readonly CascFastloadManifestService _manifestService;

    public CascUndoService(CascFastloadManifestService manifestService)
    {
        ArgumentNullException.ThrowIfNull(manifestService);
        _manifestService = manifestService;
    }

    /// <summary>
    /// Removes every CASC-owned file in the manifest from
    /// <paramref name="destinationRoot"/>, strips CASC ownership from any
    /// remaining overlays, prunes empty fastload directories, and persists
    /// the resulting manifest atomically (deleting it entirely when nothing
    /// is left and <see cref="CascUndoOptions.DeleteManifestWhenEmpty"/> is
    /// set).
    /// </summary>
    public async Task<CascUndoResult> UndoAsync(
        string destinationRoot,
        CascUndoOptions? options = null,
        IProgress<CascUndoProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationRoot))
        {
            throw new ArgumentException("Destination root must be provided.", nameof(destinationRoot));
        }

        options ??= CascUndoOptions.Default;
        var sw = Stopwatch.StartNew();

        var manifest = await _manifestService.LoadAsync(cancellationToken).ConfigureAwait(false);
        var total = manifest.Files.Count;

        // The bulk of UndoAsync is synchronous file IO over potentially
        // 100k+ entries. SemaphoreSlim.WaitAsync inside the manifest service
        // returns synchronously when uncontended, so without this Task.Run
        // the entire loop runs on the calling thread (the UI thread when
        // invoked from a button click) and freezes the launcher until
        // completion. Run on the thread pool to keep the UI responsive.
        var (filesDeleted, bytesDeleted, overlaysPreserved, entriesDropped, directoriesToCheck, remaining) = await Task.Run(() =>
        {
            var fd = 0;
            long bd = 0;
            var op = 0;
            var ed = 0;
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rem = new List<CascFastloadEntry>(manifest.Files.Count);
            var sinceReport = 0;
            const int reportEvery = 500;

            for (var i = 0; i < manifest.Files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = manifest.Files[i];

                if (!HasCascToken(entry.Source))
                {
                    rem.Add(entry);
                }
                else
                {
                    var withoutCasc = StripCascToken(entry.Source);
                    if (string.IsNullOrEmpty(withoutCasc))
                    {
                        var diskPath = Path.Combine(destinationRoot,
                            CascExtractionService.NormalizeRelativePath(entry.Path));

                        if (TryDeleteFile(diskPath, out var deletedBytes))
                        {
                            fd++;
                            bd += deletedBytes;
                            var parent = Path.GetDirectoryName(diskPath);
                            if (!string.IsNullOrEmpty(parent))
                            {
                                dirs.Add(parent);
                            }
                        }

                        ed++;
                    }
                    else
                    {
                        entry.Source = withoutCasc;
                        entry.CascCKey = null;
                        op++;
                        rem.Add(entry);
                    }
                }

                sinceReport++;
                if (sinceReport >= reportEvery)
                {
                    progress?.Report(new CascUndoProgress(i + 1, total, fd));
                    sinceReport = 0;
                }
            }

            progress?.Report(new CascUndoProgress(total, total, fd));
            return (fd, bd, op, ed, dirs, rem);
        }, cancellationToken).ConfigureAwait(false);

        var directoriesPruned = 0;
        if (options.PruneEmptyDirectories && filesDeleted > 0)
        {
            directoriesPruned = await Task.Run(
                () => PruneEmptyDirectories(destinationRoot, directoriesToCheck, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        var manifestDeleted = false;
        if (remaining.Count == 0 && options.DeleteManifestWhenEmpty)
        {
            await _manifestService.UpdateAsync(m => m.Files.Clear(), cancellationToken).ConfigureAwait(false);
            try
            {
                if (File.Exists(_manifestService.ManifestPath))
                {
                    File.Delete(_manifestService.ManifestPath);
                    manifestDeleted = true;
                }
            }
            catch
            {
                // Best-effort: leaving an empty manifest behind is harmless.
            }
        }
        else
        {
            await _manifestService.UpdateAsync(m =>
            {
                m.Files.Clear();
                m.Files.AddRange(remaining);
            }, cancellationToken).ConfigureAwait(false);
        }

        return new CascUndoResult(
            FilesDeleted: filesDeleted,
            BytesDeleted: bytesDeleted,
            OverlaysPreserved: overlaysPreserved,
            DirectoriesPruned: directoriesPruned,
            EntriesDropped: entriesDropped,
            ManifestDeleted: manifestDeleted,
            Elapsed: sw.Elapsed);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="source"/> claims CASC ownership.
    /// Tokens are <c>+</c>-separated; comparison is ordinal-ignore-case.
    /// </summary>
    internal static bool HasCascToken(string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return false;
        }

        foreach (var token in source.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(token, CascFastloadEntry.SourceTokens.Casc, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns <paramref name="source"/> with the <c>casc</c> token removed.
    /// An empty string indicates "no remaining owners" (the entry should be
    /// dropped). Other token order is preserved so manifest diffs stay small.
    /// </summary>
    internal static string StripCascToken(string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return string.Empty;
        }

        var kept = new List<string>(3);
        foreach (var token in source.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(token, CascFastloadEntry.SourceTokens.Casc, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            kept.Add(token);
        }

        return string.Join('+', kept);
    }

    private static bool TryDeleteFile(string path, out long bytesDeleted)
    {
        bytesDeleted = 0;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                bytesDeleted = new FileInfo(path).Length;
            }
            catch
            {
                // Size is informational only; ignore failures.
            }

            File.Delete(path);
            return true;
        }
        catch
        {
            // Best-effort: a file we cannot delete (locked, ACLs, removed
            // out-of-band) should not abort the whole undo pass.
            return false;
        }
    }

    /// <summary>
    /// Walks each candidate directory upward, deleting empty directories that
    /// sit beneath one of the fastload roots (<c>data\global|hd|local</c>).
    /// The fastload root itself is removed if it ends up empty (e.g. after
    /// a full undo); the loop stops once we'd step outside the fastload
    /// roots, so the install root and sibling directories the launcher does
    /// not own are never touched.
    /// </summary>
    private static int PruneEmptyDirectories(
        string destinationRoot,
        HashSet<string> directories,
        CancellationToken cancellationToken)
    {
        var pruned = 0;
        var rootFull = Path.GetFullPath(destinationRoot);

        // Deepest paths first so we collapse children before parents.
        var sorted = new List<string>(directories);
        sorted.Sort((a, b) => b.Length.CompareTo(a.Length));

        foreach (var start in sorted)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = start;
            while (!string.IsNullOrEmpty(current))
            {
                if (!IsUnderFastloadRoot(rootFull, current))
                {
                    break;
                }

                if (!Directory.Exists(current))
                {
                    current = Path.GetDirectoryName(current);
                    continue;
                }

                bool empty;
                try
                {
                    using var enumerator = Directory.EnumerateFileSystemEntries(current).GetEnumerator();
                    empty = !enumerator.MoveNext();
                }
                catch
                {
                    break;
                }

                if (!empty)
                {
                    break;
                }

                try
                {
                    Directory.Delete(current);
                    pruned++;
                }
                catch
                {
                    break;
                }

                current = Path.GetDirectoryName(current);
            }
        }

        return pruned;
    }

    private static bool IsUnderFastloadRoot(string installRoot, string candidate)
    {
        var full = Path.GetFullPath(candidate);
        foreach (var rel in FastloadRoots)
        {
            var fastloadRoot = Path.GetFullPath(Path.Combine(installRoot, rel));
            if (full.Length < fastloadRoot.Length)
            {
                continue;
            }

            if (full.StartsWith(fastloadRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

}
