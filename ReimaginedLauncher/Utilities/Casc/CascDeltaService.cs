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

/// <summary>
/// One row of the delta plan. <see cref="Entry"/> is non-null for
/// add/update/restore (it is the live CASC entry to extract); for
/// <see cref="CascDeltaReason.Removed"/> only <see cref="ManifestEntry"/> is
/// populated (the CASC entry no longer exists).
/// </summary>
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

/// <summary>
/// Outcome of <see cref="CascDeltaService.ApplyAsync"/>: what was actually
/// written / removed during the pass. Useful for telemetry and the UI
/// "Last update: X added, Y changed, Z removed" status row.
/// </summary>
public sealed record CascDeltaApplyResult(
    int Added,
    int Updated,
    int Restored,
    int Removed,
    long BytesWritten,
    TimeSpan Elapsed);

/// <summary>
/// Phase 1e — orchestrates the CKey-diff between a live CASC storage and the
/// persistent <see cref="CascFastloadManifest"/>, then applies the minimal
/// extract/delete set required to bring the install up to date.
/// </summary>
/// <remarks>
/// This is the core "without redoing extraction" win the issue asks for: a
/// typical D2R patch touches a tiny fraction of the ~150k tracked files, so a
/// delta apply is seconds-to-minutes rather than the 5–50 minute initial
/// extraction. The same algorithm covers the first-run case (an empty
/// manifest → every kept entry shows up as <see cref="CascDeltaReason.Added"/>).
/// </remarks>
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(destinationRoot);

        var entries = await _extraction.IndexAsync(storage, filter, cancellationToken).ConfigureAwait(false);
        var manifest = await _manifestService.LoadAsync(cancellationToken).ConfigureAwait(false);
        return Plan(entries, manifest, destinationRoot, cancellationToken);
    }

    /// <summary>
    /// Applies <paramref name="plan"/>: extracts every Added/Updated/Restored
    /// entry under <paramref name="destinationRoot"/>, removes orphaned files
    /// (only when their manifest entry is CASC-only — overlays are left
    /// alone so plugin / mod content survives a CASC patch), and persists the
    /// updated manifest.
    /// </summary>
    public async Task<CascDeltaApplyResult> ApplyAsync(
        SafeCascStorageHandle storage,
        CascDeltaPlan plan,
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

        // Apply removals: only delete files whose manifest entry is CASC-only.
        // Anything overlaid by a mod or plugin keeps the on-disk overlay
        // (orphan recovery / plugin reconciliation handle those separately).
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

        // Persist the updated manifest atomically.
        await _manifestService.UpdateAsync(manifest =>
        {
            foreach (var item in extractItems)
            {
                var entry = item.Entry!;
                var ckeyHex = HexEncode(entry.CKey);

                var existing = item.ManifestEntry;
                if (existing is null)
                {
                    CascFastloadManifestService.AddOrUpdate(manifest, new CascFastloadEntry
                    {
                        Path = entry.Path,
                        CKey = ckeyHex,
                        Size = (long)entry.FileSize,
                        Source = CascFastloadEntry.SourceTokens.Casc,
                        CascCKey = ckeyHex
                    });
                }
                else
                {
                    existing.CKey = ckeyHex;
                    existing.Size = (long)entry.FileSize;
                    // Refresh the underlying CASC fingerprint so future orphan
                    // recoveries can target the right CKey even if the on-disk
                    // bytes are currently a mod/plugin overlay.
                    existing.CascCKey = ckeyHex;
                    CascFastloadManifestService.AddOrUpdate(manifest, existing);
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
                    CascFastloadManifestService.Remove(manifest, m.Path);
                }
                else
                {
                    // Drop the CASC fingerprint but keep the entry so the
                    // mod/plugin overlay it tracks is still recorded.
                    m.CascCKey = null;
                    CascFastloadManifestService.AddOrUpdate(manifest, m);
                }
            }

            if (product is not null)
            {
                manifest.BuildName = product.CodeName;
                manifest.BuildNumber = product.BuildNumber;
            }
        }, cancellationToken).ConfigureAwait(false);

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
