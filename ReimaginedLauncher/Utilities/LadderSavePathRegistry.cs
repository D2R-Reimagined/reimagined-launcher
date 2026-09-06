using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ReimaginedLauncher.Utilities;

internal static class LadderSavePathRegistry
{
    internal static string RegistryPath(string modsDirectory) =>
        Path.Combine(modsDirectory, ".reimagined-launcher", "ladder-save-directories.json");

    internal static string GetOrCreate(string modsDirectory, Guid ladderId, string? ladderName)
    {
        if (ladderId == Guid.Empty)
            throw new IOException("A ladder ID is required to select its save folder.");

        var registryPath = RegistryPath(modsDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(registryPath)!);
        using var registryLock = new FileStream(registryPath + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var paths = File.Exists(registryPath)
            ? JsonSerializer.Deserialize<Dictionary<Guid, string>>(File.ReadAllText(registryPath))
              ?? throw new IOException("The ladder save folder registry is invalid.")
            : new Dictionary<Guid, string>();
        var claimedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, path) in paths)
        {
            if (!IsValidPath(path, id) || !claimedPaths.Add(path.Replace('\\', '/')))
                throw new IOException("The ladder save folder registry contains an invalid or shared folder. Character saves are not changed.");
        }

        if (paths.TryGetValue(ladderId, out var existing))
            return existing.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

        var candidates = Directory.EnumerateDirectories(modsDirectory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false
            })
            .Select(path => Path.GetRelativePath(modsDirectory, path))
            .Where(path => IsValidPath(path, ladderId) && !claimedPaths.Contains(path.Replace('\\', '/')))
            .ToArray();
        if (candidates.Length > 1)
            throw new IOException($"Multiple existing save folders match this ladder: {string.Join(", ", candidates)}. Select the correct folder before continuing; character saves are not changed.");

        var selected = candidates.Length == 1
            ? candidates[0]
            : LadderSaveDirectoryService.BuildLadderSavePath(ladderId, ladderName);
        if (claimedPaths.Contains(selected.Replace('\\', '/')))
            throw new IOException("The selected save folder already belongs to another ladder.");

        paths.Add(ladderId, selected.Replace('\\', '/'));
        var temporaryPath = registryPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(paths, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, registryPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
        return selected;
    }

    private static bool IsValidPath(string? path, Guid ladderId)
    {
        if (ladderId == Guid.Empty || string.IsNullOrWhiteSpace(path)) return false;
        var id = ladderId.ToString("N");
        if (!path.EndsWith("-" + id, StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith("-" + id[..8], StringComparison.OrdinalIgnoreCase)) return false;

        return path.Split('/', '\\').All(segment =>
            !string.IsNullOrWhiteSpace(segment) && segment is not "." and not ".."
            && !segment.Any(character => character < 32 || "<>:\"|?*".Contains(character)));
    }
}
