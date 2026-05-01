using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

/// <summary>
/// Shared async file-copy helper. Writes go to a sibling temp file then atomic-replace, so the
/// destination receives a fresh file id (avoids size+mtime skip behavior in sync tools/AV).
/// </summary>
internal static class FileCopyHelper
{
    public static async Task CopyFileAsync(string sourcePath, string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using (var sourceStream = new FileStream(
                         sourcePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read))
        {
            await WriteStreamAtomicallyAsync(sourceStream, destinationPath).ConfigureAwait(false);
        }
    }

    /// <summary>Extracts <paramref name="zipPath"/> into <paramref name="destinationDirectory"/> using per-entry atomic replace (fresh file id per overwrite).</summary>
    public static async Task ExtractZipAsync(string zipPath, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        var fullDestination = Path.GetFullPath(destinationDirectory);
        if (!fullDestination.EndsWith(Path.DirectorySeparatorChar) &&
            !fullDestination.EndsWith(Path.AltDirectorySeparatorChar))
        {
            fullDestination += Path.DirectorySeparatorChar;
        }

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.FullName))
            {
                continue;
            }

            var destinationPath = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName));
            if (!destinationPath.StartsWith(fullDestination, StringComparison.OrdinalIgnoreCase))
            {
                // Defend against zip slip.
                throw new InvalidDataException(
                    $"Zip entry '{entry.FullName}' resolves outside the destination directory.");
            }

            // Directory entry.
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            var entryDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(entryDirectory))
            {
                Directory.CreateDirectory(entryDirectory);
            }

            await using var sourceStream = entry.Open();
            await WriteStreamAtomicallyAsync(sourceStream, destinationPath).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Atomically writes <paramref name="sourceStream"/> to <paramref name="destinationPath"/> via a sibling temp + <c>File.Replace</c>/<c>Move</c>; cancellation removes the staging file.
    /// </summary>
    internal static async Task WriteStreamAtomicallyAsync(
        Stream sourceStream,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = destinationPath + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            await using (var tempStream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None))
            {
                await sourceStream.CopyToAsync(tempStream, cancellationToken).ConfigureAwait(false);
                await tempStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(destinationPath))
            {
                // Atomic NTFS rename: produces a new file id so size+mtime-keyed tools don't skip.
                File.Replace(tempPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, destinationPath);
            }

            // Bump mtime to "now" so any size+mtime-keyed comparator sees the
            // file as strictly newer than the prior version.
            try
            {
                File.SetLastWriteTimeUtc(destinationPath, DateTime.UtcNow);
            }
            catch
            {
                // Best-effort; failing to bump mtime must not abort the copy.
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
    }
}
