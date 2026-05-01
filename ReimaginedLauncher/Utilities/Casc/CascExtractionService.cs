using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities.Casc;

/// <summary>
/// Filter applied while enumerating a CASC storage; narrows extraction to the directories
/// that accelerate D2R load times (data\global\, data\hd\, data\local\).
/// </summary>
public sealed record CascExtractionFilter(
    bool IncludeGlobal = true,
    bool IncludeHd = true,
    bool IncludeLocal = true,
    uint LocaleMask = CascLocale.None)
{
    /// <summary>
    /// Default fastload filter: extract <c>data\global\</c>, <c>data\hd\</c>,
    /// and <c>data\local\</c>. The uninstalled per-language
    /// <c>locales\&lt;lang&gt;\…</c> TVFS branches are rejected by the
    /// path-prefix tests regardless.
    /// </summary>
    public static readonly CascExtractionFilter Default = new();

    /// <summary>
    /// Optional fast-iteration scope: when non-empty, only entries whose
    /// path starts with one of these prefixes are accepted (in addition to
    /// the include-Global/Hd/Local gates). Useful for targeted test runs
    /// such as <c>data\hd\ui\</c>. Comparison is case-insensitive and
    /// forward-slashes in supplied prefixes are normalised to backslashes.
    /// Empty list = no scope restriction (default).
    /// </summary>
    public IReadOnlyList<string> PathPrefixes { get; init; } = Array.Empty<string>();

    internal bool Accept(CascFileEntry entry)
    {
        var path = StripCascNamespace(entry.Path);
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        // Optional scope gate (used by the "scope to subset" test affordance).
        if (PathPrefixes.Count > 0)
        {
            var anyPrefix = false;
            for (var i = 0; i < PathPrefixes.Count; i++)
            {
                if (PathStartsWith(path, PathPrefixes[i]))
                {
                    anyPrefix = true;
                    break;
                }
            }

            if (!anyPrefix)
            {
                return false;
            }
        }

        // Locale gating is path-based, not flag-based: locale-specific assets live under
        // data\local\ (and the locales\<lang>\... namespace, filtered out by absent prefix).
        if (IncludeGlobal && PathStartsWith(path, "data\\global\\"))
        {
            return true;
        }

        if (IncludeHd && PathStartsWith(path, "data\\hd\\"))
        {
            return true;
        }

        if (IncludeLocal && PathStartsWith(path, "data\\local\\"))
        {
            return true;
        }

        return false;
    }

    /// <summary>Strips a leading TVFS namespace prefix (e.g. "data:data\global\..." → "data\global\...").</summary>
    internal static string StripCascNamespace(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];
            if (c == '\\' || c == '/')
            {
                return path;
            }

            if (c == ':')
            {
                return i + 1 < path.Length ? path[(i + 1)..] : string.Empty;
            }
        }

        return path;
    }

    private static bool PathStartsWith(string path, string prefix)
    {
        if (path.Length < prefix.Length)
        {
            return false;
        }

        // CASC paths are typically backslash-delimited but normalise both ways
        // just in case the native layer ever surfaces a forward-slash variant.
        for (var i = 0; i < prefix.Length; i++)
        {
            var pc = path[i];
            var qc = prefix[i];
            if (pc == '/')
            {
                pc = '\\';
            }

            if (qc == '/')
            {
                qc = '\\';
            }

            if (char.ToLowerInvariant(pc) != char.ToLowerInvariant(qc))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Snapshot of an in-flight extraction. Consumers (UI view-models) compute
/// percent and ETA from these values; the service throttles emission so
/// progress callbacks fire at ~10 Hz rather than per-file.
/// </summary>
public sealed record CascProgress(
    long FilesDone,
    long FilesTotal,
    long BytesDone,
    long BytesTotal,
    string? CurrentPath,
    TimeSpan Elapsed);

/// <summary>Heartbeat emitted while walking the CASC TVFS during indexing (~150k entries).</summary>
/// <param name="EntriesSeen">Total entries observed so far (pre-filter).</param>
/// <param name="EntriesAccepted">Entries that passed the active filter so far.</param>
/// <param name="CurrentPath">Most recent CASC path seen by the enumerator.</param>
/// <param name="Elapsed">Time since indexing started.</param>
public sealed record CascIndexProgress(
    long EntriesSeen,
    long EntriesAccepted,
    string? CurrentPath,
    TimeSpan Elapsed);

/// <summary>
/// Façade over <see cref="ICascNative"/>: open a local storage, query product/build,
/// index filtered entries, and atomically extract a single entry.
/// </summary>
public sealed class CascExtractionService
{
    /// <summary>Default chunk used by <see cref="ICascNative.ExtractTo"/>. 1 MiB.</summary>
    private const int ExtractBufferSize = 1 << 20;

    private readonly ICascNative _native;

    public CascExtractionService(ICascNative native)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }

    public bool IsAvailable => _native.IsAvailable;

    public string? UnavailableReason => _native.UnavailableReason;

    /// <summary>
    /// Opens the local CASC storage rooted at <paramref name="installDirectory"/>
    /// (the D2R install root, parent of the <c>Data</c> folder). Returns
    /// <c>null</c> when the native binary is unavailable or the open fails.
    /// </summary>
    public SafeCascStorageHandle? OpenLocal(string installDirectory, uint localeMask = CascLocale.All)
    {
        if (!_native.IsAvailable)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            return null;
        }

        return _native.OpenStorage(installDirectory, localeMask);
    }

    /// <summary>
    /// Returns the storage product (e.g. <c>("d2r", 67890)</c>) or <c>null</c>
    /// if the info call fails. Used by the manifest (<c>buildKey</c>) and by
    /// the dual-install cross-extract logic to confirm BN and Steam are at
    /// the same build before sharing an extraction.
    /// </summary>
    public CascStorageProduct? GetProduct(SafeCascStorageHandle storage)
    {
        return _native.GetStorageProduct(storage);
    }

    /// <summary>
    /// Synchronously enumerates and filters <paramref name="storage"/>'s
    /// entries on the calling thread. Cooperative cancellation is honoured
    /// between entries.
    /// </summary>
    public IReadOnlyList<CascFileEntry> Index(
        SafeCascStorageHandle storage,
        CascExtractionFilter? filter = null,
        IProgress<CascIndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);

        var f = filter ?? CascExtractionFilter.Default;
        var entries = new List<CascFileEntry>(capacity: 8192);

        var sw = Stopwatch.StartNew();
        var lastEmit = TimeSpan.Zero;
        var emitInterval = TimeSpan.FromMilliseconds(100); // ~10 Hz heartbeat
        long seen = 0;
        long lastLogged = 0;
        // Track last *accepted* path separately from last *seen* so the heartbeat
        // only surfaces files that passed the filter.
        string? lastAcceptedPath = null;

        LaunchDiagnostics.Log($"CASC Index: starting (filter: includeGlobal={f.IncludeGlobal}, includeHd={f.IncludeHd}, includeLocal={f.IncludeLocal}, localeMask=0x{f.LocaleMask:X}).");

        // CascLib enumerates alphabetically; once we leave `data\` we won't see further wanted
        // entries, and `CascFindNextFile` can stall on uninstalled-locale namespaces. Break out.
        bool sawDataNamespace = false;

        try
        {
            foreach (var entry in _native.EnumerateFiles(storage))
            {
                cancellationToken.ThrowIfCancellationRequested();
                seen++;

                var canonical = CascExtractionFilter.StripCascNamespace(entry.Path);
                bool isDataNamespace = !string.IsNullOrEmpty(canonical)
                    && canonical.StartsWith("data\\", StringComparison.OrdinalIgnoreCase);

                if (isDataNamespace)
                {
                    sawDataNamespace = true;
                }
                else if (sawDataNamespace)
                {
                    // We've definitively left the `data\` namespace. Stop now
                    // before CascFindNextFile parks on uninstalled-locale data.
                    LaunchDiagnostics.Log($"CASC Index: leaving data\\ namespace at {seen:N0} seen, {entries.Count:N0} matched. Stopping early to avoid uninstalled-locale stall (next path was '{canonical}').");
                    break;
                }

                if (f.Accept(entry))
                {
                    entries.Add(entry);
                    lastAcceptedPath = entry.Path;
                }

                if (seen - lastLogged >= 5000)
                {
                    lastLogged = seen;
                    LaunchDiagnostics.Log($"CASC Index: {seen:N0} seen, {entries.Count:N0} matched, lastMatched='{lastAcceptedPath ?? "(none yet)"}'.");
                }

                if (progress is not null)
                {
                    var elapsed = sw.Elapsed;
                    if (elapsed - lastEmit >= emitInterval)
                    {
                        lastEmit = elapsed;
                        progress.Report(new CascIndexProgress(seen, entries.Count, lastAcceptedPath, elapsed));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            LaunchDiagnostics.Log($"CASC Index: cancellation observed at {seen:N0} seen / {entries.Count:N0} matched (elapsed {sw.Elapsed}).");
            throw;
        }
        catch (Exception ex)
        {
            LaunchDiagnostics.LogException($"CASC Index: faulted at {seen:N0} seen / {entries.Count:N0} matched", ex);
            throw;
        }

        LaunchDiagnostics.Log($"CASC Index: completed. {seen:N0} entries enumerated, {entries.Count:N0} matched, elapsed {sw.Elapsed}.");

        // Final heartbeat so the UI sees the full count once the walk completes.
        progress?.Report(new CascIndexProgress(seen, entries.Count, null, sw.Elapsed));

        return entries;
    }

    /// <summary>
    /// Asynchronous wrapper over <see cref="Index"/>. The CascLib enumeration
    /// is blocking so the work runs on the thread pool.
    /// </summary>
    public Task<IReadOnlyList<CascFileEntry>> IndexAsync(
        SafeCascStorageHandle storage,
        CascExtractionFilter? filter = null,
        IProgress<CascIndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);

        return Task.Run(() => Index(storage, filter, progress, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Extracts a single CASC entry via temp file + atomic replace. Returns bytes written.
    /// Runs on the thread pool because CascLib's read API is blocking.
    /// </summary>
    public Task<long> ExtractEntryAsync(
        SafeCascStorageHandle storage,
        CascFileEntry entry,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("Destination path must be provided.", nameof(destinationPath));
        }

        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() => ExtractEntryCore(storage, entry, destinationPath, cancellationToken), cancellationToken);
    }

    private long ExtractEntryCore(
        SafeCascStorageHandle storage,
        CascFileEntry entry,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = destinationPath + ".tmp-" + Guid.NewGuid().ToString("N");
        var buffer = new byte[ExtractBufferSize];
        long bytesWritten;

        try
        {
            using (var tempStream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                // CascOpenFile requires the exact TVFS-prefixed name from CascFindFirst/Next;
                // fall back to the stripped Path when FullName is not populated.
                var openName = string.IsNullOrEmpty(entry.FullName) ? entry.Path : entry.FullName!;
                bytesWritten = _native.ExtractTo(storage, openName, tempStream, buffer);
                tempStream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(destinationPath))
            {
                File.Replace(tempPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, destinationPath);
            }

            try
            {
                File.SetLastWriteTimeUtc(destinationPath, DateTime.UtcNow);
            }
            catch
            {
                // Best-effort mtime bump; ignore failures.
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Best-effort cleanup of the staging file.
                }
            }
        }

        return bytesWritten;
    }

    /// <summary>Extracts all entries beneath <paramref name="destinationRoot"/>, mirroring CASC paths to disk.</summary>
    public async Task ExtractAllAsync(
        SafeCascStorageHandle storage,
        IReadOnlyCollection<CascFileEntry> entries,
        string destinationRoot,
        IProgress<CascProgress>? progress = null,
        TimeSpan progressInterval = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(entries);

        if (string.IsNullOrWhiteSpace(destinationRoot))
        {
            throw new ArgumentException("Destination root must be provided.", nameof(destinationRoot));
        }

        Directory.CreateDirectory(destinationRoot);

        var totalFiles = entries.Count;
        var totalBytes = entries.Sum(e => (long)e.FileSize);
        long filesDone = 0;
        long bytesDone = 0;

        var sw = Stopwatch.StartNew();
        var interval = progressInterval == default
            ? TimeSpan.FromMilliseconds(100)
            : progressInterval;
        var lastReport = TimeSpan.Zero;

        progress?.Report(new CascProgress(0, totalFiles, 0, totalBytes, null, sw.Elapsed));

        LaunchDiagnostics.Log($"CASC ExtractAll: starting. {totalFiles:N0} files, {totalBytes:N0} bytes, dest='{destinationRoot}'.");
        long lastLoggedFiles = 0;

        try
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relative = NormalizeRelativePath(entry.Path);
                var destination = Path.Combine(destinationRoot, relative);

                var written = await ExtractEntryAsync(storage, entry, destination, cancellationToken).ConfigureAwait(false);

                filesDone++;
                bytesDone += written;

                if (filesDone - lastLoggedFiles >= 500)
                {
                    lastLoggedFiles = filesDone;
                    LaunchDiagnostics.Log($"CASC ExtractAll: {filesDone:N0}/{totalFiles:N0} files, {bytesDone:N0}/{totalBytes:N0} bytes, last='{entry.Path}'.");
                }

                if (sw.Elapsed - lastReport >= interval)
                {
                    lastReport = sw.Elapsed;
                    progress?.Report(new CascProgress(filesDone, totalFiles, bytesDone, totalBytes, entry.Path, sw.Elapsed));
                }
            }
        }
        catch (OperationCanceledException)
        {
            LaunchDiagnostics.Log($"CASC ExtractAll: cancellation observed at {filesDone:N0}/{totalFiles:N0} files (elapsed {sw.Elapsed}).");
            throw;
        }
        catch (Exception ex)
        {
            LaunchDiagnostics.LogException($"CASC ExtractAll: faulted at {filesDone:N0}/{totalFiles:N0} files", ex);
            throw;
        }

        LaunchDiagnostics.Log($"CASC ExtractAll: completed. {filesDone:N0} files, {bytesDone:N0} bytes, elapsed {sw.Elapsed}.");

        progress?.Report(new CascProgress(filesDone, totalFiles, bytesDone, totalBytes, null, sw.Elapsed));
    }

    /// <summary>
    /// Maps a CASC entry path (always backslash-delimited) to a relative path
    /// suitable for <see cref="Path.Combine(string, string)"/> on the current
    /// platform.
    /// </summary>
    internal static string NormalizeRelativePath(string cascPath)
    {
        if (string.IsNullOrEmpty(cascPath))
        {
            return cascPath;
        }

        var stripped = CascExtractionFilter.StripCascNamespace(cascPath);

        if (Path.DirectorySeparatorChar == '\\')
        {
            return stripped;
        }

        return stripped.Replace('\\', Path.DirectorySeparatorChar);
    }
}
