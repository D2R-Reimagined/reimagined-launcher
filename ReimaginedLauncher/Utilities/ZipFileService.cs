using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.Versioning;

namespace ReimaginedLauncher.Utilities;

public static class ZipFileService
{
    /// <summary>
    /// Same as <see cref="ZipFile.ExtractToDirectory(string, string, bool)"/>,
    /// followed by a reset of the extracted files' permissions (if using a Unix based OS).
    /// </summary>
    /// <param name="sourceArchiveFileName"></param>
    /// <param name="destinationDirectoryName"></param>
    /// <param name="overwriteFiles"></param>
    public static void ExtractToDirectory(
        string sourceArchiveFileName,
        string destinationDirectoryName,
        bool overwriteFiles)
    {
        ZipFile.ExtractToDirectory(sourceArchiveFileName, destinationDirectoryName, overwriteFiles: true);
        ResetPermissions(destinationDirectoryName);
    }

    /// <summary>
    /// Same as <see cref="ZipFile.ExtractToDirectory(string, string)"/>,
    /// followed by a reset of the extracted files' permissions (if using a Unix based OS).
    /// </summary>
    /// <param name="sourceArchiveFileName"></param>
    /// <param name="destinationDirectoryName"></param>
    public static void ExtractToDirectory(string sourceArchiveFileName, string destinationDirectoryName)
    {
        ZipFile.ExtractToDirectory(sourceArchiveFileName, destinationDirectoryName);
        ResetPermissions(destinationDirectoryName);
    }

    /// <summary>
    /// Same as <see cref="ZipFile.Open(string, ZipArchiveMode)"/>.
    /// </summary>
    /// <param name="sourceArchiveFileName"></param>
    /// <param name="destinationDirectoryName"></param>
    public static ZipArchive Open(string archiveFileName, ZipArchiveMode mode)
    {
        return ZipFile.Open(archiveFileName, mode);
    }

    /// <summary>
    /// Same as <see cref="ZipFile.OpenRead(string)"/>.
    /// </summary>
    /// <param name="sourceArchiveFileName"></param>
    /// <param name="destinationDirectoryName"></param>
    public static ZipArchive OpenRead(string archiveFileName)
    {
        return ZipFile.OpenRead(archiveFileName);
    }
    
    /// <summary>
    /// Sets the file/directory permissions to default Unix ones for user, on Unix systems (Linux and macOS).
    /// Sets files to 0644, and directories to 0755.
    /// </summary>
    private static void ResetPermissions(string directory)
    {
        if (!OperatingSystem.IsWindows() && Directory.Exists(directory))
        {
            ResetDirectoryPermission(directory);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static void ResetDirectoryPermission(string dir)
    {
        ResetFilePermission(dir, isDirectory: true);

        foreach (var file in Directory.EnumerateFiles(dir))
        {
            ResetFilePermission(file, isDirectory: false);
        }

        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if (new DirectoryInfo(sub).LinkTarget is null)
            {
                ResetDirectoryPermission(sub);
            }
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static void ResetFilePermission(string path, bool isDirectory)
    {
        if (new FileInfo(path).LinkTarget is not null)
        {
            return;
        }

        File.SetUnixFileMode(path, isDirectory
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
              UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
              UnixFileMode.OtherRead | UnixFileMode.OtherExecute // 0755
            : UnixFileMode.UserRead | UnixFileMode.UserWrite |
              UnixFileMode.GroupRead | UnixFileMode.OtherRead); // 0644
    }
}