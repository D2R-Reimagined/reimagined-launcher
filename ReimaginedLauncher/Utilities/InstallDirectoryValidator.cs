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

        try
        {
            var directory = installDirectory.EndsWith(ExecutableName, StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(installDirectory)
                : installDirectory;

            if (string.IsNullOrEmpty(directory))
                return directory;

            if (!directory.EndsWith(Path.DirectorySeparatorChar) && !directory.EndsWith(Path.AltDirectorySeparatorChar))
                directory += Path.DirectorySeparatorChar;

            return directory;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static bool IsValidInstallDirectory(string? installDirectory)
    {
        var normalizedDirectory = NormalizeInstallDirectory(installDirectory);
        if (string.IsNullOrWhiteSpace(normalizedDirectory))
            return false;

        try
        {
            return File.Exists(Path.Combine(normalizedDirectory, ExecutableName));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool IsValidSteamInstallDirectory(string? installDirectory)
    {
        if (!IsValidInstallDirectory(installDirectory))
            return false;

        try
        {
            var normalizedDirectory = NormalizeInstallDirectory(installDirectory)!;
            return Directory.EnumerateFiles(normalizedDirectory, "steam_*.dll").Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public static bool IsValidD2RmmModsDirectory(string? modsDirectory)
    {
        if (string.IsNullOrWhiteSpace(modsDirectory))
            return false;

        try
        {
            var dirName = Path.GetFileName(modsDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.Equals(dirName, "mods", StringComparison.OrdinalIgnoreCase))
                return false;

            var parentDir = Path.GetDirectoryName(modsDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(parentDir))
                return false;

            return File.Exists(Path.Combine(parentDir, "D2RMM.exe"));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static string GetD2RmmValidationMessage(string? modsDirectory)
    {
        if (string.IsNullOrWhiteSpace(modsDirectory))
            return "Invalid location. Please select the mods folder inside the D2RMM directory.";

        try
        {
            var trimmed = modsDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var dirName = Path.GetFileName(trimmed);

            if (!string.Equals(dirName, "mods", StringComparison.OrdinalIgnoreCase))
                return "Invalid location. Please select the mods folder inside the D2RMM directory.";

            var parentDir = Path.GetDirectoryName(trimmed);
            if (string.IsNullOrEmpty(parentDir))
                return "Invalid location. Please select the mods folder inside the D2RMM directory.";

            return $"Invalid location. D2RMM.exe not found in the {parentDir} directory.";
        }
        catch (ArgumentException)
        {
            return "Invalid location. Please select the mods folder inside the D2RMM directory.";
        }
    }

    public static string? GetExecutablePath(string? installDirectory)
    {
        var normalizedDirectory = NormalizeInstallDirectory(installDirectory);
        if (string.IsNullOrWhiteSpace(normalizedDirectory))
            return null;

        try
        {
            var executablePath = Path.Combine(normalizedDirectory, ExecutableName);
            return File.Exists(executablePath) ? executablePath : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
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

        try
        {
            string[] candidates = ["Reimagined", "Reimagined.mpq"];

            foreach (var name in candidates)
            {
                var candidatePath = Path.Combine(modsDirectory, name);
                var modInfoPath = Path.Combine(candidatePath, "modinfo.json");
                var dataFolder = Path.Combine(candidatePath, "data");

                if (Directory.Exists(candidatePath) && Directory.Exists(dataFolder) && File.Exists(modInfoPath))
                    return candidatePath;
            }
        }
        catch (ArgumentException)
        {
            return null;
        }

        return null;
    }
}
