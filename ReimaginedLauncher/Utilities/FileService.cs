using System;
using System.IO;

namespace ReimaginedLauncher.Utilities;

public static class FileService
{
    public static string? FindAncestorDirectory(string? path, string directoryName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var directory = new DirectoryInfo(path);
        while (directory is not null)
        {
            if (string.Equals(directory.Name, directoryName, StringComparison.OrdinalIgnoreCase))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}