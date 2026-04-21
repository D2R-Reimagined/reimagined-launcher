using System;
using System.IO;
using System.Linq;

namespace ReimaginedLauncher.Utilities;

public static class InstallDirectoryValidator
{
    private const string ExecutableName = "D2R.exe";

    public static string? NormalizeInstallDirectory(string? installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
            return null;

        var directory = installDirectory.EndsWith(ExecutableName, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(installDirectory)
            : installDirectory;

        if (string.IsNullOrEmpty(directory))
            return directory;

        if (!directory.EndsWith(Path.DirectorySeparatorChar) && !directory.EndsWith(Path.AltDirectorySeparatorChar))
            directory += Path.DirectorySeparatorChar;

        return directory;
    }

    public static bool IsValidInstallDirectory(string? installDirectory)
    {
        var normalizedDirectory = NormalizeInstallDirectory(installDirectory);
        if (string.IsNullOrWhiteSpace(normalizedDirectory))
            return false;

        return File.Exists(Path.Combine(normalizedDirectory, ExecutableName));
    }

    public static bool IsValidSteamInstallDirectory(string? installDirectory)
    {
        if (!IsValidInstallDirectory(installDirectory))
            return false;

        var normalizedDirectory = NormalizeInstallDirectory(installDirectory)!;
        return Directory.EnumerateFiles(normalizedDirectory, "steam_*.dll").Any();
    }

    public static bool IsValidD2RmmModsDirectory(string? modsDirectory)
    {
        if (string.IsNullOrWhiteSpace(modsDirectory))
            return false;

        var dirName = Path.GetFileName(modsDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.Equals(dirName, "mods", StringComparison.OrdinalIgnoreCase))
            return false;

        var parentDir = Path.GetDirectoryName(modsDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(parentDir))
            return false;

        return File.Exists(Path.Combine(parentDir, "D2RMM.exe"));
    }

    public static string GetD2RmmValidationMessage(string? modsDirectory)
    {
        if (string.IsNullOrWhiteSpace(modsDirectory))
            return "Invalid location. Please select the mods folder inside the D2RMM directory.";

        var trimmed = modsDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dirName = Path.GetFileName(trimmed);

        if (!string.Equals(dirName, "mods", StringComparison.OrdinalIgnoreCase))
            return "Invalid location. Please select the mods folder inside the D2RMM directory.";

        var parentDir = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrEmpty(parentDir))
            return "Invalid location. Please select the mods folder inside the D2RMM directory.";

        return $"Invalid location. D2RMM.exe not found in the {parentDir} directory.";
    }

    public static string? GetExecutablePath(string? installDirectory)
    {
        var normalizedDirectory = NormalizeInstallDirectory(installDirectory);
        if (string.IsNullOrWhiteSpace(normalizedDirectory))
            return null;

        var executablePath = Path.Combine(normalizedDirectory, ExecutableName);
        return File.Exists(executablePath) ? executablePath : null;
    }

    /// <summary>
    /// Resolves the D2RMM mod folder inside the given mods directory.
    /// Accepts either "Reimagined" or "Reimagined.mpq" as long as the folder
    /// contains a "data" subfolder with a "modinfo.json" file.
    /// Prefers "Reimagined" over "Reimagined.mpq" when both exist.
    /// </summary>
    public static string? ResolveD2RmmModFolder(string? modsDirectory)
    {
        if (string.IsNullOrWhiteSpace(modsDirectory))
            return null;

        var candidates = new[] { "Reimagined", "Reimagined.mpq" };
        foreach (var candidate in candidates)
        {
            var candidatePath = Path.Combine(modsDirectory, candidate);
            if (Directory.Exists(candidatePath) &&
                Directory.Exists(Path.Combine(candidatePath, "data")) &&
                File.Exists(Path.Combine(candidatePath, "modinfo.json")))
            {
                return candidatePath;
            }
        }

        return null;
    }
}
