using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities.Casc;

/// <summary>
/// Filter applied while enumerating a CASC storage. Phase 1 narrows extraction
/// to the directories that actually accelerate D2R load times
/// (<c>data\global\</c>, <c>data\hd\</c>, <c>data\local\</c>) and to the
/// locales the user has opted into.
/// </summary>
/// <param name="IncludeGlobal">Include entries under <c>data\global\</c>.</param>
/// <param name="IncludeHd">Include entries under <c>data\hd\</c>.</param>
/// <param name="IncludeLocal">Include entries under <c>data\local\</c>.</param>
/// <param name="LocaleMask">
/// Bitmask of <see cref="CascLocale"/> values to keep. Locale-neutral entries
/// (<c>dwLocaleFlags == 0</c>) are always kept. Defaults to all locales.
/// </param>
public sealed record CascExtractionFilter(
    bool IncludeGlobal = true,
    bool IncludeHd = true,
    bool IncludeLocal = true,
    uint LocaleMask = CascLocale.All)
{
    public static readonly CascExtractionFilter Default = new();

    internal bool Accept(CascFileEntry entry)
    {
        if (entry.LocaleFlags != 0 && (entry.LocaleFlags & LocaleMask) == 0)
        {
            return false;
        }

        var path = entry.Path;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

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

/// <summary>
/// Higher-level façade over <see cref="ICascNative"/> that the launcher's
/// services and UI bind to. Responsibilities at Phase 1c: open a local
/// storage, surface its product/build info, index a filtered subset of
/// entries, and atomically extract a single entry to disk. Orchestration
/// (full-tree extraction, delta, undo, cross-install, orphan recovery) lands
/// in the subsequent Phase 1 sub-tasks and reuses the primitives defined here.
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);

        var f = filter ?? CascExtractionFilter.Default;
        var entries = new List<CascFileEntry>(capacity: 8192);

        foreach (var entry in _native.EnumerateFiles(storage))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (f.Accept(entry))
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>
    /// Asynchronous wrapper over <see cref="Index"/>. The CascLib enumeration
    /// is blocking so the work runs on the thread pool.
    /// </summary>
    public Task<IReadOnlyList<CascFileEntry>> IndexAsync(
        SafeCascStorageHandle storage,
        CascExtractionFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);

        return Task.Run(() => Index(storage, filter, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Extracts a single CASC entry to <paramref name="destinationPath"/> via
    /// a sibling temp file + atomic <see cref="File.Replace(string, string, string)"/>
    /// swap (or <see cref="File.Move(string, string)"/> when the destination
    /// is new). Returns the number of bytes written.
    /// </summary>
    /// <remarks>
    /// CascLib's read API is blocking, so the extraction itself runs on the
    /// thread pool. Cancellation is checked before the native call begins;
    /// once a file is in flight it runs to completion or fails — single-file
    /// granularity is sufficient for the delta workload because each file is
    /// small (kilobytes to a few MB).
    /// </remarks>
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
                bytesWritten = _native.ExtractTo(storage, entry.Path, tempStream, buffer);
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

    /// <summary>
    /// Extracts all entries in <paramref name="entries"/> beneath
    /// <paramref name="destinationRoot"/>, mirroring each entry's CASC path
    /// into the filesystem (e.g. <c>data\global\excel\armor.txt</c> →
    /// <c>{destinationRoot}\data\global\excel\armor.txt</c>). Progress is
    /// reported to <paramref name="progress"/> at most once per
    /// <paramref name="progressInterval"/>.
    /// </summary>
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

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = NormalizeRelativePath(entry.Path);
            var destination = Path.Combine(destinationRoot, relative);

            var written = await ExtractEntryAsync(storage, entry, destination, cancellationToken).ConfigureAwait(false);

            filesDone++;
            bytesDone += written;

            if (sw.Elapsed - lastReport >= interval)
            {
                lastReport = sw.Elapsed;
                progress?.Report(new CascProgress(filesDone, totalFiles, bytesDone, totalBytes, entry.Path, sw.Elapsed));
            }
        }

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

        if (Path.DirectorySeparatorChar == '\\')
        {
            return cascPath;
        }

        return cascPath.Replace('\\', Path.DirectorySeparatorChar);
    }
}
