using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities.Casc;

/// <summary>
/// Reason a particular entry ended up in the delta plan.
/// </summary>
public enum CascDeltaReason
{
    /// <summary>Path is in CASC but absent from the manifest.</summary>
    Added,

    /// <summary>Path exists in the manifest but the CASC CKey changed.</summary>
    Updated,

    /// <summary>
    /// Manifest and CASC CKey agree, but the on-disk file is missing — likely
    /// deleted out-of-band or never finished extracting on the previous run.
    /// </summary>
    Restored,

    /// <summary>
    /// Path is in the manifest but no longer surfaced by CASC. The entry will
    /// be removed from disk (when its <c>Source</c> is CASC-only) and dropped
    /// from the manifest.
    /// </summary>
    Removed
}

/// <summary>One row of the delta plan; Entry is null only for Removed rows.</summary>
public sealed record CascDeltaItem(
    string Path,
    CascDeltaReason Reason,
    CascFileEntry? Entry,
    CascFastloadEntry? ManifestEntry);

/// <summary>
/// Result of <see cref="CascDeltaService.Plan"/>: every action the apply
/// pass would take, plus a count of files that need no work.
/// </summary>
public sealed class CascDeltaPlan
{
    public IReadOnlyList<CascDeltaItem> Added { get; init; } = Array.Empty<CascDeltaItem>();
    public IReadOnlyList<CascDeltaItem> Updated { get; init; } = Array.Empty<CascDeltaItem>();
    public IReadOnlyList<CascDeltaItem> Restored { get; init; } = Array.Empty<CascDeltaItem>();
    public IReadOnlyList<CascDeltaItem> Removed { get; init; } = Array.Empty<CascDeltaItem>();
    public long UnchangedCount { get; init; }

    /// <summary>True when no extract or delete work is required.</summary>
    public bool IsNoOp =>
        Added.Count == 0 &&
        Updated.Count == 0 &&
        Restored.Count == 0 &&
        Removed.Count == 0;

    /// <summary>Total files that will be (re-)extracted.</summary>
    public int ExtractCount => Added.Count + Updated.Count + Restored.Count;

    /// <summary>Total bytes that will be (re-)extracted.</summary>
    public long ExtractBytes =>
        Added.Concat(Updated).Concat(Restored).Sum(i => (long)(i.Entry?.FileSize ?? 0));
}

/// <summary>Outcome of an ApplyAsync pass.</summary>
public sealed record CascDeltaApplyResult(
    int Added,
    int Updated,
    int Restored,
    int Removed,
    long BytesWritten,
    TimeSpan Elapsed);

/// <summary>
/// CKey-diffs a live CASC storage against the persisted manifest and applies the minimal
/// extract/delete set. Also handles first-run (empty manifest → every entry is Added).
/// </summary>
public sealed class CascDeltaService
{
    private readonly CascExtractionService _extraction;
    private readonly CascFastloadManifestService _manifestService;

    public CascDeltaService(CascExtractionService extraction, CascFastloadManifestService manifestService)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(manifestService);
        _extraction = extraction;
        _manifestService = manifestService;
    }

    /// <summary>
    /// Diffs <paramref name="entries"/> against <paramref name="manifest"/>
    /// and produces the action plan. <paramref name="destinationRoot"/> is
    /// consulted only for the <c>Restored</c> bucket (file-on-disk check).
    /// </summary>
    public CascDeltaPlan Plan(
        IReadOnlyCollection<CascFileEntry> entries,
        CascFastloadManifest manifest,
        string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(destinationRoot);

        var added = new List<CascDeltaItem>();
        var updated = new List<CascDeltaItem>();
        var restored = new List<CascDeltaItem>();
        var removed = new List<CascDeltaItem>();
        long unchanged = 0;

        var manifestByPath = new Dictionary<string, CascFastloadEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in manifest.Files)
        {
            manifestByPath[m.Path] = m;
        }

        var seenInCasc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            seenInCasc.Add(entry.Path);

            var ckeyHex = HexEncode(entry.CKey);

            if (!manifestByPath.TryGetValue(entry.Path, out var m))
            {
                added.Add(new CascDeltaItem(entry.Path, CascDeltaReason.Added, entry, null));
                continue;
            }

            if (!string.Equals(m.CKey, ckeyHex, StringComparison.OrdinalIgnoreCase))
            {
                updated.Add(new CascDeltaItem(entry.Path, CascDeltaReason.Updated, entry, m));
                continue;
            }

            // Same CKey; verify the file actually exists on disk.
            var diskPath = Path.Combine(destinationRoot, CascExtractionService.NormalizeRelativePath(entry.Path));
            if (!File.Exists(diskPath))
            {
                restored.Add(new CascDeltaItem(entry.Path, CascDeltaReason.Restored, entry, m));
                continue;
            }

            unchanged++;
        }

        foreach (var m in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (seenInCasc.Contains(m.Path))
            {
                continue;
            }

            removed.Add(new CascDeltaItem(m.Path, CascDeltaReason.Removed, null, m));
        }

        return new CascDeltaPlan
        {
            Added = added,
            Updated = updated,
            Restored = restored,
            Removed = removed,
            UnchangedCount = unchanged
        };
    }

    /// <summary>
    /// Convenience: open the storage, index it under <paramref name="filter"/>,
    /// and return the resulting plan. The caller is responsible for keeping
    /// the storage handle alive for a subsequent <see cref="ApplyAsync"/>.
    /// </summary>
    public async Task<CascDeltaPlan> PlanAsync(
        SafeCascStorageHandle storage,
        string destinationRoot,
        CascExtractionFilter? filter = null,
        IProgress<CascIndexProgress>? indexProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(destinationRoot);

        var entries = await _extraction
            .IndexAsync(storage, filter, indexProgress, cancellationToken)
            .ConfigureAwait(false);
        var manifest = await _manifestService.LoadAsync(cancellationToken).ConfigureAwait(false);
        return Plan(entries, manifest, destinationRoot, cancellationToken);
    }

    /// <summary>Extracts Added/Updated/Restored entries, removes CASC-only orphans, and persists the manifest.</summary>
    public async Task<CascDeltaApplyResult> ApplyAsync(
        SafeCascStorageHandle storage,
        CascDeltaPlan plan,
        Action<string>? setStatus,
        string destinationRoot,
        CascStorageProduct? product,
        IProgress<CascProgress>? progress = null,
        TimeSpan progressInterval = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(destinationRoot);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var extractItems = plan.Added
            .Concat(plan.Updated)
            .Concat(plan.Restored)
            .ToList();

        long bytesWritten = 0;

        if (extractItems.Count > 0)
        {
            var entries = extractItems
                .Select(i => i.Entry!)
                .ToList();

            await _extraction
                .ExtractAllAsync(storage, entries, destinationRoot, progress, progressInterval, cancellationToken)
                .ConfigureAwait(false);

            bytesWritten = entries.Sum(e => (long)e.FileSize);
        }

        // Only delete files whose manifest entry is CASC-only; mod/plugin overlays are left alone.
        var removedApplied = 0;
        foreach (var removal in plan.Removed)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var m = removal.ManifestEntry;
            if (m is null)
            {
                continue;
            }

            if (!IsCascOnlySource(m.Source))
            {
                continue;
            }

            var diskPath = Path.Combine(destinationRoot, CascExtractionService.NormalizeRelativePath(m.Path));
            try
            {
                if (File.Exists(diskPath))
                {
                    File.Delete(diskPath);
                }

                TryRemoveEmptyParents(diskPath, destinationRoot);
                removedApplied++;
            }
            catch
            {
                // Best-effort delete; a failure here will surface again on the
                // next plan as a still-present orphan and can be retried.
            }
        }

        // Persist the updated manifest atomically. Build a path-keyed dict, mutate in place, then
        // rebuild manifest.Files once to keep the per-entry update O(1) at ~150k entries.
        LaunchDiagnostics.Log($"CASC ApplyAsync: persisting manifest ({extractItems.Count:N0} extract entries, {plan.Removed.Count:N0} removals)...");
        try { setStatus?.Invoke($"Persisting manifest ({extractItems.Count:N0} entries)..."); } catch { /* status sink errors are non-fatal */ }
        var persistSw = System.Diagnostics.Stopwatch.StartNew();
        await _manifestService.UpdateAsync(manifest =>
        {
            var byPath = new Dictionary<string, CascFastloadEntry>(
                manifest.Files.Count + extractItems.Count,
                StringComparer.OrdinalIgnoreCase);
            foreach (var existing in manifest.Files)
            {
                byPath[existing.Path] = existing;
            }

            foreach (var item in extractItems)
            {
                var entry = item.Entry!;
                var ckeyHex = HexEncode(entry.CKey);

                if (byPath.TryGetValue(entry.Path, out var existing))
                {
                    existing.CKey = ckeyHex;
                    existing.Size = (long)entry.FileSize;
                    // Refresh the underlying CASC fingerprint for future orphan recovery.
                    existing.CascCKey = ckeyHex;
                }
                else
                {
                    byPath[entry.Path] = new CascFastloadEntry
                    {
                        Path = entry.Path,
                        CKey = ckeyHex,
                        Size = (long)entry.FileSize,
                        Source = CascFastloadEntry.SourceTokens.Casc,
                        CascCKey = ckeyHex
                    };
                }
            }

            foreach (var removal in plan.Removed)
            {
                var m = removal.ManifestEntry;
                if (m is null)
                {
                    continue;
                }

                if (IsCascOnlySource(m.Source))
                {
                    byPath.Remove(m.Path);
                }
                else
                {
                    // Drop the CASC fingerprint but keep the entry so the
                    // mod/plugin overlay it tracks is still recorded.
                    m.CascCKey = null;
                    byPath[m.Path] = m;
                }
            }

            manifest.Files.Clear();
            foreach (var v in byPath.Values)
            {
                manifest.Files.Add(v);
            }

            if (product is not null)
            {
                manifest.BuildName = product.CodeName;
                manifest.BuildNumber = product.BuildNumber;
            }
        }, cancellationToken).ConfigureAwait(false);
        persistSw.Stop();
        LaunchDiagnostics.Log($"CASC ApplyAsync: manifest persisted in {persistSw.Elapsed}.");

        sw.Stop();

        return new CascDeltaApplyResult(
            plan.Added.Count,
            plan.Updated.Count,
            plan.Restored.Count,
            removedApplied,
            bytesWritten,
            sw.Elapsed);
    }

    private static bool IsCascOnlySource(string? source)
    {
        return string.Equals(source, CascFastloadEntry.SourceTokens.Casc, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryRemoveEmptyParents(string filePath, string destinationRoot)
    {
        try
        {
            var rootFull = Path.GetFullPath(destinationRoot);
            var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            while (!string.IsNullOrEmpty(dir) &&
                   dir.Length > rootFull.Length &&
                   dir.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) &&
                   Directory.Exists(dir) &&
                   Directory.EnumerateFileSystemEntries(dir).FirstOrDefault() is null)
            {
                Directory.Delete(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch
        {
            // Best-effort; leaving an empty directory is harmless.
        }
    }

    private static string HexEncode(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        return Convert.ToHexString(bytes);
    }
}
