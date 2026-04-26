using System.IO;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

/// <summary>
/// Shared async file-copy helper used by plugin asset application and backup
/// flows. Centralises FileStream sharing flags so both call sites stay in sync.
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

        await using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var destinationStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await sourceStream.CopyToAsync(destinationStream).ConfigureAwait(false);
    }
}
