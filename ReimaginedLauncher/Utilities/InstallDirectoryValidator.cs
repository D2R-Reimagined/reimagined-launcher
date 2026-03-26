using System;
using System.IO;

namespace ReimaginedLauncher.Utilities;

public static class InstallDirectoryValidator
{
    private const string ExecutableName = "D2R.exe";

    public static string? NormalizeInstallDirectory(string? installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
            return null;

        return installDirectory.EndsWith(ExecutableName, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(installDirectory)
            : installDirectory;
    }

    public static bool IsValidInstallDirectory(string? installDirectory)
    {
        var normalizedDirectory = NormalizeInstallDirectory(installDirectory);
        if (string.IsNullOrWhiteSpace(normalizedDirectory))
            return false;

        return File.Exists(Path.Combine(normalizedDirectory, ExecutableName));
    }

    public static string? GetExecutablePath(string? installDirectory)
    {
        var normalizedDirectory = NormalizeInstallDirectory(installDirectory);
        if (string.IsNullOrWhiteSpace(normalizedDirectory))
            return null;

        var executablePath = Path.Combine(normalizedDirectory, ExecutableName);
        return File.Exists(executablePath) ? executablePath : null;
    }
}
