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
        {
            if (TryShortenFullIdPath(modsDirectory, ladderId, existing, paths, claimedPaths, out var shortened))
            {
                paths[ladderId] = shortened.Replace('\\', '/');
                WriteRegistry(registryPath, paths);
                return NormalizeSeparators(shortened);
            }

            return NormalizeSeparators(existing);
        }

        var candidates = Directory.EnumerateDirectories(modsDirectory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false
            })
            .Select(path => Path.GetRelativePath(modsDirectory, path))
            .Where(path => IsValidPath(path, ladderId) && !LadderSaveDirectoryService.IsPreviewSavePath(path)
                && !claimedPaths.Contains(path.Replace('\\', '/')))
            .ToArray();
        if (candidates.Length > 1)
            throw new IOException($"Multiple existing save folders match this ladder: {string.Join(", ", candidates)}. Select the correct folder before continuing; character saves are not changed.");

        var useFullId = HasShortIdConflict(paths, ladderId);
        string selected;
        if (candidates.Length == 1)
        {
            selected = candidates[0];
            if (TryShortenFullIdPath(modsDirectory, ladderId, selected, paths, claimedPaths, out var shortenedCandidate))
            {
                selected = shortenedCandidate;
            }
        }
        else
        {
            selected = LadderSaveDirectoryService.BuildLadderSavePath(ladderId, ladderName, useFullId);
            if (claimedPaths.Contains(selected.Replace('\\', '/')) && !useFullId)
            {
                selected = LadderSaveDirectoryService.BuildLadderSavePath(ladderId, ladderName, useFullId: true);
            }
        }

        if (claimedPaths.Contains(selected.Replace('\\', '/')))
            throw new IOException("The selected save folder already belongs to another ladder.");

        paths.Add(ladderId, selected.Replace('\\', '/'));
        WriteRegistry(registryPath, paths);
        return NormalizeSeparators(selected);
    }

    private static bool TryShortenFullIdPath(
        string modsDirectory,
        Guid ladderId,
        string currentPath,
        IReadOnlyDictionary<Guid, string> paths,
        IReadOnlySet<string> claimedPaths,
        out string shortenedPath)
    {
        shortenedPath = currentPath;
        var id = ladderId.ToString("N");
        if (!currentPath.EndsWith("-" + id, StringComparison.OrdinalIgnoreCase)
            || HasShortIdConflict(paths, ladderId))
        {
            return false;
        }

        var candidate = currentPath[..^(id.Length - 8)];
        if (claimedPaths.Contains(candidate.Replace('\\', '/')))
        {
            return false;
        }

        var oldDirectory = Path.Combine(modsDirectory, NormalizeSeparators(currentPath));
        var newDirectory = Path.Combine(modsDirectory, NormalizeSeparators(candidate));
        if (Directory.Exists(newDirectory))
        {
            if (!Directory.Exists(oldDirectory))
            {
                shortenedPath = candidate;
                return true;
            }

            if (!Directory.EnumerateFileSystemEntries(LadderSaveDirectoryService.ToExtendedPath(oldDirectory)).Any())
            {
                try
                {
                    Directory.Delete(LadderSaveDirectoryService.ToExtendedPath(oldDirectory), recursive: false);
                    shortenedPath = candidate;
                    return true;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            }

            return false;
        }

        if (Directory.Exists(oldDirectory))
        {
            try
            {
                LadderSaveDirectoryService.ShortenRejectedSnapshotPaths(oldDirectory);
                DeleteStaleSnapshotLocks(oldDirectory);
                Directory.Move(
                    LadderSaveDirectoryService.ToExtendedPath(oldDirectory),
                    LadderSaveDirectoryService.ToExtendedPath(newDirectory));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LaunchDiagnostics.Log($"ladder saves: could not shorten existing folder \"{currentPath}\": {exception.Message}");
                return false;
            }
        }

        shortenedPath = candidate;
        return true;
    }

    private static void DeleteStaleSnapshotLocks(string ladderDirectory)
    {
        var serverSavesDirectory = LadderSaveDirectoryService.ToExtendedPath(
            Path.Combine(ladderDirectory, ".server-saves"));
        if (!Directory.Exists(serverSavesDirectory)) return;

        foreach (var lockFile in Directory.GetFiles(serverSavesDirectory, "snapshot-*.json.lock"))
        {
            try
            {
                if (new FileInfo(lockFile).Length == 0)
                {
                    File.Delete(lockFile);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool HasShortIdConflict(IReadOnlyDictionary<Guid, string> paths, Guid ladderId)
    {
        var shortSuffix = "-" + ladderId.ToString("N")[..8];
        foreach (var (otherId, otherPath) in paths)
        {
            if (otherId == ladderId) continue;
            if (otherId.ToString("N").StartsWith(shortSuffix.AsSpan(1), StringComparison.OrdinalIgnoreCase)
                || otherPath.EndsWith(shortSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteRegistry(string registryPath, Dictionary<Guid, string> paths)
    {
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
    }

    private static string NormalizeSeparators(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

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
