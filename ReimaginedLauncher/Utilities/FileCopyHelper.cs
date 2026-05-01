using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

/// <summary>
/// Shared async file-copy helper used by plugin asset application, backup,
/// and mod install flows. Writes go to a sibling temp file first and are then
/// swapped into place via <see cref="File.Replace(string, string, string)"/>
/// (or <see cref="File.Move(string, string, bool)"/> when no prior file exists)
/// so the destination always receives a brand new file id. This avoids a class
/// of "looks the same, skip it" failures caused by content-unaware tools that
/// key on size + mtime (robocopy without /IS, xcopy /D, rsync defaults, cloud
/// sync placeholders, SMB metadata caching, AV silently dropping writes).
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

    /// <summary>
    /// Extracts every entry from <paramref name="zipPath"/> into
    /// <paramref name="destinationDirectory"/> using the same atomic-replace
    /// strategy as <see cref="CopyFileAsync"/>. This is a drop-in replacement
    /// for <c>ZipFile.ExtractToDirectory(zip, dst, overwriteFiles: true)</c>
    /// that produces a fresh file id per overwritten entry.
    /// </summary>
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

    private static async Task WriteStreamAtomicallyAsync(Stream sourceStream, string destinationPath)
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
                await sourceStream.CopyToAsync(tempStream).ConfigureAwait(false);
                await tempStream.FlushAsync().ConfigureAwait(false);
            }

            if (File.Exists(destinationPath))
            {
                // File.Replace performs an atomic NTFS rename that swaps both
                // data and metadata. Downstream sync tools, cloud agents, and
                // SMB caches see a new file id rather than a same-size/same-
                // mtime overwrite they might otherwise skip.
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
