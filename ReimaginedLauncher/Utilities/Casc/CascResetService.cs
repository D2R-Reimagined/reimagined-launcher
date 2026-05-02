using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities.Casc;

/// <summary>Knobs for <see cref="CascResetService.ResetAsync"/>.</summary>
/// <param name="PruneEmptyDirectories">
/// Remove now-empty parent directories beneath the fastload roots after files
/// are deleted. Defaults to <c>true</c>.
/// </param>
public sealed record CascResetOptions(bool PruneEmptyDirectories = true)
{
    public static readonly CascResetOptions Default = new();
}

/// <summary>Periodic progress for <see cref="CascResetService.ResetAsync"/>.</summary>
public sealed record CascResetProgress(
    int FilesScanned,
    int OrphansDeleted,
    int MismatchedDeleted);

/// <summary>Outcome of a reset pass.</summary>
/// <param name="FilesScanned">On-disk files inspected under the fastload roots.</param>
/// <param name="OrphansDeleted">Files removed because they were not tracked by the manifest at all.</param>
/// <param name="MismatchedDeleted">Tracked CASC-only files removed because their on-disk size did not match the manifest.</param>
/// <param name="OverlayMismatchesIgnored">Tracked overlay (mod/plugin) entries with a size mismatch that were left in place.</param>
/// <param name="ManifestEntriesDropped">Manifest entries cleared so the next delta pass re-extracts them.</param>
/// <param name="DirectoriesPruned">Empty directories removed beneath the fastload roots.</param>
public sealed record CascResetResult(
    int FilesScanned,
    int OrphansDeleted,
    int MismatchedDeleted,
    int OverlayMismatchesIgnored,
    long BytesDeleted,
    int ManifestEntriesDropped,
    int DirectoriesPruned,
    TimeSpan Elapsed);

/// <summary>
/// Reconciles the on-disk fastload tree against the persisted manifest so the
/// installation matches the launcher's tracked vanilla extraction state.
///
/// Removes files that are not tracked at all (e.g. third-party content the user
/// dropped into <c>Reimagined.mpq\data\</c> manually) and CASC-owned files
/// whose on-disk size disagrees with the manifest. Manifest overlay entries
/// (<c>mod</c>/<c>plugin</c>) are intentionally preserved — they are the
/// launcher-owned mod payload, not "vanilla".
///
/// Re-extraction of dropped CASC files is the caller's job (run a delta
/// update afterwards); this service intentionally does not open the live
/// CASC storage so it remains usable when D2R is offline.
/// </summary>
public sealed class CascResetService
{
    private static readonly string[] FastloadRoots =
    [
        "data\\global",
        "data\\hd",
        "data\\local"
    ];

    private readonly CascFastloadManifestService _manifestService;

    public CascResetService(CascFastloadManifestService manifestService)
    {
        ArgumentNullException.ThrowIfNull(manifestService);
        _manifestService = manifestService;
    }

    public async Task<CascResetResult> ResetAsync(
        string destinationRoot,
        CascResetOptions? options = null,
        IProgress<CascResetProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationRoot))
        {
            throw new ArgumentException("Destination root must be provided.", nameof(destinationRoot));
        }

        options ??= CascResetOptions.Default;
        var sw = Stopwatch.StartNew();

        var manifest = await _manifestService.LoadAsync(cancellationToken).ConfigureAwait(false);

        // Path index keyed on the disk-relative form so the on-disk walk can
        // look entries up directly without re-normalising on every hit.
        var index = new Dictionary<string, CascFastloadEntry>(
            manifest.Files.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            var rel = CascExtractionService.NormalizeRelativePath(entry.Path);
            if (!string.IsNullOrEmpty(rel))
            {
                index[rel] = entry;
            }
        }

        var pathsToDrop = new List<string>();
        var directoriesTouched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var (filesScanned, orphansDeleted, mismatchedDeleted, overlayMismatchesIgnored, bytesDeleted) = await Task.Run(() =>
        {
            var scanned = 0;
            var orphans = 0;
            var mismatches = 0;
            var overlayIgnored = 0;
            long bytes = 0;
            var sinceReport = 0;
            const int reportEvery = 250;

            var rootFull = Path.GetFullPath(destinationRoot);

            foreach (var rel in FastloadRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fastloadRoot = Path.Combine(destinationRoot, NormalizeForCurrentPlatform(rel));
                if (!Directory.Exists(fastloadRoot))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(fastloadRoot, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scanned++;

                    string relativeKey;
                    try
                    {
                        relativeKey = Path.GetRelativePath(rootFull, Path.GetFullPath(file));
                    }
                    catch
                    {
                        continue;
                    }

                    if (!index.TryGetValue(relativeKey, out var entry))
                    {
                        if (TryDeleteFile(file, out var deletedBytes))
                        {
                            orphans++;
                            bytes += deletedBytes;
                            QueueParent(directoriesTouched, file);
                        }
                    }
                    else
                    {
                        long size;
                        try
                        {
                            size = new FileInfo(file).Length;
                        }
                        catch
                        {
                            // Inaccessible file: skip rather than risk a false-positive deletion.
                            continue;
                        }

                        if (size == entry.Size)
                        {
                            // Healthy tracked file; nothing to do.
                        }
                        else if (IsCascOnly(entry.Source))
                        {
                            if (TryDeleteFile(file, out var deletedBytes))
                            {
                                mismatches++;
                                bytes += deletedBytes;
                                QueueParent(directoriesTouched, file);
                                pathsToDrop.Add(entry.Path);
                            }
                        }
                        else
                        {
                            // Overlay (mod/plugin) — not "vanilla"; leave for the
                            // user / mod reinstall flow rather than nuking it here.
                            overlayIgnored++;
                        }
                    }

                    sinceReport++;
                    if (sinceReport >= reportEvery)
                    {
                        progress?.Report(new CascResetProgress(scanned, orphans, mismatches));
                        sinceReport = 0;
                    }
                }
            }

            progress?.Report(new CascResetProgress(scanned, orphans, mismatches));
            return (scanned, orphans, mismatches, overlayIgnored, bytes);
        }, cancellationToken).ConfigureAwait(false);

        var manifestEntriesDropped = 0;
        if (pathsToDrop.Count > 0)
        {
            var dropSet = new HashSet<string>(pathsToDrop, StringComparer.OrdinalIgnoreCase);
            await _manifestService.UpdateAsync(m =>
            {
                manifestEntriesDropped = m.Files.RemoveAll(f => dropSet.Contains(f.Path));
            }, cancellationToken).ConfigureAwait(false);
        }

        var directoriesPruned = 0;
        if (options.PruneEmptyDirectories && directoriesTouched.Count > 0)
        {
            directoriesPruned = await Task.Run(
                () => PruneEmptyDirectories(destinationRoot, directoriesTouched, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        return new CascResetResult(
            FilesScanned: filesScanned,
            OrphansDeleted: orphansDeleted,
            MismatchedDeleted: mismatchedDeleted,
            OverlayMismatchesIgnored: overlayMismatchesIgnored,
            BytesDeleted: bytesDeleted,
            ManifestEntriesDropped: manifestEntriesDropped,
            DirectoriesPruned: directoriesPruned,
            Elapsed: sw.Elapsed);
    }

    private static bool IsCascOnly(string? source)
    {
        return string.Equals(source, CascFastloadEntry.SourceTokens.Casc, StringComparison.OrdinalIgnoreCase);
    }

    private static void QueueParent(HashSet<string> directories, string filePath)
    {
        var parent = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(parent))
        {
            directories.Add(parent);
        }
    }

    private static string NormalizeForCurrentPlatform(string rel)
    {
        return Path.DirectorySeparatorChar == '\\'
            ? rel
            : rel.Replace('\\', Path.DirectorySeparatorChar);
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
                // Size is informational only.
            }

            File.Delete(path);
            return true;
        }
        catch
        {
            // Best-effort: a single locked/ACL-protected file should not abort the pass.
            return false;
        }
    }

    private static int PruneEmptyDirectories(
        string destinationRoot,
        HashSet<string> directories,
        CancellationToken cancellationToken)
    {
        var pruned = 0;
        var rootFull = Path.GetFullPath(destinationRoot);

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
            var fastloadRoot = Path.GetFullPath(Path.Combine(installRoot, NormalizeForCurrentPlatform(rel)));
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
